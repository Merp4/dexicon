using Dexicon.Core.Configuration;
using Dexicon.Core.Embedding;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dexicon.Tests;

/// <summary>
/// The embedding seam, which now sits over Microsoft.Extensions.AI so a chunk set can
/// point at Ollama, OpenAI or Azure.
///
/// The invariant these protect is the one that broke twice: the MODEL IS A PER-CALL
/// ARGUMENT. Chunk sets choose a model at runtime and store it in the catalogue, so
/// anything that binds a model at registration — keyed DI, a generator per model — cannot
/// see a set created after the process started. The first version read the globally
/// configured model and ignored its caller, which would have filled an mxbai collection
/// with nomic vectors: no error, just wrong results.
/// </summary>
public sealed class EmbeddingProviderTests
{
    private const string Provider = "test";

    // ── The invariant ────────────────────────────────────────────────────────

    [Fact]
    public async Task TheModelAskedForIsTheModelUsed()
    {
        var generator = new RecordingGenerator(dimensions: 8);
        var service = Service(generator);

        await service.EmbedAsync(new EmbeddingTarget(Provider, "mxbai-embed-large"), ["hello"]);

        generator.ModelsUsed.ShouldBe(["mxbai-embed-large"],
            "a set pinned to a model must embed with THAT model, not the configured default");
    }

    [Fact]
    public async Task TwoTargetsInOneProcessDoNotLeakIntoEachOther()
    {
        // The runtime-selection case: two chunk sets on one provider, different models.
        var generator = new RecordingGenerator(dimensions: 8);
        var service = Service(generator);

        await service.EmbedAsync(new EmbeddingTarget(Provider, "model-a"), ["x"]);
        await service.EmbedAsync(new EmbeddingTarget(Provider, "model-b"), ["y"]);

        generator.ModelsUsed.ShouldBe(["model-a", "model-b"]);
    }

    [Fact]
    public async Task AnEmptyModelIsRefusedRatherThanDefaulted()
    {
        // Quietly substituting the configured model is how the original bug looked from
        // the outside: everything works, and the vectors are from the wrong space.
        var service = Service(new RecordingGenerator(dimensions: 8));

        await Should.ThrowAsync<ArgumentException>(
            () => service.EmbedAsync(new EmbeddingTarget(Provider, ""), ["x"]));
    }

    // ── Dimension probing ────────────────────────────────────────────────────

    [Fact]
    public async Task DimensionsAreProbedOncePerTargetAcrossInstances()
    {
        // The service is registered per request, so instance state cannot cache anything.
        // /healthz did a real round-trip on every fifteen-second poll because of exactly
        // that, timed out under indexing load, and painted the dependency dots red.
        var generator = new RecordingGenerator(dimensions: 768);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var target = new EmbeddingTarget(Provider, "nomic-embed-text");

        for (var i = 0; i < 3; i++)
            (await Service(generator, cache).ProbeDimensionsAsync(target)).ShouldBe(768);

        generator.Calls.ShouldBe(1, "the probe is a round-trip; it must not repeat per request");
    }

    [Fact]
    public async Task KnownDimensionsNeverCallsOut()
    {
        var generator = new RecordingGenerator(dimensions: 768);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var target = new EmbeddingTarget(Provider, "nomic-embed-text");

        await Service(generator, cache).ProbeDimensionsAsync(target);
        var after = generator.Calls;

        Service(generator, cache).KnownDimensions(target).ShouldBe(768);
        generator.Calls.ShouldBe(after, "reading a known dimensionality must not hit the network");
    }

    [Fact]
    public async Task TheSameModelUnderTwoProvidersIsCachedSeparately()
    {
        // Two providers can serve a model of the same name and they are different vector
        // spaces. A cache keyed on the model alone would serve one's dimensionality —
        // and eventually one's vectors — for the other.
        var generator = new RecordingGenerator(dimensions: 8);
        var cache = new MemoryCache(new MemoryCacheOptions());

        await Service(generator, cache).ProbeDimensionsAsync(new EmbeddingTarget("local", "shared-name"));
        await Service(generator, cache).ProbeDimensionsAsync(new EmbeddingTarget("hosted", "shared-name"));

        generator.Calls.ShouldBe(2);
    }

    // ── Batching ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ABatchComesBackInInputOrder()
    {
        // Batches run concurrently under MaxConcurrency, and completion order says nothing
        // about input order. A chunk paired with its neighbour's vector is a silent,
        // unfalsifiable search-quality bug.
        var service = Service(new OrderRevealingGenerator(), batchSize: 1, maxConcurrency: 4);

        var inputs = Enumerable.Range(0, 24).Select(i => i.ToString()).ToArray();
        var vectors = await service.EmbedAsync(new EmbeddingTarget(Provider, "m"), inputs);

        vectors.Count.ShouldBe(inputs.Length);
        for (var i = 0; i < inputs.Length; i++)
            vectors[i][0].ShouldBe(i, $"input {i} must get input {i}'s vector");
    }

    [Fact]
    public async Task NoInputsMeansNoCalls()
    {
        var generator = new RecordingGenerator(dimensions: 8);
        (await Service(generator).EmbedAsync(new EmbeddingTarget(Provider, "m"), [])).ShouldBeEmpty();
        generator.Calls.ShouldBe(0);
    }

    // ── Failure ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AFailingProviderSurfacesAsEmbeddingUnavailable()
    {
        // Search catches this specific type to degrade to keyword AND SAY SO. If a
        // provider's own exception escaped instead, the degradation path would never run
        // and the request would simply fail.
        var service = Service(new ThrowingGenerator());

        var ex = await Should.ThrowAsync<EmbeddingUnavailableException>(
            () => service.EmbedAsync(new EmbeddingTarget(Provider, "m"), ["x"]));

        ex.Message.ShouldContain("test/m");
    }

    [Fact]
    public async Task AnUnconfiguredProviderNamesTheOnesThatExist()
    {
        var service = Service(new RecordingGenerator(dimensions: 8));

        var ex = await Should.ThrowAsync<UnknownEmbeddingProviderException>(
            () => service.EmbedAsync(new EmbeddingTarget("nope", "m"), ["x"]));

        ex.Message.ShouldContain("nope");
        ex.Message.ShouldContain(Provider);
    }

    // ── Fixture ──────────────────────────────────────────────────────────────

    private static EmbeddingService Service(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        IMemoryCache? cache = null, int batchSize = 32, int maxConcurrency = 4)
    {
        var options = Options.Create(new DexiconOptions
        {
            Embedding = new EmbeddingOptions
            {
                Provider = Provider,
                BatchSize = batchSize,
                MaxConcurrency = maxConcurrency,
                Providers = new(StringComparer.OrdinalIgnoreCase)
                {
                    [Provider] = new() { Kind = EmbeddingProviderKind.Ollama },
                    ["local"] = new() { Kind = EmbeddingProviderKind.Ollama },
                    ["hosted"] = new() { Kind = EmbeddingProviderKind.Ollama },
                },
            },
            // No retries: a test that waits out a backoff is a test nobody runs.
            Ollama = new OllamaOptions { MaxRetries = 0 },
        });

        return new EmbeddingService(
            new StubFactory(generator, options.Value.Embedding),
            options,
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            NullLogger<EmbeddingService>.Instance);
    }

    /// <summary>Hands out one generator, and resolves provider names for real.</summary>
    private sealed class StubFactory(
        IEmbeddingGenerator<string, Embedding<float>> generator, EmbeddingOptions options)
        : IEmbeddingGeneratorFactory
    {
        public IReadOnlyCollection<string> ProviderNames => options.Providers.Keys;

        public EmbeddingProviderOptions Options(string provider) =>
            options.Providers.TryGetValue(provider, out var o)
                ? o
                : throw new UnknownEmbeddingProviderException(provider, options.Providers.Keys);

        public IEmbeddingGenerator<string, Embedding<float>> GeneratorFor(EmbeddingTarget target)
        {
            _ = Options(target.Provider);   // resolve, so an unknown provider still throws
            return generator;
        }
    }

    private sealed class RecordingGenerator(int dimensions) : IEmbeddingGenerator<string, Embedding<float>>
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public List<string> ModelsUsed { get; } = [];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            lock (ModelsUsed) if (options?.ModelId is { } id) ModelsUsed.Add(id);

            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                values.Select(_ => new Embedding<float>(new float[dimensions]))));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>Echoes each input back as its vector, finishing in a deliberately wrong order.</summary>
    private sealed class OrderRevealingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private int _seen;

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            // Later requests finish sooner: if order came from completion, this fails.
            await Task.Delay(Math.Max(0, 40 - Interlocked.Increment(ref _seen) * 2), cancellationToken);

            return new GeneratedEmbeddings<Embedding<float>>(
                values.Select(v => new Embedding<float>(new[] { float.Parse(v) })));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ThrowingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default) =>
            // A provider-specific exception, of the kind a catch list would miss.
            throw new HttpRequestException("connection refused");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
