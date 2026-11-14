using System.Diagnostics;
using System.Globalization;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Embedding;
using Dexicon.Core.Indexing;
using Dexicon.Core.Vectors;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Dexicon.Core.Search;

public sealed record SearchRequest
{
    public required string Query { get; init; }
    public IReadOnlyList<string>? Corpus { get; init; }
    public SearchMode Mode { get; init; } = SearchMode.Hybrid;
    public int Limit { get; init; } = 10;
    public string? PathPrefix { get; init; }
    public string? Language { get; init; }
    public string? Symbol { get; init; }

    /// <summary>
    /// Restrict to chunks in the section of this exact name, such as `message` or `diff` for a git-history
    /// commit, or null for any. See <see cref="SearchQuery.Section"/>.
    /// </summary>
    public string? Section { get; init; }

    /// <summary>A source root path to restrict to, e.g. `manuals/AI`. Null searches them all.</summary>
    public string? Source { get; init; }

    /// <summary>
    /// Characters of each hit to return, centred on what matched. Zero returns whole
    /// chunks, which is what this did before it was measured: five results averaged 40,797
    /// characters, roughly 10,200 tokens, because the chunk is sized for retrieval rather
    /// than for reading.
    /// </summary>
    public int MaxCharsPerHit { get; init; } = DefaultMaxCharsPerHit;

    /// <summary>
    /// Enough to carry the matched passage and its surroundings. At this size, 88% of hits
    /// in the measured corpus have their first matching term inside the window, and it is
    /// centred on the match rather than taken from the head, which is what makes the rest
    /// land too.
    /// </summary>
    public const int DefaultMaxCharsPerHit = 1500;

    /// <summary>
    /// Collapse hits that are the same document in another format. Off returns both, which
    /// is what comparing two extractors on one title needs.
    /// </summary>
    public bool DistinctTitles { get; init; } = true;

    /// <summary>
    /// Words every hit's text must contain, in this order, ignoring case and punctuation
    /// (<see cref="SearchPhrase"/> has the rules). Null or blank searches without one. When
    /// <see cref="Query"/> is blank the phrase is the query. It narrows the candidates
    /// before ranking and nothing is stored for it, so a search that leaves it out is the
    /// same query it always was.
    /// </summary>
    public string? Phrase { get; init; }
}

public sealed record SearchResult
{
    public required string Query { get; init; }

    /// <summary>The phrase every hit contains, as searched: whitespace collapsed. Null for a search without one.</summary>
    public string? Phrase { get; init; }

    public required SearchMode Mode { get; init; }
    public bool Degraded { get; init; }
    public string? DegradedReason { get; init; }
    public required IReadOnlyList<ScopeEntry> Scope { get; init; }
    public required IReadOnlyList<SearchHit> Hits { get; init; }
    public long TookMs { get; init; }

    /// <summary>Set when a corpus in scope is mid-index, so incomplete results are not read as absence.</summary>
    public string? Note { get; init; }

    public sealed record ScopeEntry(string Id, string Name, CorpusState State);
}

public sealed class SearchService(
    ScopeResolver scopes,
    IVectorStore vectors,
    IEmbeddingService embedder,
    IMemoryCache cache,
    ILogger<SearchService> log)
{
    private static readonly TimeSpan QueryEmbeddingTtl = TimeSpan.FromMinutes(5);

    public async Task<SearchResult> SearchAsync(
        Principal principal, SearchRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // Before anything is read: a phrase that cannot match is refused, not searched.
        var phrase = SearchPhrase.Create(request.Phrase);

        // A caller who has only the phrase leaves the query blank. It is embedded and
        // encoded as the query, and echoed in the result as the query that ran.
        var queryText = phrase is not null && string.IsNullOrWhiteSpace(request.Query)
            ? phrase.Text
            : request.Query;

        var scope = await scopes.ResolveReadableAsync(principal, request.Corpus, ct);

        // Belt and braces with the repository guard: if resolution ever returned empty
        // without throwing, this stops it becoming a search over everything.
        if (scope.Corpora.Count == 0)
            throw new ScopeResolutionException($"Resolved scope for key '{principal.Name}' was empty.", []);

        // Read now, before any vector is queried, so the note describes the index the hits came
        // from. A job that finishes while the query runs would otherwise clear a warning that
        // the results it returned were incomplete. One that starts meanwhile is not reported:
        // the query had already run against what was there.
        var activity = await scopes.IndexingAsync(scope.Ids, ct);

        // Qualified, so a result from a non-default set says which set it came from.
        var byId = scope.Targets.ToDictionary(t => t.Corpus.Id, t => t.QualifiedName, StringComparer.Ordinal);
        var sparse = SparseEncoder.Encode(queryText);

        // Resolved once, against the scope already authorised above, so naming a source
        // can only ever narrow the search, never reach into a corpus the caller cannot see.
        IReadOnlyList<string>? sourceIds = null;
        if (!string.IsNullOrWhiteSpace(request.Source))
            sourceIds = await scopes.SourceIdsAsync(
                [.. scope.Corpora.Select(c => c.Id)], request.Source, ct);

        // A file_path is relative to its source root, so two sources of one corpus can
        // return the same path for different files. Without the folder, those results are
        // indistinguishable, and one of them is the wrong answer to whatever was asked.
        var sourceRoots = await scopes.SourceRootsAsync([.. scope.Corpora.Select(c => c.Id)], ct);

        var hits = new List<SearchHit>();
        var degraded = false;
        string? degradedReason = null;

        // A collection is one vector space, so a scope spanning two embedding models is
        // two queries. Merged by score afterwards, which is approximate across models.
        // The alternative is refusing, and refusing a legitimate cross-corpus search is
        // worse than an approximate merge that is documented as such.
        var groups = 0;
        var degradedGroups = 0;
        foreach (var group in scope.ByCollection)
        {
            groups++;
            var corpusIds = group.Select(c => c.Id).Distinct(StringComparer.Ordinal).ToList();
            var chunkSetIds = group.Select(c => c.Set.Id).Distinct(StringComparer.Ordinal).ToList();
            var dims = group.First().Set.EmbeddingDimensions;

            float[]? dense = null;
            if (request.Mode is SearchMode.Hybrid or SearchMode.Semantic)
            {
                try
                {
                    dense = await EmbedQueryAsync(queryText, group.First().Set.Target(), ct);
                    if (dense.Length != dims)
                        throw new EmbeddingDimensionMismatchException(group.Key, dims, dense.Length);
                }
                catch (EmbeddingUnavailableException ex)
                {
                    // Degrade to keyword and SAY SO. A silently keyword-only result that
                    // looks like a hybrid one is the failure mode this project exists to
                    // avoid, so it is surfaced in the response, not only in the log.
                    log.LogWarning(ex, "Embedding unavailable; degrading search to keyword-only");
                    degraded = true;
                    degradedGroups++;
                    degradedReason = "embedding service unavailable; keyword-only results";
                }
            }

            var response = await vectors.SearchAsync(new SearchQuery
            {
                Text = queryText,
                CorpusIds = corpusIds,
                ChunkSetIds = chunkSetIds,
                CollectionName = group.Key,
                Mode = request.Mode,
                // Over-fetched when duplicates may be collapsed, so dropping one promotes
                // the next distinct hit instead of returning fewer results than asked for.
                //
                // How far it has to reach depends on how many chunks a document has, and
                // that is decided by the chunk size. Four times the limit starved whichever
                // set was cut finest: measured over six queries asking for ten, a set at
                // 2,065 tokens returned 6.5 and the same corpus at 1,365 returned 3.7,
                // because its 189 chunks per file crowded the candidates with repeats. The
                // multiple is the fix's weak point and it is a heuristic, so it is a
                // generous one and the response still says when it came up short.
                Limit = request.DistinctTitles ? Math.Min(request.Limit * 16, 400) : request.Limit,
                SourceIds = sourceIds,
                PathPrefix = request.PathPrefix,
                Language = request.Language,
                Symbol = request.Symbol,
                Section = request.Section,
                Phrase = phrase?.Text,
            }, dense, sparse, ct);

            if (response.Degraded)
            {
                degraded = true;
                degradedReason ??= response.DegradedReason;
            }

            foreach (var hit in response.Hits)
            {
                hit.CorpusName = byId.TryGetValue(hit.CorpusId, out var name) ? name : hit.CorpusId;
                if (hit.SourceId is { } sid && sourceRoots.TryGetValue(sid, out var root))
                    hit.SourceRoot = root;
                hits.Add(hit);
            }
        }

        // Ordered, then made distinct, then cut to the limit. Dropping a duplicate has to
        // promote the next distinct hit rather than leave a gap, so the limit is applied
        // last; the vector query over-fetches above for the same reason.
        var ranked = hits.OrderByDescending(h => h.Score);

        var ordered = request.DistinctTitles
            ? SearchPresentation.DistinctByTitle(ranked, request.Limit).ToList()
            : ranked.Take(request.Limit).ToList();

        // Collapsing can return fewer results than were asked for, when the corpus simply
        // does not hold that many distinct documents on the subject. Silently short is the
        // one thing that reads as a fault, so it is said.
        var collapsed = request.DistinctTitles && ordered.Count < request.Limit
            && hits.Count > ordered.Count;

        // After ranking, never before: the whole chunk is what was embedded and what
        // scored, and windowing before the merge would rank hits on a preview.
        if (request.MaxCharsPerHit > 0)
            foreach (var hit in ordered)
                hit.Content = SearchPresentation.Window(hit.Content, queryText, request.MaxCharsPerHit, phrase);

        // An agent that searches a half-built index and gets nothing concludes the code
        // does not exist. Telling it the index is incomplete costs one sentence.
        // The SET's state, not the corpus's: a corpus is "indexing" while a replacement
        // set backfills, but the set being searched is complete and its results are not.
        var indexing = scope.Targets
            .Where(t => activity.Of(t.Set) == CorpusState.Indexing)
            .Select(t => t.QualifiedName).ToList();
        var notes = new List<string>();
        if (indexing.Count > 0)
            notes.Add($"Corpus {string.Join(", ", indexing.Select(n => $"'{n}'"))} is still indexing; results are incomplete.");

        // The last outcome of a set whose source was out of reach, or whose files failed to embed. Search keeps
        // answering from what was indexed before, so without this an agent reads month-old results as current.
        // The date is the last pass that completed, which a failed pass does not move.
        var unavailable = scope.Targets.Where(t => activity.Of(t.Set) == CorpusState.Unavailable).ToList();
        if (unavailable.Count > 0)
            notes.Add($"Corpus {Described(unavailable)} could not read its source on its last pass; "
                      + "results are from the index it had before.");

        var failedToEmbed = scope.Targets.Where(t => activity.Of(t.Set) == CorpusState.Degraded).ToList();
        if (failedToEmbed.Count > 0)
            notes.Add($"Corpus {Described(failedToEmbed)} had files that failed to embed on its last pass; "
                      + "they are missing from results until a later pass indexes them.");

        if (collapsed)
            notes.Add($"{ordered.Count} of {request.Limit} asked for: the rest were the same documents again. "
                      + "Pass distinctTitles=false to see every copy.");

        if (TermlessKeywordNote(phrase, sparse.IsEmpty, ordered.Count, request.Mode, groups, degradedGroups) is { } termless)
            notes.Add(termless);

        var note = notes.Count > 0 ? string.Join(" ", notes) : null;

        return new SearchResult
        {
            Query = queryText,
            Phrase = phrase?.Text,
            Mode = degraded ? SearchMode.Keyword : request.Mode,
            Degraded = degraded,
            DegradedReason = degradedReason,
            Scope = scope.Targets
                .Select(t => new SearchResult.ScopeEntry(t.Corpus.Id, t.QualifiedName, activity.Of(t.Set))).ToList(),
            Hits = ordered,
            TookMs = sw.ElapsedMilliseconds,
            Note = note,
        };
    }

    /// <summary>The corpora of a note, each with when its set last finished a pass, or that it never has.</summary>
    private static string Described(IEnumerable<ScopedCorpus> targets) =>
        string.Join(", ", targets.Select(t => string.Create(CultureInfo.InvariantCulture,
            $"'{t.QualifiedName}' (last indexed {(t.Set.LastIndexedUtc is { } at ? $"{at:yyyy-MM-dd HH:mm}Z" : "never")})")));

    /// <summary>
    /// The note for a phrase search that keyword retrieval could not answer. Keyword mode retrieves by the
    /// terms of the query, and a query made only of stop words and single characters has none (the sparse
    /// vector drops them), so it returns nothing whatever the text holds. For a phrase search that reads as
    /// "the phrase is not in the index", which is the one thing it does not mean.
    ///
    /// Only when nothing came back, and for a search that asked for hybrid or semantic only when every
    /// collection fell back to keyword: with two collections, one can lose its embeddings while the other
    /// is searched properly, and its empty answer is a real one.
    /// </summary>
    internal static string? TermlessKeywordNote(SearchPhrase? phrase, bool sparseIsEmpty, int hits,
        SearchMode requested, int groups, int degradedGroups)
    {
        if (phrase is null || !sparseIsEmpty || hits > 0) return null;

        if (requested == SearchMode.Keyword)
            return "Keyword mode retrieves by the words of the query, and this query has none the keyword index "
                   + "holds (stop words and single characters are not indexed), so it can match nothing whatever "
                   + "the text says. Use hybrid or semantic mode to find the phrase.";

        return groups > 0 && degradedGroups == groups
            ? "The embedding service was unavailable, so the search fell back to keyword mode, and this query "
              + "has no word the keyword index holds (stop words and single characters are not indexed). It can "
              + "match nothing whatever the text says. Try again when the embedding service is back."
            : null;
    }

    /// <summary>
    /// The query vector for one MODEL, cached: agents repeat queries far more than people
    /// do, so the cache earns its keep. Keyed by model as well as text: a scope spanning
    /// two chunk sets on different models needs a vector from each, and caching on the
    /// query alone would serve the first model's vector to the second collection: a
    /// comparison between two unrelated vector spaces, whose results are meaningless.
    /// </summary>
    private async Task<float[]> EmbedQueryAsync(string query, EmbeddingTarget target, CancellationToken ct)
    {
        var key = $"qemb::{target}::{query}";
        if (cache.TryGetValue(key, out float[]? cached) && cached is not null) return cached;

        var vector = (await embedder.EmbedAsync(target, EmbedPurpose.Query, [query], ct: ct))[0];
        cache.Set(key, vector, QueryEmbeddingTtl);
        return vector;
    }
}
