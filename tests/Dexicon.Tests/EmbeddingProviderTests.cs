using System.Net;
using System.Text;
using Dexicon.Core.Configuration;
using Dexicon.Core.Embedding;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dexicon.Tests;

/// <summary>
/// The embedding provider is registered with <c>AddHttpClient</c>, which means a NEW
/// instance per injection. Anything it remembers in a field is therefore forgotten
/// immediately, and the dimension probe — a real round-trip to Ollama — ran again on
/// every single <c>/healthz</c> poll, i.e. every 15 seconds per open browser tab.
///
/// That was not merely wasteful. While indexing saturates a CPU-bound Ollama the probe
/// times out, the poll fails, and the UI painted both dependency status dots red during
/// entirely normal operation. A monitor that cries wolf is worse than no monitor.
/// </summary>
public sealed class EmbeddingProviderTests
{
    [Fact]
    public async Task DimensionsAreProbedOncePerModel_NotOncePerInstance()
    {
        var handler = new CountingHandler(dimensions: 768);
        var cache = new MemoryCache(new MemoryCacheOptions());

        // Three separate instances, exactly as three injections would produce.
        for (var i = 0; i < 3; i++)
            (await NewProvider(handler, cache).ProbeDimensionsAsync("nomic-embed-text")).ShouldBe(768);

        handler.Calls.ShouldBe(1, "the probe is a round-trip to Ollama; it must not repeat per request");
    }

    [Fact]
    public async Task AFreshInstanceReportsDimensionsWithoutCallingOllama()
    {
        // What /healthz actually does: read `Dimensions` and only probe when it is 0.
        var handler = new CountingHandler(dimensions: 768);
        var cache = new MemoryCache(new MemoryCacheOptions());

        await NewProvider(handler, cache).ProbeDimensionsAsync("nomic-embed-text");
        var callsAfterFirstProbe = handler.Calls;

        NewProvider(handler, cache).Dimensions.ShouldBe(768);

        handler.Calls.ShouldBe(callsAfterFirstProbe, "reading a known dimensionality must not hit the network");
    }

    [Fact]
    public async Task ChangingTheModelReProbes()
    {
        // Dimensionality is a property of the model, so the cache key must include it:
        // serving 768 for a 1024-dimension model would corrupt a whole collection.
        var handler = new CountingHandler(dimensions: 768);
        var cache = new MemoryCache(new MemoryCacheOptions());

        await NewProvider(handler, cache).ProbeDimensionsAsync("nomic-embed-text");
        handler.Dimensions = 1024;
        (await NewProvider(handler, cache).ProbeDimensionsAsync("mxbai-embed-large")).ShouldBe(1024);

        handler.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task ABatchOfInputsComesBackInInputOrder()
    {
        // EmbedAsync runs batches concurrently under MaxConcurrency. Completion order
        // says nothing about input order, and a chunk paired with its neighbour's vector
        // is a silent, unfalsifiable search-quality bug.
        var handler = new OrderRevealingHandler();
        var provider = NewProvider(handler, new MemoryCache(new MemoryCacheOptions()),
            new EmbeddingOptions { Model = "m", BatchSize = 1, MaxConcurrency = 4 });

        var inputs = Enumerable.Range(0, 24).Select(i => i.ToString()).ToArray();
        var vectors = await provider.EmbedAsync(inputs);

        vectors.Count.ShouldBe(inputs.Length);
        for (var i = 0; i < inputs.Length; i++)
            vectors[i][0].ShouldBe(i, $"input {i} must get input {i}'s vector");
    }

    private static OllamaEmbeddingProvider NewProvider(
        HttpMessageHandler handler, IMemoryCache cache, EmbeddingOptions? embedding = null)
    {
        var options = new DexiconOptions
        {
            Embedding = embedding ?? new EmbeddingOptions { Model = "nomic-embed-text" },
            Ollama = new OllamaOptions { Endpoint = "http://dexicon-ollama:11434", MaxRetries = 0 },
        };
        return new OllamaEmbeddingProvider(
            new HttpClient(handler, disposeHandler: false),
            Options.Create(options),
            cache,
            NullLogger<OllamaEmbeddingProvider>.Instance);
    }

    private sealed class CountingHandler(int dimensions) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public int Dimensions { get; set; } = dimensions;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            var vector = string.Join(',', Enumerable.Repeat('0', Dimensions));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"embeddings\":[[{vector}]]}}", Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>Echoes each input back as its own vector, with a delay that inverts completion order.</summary>
    private sealed class OrderRevealingHandler : HttpMessageHandler
    {
        private int _seen;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            var input = System.Text.Json.JsonDocument.Parse(body).RootElement
                .GetProperty("input")[0].GetString()!;

            // Later requests finish sooner: if order came from completion, this test fails.
            await Task.Delay(Math.Max(0, 40 - Interlocked.Increment(ref _seen) * 2), ct);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"embeddings\":[[{input}]]}}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
