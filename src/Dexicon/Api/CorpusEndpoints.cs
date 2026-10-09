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
    /// <summary>
    /// The path of a corpus, or of one of its chunk sets, for a <c>Location</c> header. A name may hold
    /// a space, a slash or a letter outside ASCII, so each is escaped as the path segment it is; a header
    /// value outside ASCII makes Kestrel throw after the change it answers has been saved.
    /// </summary>
    internal static string LocationOf(string corpusName, string? chunkSetName = null) =>
        $"/api/corpora/{Uri.EscapeDataString(corpusName)}"
        + (chunkSetName is null ? string.Empty : $"/chunk-sets/{Uri.EscapeDataString(chunkSetName)}");

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

        g.MapPost("/", CreateAsync).Produces<CorpusSummary>(StatusCodes.Status201Created);

        g.MapPatch("/{nameOrId}", UpdateAsync).Produces<CorpusUpdated>();

        g.MapDelete("/{nameOrId}", async (string nameOrId, RequestContext rc, ScopeResolver scopes,
            CorpusConfiguration config, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var corpus = await scopes.ResolveWritableAsync(rc.RequirePrincipal(), nameOrId, ct);

            var removed = await config.RemoveCorpusAsync(corpus, ct);
            return removed.Refusal is { } refused ? refused.ToResult() : Results.NoContent();
        }).Produces(StatusCodes.Status204NoContent);

        g.MapPost("/{nameOrId}/sources", async (string nameOrId, AddSourceRequest body, RequestContext rc,
            ScopeResolver scopes, CorpusConfiguration config, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var corpus = await scopes.ResolveWritableAsync(rc.RequirePrincipal(), nameOrId, ct);

            var added = await config.AddSourceAsync(corpus, body, ct);
            return added.Refusal is { } refused ? refused.ToResult() : Results.Ok(added.Value);
        }).Produces<SourceAdded>();

        // Adding a folder was one call; removing one was deleting the whole corpus and
        // building it again, losing its chunk sets, its history and every other source
        // with it. A path typed wrong is not a reason to lose all of that.
        g.MapDelete("/{nameOrId}/sources/{sourceId}", async (string nameOrId, string sourceId,
            RequestContext rc, ScopeResolver scopes, CorpusConfiguration config, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var corpus = await scopes.ResolveWritableAsync(rc.RequirePrincipal(), nameOrId, ct);

            var removed = await config.RemoveSourceAsync(corpus, sourceId, ct);
            return removed.Refusal is { } refused ? refused.ToResult() : Results.NoContent();
        }).Produces(StatusCodes.Status204NoContent);

        // Filters were write-once: set when the folder was added and then unreachable, so
        // changing one meant deleting the source, which drops its files from every chunk
        // set, and re-embedding the folder from scratch. Nobody iterates on a glob at that
        // price.
        g.MapPatch("/{nameOrId}/sources/{sourceId}", async (string nameOrId, string sourceId,
            UpdateSourceRequest body, RequestContext rc, ScopeResolver scopes, CorpusConfiguration config,
            CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var corpus = await scopes.ResolveWritableAsync(rc.RequirePrincipal(), nameOrId, ct);

            var updated = await config.UpdateSourceAsync(corpus, sourceId, body, ct);
            return updated.Refusal is { } refused ? refused.ToResult() : Results.Ok(updated.Value);
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
        g.MapPost("/{nameOrId}/sweep", SweepAsync).Produces<SweepQueued>(StatusCodes.Status202Accepted);

        g.MapPost("/{nameOrId}/reindex", async (string nameOrId, bool? full, RequestContext rc,
            ScopeResolver scopes, IndexJobQueue queue, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Ingest) is { } denied) return denied;
            var corpus = await scopes.ResolveWritableAsync(rc.RequirePrincipal(), nameOrId, ct);
            var job = await queue.EnqueueAsync(corpus.Id, full == true ? JobKind.Full : JobKind.Refresh, ct: ct);
            return Results.Accepted($"/api/jobs/{job.Id}", job.ToSummary());
        }).Produces<JobSummary>(StatusCodes.Status202Accepted).WithGroupName(OpenApiDocuments.Integration);

        // `name` and `sort` are the query's, not the page's. A client can only filter and
        // order what it has fetched, so on a corpus larger than one page a name that IS
        // in the corpus came back as no match — which is how a file that had just been
        // added read as one that was never indexed.
        g.MapGet("/{nameOrId}/files", async (string nameOrId, string? status, string? name,
            string? sort, int? limit, int? offset,
            RequestContext rc, ScopeResolver scopes, CatalogDbContext db, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;
            // `books:fine` selects a set here too, exactly as it does in search.
            var scope = await scopes.ResolveReadableAsync(rc.RequirePrincipal(), [nameOrId], ct);
            var target = scope.Targets[0];
            var corpus = target.Corpus;

            var sourceIds = await db.Sources.Where(s => s.CorpusId == corpus.Id).Select(s => s.Id).ToListAsync(ct);

            var q = FilesOf(db, sourceIds, target.Set.Id, status, name);
            var total = await q.CountAsync(ct);

            var rows = await SortFiles(q, sort)
                .Skip(Math.Max(0, offset ?? 0)).Take(Math.Clamp(limit ?? 100, 1, 1000))
                .ToListAsync(ct);

            return Results.Ok(new FileListResponse(
                total, target.Set.Name, [.. rows.Select(x => x.File.ToSummary(x.State))]));
        }).Produces<FileListResponse>();

        // ── One file, put back together ──────────────────────────────────────
        // A path rather than a file id, and a query parameter rather than a route segment:
        // the handle a caller already has is the path, because that is what search returns,
        // and a relative path contains slashes.
        g.MapGet("/{nameOrId}/file", FileAsync).Produces<IndexedFileText>();
    }

    /// <summary>The handler of <c>POST /api/corpora</c>, a method so a test can call it without a server.</summary>
    internal static async Task<IResult> CreateAsync(CreateCorpusRequest body, RequestContext rc, CatalogDbContext db,
        CorpusConfiguration config, IOptions<DexiconOptions> opts, CancellationToken ct)
    {
        if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;

        var created = await config.CreateCorpusAsync(body, ct);
        if (created.Refusal is { } refused) return refused.ToResult();

        // The reply is read after the corpus is saved, so not on the caller's token: a cancel here threw
        // from a handler whose creation had happened.
        var corpus = created.Value!;
        return Results.Created($"/api/corpora/{corpus.Id}",
            await Summarise(db, corpus, opts.Value.Indexing, CancellationToken.None));
    }

    /// <summary>The handler of <c>PATCH /api/corpora/{name}</c>, a method so a test can call it without a server.</summary>
    internal static async Task<IResult> UpdateAsync(string nameOrId, UpdateCorpusRequest body, RequestContext rc,
        ScopeResolver scopes, CatalogDbContext db, CorpusConfiguration config, IOptions<DexiconOptions> opts,
        CancellationToken ct)
    {
        if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
        var corpus = await scopes.ResolveWritableAsync(rc.RequirePrincipal(), nameOrId, ct);

        if ((await config.UpdateCorpusAsync(corpus, body, ct)).Refusal is { } refused) return refused.ToResult();

        // Not the caller's token, as in CreateAsync: the change is saved.
        return Results.Ok(new CorpusUpdated(await Summarise(db, corpus, opts.Value.Indexing, CancellationToken.None)));
    }

    /// <summary>The handler of <c>POST /api/corpora/{name}/sweep</c>, a method so a test can call it without a server.</summary>
    internal static async Task<IResult> SweepAsync(string nameOrId, RequestContext rc, ScopeResolver scopes,
        SweepQueue sweeps, CancellationToken ct)
    {
        if (rc.RequireScope(Scopes.Ingest) is { } denied) return denied;
        var corpus = await scopes.ResolveWritableAsync(rc.RequirePrincipal(), nameOrId, ct);

        // False means one is already waiting, which is the same answer arriving from
        // the sweep already queued rather than a refusal.
        var queued = sweeps.Enqueue(corpus.Id);
        return Results.Accepted(LocationOf(corpus.Name), new SweepQueued(corpus.Name, queued));
    }

    /// <summary>
    /// The handler of <c>GET /api/corpora/{name}/file</c>, a method so a test can call it
    /// without a server.
    /// </summary>
    internal static async Task<IResult> FileAsync(string nameOrId, string path, int? start, RequestContext rc,
        ScopeResolver scopes, IVectorStore vectors, DocumentReader documents, CancellationToken ct)
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
        //
        // A path two sources of the corpus hold is two files, and chunks of both would
        // stitch into one text. FileSources picks the source with the most chunks, as
        // get_context does, and the response carries the warning.
        var file = await FileSources.ResolveAsync(chunks, documents, target.Corpus.Id, path, ct);
        chunks = file.Chunks;
        var document = await documents.ForAsync(target.Corpus.Id, target.Set.Id, path, file.SourceId, ct);

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
            store,
            file.Warning));
    }

    /// <summary>One file, and what one chunk set made of it.</summary>
    /// <remarks>
    /// Init properties rather than a positional record: EF projects a member
    /// initialisation and cannot translate a constructor call inside this left join.
    /// The build accepts either, so the difference only shows at run time.
    /// </remarks>
    internal sealed record FileRow
    {
        public required IndexedFile File { get; init; }

        /// <summary>
        /// Null for a file attached before the set existed. That is Pending rather than
        /// missing, and dropping it would hide the files a new set still has to do.
        /// </summary>
        public FileChunkState? State { get; init; }
    }

    /// <summary>
    /// The files of a corpus as one chunk set sees them, narrowed by status and name.
    ///
    /// The name is matched HERE rather than by the caller, because a caller can only
    /// filter the page it fetched: on a corpus larger than one page a name that is in
    /// the corpus came back as no match, which is how a file that had just been added
    /// read as one that was never indexed.
    /// </summary>
    internal static IQueryable<FileRow> FilesOf(
        CatalogDbContext db, List<string> sourceIds, string chunkSetId, string? status, string? name)
    {
        var q = from f in db.Files.Where(f => sourceIds.Contains(f.SourceId))
                join s in db.FileChunkStates.Where(s => s.ChunkSetId == chunkSetId)
                    on f.Id equals s.FileId into gj
                from s in gj.DefaultIfEmpty()
                select new FileRow { File = f, State = s };

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<FileStatus>(status, true, out var parsed))
            q = parsed == FileStatus.Pending
                ? q.Where(x => x.State == null || x.State.Status == parsed)
                : q.Where(x => x.State != null && x.State.Status == parsed);

        if (!string.IsNullOrWhiteSpace(name))
        {
            // LIKE, for the case-insensitivity SQLite gives it over ASCII, with the
            // pattern characters escaped: a caller typing `%` means the character, and
            // unescaped it would match every file and read as the filter doing nothing.
            // Parameterising does not escape them - the wildcard is interpreted from the
            // parameter's value, not from the SQL text.
            const string Escape = "\\";
            var needle = name.Trim()
                .Replace(Escape, Escape + Escape, StringComparison.Ordinal)
                .Replace("%", Escape + "%", StringComparison.Ordinal)
                .Replace("_", Escape + "_", StringComparison.Ordinal);
            q = q.Where(x => EF.Functions.Like(x.File.RelativePath, $"%{needle}%", Escape));
        }

        return q;
    }

    /// <summary>
    /// Orders a page of files, always ending on a key that is unique.
    ///
    /// The first key is not unique for any of these: two files of the same size, chunk
    /// count or status would come back in whatever order the database chose, and paging
    /// an unstable order repeats one row and skips another between pages. A list that
    /// loses a file while you page through it is the same defect this whole change is
    /// about, arriving by a different route.
    ///
    /// Neither is the path. A file_path is relative to its SOURCE, so a corpus whose
    /// sources overlap holds the same path twice — 190 files at 187 distinct paths on the
    /// shelf this was measured against, six of them also equal on size. Ordering that
    /// ends at the path leaves those rows unseparated and the defect intact for exactly
    /// the corpora that have more than one source. Only the id settles it.
    /// </summary>
    internal static IOrderedQueryable<FileRow> SortFiles(IQueryable<FileRow> q, string? sort) =>
        sort?.ToLowerInvariant() switch
        {
            "size" => q.OrderByDescending(x => x.File.SizeBytes)
                       .ThenBy(x => x.File.RelativePath).ThenBy(x => x.File.Id),
            "chunks" => q.OrderByDescending(x => x.State == null ? 0 : x.State.ChunkCount)
                         .ThenBy(x => x.File.RelativePath).ThenBy(x => x.File.Id),
            "status" => q.OrderBy(x => x.State == null ? "" : x.State.Status.ToString())
                         .ThenBy(x => x.File.RelativePath).ThenBy(x => x.File.Id),
            _ => q.OrderBy(x => x.File.RelativePath).ThenBy(x => x.File.Id),
        };

    /// <summary>
    /// The file-shaped settings a history source has no use for, or null if there are none.
    ///
    /// A commit history is walked by `git log`, not by the file walker, so only the
    /// include globs mean anything there — they become pathspecs. `useGitignore`,
    /// `maxFileBytes` and `excludeGlobs` are read by nothing on that path, so storing
    /// them makes the API answer 200 to a request it did not honour and leaves a value
    /// in the row that nothing will ever act on.
    ///
    /// Refused rather than ignored, for the symmetry the other direction already has:
    /// history settings on a file source are a 400. The UI hides these fields for a
    /// history source, which is what is offered rather than what is enforced — this is
    /// the same rule where the request is actually handled.
    /// </summary>
    internal static string? FileOnlySettingsFor(
        SourceKind kind, bool? useGitignore, int? maxFileBytes, IReadOnlyList<string>? excludeGlobs)
    {
        if (kind != SourceKind.GitHistory) return null;

        var named = new List<string>(3);
        if (useGitignore is not null) named.Add("useGitignore");
        if (maxFileBytes is not null) named.Add("maxFileBytes");
        if (excludeGlobs is not null) named.Add("excludeGlobs");

        return named.Count == 0 ? null : string.Join(", ", named);
    }

    /// <summary>
    /// The file source a corpus is created with, when the request names a folder.
    ///
    /// Its filters are null, as they are for a source added later with none sent: it
    /// follows the corpus, and through it the configuration. It was created holding
    /// <c>.gitignore</c> on and the configured cap as its own values, so a corpus default
    /// for either never reached it, and a later change to the configured cap did not
    /// either.
    /// </summary>
    internal static Source FirstSource(string corpusId, string workspacePath) => new()
    {
        Id = Ulid.NewUlid().ToString(),
        CorpusId = corpusId,
        Kind = SourceKind.Workspace,
        RootPath = workspacePath.Trim('/', '\\'),
        CreatedUtc = DateTime.UtcNow,
    };

    /// <summary>
    /// Why a history source cannot be added at this path, or null when it can.
    ///
    /// Two different answers. A folder with no repository at its root is the caller's to
    /// fix, so 400 and where to point instead. git that cannot be asked at all, because the
    /// binary is missing or the question timed out, says nothing about the folder: it came
    /// out as a 500, and a 400 would send someone to check a path that is right. So 503,
    /// with git's reason. <paramref name="isRepository"/> is the probe, passed in so a test
    /// can make it fail the way a deployment's git does.
    /// </summary>
    internal static async Task<ConfigRefusal?> NotAHistoryRootAsync(
        string workspaceRoot, string workspacePath,
        Func<GitRepository, CancellationToken, Task<bool>> isRepository, CancellationToken ct)
    {
        var repo = GitHistory.RepositoryIn(workspaceRoot, workspacePath);

        try
        {
            if (repo is not null && await isRepository(repo, ct)) return null;
        }
        catch (GitHistoryException ex)
        {
            return new ConfigRefusal("git could not be asked", ex.Message, 503);
        }

        return new ConfigRefusal(
            "Not a git repository",
            $"'{workspacePath}' has no git repository in it, so there is no "
            + "history to index. Point this at the folder holding .git.",
            400);
    }

    /// <summary>
    /// History settings git cannot be asked with, refused where they arrive.
    ///
    /// They were stored as sent, so a malformed ref, a commit limit of zero or a diff cap
    /// past the read ceiling was answered 200 and discovered on the next pass, as the
    /// source being unavailable with the reason in a job. The same checks the inventory
    /// makes, and none of them runs git, so a request is still judged before any process
    /// starts. Null settings are the defaults, which are usable.
    /// </summary>
    internal static ConfigRefusal? UnusableHistorySettings(GitHistoryOptions? git) =>
        git is not null && GitHistory.Problem(git) is { } problem
            ? new ConfigRefusal("Unusable history settings", problem, 400)
            : null;

    /// <summary>The sentence that says why a refusal about git applies to a caller who may have no history source.</summary>
    private const string GitReads =
        "A history source passes its include list to git as pathspecs, and a corpus's default include list is inherited by history sources.";

    /// <summary>
    /// A glob list that cannot be used, refused where it arrives. Stored as sent it failed every pass of
    /// every source that read it, with the reason in a job and the request answered 200. The position is
    /// named and the pattern is not echoed, because the text is the caller's and the message is logged.
    /// <paramref name="includeReader"/> says what reads the include list (<see cref="SourceFilters.FirstUnusable"/>);
    /// the exclude list is only ever read by the file walk. The configure tools name the same lists
    /// <c>include</c> and <c>exclude</c>, so the message gives both. The caps on a list's length and on an
    /// element's length (<see cref="SourceFilters.MaxGlobsPerList"/>, <see cref="SourceFilters.MaxGlobLength"/>)
    /// apply to both lists whichever reader they have.
    /// </summary>
    internal static ConfigRefusal? UnusableGlobs(
        IReadOnlyList<string>? include, IReadOnlyList<string>? exclude,
        SourceFilters.GlobReader includeReader = SourceFilters.GlobReader.Walk)
    {
        foreach (var (field, argument, globs, reader) in new (string, string, IReadOnlyList<string>?, SourceFilters.GlobReader)[]
                 {
                     ("includeGlobs", "include", include, includeReader),
                     ("excludeGlobs", "exclude", exclude, SourceFilters.GlobReader.Walk),
                 })
        {
            if (SourceFilters.Check(globs, reader) is not { } problem) continue;

            var at = problem.Index;
            var named = $"{field}[{at}] ({argument}[{at}] for the configure tools)";

            // Numbers and fixed text only: the elements are the caller's, and the message is logged.
            var detail = problem.Kind switch
            {
                SourceFilters.GlobProblemKind.TooMany =>
                    $"{field} ({argument} for the configure tools) holds more than {SourceFilters.MaxGlobsPerList} patterns, "
                    + "which is the most a list can hold.",
                SourceFilters.GlobProblemKind.TooLong =>
                    $"{named} is longer than {SourceFilters.MaxGlobLength} characters, which is the most a pattern can hold.",
                SourceFilters.GlobProblemKind.ClimbsOut =>
                    $"{named} is a path that climbs out of the repository with '..', which git rejects. " + GitReads,
                SourceFilters.GlobProblemKind.RootedPath =>
                    $"{named} starts with '//', or with '/' after pathspec magic such as :(glob). Git rejects it as a "
                    + "path outside the repository: write the path with at most one leading '/' and none after magic. "
                    + GitReads,
                SourceFilters.GlobProblemKind.MalformedMagic =>
                    $"{named} has pathspec magic that git rejects: a ':(' with no closing ')', a word other than top, literal, "
                    + "icase, glob, exclude and attr:, glob together with literal, an empty attr:, or a character after "
                    + "':' that is not one of !, ^ and / (the magic git implements). " + GitReads,
                SourceFilters.GlobProblemKind.SlashThenMagic =>
                    $"{named} starts with '/:'. Without its slash it would read as pathspec magic, so it is refused: "
                    + "write the path without the leading '/', or put the magic first. " + GitReads,
                _ => $"{named} is " + reader switch
                {
                    SourceFilters.GlobReader.Git => "null, empty or holding a null character, which git cannot take as a pathspec",
                    SourceFilters.GlobReader.WalkAndGit =>
                        "null, empty, holding a null character (a history source passes the list to git as pathspecs), "
                        + "or a pattern that does not compile, such as [z-a]",
                    _ => "null, or a pattern that does not compile, such as [z-a]",
                } + ".",
            };
            return new ConfigRefusal("Unusable glob", $"{detail} Nothing was saved.", 400);
        }

        return null;
    }

    /// <summary>
    /// Stores a history source's settings when they differ from what it has, and says
    /// whether they did.
    ///
    /// A request that re-states the current settings is not a change. Without that,
    /// saving the form unchanged queues a refresh, which is the mistake the filter path
    /// already avoids. Compared as settings rather than as stored text: a row written
    /// before a setting existed has no key for it, and the same settings serialised now
    /// carry it at its default, so the text differed on every unchanged save.
    /// </summary>
    internal static bool ApplyHistorySettings(Source source, GitHistoryOptions git)
    {
        if (GitHistoryOptions.FromJson(source.GitOptions) == git) return false;

        source.GitOptions = git.ToJson();
        return true;
    }

    /// <summary>
    /// The names <c>clear</c> understands. Anything else is a typo the caller wants to
    /// know about: unknown names were dropped on the floor and the request answered 200,
    /// so `clear: ["maxFileKb"]` (the configure tools' name for the setting) or a field
    /// renamed one day left the setting in place and reported success.
    ///
    /// Case-insensitive, because that is how <see cref="ApplyFilters"/> compares them, so
    /// `maxfilebytes` is accepted.
    /// </summary>
    internal static readonly string[] ClearableFilters =
        ["useGitignore", "maxFileBytes", "includeGlobs", "excludeGlobs"];

    /// <summary>
    /// The refusal for the first entry of <c>clear</c> that is not a name it understands, or null when all
    /// are. A null entry (<c>"clear": [null]</c>) is unknown: it matched no name and was skipped, so the request
    /// was accepted and cleared nothing. A non-null name is the caller's text, so it is shown on one line and
    /// cut short; a null entry has no text to show.
    /// </summary>
    internal static ConfigRefusal? UnknownClearName(IReadOnlyList<string>? clear)
    {
        if (clear is null) return null;

        foreach (var name in clear)
        {
            if (name is not null && ClearableFilters.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

            // The wording a named filter has always had; a null entry has no name to show.
            return new ConfigRefusal(
                "Unknown filter",
                name is null
                    ? "A null entry is not a filter that can be cleared. clear takes field names: "
                      + $"{string.Join(", ", ClearableFilters)}."
                    : $"'{Mapping.Shown(name)}' is not a filter that can be cleared. "
                      + $"Name one of: {string.Join(", ", ClearableFilters)}.",
                400);
        }

        return null;
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
        // Workspace sources only, because coverage is about FILES on a mount that no
        // source reads. A git-history source has a root and covers none of the files
        // under it, so counting it made a repository look covered and suppressed the
        // very gap that should have said "add a workspace source here too".
        var sources = await db.Sources
            .Where(s => s.CorpusId == corpus.Id && s.Kind == SourceKind.Workspace)
            .ToListAsync(ct);

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
        var activity = await IndexingActivity.ReadAsync(db, [c.Id], ct);

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
                failedCount: rows.Where(r => r.Status == FileStatus.Failed).Sum(r => r.Count),
                state: activity.Of(s));
        }).ToList();

        // The corpus-level figures describe the DEFAULT set, because that is what a search
        // with an unqualified name actually reaches. Summing every set would report a
        // number no query can return.
        var headline = setSummaries.FirstOrDefault(s => s.IsDefault) ?? setSummaries.FirstOrDefault();
        var defaultRows = headline is null ? [] : perSet.Where(r => r.ChunkSetId == headline.Id).ToList();

        return new CorpusSummary(
            c.Id, c.Name, c.Description,
            activity.Of(c).ToString().ToLowerInvariant(),
            c.CreatedUtc, c.LastIndexedUtc,
            sources.Count,
            headline?.FileCount ?? 0,
            headline?.ChunkCount ?? 0,
            defaultRows.Where(r => r.Status is FileStatus.Skipped or FileStatus.Empty).Sum(r => r.Count),
            headline?.FailedCount ?? 0,
            headline?.PendingCount ?? 0,
            sources.Select(s => s.ToSummary(c, indexing, filesPerSource.GetValueOrDefault(s.Id))).ToList(),
            setSummaries,
            c.DefaultsOf(),
            new ConfiguredFilters(SourceFilters.ConfiguredUseGitignore, indexing.MaxFileBytes, indexing.DocumentMaxBytes));
    }
}
