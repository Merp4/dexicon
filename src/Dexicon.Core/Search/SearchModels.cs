namespace Dexicon.Core.Search;

public enum SearchMode { Hybrid = 0, Semantic = 1, Keyword = 2 }

/// <summary>One embedded span of a file — the unit stored in Qdrant and returned by search.</summary>
public sealed record Chunk
{
    public required string CorpusId { get; init; }

    /// <summary>Which chunk set produced this. The corpus stays the tenant key.</summary>
    public required string ChunkSetId { get; init; }

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

    /// <summary>The verbatim text. This is what is stored and returned.</summary>
    public required string Content { get; init; }

    /// <summary>
    /// What to embed, when it differs from <see cref="Content"/> — a chunk set with
    /// heading context embeds the chunk under its heading trail. Empty means "embed the
    /// content", which is the ordinary case.
    /// </summary>
    public string EmbedText { get; init; } = string.Empty;

    /// <summary>The text that actually goes to the embedding model.</summary>
    public string TextToEmbed => EmbedText.Length > 0 ? EmbedText : Content;
}

public sealed record SearchHit
{
    public required string CorpusId { get; init; }
    public string? CorpusName { get; set; }

    /// <summary>
    /// Which source this chunk came from. A file_path is relative to its source root, so
    /// within a corpus it is NOT unique: two sources holding "Logic For Dummies.pdf" are
    /// two different books sharing one path. Null only for points written before this was
    /// read back, which are indistinguishable anyway.
    /// </summary>
    public string? SourceId { get; init; }

    /// <summary>
    /// The root path of the source this came from, e.g. `orly/AI`. Filled in after the
    /// vector query, because the payload stores the id and a person needs the folder.
    /// </summary>
    public string? SourceRoot { get; set; }

    public required string FilePath { get; init; }
    public string? Language { get; init; }
    public int StartLine { get; init; }
    public int EndLine { get; init; }

    /// <summary>
    /// This chunk's position in its file. The only way to order chunks that share a line
    /// number, which happens when the chunker splits a line longer than the whole budget.
    /// It was written to the Qdrant payload from the start and never read back.
    /// </summary>
    public int ChunkIndex { get; init; }

    public int? Page { get; init; }
    public string? Section { get; init; }
    public IReadOnlyList<string> Symbols { get; init; } = [];
    public required string Content { get; init; }
    public float Score { get; init; }

    /// <summary>
    /// `file:line`, or `file#unit=n` for a document — clickable in every editor, and what
    /// an agent pastes back into a conversation.
    /// </summary>
    public string Location => Page is { } p
        ? $"{FilePath}#{UnitAnchor(FilePath)}={p}"
        : StartLine == EndLine ? $"{FilePath}:{StartLine}" : $"{FilePath}:{StartLine}-{EndLine}";

    /// <summary>
    /// The provenance unit's NAME, by format. <see cref="Page"/> holds a unit number
    /// whatever the unit is, and rendering every one of them as "page" put "#page=14" on a
    /// chapter of an EPUB and on slide 14 of a deck. A citation is the part of a search
    /// result a person or a model repeats verbatim, so a confidently wrong one propagates.
    ///
    /// Derived from the extension rather than stored: the information is already here, and
    /// a payload field that can drift out of step with the file it describes is worse than
    /// none. <c>#page=</c> for PDFs is also a real convention — viewers honour it.
    /// </summary>
    private static string UnitAnchor(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".epub" => "chapter",
            ".pptx" or ".ppt" => "slide",
            _ => "page",
        };
}

public sealed record SearchQuery
{
    public required string Text { get; init; }

    /// <summary>
    /// The resolved, authorised corpus ids. Never empty — an empty scope is an error
    /// raised well before this point, not a search over everything.
    /// </summary>
    public required IReadOnlyList<string> CorpusIds { get; init; }

    /// <summary>
    /// The chunk sets to search, one per corpus in scope. A corpus mid-migration holds two
    /// sets in two collections; without this filter a query would see both and return the
    /// same passage twice, scored in two different vector spaces.
    /// </summary>
    public required IReadOnlyList<string> ChunkSetIds { get; init; }

    public required string CollectionName { get; init; }
    public SearchMode Mode { get; init; } = SearchMode.Hybrid;
    public int Limit { get; init; } = 10;
    /// <summary>
    /// Resolved source ids to restrict to, or null for every source in scope.
    ///
    /// The only way to narrow a multi-source corpus by WHERE content came from. PathPrefix
    /// cannot do it: file_path is relative to a source root, so a corpus with a source at
    /// `orly/Architecture` stores its books as bare filenames and no prefix matches the
    /// folder they live in.
    /// </summary>
    public IReadOnlyList<string>? SourceIds { get; init; }

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
