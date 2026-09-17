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
    public StorageOptions Storage { get; init; } = new();
    public LogOptions Log { get; init; } = new();
}

public sealed class QdrantOptions
{
    /// <summary>gRPC endpoint. Namespaced service name, never a bare "qdrant" — see docs/09.</summary>
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
    /// `embeddinggemma` because it won both retrieval sweeps — see
    /// docs/benchmarks.md. This is the default for NEW corpora only: an existing chunk
    /// set records its own model and is untouched, which is why changing this costs a
    /// larger first pull and nothing else.
    /// </summary>
    public string Model { get; init; } = "embeddinggemma";

    /// <summary>Which configured provider new chunk sets use by default.</summary>
    public string Provider { get; init; } = "ollama";

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
/// credentials live here, in configuration, and never in the catalogue — a database row
/// that carries an API key is a database row you cannot back up casually.
/// </summary>
public sealed class EmbeddingProviderOptions
{
    public EmbeddingProviderKind Kind { get; init; }

    /// <summary>Ollama and Azure. Ignored for OpenAI, which has one.</summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// The NAME of the environment variable holding the API key — not the key.
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
    /// curated list beats a filtered dump — free text still works.
    /// </summary>
    public List<string> Models { get; init; } = [];
}

public sealed class IndexingOptions
{
    public int MaxFileBytes { get; init; } = 262_144;

    /// <summary>
    /// Size cap for formats that go through an extractor — PDF, EPUB, DOCX, PPTX. Separate
    /// from <see cref="MaxFileBytes"/> because a 300-page PDF is normal and a 300 KB source
    /// file is not, and one number cannot mean both.
    ///
    /// 512 MB. It was a hard-coded 64 MB, chosen when PDF extraction copied the whole file
    /// into a growing MemoryStream and then called ToArray() on it — nearly 400 MB of raw
    /// bytes for a 128 MB book before a page was parsed. PdfPig reads a seekable stream, so
    /// that copying is gone and the ceiling with it.
    ///
    /// It is still a cap rather than no cap: extraction holds the TEXT of the document in
    /// memory, and a chunked, embedded index of a very large file is slow rather than
    /// broken. Raise it if you have the memory; a file over it is reported as skipped with
    /// its size and the cap, never silently dropped.
    /// </summary>
    public long DocumentMaxBytes { get; init; } = 512L * 1024 * 1024;

    public int ChunkSize { get; init; } = 768;
    public int ChunkOverlap { get; init; } = 100;
    public string BoundaryMode { get; init; } = "language-aware";

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
    public string Tenant { get; init; } = "default";

    /// <summary>Blank generates one on first run and logs it exactly once.</summary>
    public string? Token { get; init; }
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

    public string CatalogPath => Path.Combine(DataPath, CatalogFileName);
    public string BlobRoot => Path.Combine(DataPath, "blobs");
}
