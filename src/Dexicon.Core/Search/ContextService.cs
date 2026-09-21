using System.Diagnostics;
using Dexicon.Core.Auth;
using Dexicon.Core.Documents;
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

    /// <summary>
    /// Blocks whose chunk was cut to fit, at most one and always the last. Distinct from
    /// <see cref="Truncated"/>: results dropped and results shortened are different facts,
    /// and a caller can act on each.
    /// </summary>
    public int PartialBlocks { get; init; }

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
public sealed class ContextService(
    SearchService search, ScopeResolver scopes, IVectorStore vectors, DocumentReader documents)
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

        // Unconditionally, including at zero neighbours. Zero used to take the hits as
        // they came, which meant the DEFAULT request never reached the document and got
        // chunk payloads — the shape this endpoint was supposed to stop returning.
        var candidates = await BuildCandidatesAsync(
            principal, request.Corpus, result.Hits, request.Neighbours, ct);

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
            PartialBlocks = assembled.PartialBlocks,
            Note = notes.Any() ? string.Join(" ", notes) : null,
            TookMs = sw.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// What to re-resolve the scope against when expanding: the caller's own corpus list,
    /// or the hits' corpora when the caller named nothing.
    ///
    /// The caller's list carries the <c>corpus:set</c> qualification and a corpus id does
    /// not, so resolving the hits' ids landed on the corpus's DEFAULT set. Asking for
    /// `books:fine` then read the neighbours out of `books`. Nothing failed: a chunk index
    /// means different things in two chunkings, so the window either pulled unrelated text
    /// or, more often, missed the hit's own index and fell back to the hit alone, which
    /// reads as expansion simply doing nothing.
    ///
    /// That is the case chunk sets exist for (D-21): a replacement backfilling on a new
    /// model while the live set keeps serving. Context for the set under evaluation came
    /// from the other one.
    ///
    /// Falling back to the hits' corpora is safe only because a caller who named nothing
    /// got the default sets from the search as well, so the two agree.
    /// </summary>
    internal static IReadOnlyList<string> ScopeForExpansion(
        IReadOnlyList<string>? requested, IReadOnlyList<SearchHit> hits) =>
        requested is { Count: > 0 }
            ? requested
            : [.. hits.Select(h => h.CorpusId).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Adds the chunks either side of each hit, so a passage can be read past the edge of
    /// what matched.
    ///
    /// One read per distinct file, reused across every hit in that file. The scope is
    /// resolved again here rather than carried through the search result, because the
    /// collection and chunk set a file's chunks live in are not part of a search result.
    /// It is resolved from the caller's own corpus list, which carries the `corpus:set`
    /// qualification; resolving the hits' corpus ids instead drops it and lands on the
    /// default set.
    /// </summary>
    private async Task<IReadOnlyList<ContextCandidate>> BuildCandidatesAsync(
        Principal principal, IReadOnlyList<string>? requested, IReadOnlyList<SearchHit> hits,
        int neighbours, CancellationToken ct)
    {
        if (hits.Count == 0) return [];

        var scope = await scopes.ResolveReadableAsync(principal, ScopeForExpansion(requested, hits), ct);
        // One target per corpus, which SearchService assumes as well. Left to throw on a
        // duplicate rather than grouped and picked from: choosing arbitrarily between two
        // sets of one corpus is the same silent-wrong-set failure this change removes.
        var targets = scope.Targets.ToDictionary(t => t.Corpus.Id, StringComparer.Ordinal);

        var files = new Dictionary<(string Corpus, string Path), IReadOnlyList<SearchHit>>();
        var docs = new Dictionary<(string Corpus, string Set, string? Source, string Path), DocumentBody?>();
        var candidates = new List<ContextCandidate>(hits.Count);

        foreach (var hit in hits)
        {
            if (!targets.TryGetValue(hit.CorpusId, out var target))
            {
                candidates.Add(new ContextCandidate(hit, [hit]));
                continue;
            }

            List<SearchHit> pieces;

            if (neighbours == 0)
            {
                // No chunk list needed to know the hit's own span, and fetching one to
                // rediscover the chunk already in hand is a vector read per file for
                // nothing. The document read below still happens, which is the point.
                pieces = [hit];
            }
            else
            {
                var key = (hit.CorpusId, hit.FilePath);
                if (!files.TryGetValue(key, out var chunks))
                {
                    chunks = await vectors.GetFileChunksAsync(
                        target.Set.CollectionName, target.Set.Id, hit.FilePath, ct);
                    files[key] = chunks;
                }

                // Within the one source the hit came from. Two sources of a corpus can
                // hold the same path, and interleaving their chunks stitches two
                // different files into one passage that reads as continuous.
                var sameFile = hit.SourceId is { } source
                    ? chunks.Where(c => c.SourceId == source).ToList()
                    : [.. chunks];

                pieces = sameFile
                    .Where(c => Math.Abs(c.ChunkIndex - hit.ChunkIndex) <= neighbours)
                    .OrderBy(c => c.ChunkIndex)
                    .ToList();

                // A hit whose own chunk did not come back is a hit whose index has moved
                // under the query. What matched is still the honest answer.
                if (!pieces.Exists(p => p.ChunkIndex == hit.ChunkIndex))
                {
                    candidates.Add(new ContextCandidate(hit, [hit]));
                    continue;
                }
            }

            candidates.Add(await FromDocumentAsync(target.Corpus.Id, target.Set.Id, hit, pieces, docs, ct)
                           ?? new ContextCandidate(hit, pieces));
        }

        return candidates;
    }

    /// <summary>
    /// The passage for a hit, read out of the document rather than glued from the chunks
    /// either side of it — which is what `get_context` already does, and what this
    /// endpoint did not.
    ///
    /// The chunks still decide WHERE: the span is their line range, so `neighbours`
    /// keeps its meaning and a caller asking for two either side gets the same width as
    /// before. Only the TEXT changes source. Stitching chunk payloads leaves whatever
    /// the chunker did not cut — a heading skipped by a boundary rule, the gap where an
    /// oversized chunk was split — missing from a passage that reads as continuous.
    ///
    /// Null where there is no document to read, which is the ordinary answer for code
    /// and plain text on a mount: reading those IS the extraction, so nothing is cached
    /// and the chunks are the only copy. The caller falls back to them, as
    /// <c>get_context</c> does, so the two agree on both paths rather than only on one.
    /// </summary>
    internal async Task<ContextCandidate?> FromDocumentAsync(
        string corpusId, string chunkSetId, SearchHit hit, List<SearchHit> pieces,
        Dictionary<(string Corpus, string Set, string? Source, string Path), DocumentBody?>? cache = null,
        CancellationToken ct = default)
    {
        // One read per file, not per hit. Several hits in one book is the ordinary shape
        // of a result, and a document is the whole extracted text — hundreds of
        // thousands of characters for a technical book — so reading it again for each
        // hit is the same load repeated. Misses are cached too: a file with no document
        // must not be looked up once per hit to learn that again.
        var key = (corpusId, chunkSetId, hit.SourceId, hit.FilePath);
        DocumentBody? document;

        if (cache is not null && cache.TryGetValue(key, out var cached)) document = cached;
        else
        {
            document = await documents.ForAsync(corpusId, chunkSetId, hit.FilePath, hit.SourceId, ct);
            if (cache is not null) cache[key] = document;
        }

        if (document is null) return null;

        var lo = pieces.Min(p => p.StartLine);
        var hi = pieces.Max(p => p.EndLine);

        var (text, gotLo, gotHi) = Passage.Window(document.Text, lo, hi);

        // Anything short of the whole span means the document and these chunks were cut
        // from different versions of the file. Window returns what it could reach, so a
        // document ending at line 15 answers a request for 10-20 with 10-15 — a passage
        // shorter than its own citation claims, and quotable. Only the exact span is
        // usable; otherwise the chunks are what the hit's line numbers address.
        if (text.Length == 0 || gotLo != lo || gotHi != hi) return null;

        return new ContextCandidate(hit, [hit with
        {
            StartLine = gotLo,
            EndLine = gotHi,
            Content = text,
        }]);
    }
}
