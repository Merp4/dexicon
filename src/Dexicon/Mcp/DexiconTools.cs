using System.ComponentModel;
using System.Text;
using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Indexing;
using Dexicon.Core.Search;
using Dexicon.Core.Vectors;
using Dexicon.Infrastructure;
using Microsoft.EntityFrameworkCore;
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
        [Description("Corpus names to search. Omit to search everything visible to you. Use list_corpora to discover them.")] string[]? corpus = null,
        [Description("hybrid blends meaning with exact terms; semantic is meaning only; keyword is exact-match only and keeps working when embeddings are unavailable.")] string mode = "hybrid",
        [Description("Maximum results, 1-50.")] int limit = 10,
        [Description("Restrict to files under this path, e.g. src/Auth/.")] string? pathPrefix = null,
        [Description("Restrict to one language, e.g. csharp, python, typescript.")] string? language = null,
        [Description("Restrict to chunks declaring this symbol, e.g. TokenService.")] string? symbol = null,
        CancellationToken ct = default)
    {
        Require(rc, Scopes.Search);

        SearchResult result;
        try
        {
            result = await search.SearchAsync(rc.RequireTenant(), new SearchRequest
            {
                Query = query,
                Corpus = corpus,
                Mode = Mapping.ParseMode(mode),
                Limit = Math.Clamp(limit, 1, 50),
                PathPrefix = pathPrefix,
                Language = language,
                Symbol = symbol,
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
    /// </summary>
    private static string Render(SearchResult result)
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
            sb.Append("\nNothing matched. If this is unexpected, check index_status — the corpus may still be indexing, or the content may not be indexed at all.\n");
            return sb.ToString();
        }

        var i = 1;
        foreach (var hit in result.Hits)
        {
            sb.Append($"\n{i++}. {hit.Location}");
            if (hit.Section is { Length: > 0 }) sb.Append($"  · {hit.Section}");
            if (result.Scope.Count > 1) sb.Append($"  [{hit.CorpusName}]");
            sb.Append('\n');

            foreach (var line in hit.Content.Split('\n'))
                sb.Append("   ").Append(line).Append('\n');
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "list_corpora")]
    [Description("List the corpora you can search, with their sizes and state. These names are the legal values for search_index's corpus parameter.")]
    public static async Task<string> ListCorporaAsync(
        RequestContext rc,
        ScopeResolver scopes,
        CatalogDbContext db,
        CancellationToken ct = default)
    {
        Require(rc, Scopes.Search);
        var tenant = rc.RequireTenant();
        var visible = await scopes.VisibleAsync(tenant, ct);

        if (visible.Count == 0)
            return $"No corpora are visible to tenant '{tenant}'. Create one in the Dexicon UI first.";

        var sb = new StringBuilder($"{visible.Count} corpus/corpora visible to '{tenant}':\n");
        foreach (var c in visible)
        {
            var s = await CorpusEndpoints.Summarise(db, c, tenant, ct);
            sb.Append($"\n- {s.Name}");
            if (!s.Owned) sb.Append(" (shared)");
            sb.Append($"\n    state: {s.State}, {s.FileCount:N0} files, {s.ChunkCount:N0} chunks");
            sb.Append($"\n    model: {s.EmbeddingModel} ({s.EmbeddingDimensions}d)");
            if (s.LastIndexedUtc is { } t) sb.Append($"\n    last indexed: {t:u}");
            if (s.FailedCount > 0) sb.Append($"\n    {s.FailedCount} file(s) failed — see the UI for why");
            if (s.Description is { Length: > 0 }) sb.Append($"\n    {s.Description}");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    [McpServerTool(Name = "get_context")]
    [Description("Return the indexed lines surrounding a location, stitched together. Use after search_index when a hit needs its surroundings.")]
    public static async Task<string> GetContextAsync(
        RequestContext rc,
        ScopeResolver scopes,
        IVectorStore vectors,
        [Description("Corpus name, as given by list_corpora.")] string corpus,
        [Description("File path exactly as returned by search_index.")] string filePath,
        [Description("Line number to centre on.")] int aroundLine,
        [Description("Lines of context before.")] int before = 30,
        [Description("Lines of context after.")] int after = 30,
        CancellationToken ct = default)
    {
        Require(rc, Scopes.Search);

        Corpus target;
        try
        {
            var scope = await scopes.ResolveReadableAsync(rc.RequireTenant(), [corpus], ct);
            target = scope.Corpora[0];
        }
        catch (ScopeResolutionException ex) { throw new McpException(ex.Message); }

        // Read from the INDEX rather than from disk, so this works for uploads, which have
        // no file to read — but by FILTER, not by search.
        //
        // This used to run a keyword search for the path and keep the top 50 hits, which
        // let relevance decide which of a file's chunks came back. Asking for the lines
        // around line 2,625 of a book returned nothing at all, because the chunks holding
        // those lines did not rank for their own filename. Fetching a known span is a
        // lookup; ranking has no business in it.
        var chunks = await vectors.GetFileChunksAsync(target.CollectionName, target.Id, filePath, ct);

        var lo = Math.Max(1, aroundLine - before);
        var hi = aroundLine + after;

        var pieces = chunks
            .Where(h => h.EndLine >= lo && h.StartLine <= hi)
            .ToList();

        if (pieces.Count == 0)
            throw new McpException(chunks.Count == 0
                ? $"No indexed file '{filePath}' in corpus '{corpus}'. " +
                  "Check the path is exactly as search_index returned it."
                : $"'{filePath}' is indexed in corpus '{corpus}' but has no content around line " +
                  $"{aroundLine}; it spans lines {chunks.Min(c => c.StartLine)}-{chunks.Max(c => c.EndLine)}.");

        var header = $"{filePath}:{pieces[0].StartLine}-{pieces[^1].EndLine} (corpus: {corpus})\n\n";
        return header + Stitch(pieces.Select(p => (p.StartLine, p.EndLine, p.Content)));
    }

    /// <summary>
    /// Joins overlapping chunks of ONE file back into a single readable passage.
    /// Internal rather than inlined so it can be tested without an MCP server: the
    /// off-by-one here decides whether a model reads duplicated or missing lines.
    /// </summary>
    /// <param name="pieces">Chunks of one file, in file order (by chunk index).</param>
    internal static string Stitch(IEnumerable<(int StartLine, int EndLine, string Content)> pieces)
    {
        var sb = new StringBuilder();
        var emittedThrough = 0;
        var lastRange = (Start: 0, End: 0);

        foreach (var p in pieces)
        {
            // Several chunks can share ONE line number: the chunker splits a line that is
            // longer than the whole budget, and every piece honestly reports that line.
            // Line-based de-overlapping cannot separate those — by line they are all
            // "already emitted" — so they are stitched on their text instead. Without
            // this, get_context on a minified file returned only its first chunk.
            if (p.StartLine == p.EndLine && (p.StartLine, p.EndLine) == lastRange)
            {
                AppendWithoutRepeating(sb, p.Content);
                continue;
            }

            // Wholly inside what has already been emitted.
            if (p.EndLine <= emittedThrough) continue;

            // Chunks from one pass tile the file, so a gap here means the index really is
            // missing those lines. Butting the two ends together would hand a model code
            // that reads as contiguous and is not — the kind of wrong it cannot detect —
            // so it is disclosed instead. This fired on chunks left behind by an older
            // chunker, which is how that staleness was found at all.
            if (emittedThrough > 0 && p.StartLine > emittedThrough + 1)
                sb.Append($"\n… lines {emittedThrough + 1}-{p.StartLine - 1} not indexed …\n\n");

            // Drop the leading lines shared with the previous chunk. Overlap is configured
            // in characters, not lines, so the shared span is derived from the line
            // numbers rather than assumed from the setting.
            var skip = Math.Max(0, emittedThrough - p.StartLine + 1);
            foreach (var line in p.Content.Split('\n').Skip(skip)) sb.Append(line).Append('\n');

            emittedThrough = Math.Max(emittedThrough, p.EndLine);
            lastRange = (p.StartLine, p.EndLine);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Appends <paramref name="piece"/>, dropping any prefix already present at the end of
    /// the buffer. Two slices of one line overlap by the configured amount, which this
    /// cannot know — so it measures the repeat instead of assuming it.
    /// </summary>
    private static void AppendWithoutRepeating(StringBuilder sb, string piece)
    {
        // These slices are all one line, so the newline the previous piece ended with does
        // not belong between them — and leaving it there would also block every match,
        // since no chunk's text begins with the end of the last one plus a newline.
        while (sb.Length > 0 && sb[^1] == '\n') sb.Length--;

        // Bounded window: an overlap is a fraction of a chunk, and scanning the whole
        // buffer for each piece would be quadratic on a file that is one very long line.
        var window = Math.Min(Math.Min(sb.Length, piece.Length), 8192);
        if (window == 0) { sb.Append(piece).Append('\n'); return; }

        var tail = sb.ToString(sb.Length - window, window);
        var overlap = LongestPrefixThatIsAlsoASuffix(piece[..window], tail);

        sb.Append(piece.AsSpan(overlap)).Append('\n');
    }

    /// <summary>
    /// The length of the longest prefix of <paramref name="prefixOf"/> that is also a
    /// suffix of <paramref name="suffixOf"/> — the classic KMP failure function over
    /// <c>prefixOf + sentinel + suffixOf</c>, which gets the answer in one linear pass
    /// rather than testing every candidate length.
    /// </summary>
    private static int LongestPrefixThatIsAlsoASuffix(string prefixOf, string suffixOf)
    {
        // '￿' is a permanent noncharacter, so it cannot appear in either input and
        // cannot let a match run across the join.
        var n = prefixOf.Length + 1 + suffixOf.Length;
        var failure = new int[n];
        var k = 0;

        for (var i = 1; i < n; i++)
        {
            var c = CharAt(i);
            while (k > 0 && c != CharAt(k)) k = failure[k - 1];
            if (c == CharAt(k)) k++;
            failure[i] = k;
        }

        return failure[n - 1];

        char CharAt(int i) =>
            i < prefixOf.Length ? prefixOf[i]
            : i == prefixOf.Length ? '￿'
            : suffixOf[i - prefixOf.Length - 1];
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
        try { target = await scopes.ResolveWritableAsync(rc.RequireTenant(), corpus, ct); }
        catch (ScopeResolutionException ex) { throw new McpException(ex.Message); }

        var job = await queue.EnqueueAsync(target.Id, full ? JobKind.Full : JobKind.Refresh, ct);

        // Never blocks: indexing a large repository outlasts any sensible tool timeout.
        return $"Queued {(full ? "full" : "incremental")} reindex of '{target.Name}' as job {job.Id} " +
               $"(state: {job.State.ToString().ToLowerInvariant()}). Poll index_status for progress.";
    }

    [McpServerTool(Name = "index_status")]
    [Description("Report indexing state, counts and last error. The honest answer to 'why did search return nothing'.")]
    public static async Task<string> IndexStatusAsync(
        RequestContext rc,
        ScopeResolver scopes,
        CatalogDbContext db,
        [Description("Corpus name. Omit for every corpus you can see.")] string? corpus = null,
        CancellationToken ct = default)
    {
        Require(rc, Scopes.Search);
        var tenant = rc.RequireTenant();

        List<Corpus> targets;
        if (string.IsNullOrWhiteSpace(corpus))
        {
            targets = await scopes.VisibleAsync(tenant, ct);
        }
        else
        {
            try { targets = (await scopes.ResolveReadableAsync(tenant, [corpus], ct)).Corpora.ToList(); }
            catch (ScopeResolutionException ex) { throw new McpException(ex.Message); }
        }

        if (targets.Count == 0) return $"No corpora visible to tenant '{tenant}'.";

        var sb = new StringBuilder();
        foreach (var c in targets)
        {
            var summary = await CorpusEndpoints.Summarise(db, c, tenant, ct);
            // By QueuedUtc, not StartedUtc: a job that has been QUEUED but not yet started
            // is the latest news about this corpus, and ordering on StartedUtc reported
            // the previous job instead -- so index_status said "succeeded" to an agent
            // whose reindex was still sitting in the queue.
            var job = await db.Jobs.Where(j => j.CorpusId == c.Id)
                .OrderByDescending(j => j.QueuedUtc).ThenByDescending(j => j.Id)
                .FirstOrDefaultAsync(ct);

            sb.Append($"{c.Name}: {summary.State}\n");
            sb.Append($"  {summary.FileCount:N0} files indexed, {summary.ChunkCount:N0} chunks");
            if (summary.SkippedCount > 0) sb.Append($", {summary.SkippedCount:N0} skipped");
            if (summary.FailedCount > 0) sb.Append($", {summary.FailedCount:N0} failed");
            sb.Append('\n');
            sb.Append($"  model: {c.EmbeddingModel} ({c.EmbeddingDimensions}d)\n");
            sb.Append($"  last indexed: {(c.LastIndexedUtc is { } t ? $"{t:u}" : "never")}\n");

            if (job is not null)
            {
                sb.Append($"  latest job {job.Id}: {job.State.ToString().ToLowerInvariant()}");
                if (job.Phase is { Length: > 0 }) sb.Append($" ({job.Phase})");
                if (job.State == JobState.Running && job.FilesTotal > 0)
                {
                    var pct = 100.0 * (job.FilesDone + job.FilesSkipped + job.FilesFailed) / job.FilesTotal;
                    sb.Append($" — {pct:F0}% ({job.FilesDone + job.FilesSkipped + job.FilesFailed:N0} / {job.FilesTotal:N0} files)");
                }
                sb.Append('\n');
                if (job.Error is { Length: > 0 }) sb.Append($"  error: {job.Error}\n");
            }
            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static void Require(RequestContext rc, string scope)
    {
        var principal = rc.Principal
            ?? throw new McpException("Not authenticated. Add an Authorization: Bearer dex_… header to the MCP server configuration.");

        if (!principal.Has(scope))
            throw new McpException(
                $"This token has scopes [{string.Join(", ", principal.Scopes)}] and needs '{scope}'. " +
                "Issue a token with that scope in the Dexicon UI.");
    }
}
