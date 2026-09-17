using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Mcp;
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
        }).Produces<IReadOnlyList<CorpusSummary>>();

        g.MapGet("/{nameOrId}", async (string nameOrId, RequestContext rc, ScopeResolver scopes,
            CatalogDbContext db, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;
            var tenant = rc.RequireTenant();
            var scope = await scopes.ResolveReadableAsync(tenant, [nameOrId], ct);
            return Results.Ok(await Summarise(db, scope.Corpora[0], tenant, ct));
        }).Produces<CorpusSummary>();

        g.MapPost("/", async (CreateCorpusRequest body, RequestContext rc, CatalogDbContext db,
            IVectorStore vectors, IEmbeddingService embedder, IOptions<DexiconOptions> opts,
            CorpusIndexer indexer, IndexJobQueue queue, CancellationToken ct) =>
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

            var provider = string.IsNullOrWhiteSpace(body.EmbeddingProvider)
                ? opts.Value.Embedding.Provider
                : body.EmbeddingProvider.Trim();

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
                // Refuse rather than guess. A corpus created with the wrong dimension
                // count is unusable and the failure surfaces much later, as bad results.
                return Results.Problem(
                    title: "Embedding model unavailable",
                    detail: $"Could not probe '{target}': {ex.Message}. The corpus was not created.",
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
                State = CorpusState.Ready,
                CreatedUtc = DateTime.UtcNow,
            };

            // Every corpus is born with one set. Nothing else has to special-case the
            // "no sets yet" state, and the settings a caller passed at creation have a
            // home that is honest about what they configure.
            corpus.ChunkSets.Add(new ChunkSet
            {
                Id = Ulid.NewUlid().ToString(),
                CorpusId = corpus.Id,
                Name = "default",
                EmbeddingProvider = provider,
                EmbeddingModel = model,
                EmbeddingDimensions = dims,
                CollectionName = vectors.CollectionNameFor(target, dims),
                ChunkSize = body.ChunkSize ?? indexing.ChunkSize,
                ChunkOverlap = body.ChunkOverlap ?? indexing.ChunkOverlap,
                BoundaryMode = body.BoundaryMode ?? indexing.BoundaryMode,
                IsDefault = true,
                State = CorpusState.Ready,
                CreatedUtc = DateTime.UtcNow,
            });

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
            await vectors.EnsureCollectionAsync(corpus.ChunkSets[0].CollectionName, dims, ct);

            // Naming a folder is asking for it to be indexed. Without this the corpus is
            // created EMPTY and reports itself ready, and the only sign is a file count of
            // zero that reads like "this folder had nothing in it" — the first thing a new
            // user does, silently doing nothing, until someone thinks to press Refresh.
            if (corpus.Sources.Count > 0)
                await queue.EnqueueAsync(corpus.Id, JobKind.Full, ct: ct);

            return Results.Created($"/api/corpora/{corpus.Id}", await Summarise(db, corpus, tenant, ct));
        }).Produces<CorpusSummary>();

        g.MapPatch("/{nameOrId}", async (string nameOrId, UpdateCorpusRequest body, RequestContext rc,
            ScopeResolver scopes, CatalogDbContext db, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var tenant = rc.RequireTenant();
            var corpus = await scopes.ResolveWritableAsync(tenant, nameOrId, ct);

            // Chunk settings are NOT here any more. They belong to a chunk set, because a
            // corpus can carry several and "the corpus's chunk size" stopped meaning
            // anything the moment that became true. See /api/corpora/{id}/chunk-sets.
            if (body.Description is not null) corpus.Description = body.Description;
            if (body.Visibility is not null)
                corpus.Visibility = string.Equals(body.Visibility, "shared", StringComparison.OrdinalIgnoreCase)
                    ? CorpusVisibility.Shared : CorpusVisibility.Private;

            if (body.GrantTenantIds is not null)
            {
                var existing = await db.CorpusGrants.Where(x => x.CorpusId == corpus.Id).ToListAsync(ct);
                db.CorpusGrants.RemoveRange(existing);
                foreach (var tid in body.GrantTenantIds.Distinct(StringComparer.OrdinalIgnoreCase))
                    db.CorpusGrants.Add(new CorpusGrant { CorpusId = corpus.Id, TenantId = tid });
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new CorpusUpdated(await Summarise(db, corpus, tenant, ct)));
        }).Produces<CorpusUpdated>();

        g.MapDelete("/{nameOrId}", async (string nameOrId, RequestContext rc, ScopeResolver scopes,
            CatalogDbContext db, IVectorStore vectors, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var corpus = await scopes.ResolveWritableAsync(rc.RequireTenant(), nameOrId, ct);

            // Per collection, because a corpus mid-migration has sets in two of them and
            // a single delete would leave one half behind with nothing left to name it.
            var collections = await db.ChunkSets.Where(s => s.CorpusId == corpus.Id)
                .Select(s => s.CollectionName).Distinct().ToListAsync(ct);

            foreach (var collection in collections)
                await vectors.DeleteCorpusAsync(collection, corpus.Id, ct);

            db.Corpora.Remove(corpus);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        g.MapPost("/{nameOrId}/sources", async (string nameOrId, AddSourceRequest body, RequestContext rc,
            ScopeResolver scopes, CatalogDbContext db, CorpusIndexer indexer, IOptions<DexiconOptions> opts,
            IndexJobQueue queue, CancellationToken ct) =>
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

            // Same reason as creation: adding a folder is asking for it to be read. A
            // Refresh rather than a Full, because the corpus's other sources are already
            // indexed and re-embedding them costs real money on a hosted provider.
            var job = await queue.EnqueueAsync(corpus.Id, JobKind.Refresh, ct: ct);

            return Results.Ok(new SourceAdded(source.ToSummary(), job.ToSummary()));
        }).Produces<SourceAdded>();

        // Adding a folder was one call; removing one was deleting the whole corpus and
        // building it again, losing its chunk sets, its history and every other source
        // with it. A path typed wrong is not a reason to lose all of that.
        g.MapDelete("/{nameOrId}/sources/{sourceId}", async (string nameOrId, string sourceId,
            RequestContext rc, ScopeResolver scopes, CatalogDbContext db, IVectorStore vectors,
            CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var corpus = await scopes.ResolveWritableAsync(rc.RequireTenant(), nameOrId, ct);

            var source = await db.Sources
                .FirstOrDefaultAsync(s => s.Id == sourceId && s.CorpusId == corpus.Id, ct);

            if (source is null)
                return Results.Problem(
                    title: "No such source",
                    detail: $"Corpus '{corpus.Name}' has no source '{sourceId}'.",
                    statusCode: 404);

            var paths = await db.Files.Where(f => f.SourceId == source.Id)
                .Select(f => f.RelativePath).ToListAsync(ct);

            // Vectors first, for the same reason RemoveAttachmentAsync does it: if the
            // catalogue row went first and this threw, the corpus would keep returning
            // hits for files it no longer lists.
            //
            // Once per set — a removed folder has to leave every chunking of the corpus,
            // not only the default one — and scoped to THIS source, because a file_path is
            // relative to a source root and another source may hold the same name.
            var sets = await db.ChunkSets.Where(s => s.CorpusId == corpus.Id).ToListAsync(ct);
            foreach (var set in sets)
                foreach (var path in paths)
                    await vectors.DeleteFileChunksAsync(set.CollectionName, set.Id, source.Id, path, ct);

            // The file rows and their per-set chunk states go with it: both cascade from
            // Source, so removing it is the whole of the catalogue side.
            db.Sources.Remove(source);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        });

        g.MapPost("/{nameOrId}/reindex", async (string nameOrId, bool? full, RequestContext rc,
            ScopeResolver scopes, IndexJobQueue queue, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Ingest) is { } denied) return denied;
            var corpus = await scopes.ResolveWritableAsync(rc.RequireTenant(), nameOrId, ct);
            var job = await queue.EnqueueAsync(corpus.Id, full == true ? JobKind.Full : JobKind.Refresh, ct: ct);
            return Results.Accepted($"/api/jobs/{job.Id}", job.ToSummary());
        }).Produces<JobSummary>();

        g.MapGet("/{nameOrId}/files", async (string nameOrId, string? status, int? limit, int? offset,
            RequestContext rc, ScopeResolver scopes, CatalogDbContext db, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;
            // `books:fine` selects a set here too, exactly as it does in search.
            var scope = await scopes.ResolveReadableAsync(rc.RequireTenant(), [nameOrId], ct);
            var target = scope.Targets[0];
            var corpus = target.Corpus;

            var sourceIds = await db.Sources.Where(s => s.CorpusId == corpus.Id).Select(s => s.Id).ToListAsync(ct);

            // Left join: a file attached before this set existed has no state row yet, and
            // it is Pending rather than missing. Dropping it would hide exactly the files
            // a new set still has to do.
            var q = from f in db.Files.Where(f => sourceIds.Contains(f.SourceId))
                    join s in db.FileChunkStates.Where(s => s.ChunkSetId == target.Set.Id)
                        on f.Id equals s.FileId into gj
                    from s in gj.DefaultIfEmpty()
                    select new { File = f, State = s };

            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<FileStatus>(status, true, out var parsed))
                q = parsed == FileStatus.Pending
                    ? q.Where(x => x.State == null || x.State.Status == parsed)
                    : q.Where(x => x.State != null && x.State.Status == parsed);

            var total = await q.CountAsync(ct);
            var rows = await q.OrderBy(x => x.File.RelativePath)
                .Skip(offset ?? 0).Take(Math.Clamp(limit ?? 100, 1, 1000))
                .ToListAsync(ct);

            return Results.Ok(new FileListResponse(
                total, target.Set.Name, [.. rows.Select(x => x.File.ToSummary(x.State))]));
        }).Produces<FileListResponse>();

        // ── One file, put back together ──────────────────────────────────────
        // A path rather than a file id, and a query parameter rather than a route segment:
        // the handle a caller already has is the path, because that is what search returns,
        // and a relative path contains slashes.
        g.MapGet("/{nameOrId}/file", async (string nameOrId, string path, RequestContext rc,
            ScopeResolver scopes, IVectorStore vectors, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;
            if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest(new { error = "path is required" });

            var scope = await scopes.ResolveReadableAsync(rc.RequireTenant(), [nameOrId], ct);
            var target = scope.Targets[0];

            // By FILTER, not by search. An early version of the MCP resource used keyword
            // search for the path, which let relevance decide which of a file's chunks came
            // back — a reader asking for a file got a plausible one with holes in it.
            var chunks = await vectors.GetFileChunksAsync(
                target.Set.CollectionName, target.Set.Id, path, ct);

            if (chunks.Count == 0)
            {
                return Results.NotFound(new
                {
                    title = "Not indexed",
                    detail = $"No indexed file '{path}' in '{target.Corpus.Name}:{target.Set.Name}'. " +
                             "Paths are exactly as search reports them.",
                });
            }

            var pieces = chunks.Select(c => (c.StartLine, c.EndLine, c.Content)).ToList();
            var text = DexiconTools.Stitch(pieces, lineNumbers: false);

            // The marker Stitch writes where the index is missing lines. Counted here so a
            // caller can say "3 gaps" without reading the text for it.
            var gaps = text.Split("… lines").Length - 1;

            const int Limit = 400_000;
            var truncated = text.Length > Limit;
            if (truncated) text = text[..Limit] + "\n…(truncated)";

            return Results.Ok(new IndexedFileText(
                target.Corpus.Name,
                target.Set.Name,
                path,
                chunks.Min(c => c.StartLine),
                chunks.Max(c => c.EndLine),
                gaps,
                truncated,
                text));
        }).Produces<IndexedFileText>();
    }

    internal static async Task<CorpusSummary> Summarise(CatalogDbContext db, Corpus c, string viewerTenant,
        CancellationToken ct)
    {
        var sources = await db.Sources.Where(s => s.CorpusId == c.Id).ToListAsync(ct);

        // Files per source, so a folder that brought in nothing is visible as such.
        var filesPerSource = (await db.Files
                .Where(f => f.Source!.CorpusId == c.Id)
                .GroupBy(f => f.SourceId)
                .Select(grp => new { SourceId = grp.Key, Count = grp.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.SourceId, x => x.Count, StringComparer.Ordinal);
        var sets = await db.ChunkSets.Where(s => s.CorpusId == c.Id)
            .OrderByDescending(s => s.IsDefault).ThenBy(s => s.Name).ToListAsync(ct);

        var setIds = sets.Select(s => s.Id).ToList();

        // Counted per set: the same file is one attachment but several chunkings, and a
        // corpus total that summed them would double-count every document.
        var perSet = await db.FileChunkStates
            .Where(fs => setIds.Contains(fs.ChunkSetId))
            .GroupBy(fs => new { fs.ChunkSetId, fs.Status })
            .Select(grp => new
            {
                grp.Key.ChunkSetId,
                grp.Key.Status,
                Count = grp.Count(),
                Chunks = grp.Sum(x => x.ChunkCount),
            })
            .ToListAsync(ct);

        var setSummaries = sets.Select(s =>
        {
            var rows = perSet.Where(r => r.ChunkSetId == s.Id).ToList();
            return s.ToSummary(
                fileCount: rows.Where(r => r.Status == FileStatus.Indexed).Sum(r => r.Count),
                chunkCount: rows.Sum(r => r.Chunks),
                pendingCount: rows.Where(r => r.Status == FileStatus.Pending).Sum(r => r.Count),
                failedCount: rows.Where(r => r.Status == FileStatus.Failed).Sum(r => r.Count));
        }).ToList();

        // The corpus-level figures describe the DEFAULT set, because that is what a search
        // with an unqualified name actually reaches. Summing every set would report a
        // number no query can return.
        var headline = setSummaries.FirstOrDefault(s => s.IsDefault) ?? setSummaries.FirstOrDefault();
        var defaultRows = headline is null ? [] : perSet.Where(r => r.ChunkSetId == headline.Id).ToList();

        return new CorpusSummary(
            c.Id, c.Name, c.Description, c.TenantId,
            Owned: string.Equals(c.TenantId, viewerTenant, StringComparison.OrdinalIgnoreCase),
            c.Visibility.ToString().ToLowerInvariant(),
            c.State.ToString().ToLowerInvariant(),
            c.CreatedUtc, c.LastIndexedUtc,
            sources.Count,
            headline?.FileCount ?? 0,
            headline?.ChunkCount ?? 0,
            defaultRows.Where(r => r.Status is FileStatus.Skipped or FileStatus.Empty).Sum(r => r.Count),
            headline?.FailedCount ?? 0,
            sources.Select(s => s.ToSummary(filesPerSource.GetValueOrDefault(s.Id))).ToList(),
            setSummaries);
    }
}
