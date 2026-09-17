using System.Diagnostics;
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

    /// <summary>A source root path to restrict to, e.g. `orly/AI`. Null searches them all.</summary>
    public string? Source { get; init; }
}

public sealed record SearchResult
{
    public required string Query { get; init; }
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

    public async Task<SearchResult> SearchAsync(string tenantId, SearchRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var scope = await scopes.ResolveReadableAsync(tenantId, request.Corpus, ct);

        // Belt and braces with the repository guard: if resolution ever returned empty
        // without throwing, this stops it becoming a search over everything.
        if (scope.Corpora.Count == 0)
            throw new ScopeResolutionException($"Resolved scope for tenant '{tenantId}' was empty.", []);

        // Qualified, so a result from a non-default set says which set it came from.
        var byId = scope.Targets.ToDictionary(t => t.Corpus.Id, t => t.QualifiedName, StringComparer.Ordinal);
        var sparse = SparseEncoder.Encode(request.Query);

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
        foreach (var group in scope.ByCollection)
        {
            var corpusIds = group.Select(c => c.Id).Distinct(StringComparer.Ordinal).ToList();
            var chunkSetIds = group.Select(c => c.Set.Id).Distinct(StringComparer.Ordinal).ToList();
            var dims = group.First().Set.EmbeddingDimensions;

            float[]? dense = null;
            if (request.Mode is SearchMode.Hybrid or SearchMode.Semantic)
            {
                try
                {
                    dense = await EmbedQueryAsync(request.Query, group.First().Set.Target(), ct);
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
                    degradedReason = "embedding service unavailable; keyword-only results";
                }
            }

            var response = await vectors.SearchAsync(new SearchQuery
            {
                Text = request.Query,
                CorpusIds = corpusIds,
                ChunkSetIds = chunkSetIds,
                CollectionName = group.Key,
                Mode = request.Mode,
                Limit = request.Limit,
                SourceIds = sourceIds,
                PathPrefix = request.PathPrefix,
                Language = request.Language,
                Symbol = request.Symbol,
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

        var ordered = hits.OrderByDescending(h => h.Score).Take(request.Limit).ToList();

        // An agent that searches a half-built index and gets nothing concludes the code
        // does not exist. Telling it the index is incomplete costs one sentence.
        // The SET's state, not the corpus's: a corpus is "indexing" while a replacement
        // set backfills, but the set being searched is complete and its results are not.
        var indexing = scope.Targets
            .Where(t => t.Set.State == CorpusState.Indexing)
            .Select(t => t.QualifiedName).ToList();
        var note = indexing.Count > 0
            ? $"Corpus {string.Join(", ", indexing.Select(n => $"'{n}'"))} is still indexing; results are incomplete."
            : null;

        return new SearchResult
        {
            Query = request.Query,
            Mode = degraded ? SearchMode.Keyword : request.Mode,
            Degraded = degraded,
            DegradedReason = degradedReason,
            Scope = scope.Targets
                .Select(t => new SearchResult.ScopeEntry(t.Corpus.Id, t.QualifiedName, t.Set.State)).ToList(),
            Hits = ordered,
            TookMs = sw.ElapsedMilliseconds,
            Note = note,
        };
    }

    /// <summary>Agents repeat queries far more than people do, so this cache earns its keep.</summary>
    /// <summary>
    /// The query vector for one MODEL. Keyed by model as well as text: a scope spanning
    /// two chunk sets on different models needs a vector from each, and caching on the
    /// query alone would serve the first model's vector to the second collection: a
    /// comparison between two unrelated vector spaces, whose results are meaningless.
    /// </summary>
    private async Task<float[]> EmbedQueryAsync(string query, EmbeddingTarget target, CancellationToken ct)
    {
        var key = $"qemb::{target}::{query}";
        if (cache.TryGetValue(key, out float[]? cached) && cached is not null) return cached;

        var vector = (await embedder.EmbedAsync(target, EmbedPurpose.Query, [query], ct))[0];
        cache.Set(key, vector, QueryEmbeddingTtl);
        return vector;
    }
}
