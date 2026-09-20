namespace Dexicon.Core.Configuration;

/// <summary>
/// Root configuration, bound from <c>DEXICON__*</c> environment variables.
/// Every value has a working default except workspace availability, which is a
/// property of the bind mount rather than of configuration.
/// </summary>
public sealed class DexiconOptions
{
    public const string SectionName = "Dexicon";

    public QdrantOptions Qdrant { get; init; } = new();
    public OllamaOptions Ollama { get; init; } = new();
    public EmbeddingOptions Embedding { get; init; } = new();
    public IndexingOptions Indexing { get; init; } = new();
    public UploadOptions Upload { get; init; } = new();
    public BootstrapOptions Bootstrap { get; init; } = new();
    public AdminOptions Admin { get; init; } = new();
    public StorageOptions Storage { get; init; } = new();
    public LogOptions Log { get; init; } = new();
}

public sealed class QdrantOptions
{
    /// <summary>gRPC endpoint. Namespaced service name, never a bare "qdrant"; see docs/09.</summary>
    public string Endpoint { get; init; } = "http://dexicon-qdrant:6334";

    /// <summary>
    /// Qdrant API key. Must not be blank: Qdrant enables auth on the PRESENCE of its
    /// key variable, so an empty string turns auth on with an unmatchable key.
    /// Compose supplies a non-empty default. See docs/09-deployment.md.
    /// </summary>
    public string? ApiKey { get; init; }
}

public sealed class OllamaOptions
{
    public string Endpoint { get; init; } = "http://dexicon-ollama:11434";
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
    public int MaxRetries { get; init; } = 2;
}

public sealed class EmbeddingOptions
{
    /// <summary>Default model for new chunk sets.</summary>
    /// <summary>
    /// The model a corpus gets when its creator does not choose one.
    ///
    /// `embeddinggemma` because it led both retrieval sweeps; see
    /// docs/benchmarks.md. This is the default for new corpora only: an existing chunk
    /// set records its own model and is untouched, which is why changing this costs a
    /// larger first pull and nothing else.
    /// </summary>
    public string Model { get; init; } = "embeddinggemma";

    /// <summary>Which configured provider new chunk sets use by default.</summary>
    public string Provider { get; init; } = "ollama";

    /// <summary>
    /// Embedding requests in flight at once, PER PROVIDER and across every job.
    ///
    /// Per provider because the limit describes an endpoint: one Ollama admitting four
    /// sequences says nothing about what an OpenAI deployment will take. Across every
    /// job because it describes that endpoint and not a caller — this was a semaphore
    /// constructed inside each call, so it bounded one embed and nothing else, and two
    /// corpora indexing at once would have sent twice this number with the setting
    /// reading 4.
    ///
    /// Matched to <c>OLLAMA_NUM_PARALLEL</c> for an Ollama deployment. Measured
    /// A/B/A/B against a running index: 49.8% and 50.4% of the runner's time busy at 1,
    /// against 78.0% and 80.3% at 4.
    /// </summary>
    public int MaxConcurrency { get; init; } = 4;

    /// <summary>Chunks per embedding request.</summary>
    public int BatchSize { get; init; } = 32;

    /// <summary>
    /// The embedding backends this deployment can reach, keyed by the name a chunk set
    /// records. Ollama is configured by default and needs nothing; the rest are opt-in.
    /// </summary>
    public Dictionary<string, EmbeddingProviderOptions> Providers { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ollama"] = new() { Kind = EmbeddingProviderKind.Ollama },
    };
}

public enum EmbeddingProviderKind { Ollama = 0, OpenAI = 1, AzureOpenAI = 2 }

/// <summary>
/// One embedding backend. A chunk set records the provider NAME and the model; the
/// credentials live here, in configuration, and never in the catalogue. A database row
/// that carries an API key cannot be backed up casually.
/// </summary>
public sealed class EmbeddingProviderOptions
{
    public EmbeddingProviderKind Kind { get; init; }

    /// <summary>Ollama and Azure. Ignored for OpenAI, which has one.</summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// The name of the environment variable holding the API key, not the key itself.
    ///
    /// Configuration files get committed; environment variables do not. Naming the
    /// variable keeps appsettings.json safe to check in while the secret stays in the
    /// environment, which is the same split .env has used since the first commit.
    /// </summary>
    public string? ApiKeyEnvVar { get; init; }

    /// <summary>
    /// Read directly, for deployments that inject configuration from a secret store and
    /// have no environment to name. Left null by anything that can use
    /// <see cref="ApiKeyEnvVar"/>, and never logged.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Models to offer in the UI for providers that cannot be asked. Ollama reports what
    /// it has pulled; OpenAI's model list is long and mostly not embeddings, so a short
    /// curated list is preferable to a filtered dump; free text still works.
    /// </summary>
    public List<string> Models { get; init; } = [];
}

public sealed class IndexingOptions
{
    public int MaxFileBytes { get; init; } = 262_144;

    /// <summary>
    /// Size cap for formats that go through an extractor: PDF, EPUB, DOCX, PPTX. Separate
    /// from <see cref="MaxFileBytes"/> because a 300-page PDF is normal and a 300 KB source
    /// file is not, and one number cannot mean both.
    ///
    /// 512 MB. It was a hard-coded 64 MB, chosen when PDF extraction copied the whole file
    /// into a growing MemoryStream and then called ToArray() on it, nearly 400 MB of raw
    /// bytes for a 128 MB book before a page was parsed. PdfPig reads a seekable stream, so
    /// that copying is gone and the ceiling with it.
    ///
    /// It is still a cap rather than no cap: extraction holds the TEXT of the document in
    /// memory, and a chunked, embedded index of a very large file is slow rather than
    /// broken. Raise it if you have the memory; a file over it is reported as skipped with
    /// its size and the cap, never silently dropped.
    /// </summary>
    public long DocumentMaxBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>
    /// How long a file may go on reading itself during extraction before it is abandoned
    /// and recorded as failed. 0 disables the limit.
    ///
    /// Extraction is a synchronous call into PdfPig or the OpenXML readers, none of which
    /// take a cancellation token, so a job's own token cannot interrupt one. Without a
    /// limit a single unreadable file holds the corpus and everything queued behind it: a
    /// truncated 68 MB PDF did exactly that for over two hours, and only a container
    /// restart ended it. <see cref="Extraction.DeadlineStream"/> enforces the budget on
    /// the file's own reads, which is what lets the thread unwind instead of being left
    /// running.
    ///
    /// WHAT IT DOES NOT COVER. The clock is read between operations on the file, so this
    /// bounds a file that keeps reading, not wall-clock time in extraction. A single read
    /// that never returns, or a long stretch of computation inside the library between
    /// two reads, passes unchecked. Both are out of reach from here for the same reason
    /// the budget exists: there is no cancellation to hook and no safe way to stop a
    /// thread, so bounding them needs process isolation rather than a larger number. The
    /// case this was built for, a file issuing millions of small reads, is covered.
    ///
    /// 300s is far above anything healthy. The slowest legitimate file measured here is an
    /// intact 84 MB PDF at 13s, and the limit exists for the pathological case rather than
    /// the large one. Raise it if a genuinely enormous document is being rejected; the
    /// failure names the file and the budget.
    /// </summary>
    public int ExtractionTimeoutSeconds { get; init; } = 300;

    /// <summary>
    /// Chunk size in tokens for a new corpus, and for a new chunk set with nothing to
    /// inherit from. An existing set stores its own and reads it back, so changing this
    /// migrates nothing and costs no reindex.
    ///
    /// 256, measured. Scored on whether the text handed back contains the answer, rather
    /// than on which file ranked first, 256-token chunks answered 0.527 of 55 questions
    /// against 0.291 for 768 at a 1,500-character budget, converging at 6,000 (0.600
    /// against 0.582). A smaller chunk points at a narrower part of a document and more of
    /// them fit in a caller's budget, and at a tight budget that is most of the difference.
    ///
    /// The retrieval sweep found this before and set it aside twice, because a file split
    /// finer has more chances to land one chunk in the top ten and that flatters a file-rank
    /// metric. It is only flattery if the chunk is what the caller receives; scored on what
    /// the caller reads, it holds. See D-31 and its amendment.
    ///
    /// 256 is the smallest size demonstrated. Below it is untested, and there is a floor
    /// where a chunk carries too little to embed distinctively.
    /// </summary>
    public int ChunkSize { get; init; } = 256;

    /// <summary>
    /// Overlap in tokens, on the same terms as the size above: a new corpus, or a new
    /// chunk set with nothing to inherit from. Held at the same eighth of the chunk size
    /// the previous default was, so this change moves one variable rather than two.
    ///
    /// The same measurement swept overlap and found nothing to gain: at 256 tokens it
    /// scored 0.527 with no overlap against 0.473 with 100, and identically at a wider
    /// budget. That difference is 29 answers against 26 out of 55, which is noise, so this
    /// is not evidence for removing overlap — only that there is no case for carrying more
    /// of it. At 256 tokens the old flat 100 would have been 39% rather than 13%, inflating
    /// the chunk count by about two thirds for no measured return.
    /// </summary>
    public int ChunkOverlap { get; init; } = 32;
    public string BoundaryMode { get; init; } = "language-aware";

    /// <summary>
    /// How many corpora may be indexed at once.
    ///
    /// The queue was one job at a time for the life of the process, so a corpus that
    /// takes hours owned the machine: a 1,834-file library measured 13.7 hours to
    /// refresh, and a sixteen-file corpus queued behind it waited all of that. Nothing
    /// about the work required it. Two jobs on ONE corpus are still excluded, by the
    /// lease rather than by the queue, which is where that exclusion belongs.
    ///
    /// Raising this does not multiply the load on the embedding endpoint or the
    /// filesystem, because those are bounded separately below. It multiplies the number
    /// of catalogue connections and the memory held by in-flight documents, which is
    /// what to watch if you raise it a long way.
    /// </summary>
    public int MaxConcurrentCorpora { get; init; } = 4;

    /// <summary>
    /// How many files may be extracted at once, across every job.
    ///
    /// Extraction is CPU-bound parsing: PdfPig laying out a page, the OpenXML readers
    /// walking a package. The limit is the machine's, not a corpus's, which is why it is
    /// counted across jobs rather than within one. A corpus indexing alone may use all of
    /// it; four indexing together share it.
    /// </summary>
    public int MaxConcurrentExtractions { get; init; } = 4;

    /// <summary>0 disables automatic refresh; the UI button and MCP tool still work.</summary>
    public int RefreshMinutes { get; init; }

    /// <summary>Root under which every workspace source must resolve. Read-only bind mount.</summary>
    public string WorkspaceRoot { get; init; } = "/workspaces";
}

public sealed class UploadOptions
{
    public long MaxFileBytes { get; init; } = 209_715_200;
}

public sealed class BootstrapOptions
{
    /// <summary>Blank generates one on first run and logs it exactly once.</summary>
    public string? Token { get; init; }
}

/// <summary>
/// The administrator's credential, which is the only route to the <c>admin</c> scope.
/// </summary>
public sealed class AdminOptions
{
    /// <summary>
    /// Blank generates a password on first run and logs it exactly once, the way the
    /// bootstrap token already works. Set, it is applied on every start, so it is also the
    /// way back in after a forgotten one.
    /// </summary>
    public string? Password { get; init; }
}

/// <summary>
/// Declared as a real section rather than read as a loose configuration key. Compose
/// sets DEXICON__LOG__LEVEL, and a variable the options tree has no home for is a
/// variable nobody can discover.
/// </summary>
public sealed class LogOptions
{
    /// <summary>Trace | Debug | Information | Warning | Error</summary>
    public string Level { get; init; } = "Information";
}

public sealed class StorageOptions
{
    public string DataPath { get; init; } = "/data";
    public string CatalogFileName { get; init; } = "catalog.db";

    /// <summary>
    /// How long SQLite waits for a locked catalogue before giving up, applied as
    /// <c>PRAGMA busy_timeout</c> on every connection.
    ///
    /// 30s because that is the provider's own command timeout, so neither gives up before
    /// the other: below it, SQLite would fail a wait the caller was still prepared to make;
    /// above it, the caller aborts a wait SQLite was still making. It is a ceiling on
    /// waiting rather than a prediction of how long a write takes, which is why it is not
    /// sized against any particular job.
    /// </summary>
    public int BusyTimeoutSeconds { get; init; } = 30;

    public string CatalogPath => Path.Combine(DataPath, CatalogFileName);
    public string BlobRoot => Path.Combine(DataPath, "blobs");
}
