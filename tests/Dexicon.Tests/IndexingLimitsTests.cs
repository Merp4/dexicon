using Dexicon.Core.Configuration;
using Dexicon.Core.Embedding;
using Dexicon.Core.Indexing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// What the machine and the endpoints will take, counted once for the whole process.
///
/// While indexing ran one job at a time, "how much of this may happen at once" and "how
/// much of this may one job do" were the same question, and a semaphore built inside a
/// method answered both. They come apart the moment two corpora index together: a
/// per-call limit bounds that call and nothing else, so a setting reading 4 sent 8.
///
/// The generator below counts how many requests are in flight at their peak, which is
/// the only way to tell a shared limit from a per-caller one. Asserting on the setting,
/// or on the total number of calls, passes either way.
/// </summary>
public sealed class IndexingLimitsTests
{
    private const string Provider = "test";

    private static IOptions<DexiconOptions> Config(int embedConcurrency = 2, int batchSize = 1) =>
        Options.Create(new DexiconOptions
        {
            Embedding = new EmbeddingOptions
            {
                Provider = Provider,
                MaxConcurrency = embedConcurrency,
                BatchSize = batchSize,
            },
        });

    /// <summary>Holds each request open until released, so overlap is observable.</summary>
    private sealed class ConcurrencyWatchingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private int _inFlight;
        private int _peak;
        public int Peak => Volatile.Read(ref _peak);

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _peak)))
                Interlocked.CompareExchange(ref _peak, now, seen);

            try
            {
                // Long enough that requests admitted together overlap here. Without it
                // each could finish before the next began and the peak would be 1
                // whatever the limit allowed.
                await Task.Delay(40, cancellationToken);
                return new GeneratedEmbeddings<Embedding<float>>(
                    values.Select(_ => new Embedding<float>(new float[8])));
            }
            finally { Interlocked.Decrement(ref _inFlight); }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class OneGenerator(IEmbeddingGenerator<string, Embedding<float>> generator)
        : IEmbeddingGeneratorFactory
    {
        public IEmbeddingGenerator<string, Embedding<float>> GeneratorFor(EmbeddingTarget target) => generator;
        public IReadOnlyCollection<string> ProviderNames => [Provider];
        public EmbeddingProviderOptions Options(string provider) =>
            new() { Kind = EmbeddingProviderKind.Ollama };
    }

    private sealed class NoProfiles : IModelProfiles
    {
        public Task<ModelTemplates> ForAsync(EmbeddingTarget target, CancellationToken ct = default) =>
            Task.FromResult(ModelTemplates.Raw);

        public ModelTemplates? Suggest(string model) => null;
    }

    private static EmbeddingService Service(
        IOptions<DexiconOptions> options, IndexingLimits limits,
        IEmbeddingGenerator<string, Embedding<float>> generator) =>
        new(new OneGenerator(generator), new NoProfiles(), options, limits,
            new MemoryCache(new MemoryCacheOptions()), NullLogger<EmbeddingService>.Instance);

    /// <summary>
    /// The defect this exists for. Two callers sharing one endpoint must not each get
    /// the whole budget, because the number describes the endpoint.
    /// </summary>
    [Fact]
    public async Task TwoCallersShareOneEndpointsBudget()
    {
        var options = Config(embedConcurrency: 2);
        using var limits = new IndexingLimits(options);
        var generator = new ConcurrencyWatchingGenerator();
        var target = new EmbeddingTarget(Provider, "m");

        // Two services, as two concurrent index jobs would have: separate scopes, one
        // endpoint. Eight inputs at a batch size of one is eight requests per caller.
        var a = Service(options, limits, generator);
        var b = Service(options, limits, generator);
        string[] inputs = ["1", "2", "3", "4", "5", "6", "7", "8"];

        await Task.WhenAll(
            a.EmbedAsync(target, EmbedPurpose.Raw, inputs),
            b.EmbedAsync(target, EmbedPurpose.Raw, inputs));

        generator.Peak.ShouldBeLessThanOrEqualTo(2,
            "the limit belongs to the endpoint; a per-call gate would have allowed four");
    }

    /// <summary>A corpus indexing alone is not throttled by a budget nobody else wants.</summary>
    [Fact]
    public async Task OneCallerAloneUsesTheWholeBudget()
    {
        var options = Config(embedConcurrency: 4);
        using var limits = new IndexingLimits(options);
        var generator = new ConcurrencyWatchingGenerator();

        await Service(options, limits, generator).EmbedAsync(
            new EmbeddingTarget(Provider, "m"), EmbedPurpose.Raw,
            ["1", "2", "3", "4", "5", "6", "7", "8"]);

        generator.Peak.ShouldBe(4);
    }

    /// <summary>
    /// Per provider, because the number describes an endpoint. A local Ollama admitting
    /// four sequences says nothing about what a hosted deployment will take, and a
    /// corpus on one should not wait behind traffic to the other.
    /// </summary>
    [Fact]
    public void EachProviderHasItsOwnBudget()
    {
        using var limits = new IndexingLimits(Config(embedConcurrency: 3));

        var ollama = limits.EmbeddingFor(new EmbeddingTarget("ollama", "a"));
        var openai = limits.EmbeddingFor(new EmbeddingTarget("openai", "b"));

        ollama.ShouldNotBeSameAs(openai);
        ollama.ShouldBeSameAs(limits.EmbeddingFor(new EmbeddingTarget("ollama", "different-model")));
        ollama.CurrentCount.ShouldBe(3);
    }

    [Fact]
    public void ProviderNamesAreMatchedWithoutCase()
    {
        using var limits = new IndexingLimits(Config());

        limits.EmbeddingFor(new EmbeddingTarget("Ollama", "a"))
            .ShouldBeSameAs(limits.EmbeddingFor(new EmbeddingTarget("ollama", "a")));
    }

    /// <summary>
    /// A permit count of zero would stop indexing altogether, and a configuration file
    /// is where that typo lives.
    /// </summary>
    [Fact]
    public void AZeroOrNegativeLimitIsRaisedToOneRatherThanStoppingEverything()
    {
        using var limits = new IndexingLimits(Options.Create(new DexiconOptions
        {
            Embedding = new EmbeddingOptions { MaxConcurrency = 0 },
            Indexing = new IndexingOptions { MaxConcurrentCorpora = 0, MaxConcurrentExtractions = -1 },
        }));

        limits.MaxConcurrentCorpora.ShouldBe(1);
        limits.Extractions.CurrentCount.ShouldBe(1);
        limits.EmbeddingFor(new EmbeddingTarget("ollama", "a")).CurrentCount.ShouldBe(1);
    }
}
