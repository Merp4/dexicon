using Dexicon.Api;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Embedding;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;

namespace Dexicon.Tests;

/// <summary>
/// What the detailed health report says about embeddings.
///
/// Two faults it used to have. It reported the Ollama endpoint whatever the configured
/// provider was, so a deployment defaulting to OpenAI showed the address of a container it
/// never talks to beside a reachability badge for a backend on the other side of the
/// internet. And it reported the default model and its dimensionality as though they
/// described the deployment, when they describe only what the NEXT corpus inherits: every
/// chunk set pins its own model, and the dimensionality is what chooses its Qdrant
/// collection.
///
/// What is worth reporting instead is the inverse — a set whose model the provider no
/// longer has. That set still says `ready` and its vectors are still in Qdrant, but the
/// query cannot be embedded, so it answers nothing and says nothing.
/// </summary>
public sealed class HealthReportTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private CatalogDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();

        _db.Corpora.Add(new Corpus { Id = "c", Name = "books", CreatedUtc = DateTime.UtcNow });
        _db.Corpora.Add(new Corpus { Id = "c2", Name = "theirs", CreatedUtc = DateTime.UtcNow });
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task AddSet(string id, string name, string model, string provider = "ollama", string corpusId = "c")
    {
        _db.ChunkSets.Add(new ChunkSet
        {
            Id = id, CorpusId = corpusId, Name = name, EmbeddingModel = model,
            EmbeddingProvider = provider, CollectionName = $"dexicon_{id}",
            BoundaryMode = "blank-line", CreatedUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    private static Task<IReadOnlyList<MissingModel>> Missing(
        CatalogDbContext db, IModelCatalog catalog, params string[] corpusIds) =>
        SystemEndpoints.MissingModelsAsync(
            db, catalog, new MemoryCache(new MemoryCacheOptions()),
            corpusIds.Length == 0 ? ["c"] : corpusIds,
            // A fresh scope per call, so one test's cached answer cannot satisfy another's.
            Guid.NewGuid().ToString(), default);

    // ── Which endpoint the report names ──────────────────────────────────────

    [Fact]
    public void AnOllamaProviderReportsTheOllamaEndpoint()
    {
        var opts = Options("ollama", new EmbeddingProviderOptions { Kind = EmbeddingProviderKind.Ollama });

        SystemEndpoints.EndpointOf(new StubFactory(opts), opts, "ollama")
            .ShouldBe("http://dexicon-ollama:11434");
    }

    [Fact]
    public void AProviderWithItsOwnEndpointReportsThatOne()
    {
        var opts = Options("side", new EmbeddingProviderOptions
        { Kind = EmbeddingProviderKind.Ollama, Endpoint = "http://gpu-box:11434" });

        SystemEndpoints.EndpointOf(new StubFactory(opts), opts, "side").ShouldBe("http://gpu-box:11434");
    }

    [Fact]
    public void AHostedProviderDoesNotReportTheOllamaContainer()
    {
        // The bug: an OpenAI default showed http://dexicon-ollama:11434, an address this
        // deployment never calls, next to a badge saying it was reachable.
        var opts = Options("openai", new EmbeddingProviderOptions { Kind = EmbeddingProviderKind.OpenAI });

        var endpoint = SystemEndpoints.EndpointOf(new StubFactory(opts), opts, "openai");

        endpoint.ShouldNotContain("dexicon-ollama");
        endpoint.ShouldBe("https://api.openai.com/v1");
    }

    [Fact]
    public void AnUnconfiguredProviderSaysSoRatherThanInventingAnAddress()
    {
        var opts = Options("ollama", new EmbeddingProviderOptions { Kind = EmbeddingProviderKind.Ollama });

        SystemEndpoints.EndpointOf(new StubFactory(opts, unknown: true), opts, "ghost")
            .ShouldBe("(no such provider)");
    }

    // ── Which models are reported missing ────────────────────────────────────

    [Fact]
    public async Task AModelTheProviderStillHasIsNotReported()
    {
        await AddSet("s1", "default", "embeddinggemma");

        (await Missing(_db, new StubCatalog("embeddinggemma"))).ShouldBeEmpty();
    }

    [Fact]
    public async Task AModelTheProviderNoLongerHasNamesTheSetsThatNeedIt()
    {
        await AddSet("s1", "default", "embeddinggemma");
        await AddSet("s2", "fine", "mxbai-embed-large");

        var missing = await Missing(_db, new StubCatalog("embeddinggemma"));

        var gone = missing.ShouldHaveSingleItem();
        gone.Model.ShouldBe("mxbai-embed-large");
        gone.Provider.ShouldBe("ollama");
        // `corpus:set`, because the fix is per set: rebuild that one, or pull the model back.
        gone.Sets.ShouldBe(["books:fine"]);
    }

    [Fact]
    public async Task TheTagOllamaAddsIsNotMistakenForADifferentModel()
    {
        // Ollama lists "embeddinggemma:latest"; a chunk set records "embeddinggemma".
        // Comparing them raw would report every set in the deployment as broken.
        await AddSet("s1", "default", "embeddinggemma");

        (await Missing(_db, new StubCatalog("embeddinggemma:latest"))).ShouldBeEmpty();
    }

    [Fact]
    public async Task AnUnreachableProviderIsNotReportedAsAMissingModel()
    {
        // Unreachable is not missing. The embedding health beside this already says the
        // backend is down; claiming its models are gone too is a second alarm for one
        // fault, and the wrong one to act on.
        await AddSet("s1", "default", "embeddinggemma");

        (await Missing(_db, new StubCatalog(throws: true))).ShouldBeEmpty();
    }

    [Fact]
    public async Task AHostedProviderIsNotChecked()
    {
        // A hosted catalogue is the configured list, not what the backend holds, so a
        // model absent from it may be perfectly valid. A warning that fires on a working
        // deployment costs more than the one it catches.
        await AddSet("s1", "default", "text-embedding-3-small", provider: "openai");

        (await Missing(_db, new StubCatalog("something-else", managed: false))).ShouldBeEmpty();
    }

    [Fact]
    public async Task ACorpusOutsideTheScopeIsNeverNamed()
    {
        // The corpora count above this is a number and gives nothing away. A set names its
        // corpus, and that name is not this caller's to see.
        await AddSet("s1", "default", "gone-model", corpusId: "c2");

        (await Missing(_db, new StubCatalog("embeddinggemma"))).ShouldBeEmpty();
    }

    [Fact]
    public async Task TwoSetsOnOneMissingModelAreReportedOnce()
    {
        await AddSet("s1", "a", "mxbai-embed-large");
        await AddSet("s2", "b", "mxbai-embed-large");

        var gone = (await Missing(_db, new StubCatalog("embeddinggemma"))).ShouldHaveSingleItem();

        gone.Sets.ShouldBe(["books:a", "books:b"]);
    }

    // ── Stubs ────────────────────────────────────────────────────────────────

    private static DexiconOptions Options(string name, EmbeddingProviderOptions provider) =>
        new()
        {
            Embedding = new EmbeddingOptions
            {
                Provider = name,
                Providers = new Dictionary<string, EmbeddingProviderOptions>(StringComparer.OrdinalIgnoreCase)
                { [name] = provider },
            },
        };

    private sealed class StubFactory(DexiconOptions opts, bool unknown = false) : IEmbeddingGeneratorFactory
    {
        public IReadOnlyCollection<string> ProviderNames => opts.Embedding.Providers.Keys;

        public IEmbeddingGenerator<string, Embedding<float>> GeneratorFor(EmbeddingTarget target) =>
            throw new NotSupportedException();

        public EmbeddingProviderOptions Options(string provider) =>
            unknown || !opts.Embedding.Providers.TryGetValue(provider, out var found)
                ? throw new UnknownEmbeddingProviderException(provider, opts.Embedding.Providers.Keys)
                : found;
    }

    private sealed class StubCatalog(string? has = null, bool managed = true, bool throws = false) : IModelCatalog
    {
        public bool IsManaged(string provider) => managed;

        public Task<IReadOnlyList<AvailableModel>> ListAsync(string provider, CancellationToken ct = default) =>
            throws
                ? throw new EmbeddingUnavailableException("connection refused")
                : Task.FromResult<IReadOnlyList<AvailableModel>>(
                    has is null ? [] : [new AvailableModel(has, 0, null, null)]);

        public IAsyncEnumerable<ModelPullProgress> PullAsync(string provider, string model, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string provider, string model, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
