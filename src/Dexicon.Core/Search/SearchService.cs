using System.Diagnostics;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Embedding;
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
    IEmbeddingProvider embedder,
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

        var byId = scope.Corpora.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var sparse = SparseEncoder.Encode(request.Query);

        var hits = new List<SearchHit>();
        var degraded = false;
        string? degradedReason = null;

        // A collection is one vector space, so a scope spanning two embedding models is
        // two queries. Merged by score afterwards, which is approximate across models —
        // the honest alternative is refusing, and refusing a legitimate cross-corpus
        // search is worse than an approximate merge that is documented as such.
        foreach (var group in scope.ByCollection)
        {
            var corpusIds = group.Select(c => c.Id).ToList();
            var dims = group.First().EmbeddingDimensions;

            float[]? dense = null;
            if (request.Mode is SearchMode.Hybrid or SearchMode.Semantic)
            {
                try
                {
                    dense = await EmbedQueryAsync(request.Query, ct);
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
                    degradedReason = "embedding service unavailable — keyword-only results";
                }
            }

            var response = await vectors.SearchAsync(new SearchQuery
            {
                Text = request.Query,
                CorpusIds = corpusIds,
                CollectionName = group.Key,
                Mode = request.Mode,
                Limit = request.Limit,
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
                hit.CorpusName = byId.TryGetValue(hit.CorpusId, out var c) ? c.Name : hit.CorpusId;
                hits.Add(hit);
            }
        }

        var ordered = hits.OrderByDescending(h => h.Score).Take(request.Limit).ToList();

        // An agent that searches a half-built index and gets nothing concludes the code
        // does not exist. Telling it the index is incomplete costs one sentence.
        var indexing = scope.Corpora.Where(c => c.State == CorpusState.Indexing).Select(c => c.Name).ToList();
        var note = indexing.Count > 0
            ? $"Corpus {string.Join(", ", indexing.Select(n => $"'{n}'"))} is still indexing; results are incomplete."
            : null;

        return new SearchResult
        {
            Query = request.Query,
            Mode = degraded ? SearchMode.Keyword : request.Mode,
            Degraded = degraded,
            DegradedReason = degradedReason,
            Scope = scope.Corpora.Select(c => new SearchResult.ScopeEntry(c.Id, c.Name, c.State)).ToList(),
            Hits = ordered,
            TookMs = sw.ElapsedMilliseconds,
            Note = note,
        };
    }

    /// <summary>Agents repeat queries far more than people do, so this cache earns its keep.</summary>
    private async Task<float[]> EmbedQueryAsync(string query, CancellationToken ct)
    {
        var key = $"qemb::{embedder.Model}::{query}";
        if (cache.TryGetValue(key, out float[]? cached) && cached is not null) return cached;

        var vector = (await embedder.EmbedAsync([query], ct))[0];
        cache.Set(key, vector, QueryEmbeddingTtl);
        return vector;
    }
}
