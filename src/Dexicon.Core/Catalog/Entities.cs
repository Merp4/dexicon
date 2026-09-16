namespace Dexicon.Core.Catalog;

/// <summary>The isolation boundary. Every request resolves to exactly one.</summary>
public sealed class Tenant
{
    public required string Id { get; set; }              // slug
    public required string DisplayName { get; set; }
    public DateTime CreatedUtc { get; set; }
    public bool Disabled { get; set; }

    public List<Corpus> Corpora { get; set; } = [];
    public List<ApiToken> Tokens { get; set; } = [];
}

/// <summary>
/// The only credential. Stored as PBKDF2-HMAC-SHA256 with a per-token salt; the
/// secret itself is shown once at creation and has no retrieval path.
/// </summary>
public sealed class ApiToken
{
    public required string Id { get; set; }              // public id, appears in the token
    public required string Name { get; set; }
    public required byte[] TokenHash { get; set; }
    public required byte[] TokenSalt { get; set; }
    public required string TenantId { get; set; }
    public Tenant? Tenant { get; set; }
    public required string Scopes { get; set; }          // csv: search, ingest, admin
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastUsedUtc { get; set; }
    public DateTime? ExpiresUtc { get; set; }
    public DateTime? RevokedUtc { get; set; }

    public bool IsActive(DateTime nowUtc) =>
        RevokedUtc is null && (ExpiresUtc is null || ExpiresUtc > nowUtc);
}

public enum CorpusVisibility { Private = 0, Shared = 1 }

public enum CorpusState { Ready = 0, Indexing = 1, Degraded = 2, Unavailable = 3 }

/// <summary>
/// A named, searchable body of content owned by one tenant. The unit of visibility,
/// of reindexing, and of search scope — and the <c>is_tenant</c> key in Qdrant.
/// </summary>
public sealed class Corpus
{
    public required string Id { get; set; }              // ULID
    public required string TenantId { get; set; }
    public Tenant? Tenant { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public CorpusVisibility Visibility { get; set; }

    /// <summary>Pinned at creation. Changing it is a rebuild, never an edit.</summary>
    public required string EmbeddingModel { get; set; }
    public int EmbeddingDimensions { get; set; }
    public required string CollectionName { get; set; }

    public int ChunkSize { get; set; }
    public int ChunkOverlap { get; set; }
    public required string BoundaryMode { get; set; }
    public CorpusState State { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastIndexedUtc { get; set; }

    public List<Source> Sources { get; set; } = [];
    public List<CorpusGrant> Grants { get; set; } = [];
    public List<IndexJob> Jobs { get; set; } = [];
}

/// <summary>
/// Explicit read grants. A <see cref="CorpusVisibility.Shared"/> corpus with no rows
/// is readable by every tenant; with rows, only by those listed. Private ignores this.
/// </summary>
public sealed class CorpusGrant
{
    public required string CorpusId { get; set; }
    public Corpus? Corpus { get; set; }
    public required string TenantId { get; set; }
    public Tenant? Tenant { get; set; }
}

public enum SourceKind { Workspace = 0, Upload = 1 }

public sealed class Source
{
    public required string Id { get; set; }
    public required string CorpusId { get; set; }
    public Corpus? Corpus { get; set; }
    public SourceKind Kind { get; set; }

    /// <summary>Workspace sources only: path relative to the workspace root.</summary>
    public string? RootPath { get; set; }

    public string? IncludeGlobs { get; set; }            // json array
    public string? ExcludeGlobs { get; set; }            // json array
    public bool UseGitignore { get; set; } = true;
    public int MaxFileBytes { get; set; } = 262_144;
    public DateTime CreatedUtc { get; set; }

    public List<IndexedFile> Files { get; set; } = [];
}

public enum FileStatus { Indexed = 0, Skipped = 1, Failed = 2, Empty = 3 }

public sealed class IndexedFile
{
    public required string Id { get; set; }
    public required string SourceId { get; set; }
    public Source? Source { get; set; }

    /// <summary>Forward slashes, always, relative to the source root. Never absolute.</summary>
    public required string RelativePath { get; set; }

    /// <summary>Null until the file has been indexed successfully — that is what makes a failure retry.</summary>
    public string? ContentHash { get; set; }

    public long SizeBytes { get; set; }
    public string? MediaType { get; set; }
    public string? Language { get; set; }
    public int ChunkCount { get; set; }
    public int ExtractedChars { get; set; }
    public FileStatus Status { get; set; }

    /// <summary>Why, in words. The thing the UI shows for skipped/failed/empty.</summary>
    public string? StatusDetail { get; set; }

    public DateTime? IndexedUtc { get; set; }
}

public sealed class Blob
{
    public required string Sha256 { get; set; }
    public long SizeBytes { get; set; }
    public string? MediaType { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public enum JobKind { Full = 0, Refresh = 1, Rebuild = 2, Delete = 3 }

public enum JobState { Queued = 0, Running = 1, Succeeded = 2, Failed = 3, Degraded = 4, Cancelled = 5 }

public sealed class IndexJob
{
    public required string Id { get; set; }
    public required string CorpusId { get; set; }
    public Corpus? Corpus { get; set; }
    public JobKind Kind { get; set; }
    public JobState State { get; set; }

    /// <summary>discover | extract | embed | upsert | reconcile</summary>
    public string? Phase { get; set; }

    public int FilesTotal { get; set; }
    public int FilesDone { get; set; }
    public int FilesSkipped { get; set; }
    public int FilesFailed { get; set; }
    public int ChunksWritten { get; set; }
    public string? Error { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
}
