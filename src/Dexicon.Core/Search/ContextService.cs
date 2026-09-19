using System.Diagnostics;
using Dexicon.Core.Auth;
using Dexicon.Core.Vectors;

namespace Dexicon.Core.Search;

public sealed record ContextRequest
{
    public required string Query { get; init; }
    public IReadOnlyList<string>? Corpus { get; init; }
    public SearchMode Mode { get; init; } = SearchMode.Hybrid;

    /// <summary>How many hits to consider. What reaches the passage is decided by the budget.</summary>
    public int Limit { get; init; } = 10;

    public string? PathPrefix { get; init; }
    public string? Source { get; init; }
    public string? Language { get; init; }
    public string? Symbol { get; init; }

    /// <summary>
    /// The budget for the whole passage. Characters, not tokens: see docs/decisions.md D-29.
    /// </summary>
    public int MaxChars { get; init; } = DefaultMaxChars;

    /// <summary>
    /// Roughly 2,000 tokens at four characters each, which leaves room for a question and
    /// an answer in a small context window. A caller that knows its own budget passes one.
    /// </summary>
    public const int DefaultMaxChars = 8_000;

    /// <summary>
    /// Chunks to include either side of each hit. Zero returns what matched. Above zero
    /// reads the whole file's chunk list per file that has a hit, so it is bounded by the
    /// number of distinct files in the results rather than by this number.
    /// </summary>
    public int Neighbours { get; init; }

    /// <summary>
    /// Prefix each line with its number. Off by default: this passage goes into a prompt,
    /// where the numbers are noise, and the citation already carries the range.
    /// </summary>
    public bool LineNumbers { get; init; }

    public bool DistinctTitles { get; init; } = true;
}

public sealed record ContextResult
{
    public required string Query { get; init; }
    public required SearchMode Mode { get; init; }
    public bool Degraded { get; init; }
    public string? DegradedReason { get; init; }
    public required IReadOnlyList<SearchResult.ScopeEntry> Scope { get; init; }

    /// <summary>The assembled passage. Empty when nothing matched.</summary>
    public required string Context { get; init; }

    public required IReadOnlyList<Citation> Citations { get; init; }
    public int UsedChars { get; init; }
    public int MaxChars { get; init; }

    /// <summary>Set when a hit was left out because the budget was spent.</summary>
    public bool Truncated { get; init; }

    public int DroppedHits { get; init; }

    /// <summary>Why the passage is shorter or emptier than the query suggests it should be.</summary>
    public string? Note { get; init; }

    public long TookMs { get; init; }
}

/// <summary>
/// Retrieval for a caller with no agent loop: a hook, a CI step, a shell script.
///
/// Search answers "which passages are worth reading" and leaves fetching them to a second
/// call, which is the right shape for an agent and costs a round trip per hit for anything
/// else. This runs the search, takes whole chunks rather than previews of them, joins the
/// ones that belong to the same file, and stops at a stated budget.
///
/// See docs/decisions.md D-29.
/// </summary>
public sealed class ContextService(SearchService search, ScopeResolver scopes, IVectorStore vectors)
{
    public async Task<ContextResult> BuildAsync(
        Principal principal, ContextRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        var result = await search.SearchAsync(principal, new SearchRequest
        {
            Query = request.Query,
            Corpus = request.Corpus,
            Mode = request.Mode,
            Limit = request.Limit,
            PathPrefix = request.PathPrefix,
            Source = request.Source,
            Language = request.Language,
            Symbol = request.Symbol,
            DistinctTitles = request.DistinctTitles,

            // Whole chunks. A search window is centred on the matching terms and sized for
            // judging a hit; what goes into a prompt is the passage that was embedded.
            MaxCharsPerHit = 0,
        }, ct);

        var candidates = request.Neighbours > 0
            ? await ExpandAsync(principal, result.Hits, request.Neighbours, ct)
            : [.. result.Hits.Select(h => new ContextCandidate(h, [h]))];

        var assembled = ContextAssembler.Assemble(candidates, request.MaxChars, request.LineNumbers);

        var notes = new[] { result.Note, assembled.Note }.Where(n => n is { Length: > 0 });

        return new ContextResult
        {
            Query = request.Query,
            Mode = result.Mode,
            Degraded = result.Degraded,
            DegradedReason = result.DegradedReason,
            Scope = result.Scope,
            Context = assembled.Text,
            Citations = assembled.Citations,
            UsedChars = assembled.UsedChars,
            MaxChars = request.MaxChars,
            Truncated = assembled.Truncated,
            DroppedHits = assembled.DroppedHits,
            Note = notes.Any() ? string.Join(" ", notes) : null,
            TookMs = sw.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// Adds the chunks either side of each hit, so a passage can be read past the edge of
    /// what matched.
    ///
    /// One read per distinct file, reused across every hit in that file. The scope is
    /// resolved again here rather than carried through the search result, because the
    /// collection and chunk set a file's chunks live in are not part of a search result
    /// and cannot be guessed from the corpus name.
    /// </summary>
    private async Task<IReadOnlyList<ContextCandidate>> ExpandAsync(
        Principal principal, IReadOnlyList<SearchHit> hits, int neighbours, CancellationToken ct)
    {
        if (hits.Count == 0) return [];

        var scope = await scopes.ResolveReadableAsync(
            principal, [.. hits.Select(h => h.CorpusId).Distinct(StringComparer.Ordinal)], ct);
        var targets = scope.Targets.ToDictionary(t => t.Corpus.Id, StringComparer.Ordinal);

        var files = new Dictionary<(string Corpus, string Path), IReadOnlyList<SearchHit>>();
        var candidates = new List<ContextCandidate>(hits.Count);

        foreach (var hit in hits)
        {
            if (!targets.TryGetValue(hit.CorpusId, out var target))
            {
                candidates.Add(new ContextCandidate(hit, [hit]));
                continue;
            }

            var key = (hit.CorpusId, hit.FilePath);
            if (!files.TryGetValue(key, out var chunks))
            {
                chunks = await vectors.GetFileChunksAsync(
                    target.Set.CollectionName, target.Set.Id, hit.FilePath, ct);
                files[key] = chunks;
            }

            // Within the one source the hit came from. Two sources of a corpus can hold
            // the same path, and interleaving their chunks stitches two different files
            // into one passage that reads as continuous.
            var sameFile = hit.SourceId is { } source
                ? chunks.Where(c => c.SourceId == source).ToList()
                : [.. chunks];

            var pieces = sameFile
                .Where(c => Math.Abs(c.ChunkIndex - hit.ChunkIndex) <= neighbours)
                .OrderBy(c => c.ChunkIndex)
                .ToList();

            // A hit whose own chunk did not come back is a hit whose index has moved under
            // the query. What matched is still the honest answer, so it is what is used.
            candidates.Add(pieces.Exists(p => p.ChunkIndex == hit.ChunkIndex)
                ? new ContextCandidate(hit, pieces)
                : new ContextCandidate(hit, [hit]));
        }

        return candidates;
    }
}
