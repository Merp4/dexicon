using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Documents;
using Dexicon.Core.Embedding;
using Dexicon.Core.Indexing;
using Dexicon.Core.Search;
using Dexicon.Core.Vectors;
using Dexicon.Infrastructure;
using Dexicon.Mcp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dexicon.Api;

public static class CorpusEndpoints
{
    public static void MapCorpusEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/corpora").WithTags("Corpora");

        g.MapGet("/", async (RequestContext rc, ScopeResolver scopes, CatalogDbContext db,
            IOptions<DexiconOptions> opts, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;
            var principal = rc.RequirePrincipal();
            var visible = await scopes.VisibleAsync(principal, ct);
            var summaries = new List<CorpusSummary>(visible.Count);
            foreach (var c in visible) summaries.Add(await Summarise(db, c, opts.Value.Indexing, ct));
            return Results.Ok(summaries);
        }).Produces<IReadOnlyList<CorpusSummary>>().WithGroupName(OpenApiDocuments.Integration);

        g.MapGet("/{nameOrId}", async (string nameOrId, RequestContext rc, ScopeResolver scopes,
            CatalogDbContext db, IOptions<DexiconOptions> opts, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;
            var principal = rc.RequirePrincipal();
            var scope = await scopes.ResolveReadableAsync(principal, [nameOrId], ct);
            return Results.Ok(await Summarise(db, scope.Corpora[0], opts.Value.Indexing, ct));
        }).Produces<CorpusSummary>().WithGroupName(OpenApiDocuments.Integration);

        g.MapPost("/", async (CreateCorpusRequest body, RequestContext rc, CatalogDbContext db,
            IVectorStore vectors, IEmbeddingService embedder, IOptions<DexiconOptions> opts,
            CorpusIndexer indexer, IndexJobQueue queue, SweepQueue sweeps, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var principal = rc.RequirePrincipal();

            if (string.IsNullOrWhiteSpace(body.Name))
                return Results.Problem(title: "Name is required", statusCode: 400);

            if (await db.Corpora.AnyAsync(c => c.Name == body.Name, ct))
                return Results.Problem(
                    title: "Corpus already exists",
                    detail: $"A corpus named '{body.Name}' already exists. Names are unique " +
                            "because the name is what an agent passes to search_index.",
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
                Name = body.Name.Trim(),
                Description = body.Description,
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
            // zero that reads like "this folder had nothing in it": the first thing a new
            // user does appears to do nothing until someone thinks to press Refresh.
            if (corpus.Sources.Count > 0)
            {
                await queue.EnqueueAsync(corpus.Id, JobKind.Full, ct: ct);

                // And a sweep, which is the case D-32 was written for: a corpus created
                // while another is indexing would otherwise report nothing at all until
                // the job above reached the front of the queue.
                sweeps.Enqueue(corpus.Id);
            }

            return Results.Created($"/api/corpora/{corpus.Id}", await Summarise(db, corpus, opts.Value.Indexing, ct));
        }).Produces<CorpusSummary>();

        g.MapPatch("/{nameOrId}", async (string nameOrId, UpdateCorpusRequest body, RequestContext rc,
            ScopeResolver scopes, CatalogDbContext db, IOptions<DexiconOptions> opts,
            IndexJobQueue queue, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var principal = rc.RequirePrincipal();
            var corpus = await scopes.ResolveWritableAsync(principal, nameOrId, ct);

            // Chunk settings are NOT here any more. They belong to a chunk set, because a
            // corpus can carry several and "the corpus's chunk size" stopped meaning
            // anything the moment that became true. See /api/corpora/{id}/chunk-sets.
            if (body.Description is not null) corpus.Description = body.Description;

            // Changing what sources inherit changes which files are in the index, so it
            // queues a refresh the way adding a source does. Narrowing a glob removes the
            // files it now excludes through the walk's own reconcile: they are simply not
            // seen, which is the path a deleted file already takes.
            var filtersChanged = false;
            if (body.Defaults is { } d)
            {
                filtersChanged =
                    corpus.DefaultUseGitignore != d.UseGitignore ||
                    corpus.DefaultMaxFileBytes != d.MaxFileBytes ||
                    corpus.DefaultIncludeGlobs != SourceFilters.Store(d.IncludeGlobs) ||
                    corpus.DefaultExcludeGlobs != SourceFilters.Store(d.ExcludeGlobs);

                corpus.DefaultUseGitignore = d.UseGitignore;
                corpus.DefaultMaxFileBytes = d.MaxFileBytes;
                corpus.DefaultIncludeGlobs = SourceFilters.Store(d.IncludeGlobs);
                corpus.DefaultExcludeGlobs = SourceFilters.Store(d.ExcludeGlobs);
            }

            await db.SaveChangesAsync(ct);

            if (filtersChanged) await queue.EnqueueAsync(corpus.Id, JobKind.Refresh, ct: ct);

            return Results.Ok(new CorpusUpdated(await Summarise(db, corpus, opts.Value.Indexing, ct)));
        }).Produces<CorpusUpdated>();

        g.MapDelete("/{nameOrId}", async (string nameOrId, RequestContext rc, ScopeResolver scopes,
            CatalogDbContext db, IVectorStore vectors, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var corpus = await scopes.ResolveWritableAsync(rc.RequirePrincipal(), nameOrId, ct);

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
            IndexJobQueue queue, SweepQueue sweeps, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var corpus = await scopes.ResolveWritableAsync(rc.RequirePrincipal(), nameOrId, ct);

            try { indexer.ResolveWorkspacePath(body.WorkspacePath); }
            catch (UnauthorizedAccessException ex)
            { return Results.Problem(title: "Invalid workspace path", detail: ex.Message, statusCode: 400); }

            var source = new Source
            {
                Id = Ulid.NewUlid().ToString(),
                CorpusId = corpus.Id,
                Kind = SourceKind.Workspace,
                RootPath = body.WorkspacePath.Trim('/', '\\'),
                // Null, not a default. An omitted field means this source has no opinion
                // and follows the corpus, which is the point of the corpus having one.
                UseGitignore = body.UseGitignore,
                MaxFileBytes = body.MaxFileBytes,
                IncludeGlobs = SourceFilters.Store(body.IncludeGlobs),
                ExcludeGlobs = SourceFilters.Store(body.ExcludeGlobs),
                CreatedUtc = DateTime.UtcNow,
            };

            db.Sources.Add(source);
            await db.SaveChangesAsync(ct);

            // Same reason as creation: adding a folder is asking for it to be read. A
            // Refresh rather than a Full, because the corpus's other sources are already
            // indexed and re-embedding them costs real money on a hosted provider.
            var job = await queue.EnqueueAsync(corpus.Id, JobKind.Refresh, ct: ct);

            // And a sweep, on its own lane. The job above answers "what is in this folder"
            // as well, but only once it reaches the front of a queue that may be hours
            // deep; the sweep answers it in seconds. Adding a folder and being told the
            // corpus holds nothing is the case D-32 exists for.
            sweeps.Enqueue(corpus.Id);

            return Results.Ok(new SourceAdded(
                source.ToSummary(corpus, opts.Value.Indexing), job.ToSummary()));
        }).Produces<SourceAdded>();

        // Adding a folder was one call; removing one was deleting the whole corpus and
        // building it again, losing its chunk sets, its history and every other source
        // with it. A path typed wrong is not a reason to lose all of that.
        g.MapDelete("/{nameOrId}/sources/{sourceId}", async (string nameOrId, string sourceId,
            RequestContext rc, ScopeResolver scopes, CatalogDbContext db, IVectorStore vectors,
            CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var corpus = await scopes.ResolveWritableAsync(rc.RequirePrincipal(), nameOrId, ct);

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
            // Once per set, because a removed folder has to leave every chunking of the
            // corpus rather than only the default, and scoped to this source, because a file_path is
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

        // Filters were write-once: set when the folder was added and then unreachable, so
        // changing one meant deleting the source, which drops its files from every chunk
        // set, and re-embedding the folder from scratch. Nobody iterates on a glob at that
        // price.
        g.MapPatch("/{nameOrId}/sources/{sourceId}", async (string nameOrId, string sourceId,
            UpdateSourceRequest body, RequestContext rc, ScopeResolver scopes, CatalogDbContext db,
            IOptions<DexiconOptions> opts, IndexJobQueue queue, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var corpus = await scopes.ResolveWritableAsync(rc.RequirePrincipal(), nameOrId, ct);

            var source = await db.Sources
                .FirstOrDefaultAsync(s => s.Id == sourceId && s.CorpusId == corpus.Id, ct);

            if (source is null)
                return Results.Problem(
                    title: "No such source",
                    detail: $"Corpus '{corpus.Name}' has no source '{sourceId}'.",
                    statusCode: 404);

            if (source.Kind != SourceKind.Workspace)
                return Results.Problem(
                    title: "Not a workspace source",
                    detail: "Filters apply to a folder being walked. An upload source has no tree to filter.",
                    statusCode: 400);

            if (body.MaxFileBytes is { } m && m <= 0)
                return Results.Problem(
                    title: "Invalid size cap",
                    detail: "maxFileBytes must be greater than zero. Name it in `clear` to inherit the corpus default.",
                    statusCode: 400);

            var changed = ApplyFilters(source, body);

            await db.SaveChangesAsync(ct);

            // Only when something moved. A form submitted unchanged should not re-walk a
            // library, and a refresh on every save is how that happens.
            var job = changed ? await queue.EnqueueAsync(corpus.Id, JobKind.Refresh, ct: ct) : null;

            return Results.Ok(new SourceUpdated(
                source.ToSummary(corpus, opts.Value.Indexing), job?.ToSummary()));
        }).Produces<SourceUpdated>();

        // Its own endpoint rather than a field on the summary: answering it reads the
        // filesystem, and the summary is drawn on every navigation. Read rather than
        // stored, so it reflects the disk now and not the last index run.
        g.MapGet("/{nameOrId}/coverage", async (string nameOrId, RequestContext rc, ScopeResolver scopes,
            CatalogDbContext db, IOptions<DexiconOptions> opts, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;
            var principal = rc.RequirePrincipal();
            var scope = await scopes.ResolveReadableAsync(principal, [nameOrId], ct);
            var corpus = scope.Corpora[0];

            return Results.Ok(await CoverageAsync(db, opts.Value.Indexing, corpus, ct));
        }).Produces<CoverageReport>();

        // Discovery on demand. Deliberately not a job: it takes the corpus lease, runs on
        // the discovery lane and answers in seconds, so there is nothing to poll.
        g.MapPost("/{nameOrId}/sweep", async (string nameOrId, RequestContext rc,
            ScopeResolver scopes, SweepQueue sweeps, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Ingest) is { } denied) return denied;
            var corpus = await scopes.ResolveWritableAsync(rc.RequirePrincipal(), nameOrId, ct);

            // False means one is already waiting, which is the same answer arriving from
            // the sweep already queued rather than a refusal.
            var queued = sweeps.Enqueue(corpus.Id);
            return Results.Accepted($"/api/corpora/{corpus.Name}", new SweepQueued(corpus.Name, queued));
        }).Produces<SweepQueued>(StatusCodes.Status202Accepted);

        g.MapPost("/{nameOrId}/reindex", async (string nameOrId, bool? full, RequestContext rc,
            ScopeResolver scopes, IndexJobQueue queue, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Ingest) is { } denied) return denied;
            var corpus = await scopes.ResolveWritableAsync(rc.RequirePrincipal(), nameOrId, ct);
            var job = await queue.EnqueueAsync(corpus.Id, full == true ? JobKind.Full : JobKind.Refresh, ct: ct);
            return Results.Accepted($"/api/jobs/{job.Id}", job.ToSummary());
        }).Produces<JobSummary>().WithGroupName(OpenApiDocuments.Integration);

        g.MapGet("/{nameOrId}/files", async (string nameOrId, string? status, int? limit, int? offset,
            RequestContext rc, ScopeResolver scopes, CatalogDbContext db, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;
            // `books:fine` selects a set here too, exactly as it does in search.
            var scope = await scopes.ResolveReadableAsync(rc.RequirePrincipal(), [nameOrId], ct);
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
        g.MapGet("/{nameOrId}/file", async (string nameOrId, string path, int? start, RequestContext rc,
            ScopeResolver scopes, IVectorStore vectors, DocumentReader documents, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;
            if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest(new { error = "path is required" });

            var scope = await scopes.ResolveReadableAsync(rc.RequirePrincipal(), [nameOrId], ct);
            var target = scope.Targets[0];

            // By FILTER, not by search. An early version of the MCP resource used keyword
            // search for the path, which let relevance decide which of a file's chunks came
            // back, so a reader asking for a file got a plausible one with holes in it.
            var chunks = await vectors.GetFileChunksAsync(
                target.Set.CollectionName, target.Set.Id, path, ct);

            if (chunks.Count == 0)
            {
                // A file with no chunks is not necessarily a file that is not there. A
                // scanned PDF is indexed, known and empty, and telling the reader their
                // path was wrong sends them to check a path that is right.
                var state = await documents.StatusAtAsync(target.Corpus.Id, target.Set.Id, path, ct);

                return Results.NotFound(state is { Status: FileStatus.Empty } empty
                    ? new
                    {
                        title = "No text",
                        detail = $"'{path}' is indexed in '{target.Corpus.Name}:{target.Set.Name}' " +
                                 $"and has no text: {empty.Detail ?? "no extractable text content"}.",
                    }
                    : new
                    {
                        title = "Not indexed",
                        detail = $"No indexed file '{path}' in '{target.Corpus.Name}:{target.Set.Name}'. " +
                                 "Paths are exactly as search reports them.",
                    });
            }

            // The document itself where it is stored, and the chunks put back together
            // where it is not.
            //
            // Stitching was the only option while a chunk payload was the only copy of a
            // workspace file's text: it reassembles the file from the pieces the index
            // happens to hold and marks the lines it cannot account for. Reading the
            // extracted text instead returns the document as extracted, which cannot have
            // holes and does not depend on which chunk set is being looked at.
            //
            // Both are resolved before either is used, because the fallback needs the
            // chunks anyway: they carry the file's first line number.
            var sources = chunks.Select(c => c.SourceId).Distinct(StringComparer.Ordinal).ToList();
            var document = sources.Count == 1
                ? await documents.ForAsync(target.Corpus.Id, target.Set.Id, path, sources[0], ct)
                : null;

            var pieces = chunks.Select(c => (c.StartLine, c.EndLine, c.Content)).ToList();
            var stitched = Passage.Stitch(pieces, lineNumbers: false);

            var text = document?.Text ?? stitched;
            var store = document?.Store ?? "chunks";

            // The marker Stitch writes where the index is missing lines. Counted here so a
            // caller can say "3 gaps" without reading the text for it. A document read
            // whole has none by construction.
            var gaps = document is null ? stitched.Split("… lines").Length - 1 : 0;

            // A WINDOW of the file, not the head of it.
            //
            // This used to return the first 400,000 characters with "…(truncated)" glued on
            // the end and no way to ask for the rest. That is fine for a source file and
            // useless for a book: a 700-page technical book runs to two or three million
            // characters, so the viewer showed the first chapter or two and called the rest
            // of the book an implementation detail. Worse, it reported the line range of the
            // WHOLE file while showing a fraction of it, so the header said 1–40,521 over
            // about five thousand lines of text.
            const int WindowChars = 400_000;
            var total = text.Length;
            var offset = Math.Clamp(start ?? 0, 0, Math.Max(total - 1, 0));
            var window = text.Substring(offset, Math.Min(WindowChars, total - offset));
            var more = offset + window.Length < total;

            // Line numbers for THIS window. Counting newlines before it is exact and cheap;
            // reporting the file's own first line here is what made the header lie.
            //
            // A document starts at line 1 whatever the index holds. The stitch starts at
            // the first line any chunk covers, which is not the same number when the
            // opening of the file was never chunked.
            var firstLine = document is not null ? 1 : chunks.Min(c => c.StartLine);
            var windowStart = firstLine + text.AsSpan(0, offset).Count('\n');
            var windowEnd = windowStart + window.AsSpan().Count('\n');

            return Results.Ok(new IndexedFileText(
                target.Corpus.Name,
                target.Set.Name,
                path,
                windowStart,
                windowEnd,
                gaps,
                more,
                window,
                offset,
                total,
                more ? offset + window.Length : null,
                store));
        }).Produces<IndexedFileText>();
    }

    /// <summary>
    /// Apply a filter update to a source, returning whether anything actually moved.
    ///
    /// Three cases per field and only two of them are obvious. An omitted field leaves the
    /// value alone. A field named in <see cref="UpdateSourceRequest.Clear"/> returns it to
    /// the corpus default. A field with a value sets it.
    ///
    /// Clearing is said out loud rather than inferred from a null, because JSON gives no
    /// way to tell an absent property from an explicit null once it is bound to a nullable:
    /// inferring it would make every partial update an accidental reset of everything it
    /// did not mention.
    /// </summary>
    internal static bool ApplyFilters(Source source, UpdateSourceRequest body)
    {
        var clear = new HashSet<string>(body.Clear ?? [], StringComparer.OrdinalIgnoreCase);
        var before = (source.UseGitignore, source.MaxFileBytes, source.IncludeGlobs, source.ExcludeGlobs);

        if (clear.Contains("useGitignore")) source.UseGitignore = null;
        else if (body.UseGitignore is { } g) source.UseGitignore = g;

        if (clear.Contains("maxFileBytes")) source.MaxFileBytes = null;
        else if (body.MaxFileBytes is { } m) source.MaxFileBytes = m;

        if (clear.Contains("includeGlobs")) source.IncludeGlobs = null;
        else if (body.IncludeGlobs is not null) source.IncludeGlobs = SourceFilters.Store(body.IncludeGlobs);

        if (clear.Contains("excludeGlobs")) source.ExcludeGlobs = null;
        else if (body.ExcludeGlobs is not null) source.ExcludeGlobs = SourceFilters.Store(body.ExcludeGlobs);

        return before != (source.UseGitignore, source.MaxFileBytes, source.IncludeGlobs, source.ExcludeGlobs);
    }

    /// <summary>
    /// Directories that lead to this corpus's sources but which no source covers.
    ///
    /// Scoped to one corpus, which is the whole of what this adds over
    /// <see cref="SourceCoverage.Find"/>: reading every source in the database instead
    /// would report one corpus's gaps on another's page, and the answer would still look
    /// entirely plausible.
    /// </summary>
    internal static async Task<CoverageReport> CoverageAsync(
        CatalogDbContext db, IndexingOptions indexing, Corpus corpus, CancellationToken ct)
    {
        var sources = await db.Sources.Where(s => s.CorpusId == corpus.Id).ToListAsync(ct);

        var gaps = SourceCoverage.Find(
            indexing.WorkspaceRoot,
            sources.Select(s => new SourceCoverage.SourceRoot(
                s.RootPath, SourceFilters.Resolve(corpus, s, indexing).MaxFileBytes)),
            indexing.DocumentMaxBytes);

        return new CoverageReport(
            gaps.Select(g => new CoverageGap(g.DirectoryRelativePath, g.Files)).ToList());
    }

    internal static async Task<CorpusSummary> Summarise(CatalogDbContext db, Corpus c,
        IndexingOptions indexing, CancellationToken ct)
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
            c.Id, c.Name, c.Description,
            c.State.ToString().ToLowerInvariant(),
            c.CreatedUtc, c.LastIndexedUtc,
            sources.Count,
            headline?.FileCount ?? 0,
            headline?.ChunkCount ?? 0,
            defaultRows.Where(r => r.Status is FileStatus.Skipped or FileStatus.Empty).Sum(r => r.Count),
            headline?.FailedCount ?? 0,
            headline?.PendingCount ?? 0,
            sources.Select(s => s.ToSummary(c, indexing, filesPerSource.GetValueOrDefault(s.Id))).ToList(),
            setSummaries,
            c.DefaultsOf());
    }
}
