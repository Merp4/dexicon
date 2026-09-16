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

    public CorpusState State { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastIndexedUtc { get; set; }

    public List<Source> Sources { get; set; } = [];
    public List<CorpusGrant> Grants { get; set; } = [];
    public List<IndexJob> Jobs { get; set; } = [];

    /// <summary>
    /// How this corpus's content is cut and embedded — one entry per variation. The
    /// embedding model and chunk settings used to live on the corpus itself, which made
    /// them a property of the CONTENT rather than of a way of reading it.
    /// </summary>
    public List<ChunkSet> ChunkSets { get; set; } = [];
}

/// <summary>
/// One way of cutting and embedding a corpus's content: a model, a vector space, and a
/// chunking strategy. A corpus can carry several, over exactly the same documents.
///
/// This is what makes a model change safe. The collection name encodes the model and its
/// dimensionality, so switching models means writing into a different vector space —
/// measured at roughly twenty minutes for a modest book corpus on CPU Ollama. With one
/// configuration per corpus, that is twenty minutes of half-populated results. With
/// several, the new set is built alongside the old one, promoted when it is complete, and
/// the old one dropped: search never sees a partial index.
///
/// It is also the honest home for "the same document, chunked two ways". That worked
/// before only by duplicating the corpus, which duplicated its grants and its sources
/// along with it.
/// </summary>
public sealed class ChunkSet
{
    public required string Id { get; set; }              // ULID
    public required string CorpusId { get; set; }
    public Corpus? Corpus { get; set; }

    /// <summary>Unique within the corpus. Addressable from search as `corpus:set`.</summary>
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// Which configured backend embeds this set — <c>ollama</c>, <c>openai</c>, an Azure
    /// deployment. The NAME of a provider, resolved against configuration at use; the
    /// credentials for it never touch the catalogue.
    /// </summary>
    public string EmbeddingProvider { get; set; } = "ollama";

    /// <summary>
    /// The vector space. Pinned per set: changing a set's model in place would strand its
    /// existing vectors in a collection nothing addresses any more, so the model is
    /// changed by building a NEW set and promoting it.
    /// </summary>
    public required string EmbeddingModel { get; set; }
    public int EmbeddingDimensions { get; set; }
    public required string CollectionName { get; set; }

    public int ChunkSize { get; set; }
    public int ChunkOverlap { get; set; }
    public required string BoundaryMode { get; set; }

    /// <summary>Required when <see cref="BoundaryMode"/> is <c>custom</c>; ignored otherwise.</summary>
    public string? CustomBoundaryPattern { get; set; }

    /// <summary>
    /// Prefer the document's own structure — page, chapter, slide — as a chunk boundary.
    /// The offsets already exist for citations; this feeds them into chunking too.
    /// </summary>
    public bool UnitAware { get; set; }

    /// <summary>Cut at a sentence rather than a line when a split lands mid-paragraph.</summary>
    public bool SentenceAware { get; set; }

    /// <summary>
    /// Prepend the heading trail to each chunk's EMBEDDED text, so a chunk carries the
    /// context it was found under rather than floating free of it.
    /// </summary>
    public bool HeadingContext { get; set; }

    /// <summary>The set search uses when none is named. Exactly one per corpus.</summary>
    public bool IsDefault { get; set; }

    public CorpusState State { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastIndexedUtc { get; set; }

    public List<FileChunkState> Files { get; set; } = [];
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

/// <summary>
/// Persisted by NAME, so these numbers are free to change — but the ORDER matters:
/// Pending must be the zero value. It used to be Indexed, which meant a file was born
/// claiming to be indexed and the library listed freshly uploaded documents as
/// "indexed — 0 chunks". Defaulting to the pessimistic state makes a missed
/// assignment show up as work outstanding rather than as work falsely complete.
/// </summary>
public enum FileStatus { Pending = 0, Indexed = 1, Skipped = 2, Failed = 3, Empty = 4 }

public sealed class IndexedFile
{
    public required string Id { get; set; }
    public required string SourceId { get; set; }
    public Source? Source { get; set; }

    /// <summary>Forward slashes, always, relative to the source root. Never absolute.</summary>
    public required string RelativePath { get; set; }

    /// <summary>
    /// Set for upload-sourced files: the content-addressed blob this is an attachment
    /// of. Several corpora can point at the same blob and chunk it differently; that is
    /// the whole point of keeping bytes and chunking apart.
    /// </summary>
    public string? BlobSha256 { get; set; }
    public Blob? Blob { get; set; }

    public long SizeBytes { get; set; }
    public string? MediaType { get; set; }
    public string? Language { get; set; }
    public int ExtractedChars { get; set; }

    /// <summary>
    /// Indexing state, one row per chunk set. It used to live on this entity, which
    /// quietly asserted that a file has ONE chunking — true only while a corpus had one
    /// configuration. A hash, a chunk count and a status are properties of a file *as cut
    /// by a particular set*, not of the attachment.
    /// </summary>
    public List<FileChunkState> ChunkStates { get; set; } = [];
}

/// <summary>
/// One file as seen by one chunk set: whether it is indexed, under what fingerprint, and
/// into how many chunks. The same document in the same corpus can be freshly indexed in
/// one set and still pending in another.
/// </summary>
public sealed class FileChunkState
{
    public required string FileId { get; set; }
    public IndexedFile? File { get; set; }
    public required string ChunkSetId { get; set; }
    public ChunkSet? ChunkSet { get; set; }

    /// <summary>
    /// The chunking fingerprint. Null until this set has indexed this file successfully —
    /// that is what makes a failure retry rather than being skipped as up to date.
    /// </summary>
    public string? ContentHash { get; set; }

    public int ChunkCount { get; set; }
    public FileStatus Status { get; set; }

    /// <summary>Why, in words. The thing the UI shows for skipped/failed/empty.</summary>
    public string? StatusDetail { get; set; }

    public DateTime? IndexedUtc { get; set; }
}

/// <summary>
/// An uploaded document's bytes, content-addressed. Two uploads of the same file are
/// one blob, and the blob carries no name — the same PDF can be attached to different
/// corpora under different names, so the name belongs to the attachment.
/// </summary>
public sealed class Blob
{
    public required string Sha256 { get; set; }
    public long SizeBytes { get; set; }
    public string? MediaType { get; set; }

    /// <summary>The name it was first uploaded under. Display only; the attachment owns the real name.</summary>
    public string? OriginalFileName { get; set; }

    public DateTime CreatedUtc { get; set; }

    public BlobText? Text { get; set; }
}

/// <summary>
/// Extracted text for a blob, cached. This is what makes re-chunking cheap and what
/// lets the SAME document be chunked differently per corpus.
///
/// Extraction is deterministic in the bytes and expensive — a 437-page PDF costs about
/// 1.5 s of layout analysis. Chunking is cheap and corpus-specific. Splitting them means
/// changing a corpus's chunk size, or attaching a document to a second corpus with
/// different settings, re-chunks and re-embeds without ever re-opening the PDF.
/// </summary>
public sealed class BlobText
{
    public required string Sha256 { get; set; }
    public Blob? Blob { get; set; }

    public required string Text { get; set; }

    /// <summary>JSON array of extraction units — page/slide/chapter offsets for provenance.</summary>
    public string? UnitsJson { get; set; }

    public string? Title { get; set; }
    public int ExtractedChars { get; set; }

    /// <summary>Which extractor produced this.</summary>
    public required string Extractor { get; set; }

    /// <summary>
    /// <c>ExtractorVersions.Current</c> when this text was produced. Anything older is
    /// re-extracted on next use — this is what makes an extractor fix reach documents
    /// that were ingested before it.
    /// </summary>
    public int ExtractorVersion { get; set; }

    public DateTime ExtractedUtc { get; set; }

    /// <summary>Set when the format was readable but yielded nothing — a scanned PDF.</summary>
    public string? EmptyReason { get; set; }
}

/// <summary>
/// Persisted by NAME, so the numbers are free to change.
///
/// <c>Rebuild</c> is a full pass targeting ONE chunk set: the backfill that builds a
/// replacement while the live set keeps serving. It behaves like <c>Full</c> and is named
/// separately so the jobs list can tell "backfilling a new set" from "re-indexing
/// everything" — two very different reasons for a corpus to be busy.
///
/// A <c>Delete</c> kind existed and was never read or written: deletes are synchronous,
/// because a delete that is queued behind an hour of indexing is a delete that has not
/// happened.
/// </summary>
public enum JobKind { Full = 0, Refresh = 1, Rebuild = 2 }

public enum JobState { Queued = 0, Running = 1, Succeeded = 2, Failed = 3, Degraded = 4, Cancelled = 5 }

public sealed class IndexJob
{
    public required string Id { get; set; }
    public required string CorpusId { get; set; }
    public Corpus? Corpus { get; set; }

    /// <summary>
    /// The one chunk set this job targets, or null for every set in the corpus. Naming a
    /// set is what lets a new one be backfilled while the live set keeps serving search.
    /// </summary>
    public string? ChunkSetId { get; set; }
    public ChunkSet? ChunkSet { get; set; }

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

    /// <summary>
    /// When the job was queued. Never null, which is the point: ordering on StartedUtc
    /// put every job that never started -- including ones that FAILED before starting --
    /// permanently at the top of the list, above whatever is actually running.
    /// </summary>
    public DateTime QueuedUtc { get; set; }

    public DateTime? StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
}
