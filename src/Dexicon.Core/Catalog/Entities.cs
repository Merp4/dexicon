namespace Dexicon.Core.Catalog;

/// <summary>
/// An agent's credential. Stored as PBKDF2-HMAC-SHA256 with a per-token salt; the secret
/// itself is shown once at creation and has no retrieval path.
///
/// A key carries <c>search</c> and optionally <c>ingest</c>, never <c>admin</c>. Admin
/// comes from the password alone, because an agent's configuration file is the wrong
/// place to keep a credential that can delete a corpus. See docs/decisions.md D-28.
/// </summary>
public sealed class ApiToken
{
    public required string Id { get; set; }              // public id, appears in the token
    public required string Name { get; set; }
    public required byte[] TokenHash { get; set; }
    public required byte[] TokenSalt { get; set; }
    public required string Scopes { get; set; }          // csv: search, ingest
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastUsedUtc { get; set; }
    public DateTime? ExpiresUtc { get; set; }
    public DateTime? RevokedUtc { get; set; }

    /// <summary>
    /// The corpora this key may reach. No rows means every corpus, which is what a
    /// single-user install wants and what keeps the mapping optional rather than a step in
    /// issuing a key. A key that should reach nothing is revoked, not emptied.
    ///
    /// Resolved per request rather than carried on the cached principal: the principal
    /// cache has a 60-second TTL, and caching the mapping there would make that TTL decide
    /// how long a change in the UI took to reach the agent.
    /// </summary>
    public List<TokenCorpus> Corpora { get; set; } = [];

    public bool IsActive(DateTime nowUtc) =>
        RevokedUtc is null && (ExpiresUtc is null || ExpiresUtc > nowUtc);
}

/// <summary>
/// One corpus a key may reach. Rows are added and removed in the UI while everything
/// keeps running; the next call the agent makes sees the new set.
/// </summary>
public sealed class TokenCorpus
{
    public required string TokenId { get; set; }
    public ApiToken? Token { get; set; }
    public required string CorpusId { get; set; }
    public Corpus? Corpus { get; set; }
}

/// <summary>
/// The administrator's password, hashed exactly as a token's secret is.
///
/// One row, because there is one administrator. Several would be user accounts, which
/// D-10 rejected and D-28 did not reinstate. The row holds no session: a verified
/// password is exchanged for a short-lived bearer that lives in memory, so nothing
/// durable ever carries the <c>admin</c> scope.
/// </summary>
public sealed class AdminCredential
{
    /// <summary>Always <see cref="SingletonId"/>, so the table can hold exactly one row.</summary>
    public required string Id { get; set; }

    public const string SingletonId = "admin";

    public required byte[] PasswordHash { get; set; }
    public required byte[] PasswordSalt { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public enum CorpusState { Ready = 0, Indexing = 1, Degraded = 2, Unavailable = 3 }

/// <summary>
/// A named, searchable body of content. The unit of reindexing and of search scope, and
/// the <c>is_tenant</c> key in Qdrant.
///
/// It has no owner. Which keys may read it is a property of those keys, held in
/// <see cref="TokenCorpus"/>, so sharing a corpus is listing it against a second key
/// rather than a visibility setting plus a grant table.
/// </summary>
public sealed class Corpus
{
    public required string Id { get; set; }              // ULID
    public required string Name { get; set; }
    public string? Description { get; set; }

    public CorpusState State { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastIndexedUtc { get; set; }

    /// <summary>
    /// Who is working this corpus, and until when. Taken and released by
    /// <see cref="Indexing.CorpusLeases"/>; nothing else writes them.
    ///
    /// Two passes now want a corpus: an index job and a discovery sweep, on separate lanes
    /// (D-32). Reading <see cref="State"/> to decide between them cannot exclude either,
    /// because it is set inside the indexer once a job is already running, so a sweep that
    /// reads it and then starts can be overtaken by a job starting in the gap. A claim is
    /// one conditional update whose row count says whether it was won.
    ///
    /// <see cref="HeldUntilUtc"/> is renewed while the holder runs rather than set to a
    /// guess at how long the work will take. A crashed holder becomes reclaimable once it
    /// stops renewing, and nothing anywhere has to predict how long indexing a library of
    /// this size takes.
    /// </summary>
    public string? HeldBy { get; set; }
    public DateTime? HeldUntilUtc { get; set; }

    /// <summary>
    /// Filters every source of this corpus inherits unless it sets its own. Stored as the
    /// same shapes a source stores, so resolution is a coalesce and nothing has to
    /// translate between two representations.
    ///
    /// Inherited LIVE, not copied when a source is added: a corpus with ten folders under
    /// one parent is the case this exists for, and a default that only applied to the
    /// eleventh would leave the other ten to be edited one at a time, which is the problem.
    /// </summary>
    public string? DefaultIncludeGlobs { get; set; }     // json array
    public string? DefaultExcludeGlobs { get; set; }     // json array
    public bool? DefaultUseGitignore { get; set; }
    public int? DefaultMaxFileBytes { get; set; }

    public List<Source> Sources { get; set; } = [];
    public List<IndexJob> Jobs { get; set; } = [];

    /// <summary>
    /// How this corpus's content is cut and embedded, one entry per variation. The
    /// embedding model and chunk settings used to live on the corpus itself, which made
    /// them a property of the content rather than of a way of reading it.
    /// </summary>
    public List<ChunkSet> ChunkSets { get; set; } = [];
}

/// <summary>
/// One way of cutting and embedding a corpus's content: a model, a vector space, and a
/// chunking strategy. A corpus can carry several, over exactly the same documents.
///
/// This is what makes a model change safe. The collection name encodes the model and its
/// dimensionality, so switching models means writing into a different vector space,
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
    /// Which configured backend embeds this set: <c>ollama</c>, <c>openai</c>, an Azure
    /// deployment. The name of a provider, resolved against configuration at use; the
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
    /// Prefer the document's own structure (page, chapter, slide) as a chunk boundary.
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

public enum SourceKind { Workspace = 0, Upload = 1 }

public sealed class Source
{
    public required string Id { get; set; }
    public required string CorpusId { get; set; }
    public Corpus? Corpus { get; set; }
    public SourceKind Kind { get; set; }

    /// <summary>Workspace sources only: path relative to the workspace root.</summary>
    public string? RootPath { get; set; }

    /// <summary>
    /// This source's own filters, or null to inherit the corpus default.
    ///
    /// Null and empty are different, and the difference is the whole of inheritance: null
    /// is "I have no opinion, use the corpus's", and an empty array is "none, whatever the
    /// corpus says". Resolve through <see cref="Indexing.SourceFilters"/> rather than
    /// reading these directly, or a source will be indexed by its own opinion where it
    /// meant to hold none.
    /// </summary>
    public string? IncludeGlobs { get; set; }            // json array, null = inherit
    public string? ExcludeGlobs { get; set; }            // json array, null = inherit
    public bool? UseGitignore { get; set; }
    public int? MaxFileBytes { get; set; }
    public DateTime CreatedUtc { get; set; }

    public List<IndexedFile> Files { get; set; } = [];
}

/// <summary>
/// Persisted by name, so these numbers are free to change, but the order matters:
/// Pending must be the zero value. It used to be Indexed, which meant a file was born
/// claiming to be indexed and the library listed freshly uploaded documents as
/// "indexed, 0 chunks". Defaulting to the pessimistic state makes a missed
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

    /// <summary>
    /// SHA-256 of the file's bytes as last indexed. Set for workspace files, where it is
    /// the key into <see cref="FileText"/> and therefore the only way to reach the
    /// document whole; for uploads the blob hash already serves that purpose.
    ///
    /// Also what says whether the mount still holds what was indexed. A reader that finds
    /// a different hash on disk is looking at a file that has changed since, which is a
    /// fact worth stating rather than quietly serving either version.
    /// </summary>
    public string? Sha256 { get; set; }

    public long SizeBytes { get; set; }
    public string? MediaType { get; set; }
    public string? Language { get; set; }
    public int ExtractedChars { get; set; }

    /// <summary>
    /// Indexing state, one row per chunk set. It used to live on this entity, which
    /// implicitly asserted that a file has one chunking, true only while a corpus had one
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
    /// The chunking fingerprint. Null until this set has indexed this file successfully,
    /// which is what makes a failure retry rather than be skipped as up to date.
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
/// one blob, and the blob carries no name: the same PDF can be attached to different
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
/// Extraction is deterministic in the bytes and expensive: a 437-page PDF costs about
/// 1.5 s of layout analysis. Chunking is cheap and corpus-specific. Splitting them means
/// changing a corpus's chunk size, or attaching a document to a second corpus with
/// different settings, re-chunks and re-embeds without ever re-opening the PDF.
/// </summary>
/// <summary>
/// Extracted text for a WORKSPACE file, cached against the bytes it came from.
///
/// <see cref="BlobText"/> does this for uploads, and is why "changing a chunk size never
/// re-opens the file" is true of them. A workspace file had no equivalent: the indexer
/// extracted on every pass and discarded the text after chunking, so a refresh over an
/// unchanged tree still re-opened and re-parsed every PDF, and a re-chunk paid the whole
/// extraction cost again. One intact 84 MB PDF in this corpus costs 13.4s of that.
///
/// Keyed on a hash of the FILE'S BYTES rather than of the extracted text, because the key
/// has to be computable without doing the work it exists to avoid. Hashing bytes is one
/// sequential read; extracting is seconds. Two corpora indexing the same file share a row.
///
/// It is also the only place a workspace document exists whole. Chunks carry their own
/// text, so without this the content survives only as pieces, which is what forces a chunk
/// to be the unit a caller reads rather than merely the unit a search finds. See D-31.
/// </summary>
public sealed class FileText
{
    /// <summary>SHA-256 of the file's bytes, not of the text extracted from them.</summary>
    public required string Sha256 { get; set; }

    public required string Text { get; set; }

    /// <summary>JSON array of extraction units: page, slide or chapter offsets.</summary>
    public string? UnitsJson { get; set; }

    public string? Title { get; set; }
    public int ExtractedChars { get; set; }

    /// <summary>Which extractor produced this.</summary>
    public required string Extractor { get; set; }

    /// <summary>
    /// <c>ExtractorVersions.Current</c> when this text was produced. Anything older is
    /// re-extracted rather than trusted, which is what lets an extractor fix reach files
    /// indexed before it.
    /// </summary>
    public int ExtractorVersion { get; set; }

    public DateTime ExtractedUtc { get; set; }

    /// <summary>Set when the format was readable but yielded nothing, such as a scanned PDF.</summary>
    public string? EmptyReason { get; set; }
}

public sealed class BlobText
{
    public required string Sha256 { get; set; }
    public Blob? Blob { get; set; }

    public required string Text { get; set; }

    /// <summary>JSON array of extraction units: page, slide or chapter offsets for provenance.</summary>
    public string? UnitsJson { get; set; }

    public string? Title { get; set; }
    public int ExtractedChars { get; set; }

    /// <summary>Which extractor produced this.</summary>
    public required string Extractor { get; set; }

    /// <summary>
    /// <c>ExtractorVersions.Current</c> when this text was produced. Anything older is
    /// re-extracted on next use, which is what makes an extractor fix reach documents
    /// that were ingested before it.
    /// </summary>
    public int ExtractorVersion { get; set; }

    public DateTime ExtractedUtc { get; set; }

    /// <summary>Set when the format was readable but yielded nothing, such as a scanned PDF.</summary>
    public string? EmptyReason { get; set; }
}

/// <summary>
/// Persisted by NAME, so the numbers are free to change.
///
/// <c>Rebuild</c> is a full pass targeting ONE chunk set: the backfill that builds a
/// replacement while the live set keeps serving. It behaves like <c>Full</c> and is named
/// separately so the jobs list can tell "backfilling a new set" from "re-indexing
/// everything", which are different reasons for a corpus to be busy.
///
/// A <c>Delete</c> kind existed and was never read or written: deletes are synchronous,
/// because a delete that is queued behind an hour of indexing is a delete that has not
/// happened.
/// </summary>
/// <summary>
/// A saved task-template override for one embedding model.
///
/// Rows, not constants, because models are added at RUNTIME through the UI. A build that
/// hard-coded the framing for the models it knew about would give every model pulled
/// afterwards incorrect framing, and incorrect framing does not fail; it retrieves
/// badly. See <c>IModelProfiles</c> for the resolution order.
/// </summary>
public sealed class EmbeddingModelProfile
{
    public required string Provider { get; set; }
    public required string Model { get; set; }

    /// <summary>Template for indexed text. Contains <c>{text}</c>.</summary>
    public required string DocumentTemplate { get; set; }

    /// <summary>Template for search queries. Contains <c>{text}</c>.</summary>
    public required string QueryTemplate { get; set; }

    /// <summary>Why these values, in the operator's words. Free text.</summary>
    public string? Notes { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// What a probe measured about a model, kept so it does not have to be measured again.
/// </summary>
/// <remarks>
/// Intentionally not on <see cref="EmbeddingModelProfile"/>. That row is a choice, the
/// task framing someone configured, and it resolves ahead of the built-in defaults, so
/// writing one to hold a measurement would override the framing as well. A measurement is
/// a fact about the model; a profile is a decision about how to use it.
///
/// Every field is nullable because a probe can be interrupted and a provider can decline
/// to answer. Absent means "not measured", which is different from zero and must stay
/// different.
/// </remarks>
public sealed class EmbeddingModelMeasurement
{
    public required string Provider { get; set; }
    public required string Model { get; set; }

    public int Dimensions { get; set; }

    /// <summary>Longest input whose tail still moves the vector. Null = no limit found.</summary>
    public int? MaxInputChars { get; set; }

    /// <summary>True when over-long input returns a vector instead of an error.</summary>
    public bool TruncatesSilently { get; set; }

    public int RecommendedChunkChars { get; set; }
    public int RecommendedChunkTokens { get; set; }

    /// <summary>Measured with the model's own tokenizer. Null when the provider is silent.</summary>
    public double? CharsPerToken { get; set; }

    /// <summary>
    /// How many tokens the model reads in one go, counted with its own tokenizer. A chunk
    /// budget above this is a budget whose tail is embedded by nothing.
    ///
    /// Null when the provider reports no token counts, in which case there is no ceiling
    /// to clamp to and the configured size stands.
    /// </summary>
    public int? ContextTokens { get; set; }

    public DateTime MeasuredUtc { get; set; }
}

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
