using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Embedding;
using Dexicon.Core.Indexing;
using Dexicon.Core.Vectors;
using Dexicon.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dexicon.Api;

public static class CorpusEndpoints
{
    public static void MapCorpusEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/corpora").WithTags("Corpora");

        g.MapGet("/", async (RequestContext rc, ScopeResolver scopes, CatalogDbContext db, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;
            var tenant = rc.RequireTenant();
            var visible = await scopes.VisibleAsync(tenant, ct);
            var summaries = new List<CorpusSummary>(visible.Count);
            foreach (var c in visible) summaries.Add(await Summarise(db, c, tenant, ct));
            return Results.Ok(summaries);
        });

        g.MapGet("/{nameOrId}", async (string nameOrId, RequestContext rc, ScopeResolver scopes,
            CatalogDbContext db, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;
            var tenant = rc.RequireTenant();
            var scope = await scopes.ResolveReadableAsync(tenant, [nameOrId], ct);
            return Results.Ok(await Summarise(db, scope.Corpora[0], tenant, ct));
        });

        g.MapPost("/", async (CreateCorpusRequest body, RequestContext rc, CatalogDbContext db,
            IVectorStore vectors, IEmbeddingProvider embedder, IOptions<DexiconOptions> opts,
            CorpusIndexer indexer, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var tenant = rc.RequireTenant();

            if (string.IsNullOrWhiteSpace(body.Name))
                return Results.Problem(title: "Name is required", statusCode: 400);

            if (await db.Corpora.AnyAsync(c => c.TenantId == tenant && c.Name == body.Name, ct))
                return Results.Problem(
                    title: "Corpus already exists",
                    detail: $"Tenant '{tenant}' already has a corpus named '{body.Name}'.",
                    statusCode: 409);

            var model = string.IsNullOrWhiteSpace(body.EmbeddingModel)
                ? opts.Value.Embedding.Model
                : body.EmbeddingModel.Trim();

            int dims;
            try
            {
                dims = await embedder.ProbeDimensionsAsync(model, ct);
            }
            catch (EmbeddingUnavailableException ex)
            {
                // Refuse rather than guess. A corpus created with the wrong dimension
                // count is unusable and the failure surfaces much later, as bad results.
                return Results.Problem(
                    title: "Embedding model unavailable",
                    detail: $"Could not probe '{model}': {ex.Message}. The corpus was not created.",
                    statusCode: 503);
            }

            var indexing = opts.Value.Indexing;
            var corpus = new Corpus
            {
                Id = Ulid.NewUlid().ToString(),
                TenantId = tenant,
                Name = body.Name.Trim(),
                Description = body.Description,
                Visibility = string.Equals(body.Visibility, "shared", StringComparison.OrdinalIgnoreCase)
                    ? CorpusVisibility.Shared : CorpusVisibility.Private,
                EmbeddingModel = model,
                EmbeddingDimensions = dims,
                CollectionName = vectors.CollectionNameFor(model, dims),
                ChunkSize = body.ChunkSize ?? indexing.ChunkSize,
                ChunkOverlap = body.ChunkOverlap ?? indexing.ChunkOverlap,
                BoundaryMode = body.BoundaryMode ?? indexing.BoundaryMode,
                State = CorpusState.Ready,
                CreatedUtc = DateTime.UtcNow,
            };

            if (!string.IsNullOrWhiteSpace(body.WorkspacePath))
            {
                try { indexer.ResolveWorkspacePath(body.WorkspacePath); }
                catch (UnauthorizedAccessException ex)
                { return Results.Problem(title: "Invalid workspace path", detail: ex.Message, statusCode: 400); }

                corpus.Sources.Add(new Source
                {
                    Id = Ulid.NewUlid().ToString(),
                    CorpusId = corpus.Id,
                    Kind = SourceKind.Workspace,
                    RootPath = body.WorkspacePath.Trim('/', '\\'),
                    UseGitignore = true,
                    MaxFileBytes = indexing.MaxFileBytes,
                    CreatedUtc = DateTime.UtcNow,
                });
            }

            db.Corpora.Add(corpus);
            await db.SaveChangesAsync(ct);
            await vectors.EnsureCollectionAsync(corpus.CollectionName, dims, ct);

            return Results.Created($"/api/corpora/{corpus.Id}", await Summarise(db, corpus, tenant, ct));
        });

        g.MapPatch("/{nameOrId}", async (string nameOrId, UpdateCorpusRequest body, RequestContext rc,
            ScopeResolver scopes, CatalogDbContext db, IndexJobQueue queue, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var tenant = rc.RequireTenant();
            var corpus = await scopes.ResolveWritableAsync(tenant, nameOrId, ct);

            // Chunk settings are the interesting edit: they change how every document in
            // this corpus is sliced, so they invalidate the whole index and are applied
            // by re-chunking rather than by hoping someone remembers to reindex.
            var rechunk = false;

            if (body.ChunkSize is { } size)
            {
                if (size is < 64 or > 8192)
                    return Results.Problem(title: "chunkSize must be between 64 and 8192 tokens", statusCode: 400);
                rechunk |= size != corpus.ChunkSize;
                corpus.ChunkSize = size;
            }

            if (body.ChunkOverlap is { } overlap)
            {
                if (overlap < 0) return Results.Problem(title: "chunkOverlap cannot be negative", statusCode: 400);
                rechunk |= overlap != corpus.ChunkOverlap;
                corpus.ChunkOverlap = overlap;
            }

            if (corpus.ChunkOverlap >= corpus.ChunkSize)
                return Results.Problem(
                    title: "chunkOverlap must be smaller than chunkSize",
                    detail: $"Asked for overlap {corpus.ChunkOverlap} with size {corpus.ChunkSize}.",
                    statusCode: 400);

            if (body.BoundaryMode is { Length: > 0 } mode)
            {
                if (mode is not ("none" or "blank-line" or "language-aware"))
                    return Results.Problem(
                        title: "Unknown boundary mode",
                        detail: $"'{mode}'. Expected none, blank-line or language-aware.",
                        statusCode: 400);
                rechunk |= !string.Equals(mode, corpus.BoundaryMode, StringComparison.Ordinal);
                corpus.BoundaryMode = mode;
            }

            if (body.Description is not null) corpus.Description = body.Description;
            if (body.Visibility is not null)
                corpus.Visibility = string.Equals(body.Visibility, "shared", StringComparison.OrdinalIgnoreCase)
                    ? CorpusVisibility.Shared : CorpusVisibility.Private;

            if (body.GrantTenantIds is not null)
            {
                var existing = await db.CorpusGrants.Where(x => x.CorpusId == corpus.Id).ToListAsync(ct);
                db.CorpusGrants.RemoveRange(existing);
                foreach (var t in body.GrantTenantIds.Distinct(StringComparer.OrdinalIgnoreCase))
                    db.CorpusGrants.Add(new CorpusGrant { CorpusId = corpus.Id, TenantId = t });
            }

            await db.SaveChangesAsync(ct);

            // Queued, not done inline: re-embedding a large corpus outlasts any request.
            // The staleness fingerprint mixes the chunk settings in, so a plain refresh
            // is enough — every file now looks changed, and nothing else does.
            JobSummary? queued = null;
            if (rechunk) queued = (await queue.EnqueueAsync(corpus.Id, JobKind.Refresh, ct)).ToSummary();

            return Results.Ok(new { corpus = await Summarise(db, corpus, tenant, ct), rechunkJob = queued });
        });

        g.MapDelete("/{nameOrId}", async (string nameOrId, RequestContext rc, ScopeResolver scopes,
            CatalogDbContext db, IVectorStore vectors, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var corpus = await scopes.ResolveWritableAsync(rc.RequireTenant(), nameOrId, ct);

            await vectors.DeleteCorpusAsync(corpus.CollectionName, corpus.Id, ct);
            db.Corpora.Remove(corpus);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        g.MapPost("/{nameOrId}/sources", async (string nameOrId, AddSourceRequest body, RequestContext rc,
            ScopeResolver scopes, CatalogDbContext db, CorpusIndexer indexer, IOptions<DexiconOptions> opts,
            CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var corpus = await scopes.ResolveWritableAsync(rc.RequireTenant(), nameOrId, ct);

            try { indexer.ResolveWorkspacePath(body.WorkspacePath); }
            catch (UnauthorizedAccessException ex)
            { return Results.Problem(title: "Invalid workspace path", detail: ex.Message, statusCode: 400); }

            var source = new Source
            {
                Id = Ulid.NewUlid().ToString(),
                CorpusId = corpus.Id,
                Kind = SourceKind.Workspace,
                RootPath = body.WorkspacePath.Trim('/', '\\'),
                UseGitignore = body.UseGitignore ?? true,
                MaxFileBytes = body.MaxFileBytes ?? opts.Value.Indexing.MaxFileBytes,
                IncludeGlobs = body.IncludeGlobs is { Count: > 0 }
                    ? System.Text.Json.JsonSerializer.Serialize(body.IncludeGlobs) : null,
                ExcludeGlobs = body.ExcludeGlobs is { Count: > 0 }
                    ? System.Text.Json.JsonSerializer.Serialize(body.ExcludeGlobs) : null,
                CreatedUtc = DateTime.UtcNow,
            };

            db.Sources.Add(source);
            await db.SaveChangesAsync(ct);
            return Results.Ok(source.ToSummary());
        });

        g.MapPost("/{nameOrId}/reindex", async (string nameOrId, bool? full, RequestContext rc,
            ScopeResolver scopes, IndexJobQueue queue, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Ingest) is { } denied) return denied;
            var corpus = await scopes.ResolveWritableAsync(rc.RequireTenant(), nameOrId, ct);
            var job = await queue.EnqueueAsync(corpus.Id, full == true ? JobKind.Full : JobKind.Refresh, ct);
            return Results.Accepted($"/api/jobs/{job.Id}", job.ToSummary());
        });

        g.MapGet("/{nameOrId}/files", async (string nameOrId, string? status, int? limit, int? offset,
            RequestContext rc, ScopeResolver scopes, CatalogDbContext db, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;
            var scope = await scopes.ResolveReadableAsync(rc.RequireTenant(), [nameOrId], ct);
            var corpus = scope.Corpora[0];

            var sourceIds = await db.Sources.Where(s => s.CorpusId == corpus.Id).Select(s => s.Id).ToListAsync(ct);
            var q = db.Files.Where(f => sourceIds.Contains(f.SourceId));

            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<FileStatus>(status, true, out var parsed))
                q = q.Where(f => f.Status == parsed);

            var total = await q.CountAsync(ct);
            var files = await q.OrderBy(f => f.RelativePath)
                .Skip(offset ?? 0).Take(Math.Clamp(limit ?? 100, 1, 1000))
                .ToListAsync(ct);

            return Results.Ok(new { total, files = files.Select(f => f.ToSummary()) });
        });
    }

    internal static async Task<CorpusSummary> Summarise(CatalogDbContext db, Corpus c, string viewerTenant,
        CancellationToken ct)
    {
        var sources = await db.Sources.Where(s => s.CorpusId == c.Id).ToListAsync(ct);
        var sourceIds = sources.Select(s => s.Id).ToList();

        // These counts are READ BACK here and by index_status. A column nothing reads
        // is a feature that does not exist, so each one has a named reader.
        var files = await db.Files.Where(f => sourceIds.Contains(f.SourceId))
            .GroupBy(f => f.Status)
            .Select(grp => new { Status = grp.Key, Count = grp.Count(), Chunks = grp.Sum(x => x.ChunkCount) })
            .ToListAsync(ct);

        return new CorpusSummary(
            c.Id, c.Name, c.Description, c.TenantId,
            Owned: string.Equals(c.TenantId, viewerTenant, StringComparison.OrdinalIgnoreCase),
            c.Visibility.ToString().ToLowerInvariant(),
            c.State.ToString().ToLowerInvariant(),
            c.EmbeddingModel, c.EmbeddingDimensions, c.ChunkSize, c.ChunkOverlap, c.BoundaryMode,
            c.CreatedUtc, c.LastIndexedUtc,
            sources.Count,
            files.Where(f => f.Status == FileStatus.Indexed).Sum(f => f.Count),
            files.Sum(f => f.Chunks),
            files.Where(f => f.Status is FileStatus.Skipped or FileStatus.Empty).Sum(f => f.Count),
            files.Where(f => f.Status == FileStatus.Failed).Sum(f => f.Count),
            sources.Select(s => s.ToSummary()).ToList());
    }
}
