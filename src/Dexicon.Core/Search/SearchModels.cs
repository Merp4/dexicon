namespace Dexicon.Core.Search;

public enum SearchMode { Hybrid = 0, Semantic = 1, Keyword = 2 }

/// <summary>One embedded span of a file — the unit stored in Qdrant and returned by search.</summary>
public sealed record Chunk
{
    public required string CorpusId { get; init; }
    public required string TenantId { get; init; }
    public required string SourceId { get; init; }
    public required string FilePath { get; init; }
    public required string FileHash { get; init; }
    public string? MediaType { get; init; }
    public string? Language { get; init; }
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public int? Page { get; init; }
    public string? Section { get; init; }
    public IReadOnlyList<string> Symbols { get; init; } = [];
    public int ChunkIndex { get; init; }
    public required string Content { get; init; }
}

public sealed record SearchHit
{
    public required string CorpusId { get; init; }
    public string? CorpusName { get; set; }
    public required string FilePath { get; init; }
    public string? Language { get; init; }
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public int? Page { get; init; }
    public string? Section { get; init; }
    public IReadOnlyList<string> Symbols { get; init; } = [];
    public required string Content { get; init; }
    public float Score { get; init; }

    /// <summary>`file:line` — clickable in every editor, and what an agent pastes back.</summary>
    public string Location => Page is { } p
        ? $"{FilePath}#page={p}"
        : StartLine == EndLine ? $"{FilePath}:{StartLine}" : $"{FilePath}:{StartLine}-{EndLine}";
}

public sealed record SearchQuery
{
    public required string Text { get; init; }

    /// <summary>
    /// The resolved, authorised corpus ids. Never empty — an empty scope is an error
    /// raised well before this point, not a search over everything.
    /// </summary>
    public required IReadOnlyList<string> CorpusIds { get; init; }

    public required string CollectionName { get; init; }
    public SearchMode Mode { get; init; } = SearchMode.Hybrid;
    public int Limit { get; init; } = 10;
    public string? PathPrefix { get; init; }
    public string? Language { get; init; }
    public string? Symbol { get; init; }
}

public sealed record SearchResponse
{
    public required IReadOnlyList<SearchHit> Hits { get; init; }
    public SearchMode Mode { get; init; }
    public bool Degraded { get; init; }
    public string? DegradedReason { get; init; }
    public long TookMs { get; init; }
}

/// <summary>
/// Raised when a search reaches the vector store without a corpus scope. This is the
/// repository-level half of the three-layer guard in docs/07-tenancy-auth.md: the
/// application resolves scope and throws on empty, this refuses to issue the query at
/// all, and the storage layout (hnsw m=0) makes an unfiltered query useless anyway.
/// </summary>
public sealed class UnscopedQueryException(string caller)
    : InvalidOperationException(
        $"{caller} attempted a vector query with no corpus_id filter. There is no code path " +
        "that searches every corpus; resolve an authorised scope first. See docs/07-tenancy-auth.md.");

/// <summary>
/// Raised when the embedding model's dimensionality does not match what the collection
/// was built with. Refused rather than degraded — plausible results from a mismatched
/// vector space are worse than none.
/// </summary>
public sealed class EmbeddingDimensionMismatchException(string collection, int expected, int actual)
    : InvalidOperationException(
        $"Collection '{collection}' was indexed with {expected}-dimension vectors but the configured " +
        $"model produces {actual}. Rebuild the corpus with the current model, or restore the original one.");
