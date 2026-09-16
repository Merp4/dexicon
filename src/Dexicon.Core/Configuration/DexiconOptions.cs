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
    /// <summary>Default model for new corpora. Pinned per corpus at creation.</summary>
    public string Model { get; init; } = "nomic-embed-text";

    public int MaxConcurrency { get; init; } = 4;

    /// <summary>Chunks per embedding request.</summary>
    public int BatchSize { get; init; } = 32;
}

public sealed class IndexingOptions
{
    public int MaxFileBytes { get; init; } = 262_144;
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

public sealed class StorageOptions
{
    public string DataPath { get; init; } = "/data";
    public string CatalogFileName { get; init; } = "catalog.db";

    public string CatalogPath => Path.Combine(DataPath, CatalogFileName);
    public string BlobRoot => Path.Combine(DataPath, "blobs");
}
