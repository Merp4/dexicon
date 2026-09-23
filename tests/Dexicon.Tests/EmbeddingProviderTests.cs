using Dexicon.Core.Configuration;
using Dexicon.Core.Embedding;
using Dexicon.Core.Indexing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dexicon.Tests;

/// <summary>
/// The embedding seam, which now sits over Microsoft.Extensions.AI so a chunk set can
/// point at Ollama, OpenAI or Azure.
///
/// The invariant these protect is the one that broke twice: the MODEL IS A PER-CALL
/// ARGUMENT. Chunk sets choose a model at runtime and store it in the catalogue, so
/// anything that binds a model at registration, such as keyed DI or a generator per model, cannot
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

        await service.EmbedAsync(new EmbeddingTarget(Provider, "mxbai-embed-large"), EmbedPurpose.Raw, ["hello"]);

        generator.ModelsUsed.ShouldBe(["mxbai-embed-large"],
            "a set pinned to a model must embed with THAT model, not the configured default");
    }

    [Fact]
    public async Task TwoTargetsInOneProcessDoNotLeakIntoEachOther()
    {
        // The runtime-selection case: two chunk sets on one provider, different models.
        var generator = new RecordingGenerator(dimensions: 8);
        var service = Service(generator);

        await service.EmbedAsync(new EmbeddingTarget(Provider, "model-a"), EmbedPurpose.Raw, ["x"]);
        await service.EmbedAsync(new EmbeddingTarget(Provider, "model-b"), EmbedPurpose.Raw, ["y"]);

        generator.ModelsUsed.ShouldBe(["model-a", "model-b"]);
    }

    [Fact]
    public async Task AnEmptyModelIsRefusedRatherThanDefaulted()
    {
        // Quietly substituting the configured model is how the original bug looked from
        // the outside: everything works, and the vectors are from the wrong space.
        var service = Service(new RecordingGenerator(dimensions: 8));

        await Should.ThrowAsync<ArgumentException>(
            () => service.EmbedAsync(new EmbeddingTarget(Provider, ""), EmbedPurpose.Raw, ["x"]));
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
        // spaces. A cache keyed on the model alone would serve one set's dimensionality,
        // and eventually its vectors, for the other.
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
        var vectors = await service.EmbedAsync(new EmbeddingTarget(Provider, "m"), EmbedPurpose.Raw, inputs);

        vectors.Count.ShouldBe(inputs.Length);
        for (var i = 0; i < inputs.Length; i++)
            vectors[i][0].ShouldBe(i, $"input {i} must get input {i}'s vector");
    }

    [Fact]
    public async Task NoInputsMeansNoCalls()
    {
        var generator = new RecordingGenerator(dimensions: 8);
        (await Service(generator).EmbedAsync(new EmbeddingTarget(Provider, "m"), EmbedPurpose.Raw, [])).ShouldBeEmpty();
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
            () => service.EmbedAsync(new EmbeddingTarget(Provider, "m"), EmbedPurpose.Raw, ["x"]));

        ex.Message.ShouldContain("test/m");
    }

    [Fact]
    public async Task AnUnconfiguredProviderNamesTheOnesThatExist()
    {
        var service = Service(new RecordingGenerator(dimensions: 8));

        var ex = await Should.ThrowAsync<UnknownEmbeddingProviderException>(
            () => service.EmbedAsync(new EmbeddingTarget("nope", "m"), EmbedPurpose.Raw, ["x"]));

        ex.Message.ShouldContain("nope");
        ex.Message.ShouldContain(Provider);
    }

    // ── Fixture ──────────────────────────────────────────────────────────────

    private static EmbeddingService Service(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        IMemoryCache? cache = null, int batchSize = 32, int maxConcurrency = 4,
        ILogger<EmbeddingService>? log = null, int maxRetries = 0)
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
            Ollama = new OllamaOptions { MaxRetries = maxRetries },
        });

        return new EmbeddingService(
            new StubFactory(generator, options.Value.Embedding),
            // These tests are about batching, ordering and dimension caching; task
            // framing has its own tests and would only add noise to the inputs here.
            new NoProfiles(),
            options,
            new IndexingLimits(options),
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            log ?? NullLogger<EmbeddingService>.Instance);
    }

    // ── Naming what lost its text ────────────────────────────────────────────

    [Fact]
    public async Task TheRefusalNamesWhatWasTooLong()
    {
        // A run reporting 123 of these and naming no file gave nobody anything to act on.
        // The name travels on the exception now, which is what the indexer logs when it
        // splits and what surfaces if the split cannot be made.
        var service = Service(new RefusesLongInput(limit: 10));

        var ex = await Should.ThrowAsync<EmbeddingInputTooLongException>(() =>
            service.EmbedAsync(new EmbeddingTarget(Provider, "m"), EmbedPurpose.Document,
                [new string('x', 50)], source: "books/deep-learning.pdf chunks 1-32"));

        ex.Message.ShouldContain("books/deep-learning.pdf chunks 1-32");
        ex.Message.ShouldContain("50");
    }

    [Fact]
    public async Task AnUnnamedCallerStillProducesAReadableMessage()
    {
        // Search and the probe embed without a source, and a message opening " is longer
        // than the model's context" helps nobody.
        var service = Service(new RefusesLongInput(limit: 10));

        var ex = await Should.ThrowAsync<EmbeddingInputTooLongException>(() =>
            service.EmbedAsync(new EmbeddingTarget(Provider, "m"), EmbedPurpose.Document,
                [new string('x', 50)]));

        ex.Message.ShouldContain("an unnamed input");
    }

    [Fact]
    public async Task NoVectorIsReturnedForTextThatWasRefused()
    {
        // This used to return a vector for the opening of the input, stored under the whole
        // chunk's id. The caller can divide the text; this layer cannot, so it says so
        // rather than inventing a worse answer.
        var service = Service(new RefusesLongInput(limit: 10));

        await Should.ThrowAsync<EmbeddingInputTooLongException>(() =>
            service.EmbedAsync(new EmbeddingTarget(Provider, "m"), EmbedPurpose.Document,
                [new string('x', 50)], source: "a.pdf"));
    }

    [Fact]
    public async Task ARefusalIsTheSameWithRetriesTurnedOff()
    {
        // It never used the retry budget and must not start: with retries off the old path
        // had nowhere to run its degraded attempt and failed the file instead. A refusal is
        // the same answer whatever the budget.
        var service = Service(new RefusesLongInput(limit: 10), maxRetries: 0);

        await Should.ThrowAsync<EmbeddingInputTooLongException>(() =>
            service.EmbedAsync(new EmbeddingTarget(Provider, "m"), EmbedPurpose.Document,
                [new string('x', 50)], source: "a.pdf"));
    }

    [Fact]
    public async Task ATransientFailureStillExhaustsTheBudgetAndThrows()
    {
        // The other side of it: the extra attempt is for shortening, not a free retry for
        // an embedder that is simply down. Asserted on the CALL COUNT, because throwing
        // either way cannot tell one extra attempt from none.
        var generator = new AlwaysFails();
        var service = Service(generator, maxRetries: 0);

        await Should.ThrowAsync<EmbeddingUnavailableException>(
            () => service.EmbedAsync(new EmbeddingTarget(Provider, "m"), EmbedPurpose.Document, ["x"]));

        generator.Calls.ShouldBe(1, "no retries configured, and this failure is not an over-long input");
    }

    [Fact]
    public async Task ATransientFailureStillGetsItsConfiguredRetries()
    {
        var generator = new AlwaysFails();
        var service = Service(generator, maxRetries: 2);

        await Should.ThrowAsync<EmbeddingUnavailableException>(
            () => service.EmbedAsync(new EmbeddingTarget(Provider, "m"), EmbedPurpose.Document, ["x"]));

        generator.Calls.ShouldBe(3, "the first attempt plus two retries, and no extra");
    }

    [Fact]
    public async Task ARefusalIsNotDelayedByTheBackoff()
    {
        // The backoff exists for an embedder under strain. Over-long input is not that: it
        // fails identically every time, so it is reported at once rather than waited on.
        // Across a run with a hundred such batches the wait alone was a minute of indexing.
        //
        // maxRetries is passed explicitly because the fixture defaults it to 0, and with no
        // retries configured there is no backoff to bypass: the assertion below held
        // whatever the code did. Two retries cost 250*2^n + jitter each, so at least
        // 1,500 ms if a refusal ever enters the loop. TheBackoffIsRealWhenItApplies is the
        // control for that number.
        //
        // The limit sits at the control's 1,000 ms rather than near the few milliseconds
        // this takes on an idle machine: a CI runner took 675 ms early in a run. The call
        // count catches a retry without depending on the clock.
        var generator = new RefusesLongInput(limit: 10);
        var service = Service(generator, maxRetries: 2);

        var started = System.Diagnostics.Stopwatch.StartNew();
        await Should.ThrowAsync<EmbeddingInputTooLongException>(() =>
            service.EmbedAsync(new EmbeddingTarget(Provider, "m"), EmbedPurpose.Document,
                [new string('x', 50)], source: "a.pdf"));

        started.ElapsedMilliseconds.ShouldBeLessThan(1_000);
        generator.CallTimes.Count.ShouldBe(1, "a refusal is reported, not retried");
    }

    [Fact]
    public async Task TheBackoffIsRealWhenItApplies()
    {
        // Without this, the test above passes equally well if retries stop happening at
        // all, and the thing it claims to measure is gone with nothing to notice.
        var service = Service(new AlwaysFails(), maxRetries: 2);

        var started = System.Diagnostics.Stopwatch.StartNew();
        await Should.ThrowAsync<EmbeddingUnavailableException>(() =>
            service.EmbedAsync(new EmbeddingTarget(Provider, "m"), EmbedPurpose.Document,
                [new string('x', 5)], source: "a.pdf"));

        started.ElapsedMilliseconds.ShouldBeGreaterThan(1_000);
    }

    private sealed class AlwaysFails : IEmbeddingGenerator<string, Embedding<float>>
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            throw new HttpRequestException("connection refused");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// Refuses anything past <paramref name="limit"/> characters unless asked to truncate,
    /// which is how Ollama behaves once `truncate: false` is sent.
    /// </summary>
    private sealed class RefusesLongInput(int limit) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public List<DateTime> CallTimes { get; } = [];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            lock (CallTimes) CallTimes.Add(DateTime.UtcNow);

            var truncate = options?.AdditionalProperties?.TryGetValue("truncate", out var v) == true
                           && v is true;
            var list = values.ToList();

            if (!truncate && list.Any(s => s.Length > limit))
                throw new InvalidOperationException(
                    "input length exceeds maximum context length for this model");

            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                list.Select(_ => new Embedding<float>(new float[8]))));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>Keeps warnings so a test can read what was actually written.</summary>
    private sealed class CapturingLogger : ILogger<EmbeddingService>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Warning) Warnings.Add(formatter(state, ex));
        }
    }

    /// <summary>No task framing: text reaches the generator exactly as it was passed.</summary>
    private sealed class NoProfiles : IModelProfiles
    {
        public Task<ModelTemplates> ForAsync(EmbeddingTarget target, CancellationToken ct = default) =>
            Task.FromResult(ModelTemplates.Raw);

        public ModelTemplates? Suggest(string model) => null;
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
