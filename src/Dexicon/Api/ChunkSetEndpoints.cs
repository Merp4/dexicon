using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Documents;
using Dexicon.Core.Embedding;
using Dexicon.Core.Indexing;
using Dexicon.Core.Vectors;
using Dexicon.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dexicon.Api;

/// <summary>
/// Chunk sets: the ways a corpus's content is cut and embedded.
///
/// The migration story these exist for. A collection's name encodes its model and
/// dimensionality, so changing model means writing into a different vector space,
/// measured at roughly twenty minutes for a three-book corpus on CPU Ollama. Editing in
/// place would mean twenty minutes of half-populated results; instead a new set is built
/// alongside the live one and promoted only once it is complete.
///
///     POST   /chunk-sets              add a set (queued, backfills in the background)
///     POST   /chunk-sets/{n}/promote  make it the default: the atomic switch
///     DELETE /chunk-sets/{n}          drop it, vectors and all
/// </summary>
public static class ChunkSetEndpoints
{
    /// <summary>
    /// Add a chunk set to a corpus. A method of its own, and the one the route is mapped to, so a test
    /// calls the handler that runs.
    /// </summary>
    internal static async Task<IResult> CreateAsync(string nameOrId, CreateChunkSetRequest body, RequestContext rc,
        ScopeResolver scopes, CatalogDbContext db, IVectorStore vectors, IEmbeddingService embedder,
        IndexJobQueue queue, IOptions<DexiconOptions> opts, CancellationToken ct)
    {
        if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
        var principal = rc.RequirePrincipal();
        var corpus = await scopes.ResolveWritableAsync(principal, nameOrId, ct);

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

        var indexing = opts.Value.Indexing;

        // Inherit from the default set, so "the same but on another model" is a
        // two-field request rather than a full restatement of the configuration.
        var template = corpus.ChunkSets.FirstOrDefault(s => s.IsDefault) ?? corpus.ChunkSets.FirstOrDefault();

        var model = (body.EmbeddingModel ?? template?.EmbeddingModel)?.Trim();
        if (string.IsNullOrWhiteSpace(model))
            return Results.Problem(title: "An embedding model is required", statusCode: 400);

        var provider = (body.EmbeddingProvider ?? template?.EmbeddingProvider ?? "ollama").Trim();
        var target = new EmbeddingTarget(provider, model);

        int dims;
        try
        {
            dims = await embedder.ProbeDimensionsAsync(target, ct);
        }
        catch (UnknownEmbeddingProviderException ex)
        {
            return Results.Problem(title: "Unknown embedding provider", detail: ex.Message, statusCode: 400);
        }
        catch (EmbeddingUnavailableException ex)
        {
            // Refused rather than guessed. A set created with the wrong dimension
            // count is unusable, and the failure would surface much later as bad
            // results rather than as this message.
            return Results.Problem(
                title: "Embedding model unavailable",
                detail: $"Could not probe '{target}': {ex.Message}. The chunk set was not created.",
                statusCode: 503);
        }

        var set = new ChunkSet
        {
            Id = Ulid.NewUlid().ToString(),
            CorpusId = corpus.Id,
            Name = name,
            Description = body.Description,
            EmbeddingProvider = provider,
            EmbeddingModel = model,
            EmbeddingDimensions = dims,
            CollectionName = vectors.CollectionNameFor(target, dims),
            // Request, then the set this inherits from, then configuration. The
            // last step was a literal 768/100 here, so DEXICON__INDEXING__CHUNKSIZE
            // decided the size of a new corpus and nothing about a set added to one
            // that had none to inherit from.
            ChunkSize = body.ChunkSize ?? template?.ChunkSize ?? indexing.ChunkSize,
            ChunkOverlap = body.ChunkOverlap ?? template?.ChunkOverlap ?? indexing.ChunkOverlap,
            BoundaryMode = body.BoundaryMode ?? template?.BoundaryMode ?? indexing.BoundaryMode,
            CustomBoundaryPattern = body.CustomBoundaryPattern ?? template?.CustomBoundaryPattern,
            UnitAware = body.UnitAware ?? template?.UnitAware ?? false,
            SentenceAware = body.SentenceAware ?? template?.SentenceAware ?? false,
            HeadingContext = body.HeadingContext ?? template?.HeadingContext ?? false,
            // NOT default by default. A set with no vectors in it yet would answer
            // every search with nothing, which is the outage this design exists to
            // avoid. Promote it once it has finished backfilling.
            IsDefault = body.MakeDefault == true || template is null,
            // Nothing has built it, which is what the row says if its job never runs.
            // That a job is building it is read from the jobs, not written here.
            State = CorpusState.Degraded,
            CreatedUtc = DateTime.UtcNow,
        };

        // A corpus with no set to inherit from takes the server's configured settings, and when one of those
        // is what fails the answer names it.
        if (template is null
            && ChunkSettingRules.CheckNewSet(
                body.ChunkSize, body.ChunkOverlap, body.BoundaryMode, body.CustomBoundaryPattern, indexing, out _)
                is { } fromConfiguration)
            return fromConfiguration.ToResult();

        // A set that inherits from another is judged for the settings the request sent, so a template stored
        // before a rule existed is not blamed on a request that sent only a model. A set that takes the
        // server's configuration was judged whole above.
        var sent = (body.ChunkSize is not null ? ChunkSettingFields.Size : 0)
                   | (body.ChunkOverlap is not null ? ChunkSettingFields.Overlap : 0)
                   | (body.BoundaryMode is not null ? ChunkSettingFields.Mode : 0)
                   | (body.CustomBoundaryPattern is not null ? ChunkSettingFields.Pattern : 0);
        if (Validate(set, template is null ? null : sent) is { } invalid) return invalid;

        if (set.IsDefault)
            foreach (var other in corpus.ChunkSets) other.IsDefault = false;

        db.ChunkSets.Add(set);
        await db.SaveChangesAsync(ct);

        // Not cancellable from here: the set is saved, and a cancel before the job left a set marked
        // Degraded that nothing builds. The collection is prepared again before the indexer writes.
        await vectors.EnsureCollectionAsync(set.CollectionName, dims, CancellationToken.None);

        // Rebuild, not Full: both re-index everything, but this one targets a single
        // set, and the jobs list should say which of those is happening.
        var job = await queue.EnqueueAsync(corpus.Id, JobKind.Rebuild, set.Id, CancellationToken.None);

        // The reply as well: on the caller's token a cancel here threw from a handler whose set was saved.
        var activity = await IndexingActivity.ReadAsync(db, [corpus.Id], CancellationToken.None);
        return Results.Accepted(CorpusEndpoints.LocationOf(corpus.Name, set.Name),
            new ChunkSetCreated(set.ToSummary(0, 0, 0, 0, activity.Of(set)), job.ToSummary()));
    }

    /// <summary>
    /// Change a chunk set. A method of its own, and the one the route is mapped to, so a test calls the
    /// handler that runs.
    /// </summary>
    internal static async Task<IResult> UpdateAsync(string nameOrId, string setName, UpdateChunkSetRequest body,
        RequestContext rc, ScopeResolver scopes, CatalogDbContext db, IndexJobQueue queue,
        CancellationToken ct)
    {
        if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
        var principal = rc.RequirePrincipal();
        var corpus = await scopes.ResolveWritableAsync(principal, nameOrId, ct);

        var set = await FindSet(db, corpus, setName, ct);
        if (set is null) return NotFound(corpus, setName);

        // Any of these changes what ends up in Qdrant, so they are applied by
        // re-chunking rather than by hoping someone remembers to reindex. The model is
        // absent on purpose: that is a different vector space, so it is a new set.
        var rechunk = false;

        // Judged for what this request changes, read before the values are assigned. A set stored before a
        // rule existed, such as a size over 8192 that the corpus dialog used to send for a model with a long
        // context, can have its description or any other setting edited, and a setting sent with the value
        // the set already has is not a change. A setting that is changed is judged on its own, with the
        // stored values of the others (see ChunkSettingRules.CheckChange).
        var touched = (body.ChunkSize is { } sentSize && sentSize != set.ChunkSize ? ChunkSettingFields.Size : 0)
                      | (body.ChunkOverlap is { } sentOverlap && sentOverlap != set.ChunkOverlap ? ChunkSettingFields.Overlap : 0)
                      | (body.BoundaryMode is { Length: > 0 } sentMode
                         && !string.Equals(sentMode, set.BoundaryMode, StringComparison.Ordinal) ? ChunkSettingFields.Mode : 0)
                      | (body.CustomBoundaryPattern is { } sentPattern
                         && !string.Equals(sentPattern, set.CustomBoundaryPattern, StringComparison.Ordinal) ? ChunkSettingFields.Pattern : 0);

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

        if (Validate(set, touched) is { } invalid) return invalid;

        await db.SaveChangesAsync(ct);

        // The fingerprint mixes every one of these in, so a plain refresh is enough:
        // every file in this set now looks changed, and nothing in any other does.
        //
        // Not cancellable: the change is saved, and sending the same values again reads as no change,
        // so a cancel before the job left a set whose files nothing re-chunks.
        JobSummary? queued = null;
        if (rechunk)
            queued = (await queue.EnqueueAsync(corpus.Id, JobKind.Refresh, set.Id, CancellationToken.None)).ToSummary();

        var activity = await IndexingActivity.ReadAsync(db, [corpus.Id], CancellationToken.None);
        return Results.Ok(new ChunkSetUpdated(set.ToSummary(0, 0, 0, 0, activity.Of(set)), queued));
    }

    public static void MapChunkSetEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/corpora/{nameOrId}/chunk-sets").WithTags("Chunk sets");

        g.MapGet("/", async (string nameOrId, RequestContext rc, ScopeResolver scopes,
            CatalogDbContext db, IOptions<DexiconOptions> opts, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;
            var principal = rc.RequirePrincipal();
            var scope = await scopes.ResolveReadableAsync(principal, [nameOrId], ct);

            var summary = await CorpusEndpoints.Summarise(db, scope.Targets[0].Corpus, opts.Value.Indexing, ct);
            return Results.Ok(summary.ChunkSets);
        }).Produces<IReadOnlyList<ChunkSetSummary>>();

        g.MapPost("/", CreateAsync).Produces<ChunkSetCreated>(StatusCodes.Status202Accepted);

        g.MapPatch("/{setName}", UpdateAsync).Produces<ChunkSetUpdated>();

        g.MapPost("/{setName}/promote", PromoteAsync).Produces<ChunkSetPromoted>();

        g.MapDelete("/{setName}", RemoveAsync).Produces(StatusCodes.Status204NoContent);
    }

    /// <summary>
    /// Removes a set. A method of its own, and the one the route is mapped to, so a test calls the handler that
    /// runs. A refusal that kept the set without its vectors queues the refresh of the set before it is answered.
    /// </summary>
    internal static async Task<IResult> RemoveAsync(string nameOrId, string setName, RequestContext rc,
        ScopeResolver scopes, CorpusConfiguration config, CancellationToken ct)
    {
        if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
        var corpus = await scopes.ResolveWritableAsync(rc.RequirePrincipal(), nameOrId, ct);

        var removed = await config.RemoveChunkSetAsync(corpus, setName, ct);
        if (removed.Refusal is not { } refused) return Results.NoContent();

        await config.FollowUpAsync(corpus, refused);
        return refused.ToResult();
    }

    /// <summary>
    /// Makes a set the default. A method of its own, and the one the route is mapped to, so a test calls the
    /// handler that runs.
    ///
    /// Under the attachment lock, with the sets read again under it: a removal of a set checks the default
    /// flag and deletes the row under that lock, and a promotion in between would have it delete the set that
    /// had just become the default. The sets are not read from the corpus the request resolved, which can be
    /// as old as the request.
    /// </summary>
    internal static async Task<IResult> PromoteAsync(string nameOrId, string setName, RequestContext rc,
        ScopeResolver scopes, CatalogDbContext db, CancellationToken ct)
    {
        if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
        var principal = rc.RequirePrincipal();
        var corpus = await scopes.ResolveWritableAsync(principal, nameOrId, ct);

        using (await DocumentService.HoldAttachmentsAsync(ct))
        {
            var sets = await db.ChunkSets.AsNoTracking().Where(s => s.CorpusId == corpus.Id).ToListAsync(ct);
            var set = sets.FirstOrDefault(s =>
                string.Equals(s.Name, setName, StringComparison.OrdinalIgnoreCase) || s.Id == setName);

            if (set is null) return NotFound(corpus, setName);

            // A pass that could not walk a source (an unusable .dexiconignore, a limit passed, a root that is gone)
            // leaves the set unavailable and writes no state for that source's files, so no row is pending. Counting
            // pending rows alone would promote a set that search would then find missing the whole source.
            if (set.State == CorpusState.Unavailable)
                return Results.Problem(
                    title: "Chunk set is unavailable",
                    detail: $"'{set.Name}' could not index every source, so promoting it would remove that source's files " +
                            "from search. See index_status for the reason, fix it, and let the pass finish.",
                    statusCode: 409);

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

            await db.ChunkSets.Where(s => s.CorpusId == corpus.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.IsDefault, s => s.Id == set.Id), ct);

            return Results.Ok(new ChunkSetPromoted(set.Name, corpus.Name));
        }
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
    /// Create and update both judge the set by <see cref="ChunkSettingRules"/>, which creating a corpus
    /// uses for its default set as well.
    /// </summary>
    /// <param name="touched">
    /// The settings a change is about (see <see cref="ChunkSettingRules.CheckChange"/>). A failure in the
    /// settings outside it belongs to the stored set and is let through. Null judges everything, as a new
    /// set is.
    /// </param>
    private static IResult? Validate(ChunkSet set, ChunkSettingFields? touched = null) =>
        (touched is { } changed
            ? ChunkSettingRules.CheckChange(set.ChunkSize, set.ChunkOverlap, set.BoundaryMode, set.CustomBoundaryPattern, changed)
            : ChunkSettingRules.Check(set.ChunkSize, set.ChunkOverlap, set.BoundaryMode, set.CustomBoundaryPattern))
        ?.ToResult();
}
