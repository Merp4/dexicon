using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Embedding;
using Dexicon.Core.Indexing;
using Dexicon.Core.Vectors;
using Dexicon.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Api;

/// <summary>
/// Chunk sets: the ways a corpus's content is cut and embedded.
///
/// The migration story these exist for. A collection's name encodes its model and
/// dimensionality, so changing model means writing into a different vector space —
/// measured at roughly twenty minutes for a three-book corpus on CPU Ollama. Editing in
/// place would mean twenty minutes of half-populated results; instead a new set is built
/// alongside the live one and promoted only once it is complete.
///
///     POST   /chunk-sets              add a set (queued, backfills in the background)
///     POST   /chunk-sets/{n}/promote  make it the default — the atomic switch
///     DELETE /chunk-sets/{n}          drop it, vectors and all
/// </summary>
public static class ChunkSetEndpoints
{
    public static void MapChunkSetEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/corpora/{nameOrId}/chunk-sets").WithTags("Chunk sets");

        g.MapGet("/", async (string nameOrId, RequestContext rc, ScopeResolver scopes,
            CatalogDbContext db, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;
            var tenant = rc.RequireTenant();
            var scope = await scopes.ResolveReadableAsync(tenant, [nameOrId], ct);

            var summary = await CorpusEndpoints.Summarise(db, scope.Targets[0].Corpus, tenant, ct);
            return Results.Ok(summary.ChunkSets);
        });

        g.MapPost("/", async (string nameOrId, CreateChunkSetRequest body, RequestContext rc,
            ScopeResolver scopes, CatalogDbContext db, IVectorStore vectors, IEmbeddingProvider embedder,
            IndexJobQueue queue, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var tenant = rc.RequireTenant();
            var corpus = await scopes.ResolveWritableAsync(tenant, nameOrId, ct);

            if (string.IsNullOrWhiteSpace(body.Name))
                return Results.Problem(title: "A chunk set name is required", statusCode: 400);

            var name = body.Name.Trim();
            if (name.Contains(':'))
                return Results.Problem(
                    title: "A chunk set name cannot contain ':'",
                    detail: "Colon separates corpus from set in `corpus:set`, so a name containing one could not be addressed.",
                    statusCode: 400);

            await db.Entry(corpus).Collection(c => c.ChunkSets).LoadAsync(ct);

            if (corpus.ChunkSets.Exists(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
                return Results.Problem(
                    title: "Chunk set already exists",
                    detail: $"Corpus '{corpus.Name}' already has a set named '{name}'.",
                    statusCode: 409);

            // Inherit from the default set, so "the same but on another model" is a
            // two-field request rather than a full restatement of the configuration.
            var template = corpus.ChunkSets.FirstOrDefault(s => s.IsDefault) ?? corpus.ChunkSets.FirstOrDefault();

            var model = (body.EmbeddingModel ?? template?.EmbeddingModel)?.Trim();
            if (string.IsNullOrWhiteSpace(model))
                return Results.Problem(title: "An embedding model is required", statusCode: 400);

            int dims;
            try
            {
                dims = await embedder.ProbeDimensionsAsync(model, ct);
            }
            catch (EmbeddingUnavailableException ex)
            {
                // Refused rather than guessed. A set created with the wrong dimension
                // count is unusable, and the failure would surface much later as bad
                // results rather than as this message.
                return Results.Problem(
                    title: "Embedding model unavailable",
                    detail: $"Could not probe '{model}': {ex.Message}. The chunk set was not created.",
                    statusCode: 503);
            }

            var set = new ChunkSet
            {
                Id = Ulid.NewUlid().ToString(),
                CorpusId = corpus.Id,
                Name = name,
                Description = body.Description,
                EmbeddingModel = model,
                EmbeddingDimensions = dims,
                CollectionName = vectors.CollectionNameFor(model, dims),
                ChunkSize = body.ChunkSize ?? template?.ChunkSize ?? 768,
                ChunkOverlap = body.ChunkOverlap ?? template?.ChunkOverlap ?? 100,
                BoundaryMode = body.BoundaryMode ?? template?.BoundaryMode ?? "language-aware",
                CustomBoundaryPattern = body.CustomBoundaryPattern ?? template?.CustomBoundaryPattern,
                UnitAware = body.UnitAware ?? template?.UnitAware ?? false,
                SentenceAware = body.SentenceAware ?? template?.SentenceAware ?? false,
                HeadingContext = body.HeadingContext ?? template?.HeadingContext ?? false,
                // NOT default by default. A set with no vectors in it yet would answer
                // every search with nothing, which is the outage this design exists to
                // avoid. Promote it once it has finished backfilling.
                IsDefault = body.MakeDefault == true || template is null,
                State = CorpusState.Indexing,
                CreatedUtc = DateTime.UtcNow,
            };

            if (Validate(set) is { } invalid) return invalid;

            if (set.IsDefault)
                foreach (var other in corpus.ChunkSets) other.IsDefault = false;

            db.ChunkSets.Add(set);
            await db.SaveChangesAsync(ct);
            await vectors.EnsureCollectionAsync(set.CollectionName, dims, ct);

            // Targeted at this set alone, so the live one keeps serving while it builds.
            var job = await queue.EnqueueAsync(corpus.Id, JobKind.Full, set.Id, ct);

            return Results.Accepted($"/api/corpora/{corpus.Name}/chunk-sets/{set.Name}", new
            {
                chunkSet = set.ToSummary(0, 0, 0, 0),
                backfillJob = job.ToSummary(),
            });
        });

        g.MapPatch("/{setName}", async (string nameOrId, string setName, UpdateChunkSetRequest body,
            RequestContext rc, ScopeResolver scopes, CatalogDbContext db, IndexJobQueue queue,
            CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var tenant = rc.RequireTenant();
            var corpus = await scopes.ResolveWritableAsync(tenant, nameOrId, ct);

            var set = await FindSet(db, corpus, setName, ct);
            if (set is null) return NotFound(corpus, setName);

            // Any of these changes what ends up in Qdrant, so they are applied by
            // re-chunking rather than by hoping someone remembers to reindex. The model is
            // absent on purpose: that is a different vector space, so it is a new set.
            var rechunk = false;

            if (body.ChunkSize is { } size) { rechunk |= size != set.ChunkSize; set.ChunkSize = size; }
            if (body.ChunkOverlap is { } ov) { rechunk |= ov != set.ChunkOverlap; set.ChunkOverlap = ov; }
            if (body.BoundaryMode is { Length: > 0 } bm)
            {
                rechunk |= !string.Equals(bm, set.BoundaryMode, StringComparison.Ordinal);
                set.BoundaryMode = bm;
            }
            if (body.CustomBoundaryPattern is not null)
            {
                rechunk |= body.CustomBoundaryPattern != set.CustomBoundaryPattern;
                set.CustomBoundaryPattern = body.CustomBoundaryPattern;
            }
            if (body.UnitAware is { } ua) { rechunk |= ua != set.UnitAware; set.UnitAware = ua; }
            if (body.SentenceAware is { } sa) { rechunk |= sa != set.SentenceAware; set.SentenceAware = sa; }
            if (body.HeadingContext is { } hc) { rechunk |= hc != set.HeadingContext; set.HeadingContext = hc; }
            if (body.Description is not null) set.Description = body.Description;

            if (Validate(set) is { } invalid) return invalid;

            await db.SaveChangesAsync(ct);

            // The fingerprint mixes every one of these in, so a plain refresh is enough:
            // every file in this set now looks changed, and nothing in any other does.
            JobSummary? queued = null;
            if (rechunk)
                queued = (await queue.EnqueueAsync(corpus.Id, JobKind.Refresh, set.Id, ct)).ToSummary();

            return Results.Ok(new { chunkSet = set.ToSummary(0, 0, 0, 0), rechunkJob = queued });
        });

        g.MapPost("/{setName}/promote", async (string nameOrId, string setName, RequestContext rc,
            ScopeResolver scopes, CatalogDbContext db, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var tenant = rc.RequireTenant();
            var corpus = await scopes.ResolveWritableAsync(tenant, nameOrId, ct);

            await db.Entry(corpus).Collection(c => c.ChunkSets).LoadAsync(ct);
            var set = corpus.ChunkSets.FirstOrDefault(s =>
                string.Equals(s.Name, setName, StringComparison.OrdinalIgnoreCase) || s.Id == setName);

            if (set is null) return NotFound(corpus, setName);

            // The whole point of the two-step migration: promotion is the only moment
            // search changes, and it is one UPDATE. Refused while the set is still
            // building, because promoting a half-built set is exactly the outage that
            // building it separately was meant to prevent.
            var pending = await db.FileChunkStates
                .CountAsync(s => s.ChunkSetId == set.Id && s.Status == FileStatus.Pending, ct);

            if (pending > 0)
                return Results.Problem(
                    title: "Chunk set is not ready",
                    detail: $"'{set.Name}' still has {pending:N0} file(s) to index. Promoting now would " +
                            "make search return incomplete results. Wait for the backfill to finish.",
                    statusCode: 409);

            foreach (var other in corpus.ChunkSets) other.IsDefault = false;
            set.IsDefault = true;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { promoted = set.Name, corpus = corpus.Name });
        });

        g.MapDelete("/{setName}", async (string nameOrId, string setName, RequestContext rc,
            ScopeResolver scopes, CatalogDbContext db, IVectorStore vectors, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var tenant = rc.RequireTenant();
            var corpus = await scopes.ResolveWritableAsync(tenant, nameOrId, ct);

            await db.Entry(corpus).Collection(c => c.ChunkSets).LoadAsync(ct);
            var set = corpus.ChunkSets.FirstOrDefault(s =>
                string.Equals(s.Name, setName, StringComparison.OrdinalIgnoreCase) || s.Id == setName);

            if (set is null) return NotFound(corpus, setName);

            // A corpus with no sets is a corpus nothing can search. Refuse rather than
            // leave it in a state whose only exit is creating a set by hand.
            if (corpus.ChunkSets.Count == 1)
                return Results.Problem(
                    title: "Cannot delete the only chunk set",
                    detail: $"'{set.Name}' is the only way '{corpus.Name}' is indexed. Delete the corpus instead, " +
                            "or add another set and promote it first.",
                    statusCode: 409);

            if (set.IsDefault)
                return Results.Problem(
                    title: "Cannot delete the default chunk set",
                    detail: $"Promote another set first; search would otherwise have nothing to fall back to.",
                    statusCode: 409);

            // Vectors first: if the row went first and this threw, the collection would
            // keep points that nothing in the catalogue can name or clean up.
            await vectors.DeleteChunkSetAsync(set.CollectionName, set.Id, ct);
            db.ChunkSets.Remove(set);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        });
    }

    private static Task<ChunkSet?> FindSet(CatalogDbContext db, Corpus corpus, string setName, CancellationToken ct) =>
        db.ChunkSets.FirstOrDefaultAsync(
            s => s.CorpusId == corpus.Id && (s.Name == setName || s.Id == setName), ct);

    private static IResult NotFound(Corpus corpus, string setName) =>
        Results.Problem(
            title: "Unknown chunk set",
            detail: $"Corpus '{corpus.Name}' has no chunk set named '{setName}'.",
            statusCode: 404);

    /// <summary>
    /// Validation in one place, because create and update both need all of it and a rule
    /// enforced on one path only is not a rule.
    /// </summary>
    private static IResult? Validate(ChunkSet set)
    {
        if (set.ChunkSize is < 64 or > 8192)
            return Results.Problem(title: "chunkSize must be between 64 and 8192 tokens", statusCode: 400);

        if (set.ChunkOverlap < 0)
            return Results.Problem(title: "chunkOverlap cannot be negative", statusCode: 400);

        if (set.ChunkOverlap >= set.ChunkSize)
            return Results.Problem(
                title: "chunkOverlap must be smaller than chunkSize",
                detail: $"Asked for overlap {set.ChunkOverlap} with size {set.ChunkSize}.",
                statusCode: 400);

        if (set.BoundaryMode is not ("none" or "blank-line" or "language-aware" or "custom"))
            return Results.Problem(
                title: "Unknown boundary mode",
                detail: $"'{set.BoundaryMode}'. Expected none, blank-line, language-aware or custom.",
                statusCode: 400);

        if (set.BoundaryMode == "custom")
        {
            if (string.IsNullOrWhiteSpace(set.CustomBoundaryPattern))
                return Results.Problem(
                    title: "customBoundaryPattern is required for boundary mode 'custom'",
                    statusCode: 400);

            try
            {
                // Compiled here so a bad pattern fails on the request that set it, rather
                // than part-way through an indexing job an hour later.
                _ = new System.Text.RegularExpressions.Regex(
                    set.CustomBoundaryPattern, System.Text.RegularExpressions.RegexOptions.Multiline,
                    TimeSpan.FromMilliseconds(500));
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(
                    title: "Invalid custom boundary pattern",
                    detail: ex.Message,
                    statusCode: 400);
            }
        }

        return null;
    }
}
