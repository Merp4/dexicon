using System.ComponentModel;
using System.Text;
using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Documents;
using Dexicon.Core.Indexing;
using Dexicon.Core.Search;
using Dexicon.Core.Vectors;
using Dexicon.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Dexicon.Mcp;

/// <summary>
/// The MCP tool surface. FIVE tools, and the count is a design constraint rather than
/// an accident: every tool definition is context the agent pays for on every turn, and
/// a large surface measurably degrades smaller models. See docs/06-mcp-surface.md.
///
/// Errors are written for a model to act on. "Unknown corpus 'api'. Visible: api-repo,
/// rfc-library." is actionable; a stack trace is not.
/// </summary>
[McpServerToolType]
public sealed class DexiconTools
{
    [McpServerTool(Name = "search_index")]
    [Description("Semantic and keyword search over indexed code and documents. Returns matching chunks with file paths and line numbers.")]
    public static async Task<string> SearchIndexAsync(
        RequestContext rc,
        SearchService search,
        [Description("Natural language question or code fragment.")] string query,
        [Description("Corpus names to search. With several corpora, name the one or two whose list_corpora descriptions fit the question: searching all of them lets hits from the others crowd out the answer. Omit to search everything visible to you.")] string[]? corpus = null,
        [Description("hybrid blends meaning with exact terms; semantic is meaning only; keyword is exact-match only and keeps working when embeddings are unavailable.")] string mode = "hybrid",
        [Description("Maximum results, 1-50.")] int limit = 10,
        [Description("Restrict to files under this path, e.g. src/Auth/. Relative to the source root, not the corpus. Use `source` to narrow by folder instead.")] string? pathPrefix = null,
        [Description("Restrict to one source, by the root path a search result cites, e.g. books/manuals/Architecture. A parent matches everything beneath it, so books/manuals covers every topic folder under it. list_corpora does not list these; run a search first, or pass a wrong one and the error names them all.")] string? source = null,
        [Description("Restrict to one language, e.g. csharp, python, typescript.")] string? language = null,
        [Description("Restrict to chunks declaring this symbol, e.g. TokenService.")] string? symbol = null,
        [Description("Characters of each result to return, centred on the matching passage. The default is enough to read the match in context; raise it when a hit is clearly the right passage and you need more of it, or use get_context. 0 returns whole chunks, which on a book corpus is about 8,000 characters each.")]
        int maxCharsPerHit = SearchRequest.DefaultMaxCharsPerHit,
        [Description("Collapse results that are the same document in another format, e.g. a book held as both PDF and EPUB. On by default. Turn it off to compare how the two were extracted.")]
        bool distinctTitles = true,
        CancellationToken ct = default)
    {
        Require(rc, Scopes.Search);

        SearchResult result;
        try
        {
            result = await search.SearchAsync(rc.RequirePrincipal(), new SearchRequest
            {
                Query = query,
                Corpus = corpus,
                Mode = Mapping.ParseMode(mode),
                Limit = Math.Clamp(limit, 1, 50),
                PathPrefix = pathPrefix,
                Source = source,
                Language = language,
                Symbol = symbol,
                MaxCharsPerHit = Math.Clamp(maxCharsPerHit, 0, 100_000),
                DistinctTitles = distinctTitles,
            }, ct);
        }
        catch (ScopeResolutionException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (EmbeddingDimensionMismatchException ex)
        {
            throw new McpException(ex.Message);
        }

        return Render(result);
    }

    /// <summary>
    /// Formatted for a model to read, not for a machine to parse. Most clients show
    /// this text verbatim, so it leads with what matters: where the match is.
    ///
    /// Internal rather than private: this is the agent-facing surface of the whole product,
    /// and it can be checked without standing up an MCP server.
    /// </summary>
    internal static string Render(SearchResult result)
    {
        var sb = new StringBuilder();
        var scope = string.Join(", ", result.Scope.Select(s => s.Name));

        sb.Append(result.Hits.Count switch
        {
            0 => $"No results for \"{result.Query}\"",
            1 => $"1 result for \"{result.Query}\"",
            var n => $"{n} results for \"{result.Query}\"",
        });
        sb.Append($" ({result.Mode.ToString().ToLowerInvariant()}, corpus: {scope})\n");

        if (result.Degraded)
            sb.Append($"\n! DEGRADED: {result.DegradedReason}\n");
        if (result.Note is not null)
            sb.Append($"\n! {result.Note}\n");

        if (result.Hits.Count == 0)
        {
            sb.Append("\nNothing matched. If this is unexpected, check index_status: the corpus may still be indexing, or the content may not be indexed at all.\n");
            return sb.ToString();
        }

        // Only when it disambiguates. A corpus with one source would add the same folder
        // name to every line for nothing; a corpus with ten needs it, because two of its
        // books share a filename and the results are otherwise identical down to the path.
        var spansSources = result.Hits
            .Select(h => h.SourceRoot).Where(r => r is { Length: > 0 })
            .Distinct(StringComparer.Ordinal).Count() > 1;

        var i = 1;
        foreach (var hit in result.Hits)
        {
            sb.Append($"\n{i++}. {hit.Location}");
            if (spansSources && hit.SourceRoot is { Length: > 0 } root)
                sb.Append($"  · in {root}");

            // A hit in a PDF or an EPUB is cited by its unit, as `book.epub#chapter=7`,
            // which is right for a citation and unusable as an argument to get_context,
            // whose handle is a line. Without this an agent could find a passage in a
            // book and then have nothing to pass in order to read on from it.
            if (hit.Page is not null) sb.Append($"  · lines {hit.StartLine}-{hit.EndLine}");

            if (hit.Section is { Length: > 0 }) sb.Append($"  · {hit.Section}");
            if (result.Scope.Count > 1) sb.Append($"  [{hit.CorpusName}]");
            sb.Append('\n');

            foreach (var line in hit.Content.Split('\n'))
                sb.Append("   ").Append(line).Append('\n');
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "list_corpora")]
    [Description("List the corpora you can search, what each one holds, and whether it has any indexed content. Call this first when you do not already know which corpus answers a question. The names it returns are the valid values for search_index's corpus parameter.")]
    public static async Task<string> ListCorporaAsync(
        RequestContext rc,
        ScopeResolver scopes,
        CatalogDbContext db,
        IOptions<DexiconOptions> opts,
        CancellationToken ct = default)
    {
        Require(rc, Scopes.Search);
        var principal = rc.RequirePrincipal();
        var visible = await scopes.VisibleAsync(principal, ct);

        if (visible.Count == 0)
            return $"Key '{principal.Name}' can reach no corpora. Create one in the Dexicon UI, " +
                   "or map this key to one under Access.";

        var sb = new StringBuilder(
            $"{visible.Count} {(visible.Count == 1 ? "corpus" : "corpora")} " +
            $"reachable by '{principal.Name}':\n");
        foreach (var c in visible)
            sb.Append(RenderCorpus(await CorpusEndpoints.Summarise(db, c, opts.Value.Indexing, ct)));
        return sb.ToString();
    }

    /// <summary>
    /// A string as the literal an agent would type: quotes and backslashes escaped, and
    /// nothing else. The default encoder writes a quote as a six-character Unicode escape,
    /// which is valid JSON and not what anyone copies.
    /// </summary>
    private static readonly System.Text.Json.JsonSerializerOptions Literal = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// One corpus, as an agent reads it.
    ///
    /// Ordering is the whole of this method. An agent calls list_corpora to answer one
    /// question, "which of these should I search?", and the only line that answers it is
    /// the description a human wrote. That used to come last, under the state, the file
    /// counts, the chunk sets, the embedding dimensions and the overlap: five lines of
    /// operational detail an agent cannot act on, ahead of the one line it needs. Now the
    /// description leads and the machinery follows.
    ///
    /// Internal and pure for the same reason as <see cref="Render"/>: this is the
    /// agent-facing surface of the product and it should be checkable without standing up
    /// an MCP server.
    /// </summary>
    internal static string RenderCorpus(CorpusSummary s)
    {
        var sb = new StringBuilder();
        sb.Append($"\n- {s.Name}\n");

        // What it is, before what it is made of.
        sb.Append(s.Description is { Length: > 0 }
            ? $"    {s.Description}\n"
            : "    (no description: state what the corpus holds so an agent can choose between corpora)\n");

        // An empty corpus is a legal value for search_index that cannot answer anything.
        // Listed identically to a full one, it reads as a reasonable place to look, and the
        // agent spends a call discovering otherwise.
        if (s.ChunkCount == 0)
        {
            sb.Append(s.State.Equals("indexing", StringComparison.OrdinalIgnoreCase)
                ? "    NOT SEARCHABLE YET: indexing has not written any chunks. Try index_status.\n"
                : "    NOT SEARCHABLE: no indexed content. Nothing here will ever match.\n");
        }

        sb.Append($"    state: {s.State}, {s.FileCount:N0} {UnitFor(s.Sources, s.FileCount)}, {s.ChunkCount:N0} chunks");

        // Every set is addressable as `corpus:set`, so an agent that is only told the
        // corpus name cannot reach the others. Named here, with the default marked.
        foreach (var set in s.ChunkSets)
        {
            sb.Append($"\n    {(set.IsDefault ? "*" : " ")} {s.Name}:{set.Name}");
            sb.Append($" — {set.EmbeddingModel} ({set.EmbeddingDimensions}d), ");
            sb.Append($"{set.ChunkSize} tokens/{set.ChunkOverlap} overlap, {set.ChunkCount:N0} chunks");
            if (set.State != "ready") sb.Append($" [{set.State}]");
        }
        if (s.ChunkSets.Count > 1)
            sb.Append("\n    (* is the default; name another with corpus:set)");

        if (s.LastIndexedUtc is { } indexed) sb.Append($"\n    last indexed: {indexed:u}");
        if (s.FailedCount > 0)
            // The name as a JSON string, so the call can be copied as it stands whatever the
            // name holds: only a blank one is refused, and a quote in it broke the call.
            sb.Append($"\n    {s.FailedCount:N0} {UnitFor(s.Sources, s.FailedCount)} failed; " +
                      $"index_status({System.Text.Json.JsonSerializer.Serialize(s.Name, Literal)}) lists them with the reason");
        sb.Append('\n');
        return sb.ToString();
    }

    [McpServerTool(Name = "get_context")]
    [Description("Return the indexed lines surrounding a location. Use after search_index when a hit needs its surroundings, including reading on past the end of a hit, by centring further down the file.")]
    public static async Task<string> GetContextAsync(
        RequestContext rc,
        ScopeResolver scopes,
        IVectorStore vectors,
        DocumentReader documents,
        [Description("Corpus name, as given by list_corpora.")] string corpus,
        [Description("File path exactly as returned by search_index.")] string filePath,
        [Description("Line number to centre on, as search_index reports it for the hit.")] int aroundLine,
        [Description("Lines of context before.")] int before = 30,
        [Description("Lines of context after.")] int after = 30,
        [Description("Prefix each line with its number. On by default: quoting or editing a passage needs the line, and counting down from the header is where that goes wrong.")]
        bool lineNumbers = true,
        CancellationToken ct = default)
    {
        Require(rc, Scopes.Search);

        ScopedCorpus target;
        try
        {
            var scope = await scopes.ResolveReadableAsync(rc.RequirePrincipal(), [corpus], ct);
            target = scope.Targets[0];
        }
        catch (ScopeResolutionException ex) { throw new McpException(ex.Message); }

        // Read from the index rather than from disk, so this works for uploads, which have
        // no file to read, and by filter rather than by search.
        //
        // This used to run a keyword search for the path and keep the top 50 hits, which
        // let relevance decide which of a file's chunks came back. Asking for the lines
        // around line 2,625 of a book returned nothing at all, because the chunks holding
        // those lines did not rank for their own filename. Fetching a known span is a
        // lookup; ranking has no business in it.
        var chunks = await vectors.GetFileChunksAsync(
            target.Set.CollectionName, target.Set.Id, filePath, ct);

        // A file_path is relative to its source root, so within a corpus it is not unique.
        // A corpus with sources AI/ and Philosophy/ that both hold "Installation Guide.pdf"
        // returns the chunks of both here, ordered by chunk index, which interleaves two
        // different books and stitches them into one passage with line numbers on it. The
        // result is plausible and quotable while being wrong.
        var bySource = chunks
            .GroupBy(c => c.SourceId ?? string.Empty)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        var ambiguous = bySource.Count > 1;
        if (ambiguous) chunks = [.. bySource[0].OrderBy(c => c.ChunkIndex)];

        var lo = Math.Max(1, aroundLine - before);
        var hi = aroundLine + after;

        // Out of the document where there is one, so the window is the caller's range and
        // not the span of whatever chunks happen to cover it, and so it cannot have a hole
        // in it. Only where the path resolves to a single file: `ambiguous` means two
        // sources hold it, and serving one book's text under the other's name is what the
        // warning below exists for, so that case keeps the chunk path and the warning.
        if (!ambiguous && chunks.Count > 0)
        {
            var document = await documents.ForAsync(
                target.Corpus.Id, target.Set.Id, filePath, chunks[0].SourceId, ct);

            if (document is not null)
            {
                var (text, gotLo, gotHi) = Passage.Window(document.Text, lo, hi);

                if (gotHi < gotLo)
                    throw new McpException(
                        $"'{filePath}' in corpus '{corpus}' has no line {aroundLine}; "
                        + $"it runs to line {document.Text.AsSpan().Count('\n') + 1}.");

                return $"{filePath}:{gotLo}-{gotHi} (corpus: {corpus})\n\n"
                     + Passage.Stitch([(gotLo, gotHi, text)], lineNumbers);
            }
        }

        var pieces = chunks
            .Where(h => h.EndLine >= lo && h.StartLine <= hi)
            .ToList();

        if (pieces.Count == 0)
            throw new McpException(chunks.Count == 0
                ? $"No indexed file '{filePath}' in corpus '{corpus}'. " +
                  "Check the path is exactly as search_index returned it."
                : $"'{filePath}' is indexed in corpus '{corpus}' but has no content around line " +
                  $"{aroundLine}; it spans lines {chunks.Min(c => c.StartLine)}-{chunks.Max(c => c.EndLine)}.");

        // The window, not the span of the chunks overlapping it. `before` and `after` are
        // documented as lines, and a caller budgeting context should get what it asked
        // for: one line either side used to return a whole chunk, forty lines in docs/.
        var shownLo = Math.Max(lo, pieces[0].StartLine);
        var shownHi = Math.Min(hi, pieces[^1].EndLine);

        var header = $"{filePath}:{shownLo}-{shownHi} (corpus: {corpus})\n";
        if (ambiguous)
            header += $"! {bySource.Count} sources in this corpus contain a file at that path. " +
                      "They are different files with the same name. This is one of them, the " +
                      "largest; the others are not shown and not mixed in.\n";
        return header + "\n" +
               Passage.Stitch(pieces.Select(p => (p.StartLine, p.EndLine, p.Content)), lineNumbers, (lo, hi));
    }

    [McpServerTool(Name = "index_refresh")]
    [Description("Queue a reindex of a corpus and return immediately. Use when you know the files have changed and search looks stale.")]
    public static async Task<string> IndexRefreshAsync(
        RequestContext rc,
        ScopeResolver scopes,
        IndexJobQueue queue,
        [Description("Corpus name, as given by list_corpora.")] string corpus,
        [Description("Re-embed everything, ignoring content hashes. Slow; only for a corpus you believe is corrupt.")] bool full = false,
        CancellationToken ct = default)
    {
        Require(rc, Scopes.Ingest);

        Corpus target;
        try { target = await scopes.ResolveWritableAsync(rc.RequirePrincipal(), corpus, ct); }
        catch (ScopeResolutionException ex) { throw new McpException(ex.Message); }

        var job = await queue.EnqueueAsync(target.Id, full ? JobKind.Full : JobKind.Refresh, ct: ct);

        // Never blocks: indexing a large repository outlasts any sensible tool timeout.
        return $"Queued {(full ? "full" : "incremental")} reindex of '{target.Name}' as job {job.Id} " +
               $"(state: {job.State.ToString().ToLowerInvariant()}). Poll index_status for progress.";
    }

    [McpServerTool(Name = "index_status")]
    [Description("Report indexing state, counts and last error. Answers 'why did search return nothing'.")]
    public static async Task<string> IndexStatusAsync(
        RequestContext rc,
        ScopeResolver scopes,
        CatalogDbContext db,
        IOptions<DexiconOptions> opts,
        [Description("Corpus name. Omit for every corpus you can see.")] string? corpus = null,
        CancellationToken ct = default)
    {
        Require(rc, Scopes.Search);
        var principal = rc.RequirePrincipal();

        List<Corpus> targets;
        if (string.IsNullOrWhiteSpace(corpus))
        {
            targets = await scopes.VisibleAsync(principal, ct);
        }
        else
        {
            try { targets = (await scopes.ResolveReadableAsync(principal, [corpus], ct)).Corpora.ToList(); }
            catch (ScopeResolutionException ex) { throw new McpException(ex.Message); }
        }

        if (targets.Count == 0) return $"Key '{principal.Name}' can reach no corpora.";

        var sb = new StringBuilder();
        foreach (var c in targets)
        {
            var summary = await CorpusEndpoints.Summarise(db, c, opts.Value.Indexing, ct);
            // By QueuedUtc, not StartedUtc: a job that has been QUEUED but not yet started
            // is the latest news about this corpus, and ordering on StartedUtc reported
            // the previous job instead -- so index_status said "succeeded" to an agent
            // whose reindex was still sitting in the queue.
            var job = await db.Jobs.Where(j => j.CorpusId == c.Id)
                .OrderByDescending(j => j.QueuedUtc).ThenByDescending(j => j.Id)
                .FirstOrDefaultAsync(ct);

            sb.Append($"{c.Name}: {summary.State}\n");
            sb.Append($"  {summary.FileCount:N0} {UnitFor(summary.Sources, summary.FileCount)} indexed, {summary.ChunkCount:N0} chunks");
            if (summary.SkippedCount > 0) sb.Append($", {summary.SkippedCount:N0} skipped");
            if (summary.FailedCount > 0) sb.Append($", {summary.FailedCount:N0} failed");
            sb.Append('\n');
            foreach (var set in summary.ChunkSets)
            {
                sb.Append($"  {(set.IsDefault ? "*" : " ")} {set.Name}: {set.State}, ");
                sb.Append($"{set.EmbeddingModel} ({set.EmbeddingDimensions}d), {set.ChunkCount:N0} chunks");
                if (set.PendingCount > 0) sb.Append($", {set.PendingCount:N0} pending");
                if (set.FailedCount > 0) sb.Append($", {set.FailedCount:N0} failed");
                sb.Append('\n');
            }
            sb.Append($"  last indexed: {(c.LastIndexedUtc is { } t ? $"{t:u}" : "never")}\n");

            if (job is not null)
            {
                sb.Append($"  latest job {job.Id}: {job.State.ToString().ToLowerInvariant()}");
                if (job.Phase is { Length: > 0 }) sb.Append($" ({job.Phase})");
                if (job.State == JobState.Running && job.FilesTotal > 0)
                {
                    var pct = 100.0 * (job.FilesDone + job.FilesSkipped + job.FilesFailed) / job.FilesTotal;
                    sb.Append($" — {pct:F0}% ({job.FilesDone + job.FilesSkipped + job.FilesFailed:N0}"
                            + $" / {job.FilesTotal:N0} {UnitFor(summary.Sources, job.FilesTotal)})");
                }
                sb.Append('\n');
                if (job.Error is { Length: > 0 }) sb.Append($"  error: {job.Error}\n");
            }

            // Workspace sources only, as the HTTP report does. A git-history source has a
            // root and reads none of the files under it, so counting it would make a
            // repository look covered and hide the gap that says to add a file source.
            sb.Append(RenderCoverage(SourceCoverage.Find(
                opts.Value.Indexing.WorkspaceRoot,
                summary.Sources
                    .Where(s => string.Equals(s.Kind, nameof(SourceKind.Workspace),
                                StringComparison.OrdinalIgnoreCase))
                    .Select(s => new SourceCoverage.SourceRoot(s.RootPath, s.MaxFileBytes)),
                opts.Value.Indexing.DocumentMaxBytes)));

            // The detail for one corpus asked about by name: what each source reads, and
            // which files it left out and why. It stopped at counts, so "why is this file
            // not found" ended at "see the UI". Not for the unnamed listing, where it would
            // be every source and file of every corpus in one tool result.
            if (targets.Count == 1 && !string.IsNullOrWhiteSpace(corpus))
            {
                sb.Append(RenderSources(summary.Sources, summary.Defaults));
                sb.Append(RenderProblemFiles(await ProblemFilesAsync(db, summary, ct)));
            }

            sb.Append('\n');
        }

        if (string.IsNullOrWhiteSpace(corpus))
            sb.Append("Name a corpus for its sources and filters, and the files it skipped, failed or found empty with the reason for each.\n");

        return sb.ToString();
    }

    /// <summary>
    /// What each source reads and how it is filtered, so an agent can say why a file is or
    /// is not in the index, and whether a filter belongs to the source or the corpus.
    /// </summary>
    internal static string RenderSources(IReadOnlyList<SourceSummary> sources, CorpusDefaults? defaults)
    {
        if (sources.Count == 0) return "  sources: none\n";

        var sb = new StringBuilder("  sources:\n");
        foreach (var s in sources)
        {
            var where = s.RootPath switch { null => "", "" => "the workspace root", var p => OneLine(p) };

            if (string.Equals(s.Kind, "githistory", StringComparison.OrdinalIgnoreCase))
            {
                var git = s.Git ?? new GitHistoryOptions();
                sb.Append($"    commit history of {where}: {s.FileCount:N0} {(s.FileCount == 1 ? "commit" : "commits")}, follows {git.Ref}");
                if (s.Tracking is { } tracking && Distance(tracking) is { } distance)
                    sb.Append($" ({distance})");
                var holds = new[] { git.IncludeMessage ? "message" : null, git.IncludeStat ? "stat" : null, git.IncludeDiff ? "diff" : null }
                    .Where(p => p is not null).ToList();
                sb.Append($"; holds {(holds.Count == 0 ? "sha, author and date only" : string.Join(", ", holds))}");
                if (git.IncludeMerges) sb.Append("; merges included");
                if (git.MaxCommits is { } max) sb.Append($"; newest {max:N0}{(git.KeepIndexed ? ", kept once indexed" : "")}");
                if (git.Since is { } since) sb.Append($"; since {since:yyyy-MM-dd}");
                if (s.IncludeGlobs.Count > 0)
                {
                    sb.Append($"; only paths {Globs(s.IncludeGlobs)}");
                    // The paths are the include globs, which a history source can take from
                    // the corpus like a file source does.
                    if (defaults?.IncludeGlobs is not null && s.OwnIncludeGlobs is null) sb.Append(" (from the corpus defaults)");
                }
                if (s.NewestCommit is { } newest) sb.Append($"; newest {newest.Sha[..7]}, {newest.AuthoredUtc:yyyy-MM-dd}");
                sb.Append('\n');
                continue;
            }

            if (string.Equals(s.Kind, "upload", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append($"    uploaded documents: {s.FileCount:N0}, attached in the UI\n");
                continue;
            }

            // "found", not "indexed": the source counts every file it holds a row for, failed
            // and empty ones included, where the corpus line above counts only the indexed.
            sb.Append($"    files under {where}: {s.FileCount:N0} {(s.FileCount == 1 ? "file" : "files")} found; ");
            sb.Append($".gitignore {(s.UseGitignore ? "respected" : "ignored")}");

            // Always, whatever the .gitignore setting. A file an ignore file or a filter
            // excludes is dropped in the walk without a row, so it never appears among the
            // problem files below, and this line is the only sign that one may have.
            sb.Append("; .dexiconignore respected");
            sb.Append($"; code and text up to {Bytes(s.MaxFileBytes)}");
            if (s.IncludeGlobs.Count > 0) sb.Append($"; only {Globs(s.IncludeGlobs)}");
            if (s.ExcludeGlobs.Count > 0) sb.Append($"; not {Globs(s.ExcludeGlobs)}");

            // Which of those the corpus sets rather than the source, so a change is made in
            // the place that owns the value. A glob list is named only when it is shown: a
            // corpus that sets an empty one has nothing on the line to attribute.
            var fromCorpus = new[]
            {
                defaults?.UseGitignore is not null && s.OwnUseGitignore is null ? ".gitignore" : null,
                defaults?.MaxFileBytes is not null && s.OwnMaxFileBytes is null ? "size" : null,
                defaults?.IncludeGlobs is { Count: > 0 } && s.OwnIncludeGlobs is null ? "only" : null,
                defaults?.ExcludeGlobs is { Count: > 0 } && s.OwnExcludeGlobs is null ? "not" : null,
            }.Where(f => f is not null).ToList();
            if (fromCorpus.Count > 0) sb.Append($" (from the corpus defaults: {string.Join(", ", fromCorpus)})");
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// How far the followed branch was from its upstream at the last pass, or null where
    /// nothing was observed. Up to date is said, so it reads differently from no tracking at
    /// all, and the fetch is dated only when its time is known.
    /// </summary>
    internal static string? Distance(GitTracking tracking)
    {
        if (tracking.Upstream is not { } up) return null;

        string where;
        if (up.Gone) where = $"its upstream {up.ShortName} is gone";
        else if (up.Behind is null && up.Ahead is null) return null;
        else where = (up.Behind ?? 0, up.Ahead ?? 0) switch
        {
            (0, 0) => $"up to date with {up.ShortName}",
            (var behind, 0) => $"{behind} behind {up.ShortName}",
            (0, var ahead) => $"{ahead} ahead of {up.ShortName}",
            var (behind, ahead) => $"{behind} behind and {ahead} ahead of {up.ShortName}",
        };

        return tracking.LastFetchUtc is { } fetched
            ? $"{where} as of the fetch at {fetched:yyyy-MM-dd HH:mm} UTC"
            : where;
    }

    /// <summary>Files of one status in the default chunk set: how many, and the first few by path with their reason.</summary>
    internal sealed record ProblemFiles(string Status, int Total, IReadOnlyList<(string Path, string? Reason)> Sample);

    private const int ProblemSample = 10;

    private static async Task<IReadOnlyList<ProblemFiles>> ProblemFilesAsync(
        CatalogDbContext db, CorpusSummary summary, CancellationToken ct)
    {
        var set = summary.ChunkSets.FirstOrDefault(s => s.IsDefault);
        if (set is null) return [];

        var sourceIds = summary.Sources.Select(s => s.Id).ToList();
        var roots = summary.Sources.ToDictionary(s => s.Id, s => s.RootPath);
        var groups = new List<ProblemFiles>();

        foreach (var status in new[] { FileStatus.Failed, FileStatus.Skipped, FileStatus.Empty })
        {
            var q = CorpusEndpoints.FilesOf(db, sourceIds, set.Id, status.ToString(), null);
            var total = await q.CountAsync(ct);
            if (total == 0) continue;

            var rows = await q.OrderBy(x => x.File.RelativePath).ThenBy(x => x.File.Id).Take(ProblemSample)
                .Select(x => new { x.File.SourceId, x.File.RelativePath, x.State!.StatusDetail })
                .ToListAsync(ct);

            // A path is relative to its source, and a corpus with several sources can hold the
            // same one twice, so each is shown under its source's root.
            groups.Add(new ProblemFiles(status.ToString().ToLowerInvariant(), total,
                rows.Select(r => (
                    roots.GetValueOrDefault(r.SourceId) is { Length: > 0 } root ? $"{root}/{r.RelativePath}" : r.RelativePath,
                    r.StatusDetail)).ToList()));
        }

        return groups;
    }

    internal static string RenderProblemFiles(IReadOnlyList<ProblemFiles> groups)
    {
        var sb = new StringBuilder();
        foreach (var g in groups)
        {
            sb.Append($"  {g.Status}: {g.Total:N0}\n");
            foreach (var (path, reason) in g.Sample)
            {
                sb.Append($"    {OneLine(path)}");
                if (reason is { Length: > 0 })
                {
                    // One line each: a reason is often an exception message, and a stack of
                    // them would push the rest of the report out of the result.
                    var line = reason.ReplaceLineEndings(" ").Trim();
                    sb.Append($" — {(line.Length > 160 ? line[..157] + "..." : line)}");
                }
                sb.Append('\n');
            }
            if (g.Total > g.Sample.Count) sb.Append($"    ... and {g.Total - g.Sample.Count:N0} more\n");
        }
        return sb.ToString();
    }

    private static string Bytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{b / (double)(1L << 10):0.#} KB",
        _ => $"{b} bytes",
    };

    /// <summary>
    /// A path as one line. A file name on Linux can hold a line break, and printed as it
    /// is, one would end its entry early and could begin a line that reads as another
    /// status or reason. A glob is stored as it was typed, so it can hold one too.
    /// </summary>
    internal static string OneLine(string path) => path.ReplaceLineEndings(" ");

    private static string Globs(IReadOnlyList<string> globs) => string.Join(", ", globs.Select(OneLine));

    /// <summary>
    /// What a corpus's count is counting.
    ///
    /// A git-history source's units are commits, so a corpus made only of them saying
    /// "201 files indexed" contradicts the source summary the same agent can read. A
    /// corpus holding both kinds is counting two different things at once and neither
    /// word is true of the total, so it says "documents", which is true of either.
    /// </summary>
    internal static string UnitFor(IReadOnlyList<SourceSummary> sources, int n)
    {
        var history = nameof(SourceKind.GitHistory);
        var kinds = sources.Select(s => s.Kind).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var one = !kinds.Contains(history, StringComparer.OrdinalIgnoreCase) ? "file"
            : kinds.Count == 1 ? "commit"
            : "document";

        return n == 1 ? one : one + "s";
    }

    /// <summary>
    /// Files no source covers. Not skipped, not failed, not counted: absent, and absence
    /// has no row in any of the figures above it. An agent that searches for one of these
    /// gets other documents back, which is indistinguishable from a ranking result, so the
    /// wording has to say what to do about it.
    ///
    /// Capped at five files per directory. The whole list belongs in the UI; what an agent
    /// needs here is to know the corpus has a hole and roughly where.
    /// </summary>
    internal static string RenderCoverage(IReadOnlyList<SourceCoverage.Gap> gaps)
    {
        if (gaps.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        foreach (var gap in gaps)
        {
            var where = gap.DirectoryRelativePath.Length == 0
                ? "the workspace root"
                : OneLine(gap.DirectoryRelativePath);

            sb.Append($"  NOT INDEXED: {gap.Files.Count:N0} file(s) in {where} are covered by no source, ");
            sb.Append("though its subfolders are. Searching will never return them.\n");

            foreach (var file in gap.Files.Take(5))
                sb.Append($"    {OneLine(file)}\n");
            if (gap.Files.Count > 5)
                sb.Append($"    ... and {gap.Files.Count - 5:N0} more\n");

            sb.Append($"  Add a source on {where}, or move the file into one of its subfolders.\n");
        }

        return sb.ToString();
    }

    internal static void Require(RequestContext rc, string scope)
    {
        var principal = rc.Principal
            ?? throw new McpException("Not authenticated. Add an Authorization: Bearer dex_… header to the MCP server configuration.");

        if (!principal.Has(scope))
            throw new McpException(
                $"This key has scopes [{string.Join(", ", principal.Scopes)}] and needs '{scope}'. " +
                "Whoever runs Dexicon can grant it on the Access page.");
    }
}
