using Dexicon.Core.Configuration;
using Dexicon.Core.Embedding;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// What happens when a chunk is longer than the model's context.
///
/// Ollama's <c>/api/embed</c> truncates the end of over-long input and returns a vector,
/// with nothing in the response to say it happened (ollama/ollama#14259). The result is the
/// failure this project exists to avoid: the missing text is recorded as indexed, search
/// can never match it, and nothing anywhere is red. The measured library hit this on about
/// 4% of embed calls, because `embeddinggemma` has a 2,048-token context and the chunk set
/// was cut at 2,065.
///
/// So the request asks the provider to REFUSE instead, and the refusal is reported rather
/// than absorbed. It used to be retried with truncation allowed, which stored a vector for
/// the opening of a chunk under that chunk's own id: the same silent loss, arrived at
/// deliberately. Only the caller owns the text and can divide it, so the refusal is raised
/// as <see cref="EmbeddingInputTooLongException"/> and the indexer splits and retries.
///
/// It is never retried here. The same input fails identically every time, so the backoff
/// loop has nothing to offer it, and a refusal costs about 350 ms flat whatever the input
/// size, which is what makes it cheap enough to size by. See D-31.
/// </summary>
public sealed class TruncationIsNotSilentTests
{
    /// <summary>Records the options it was asked with, and fails on demand.</summary>
    private sealed class FakeGenerator(params Exception?[] failures)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        public List<bool?> TruncateAsked { get; } = [];
        public int Calls { get; private set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            TruncateAsked.Add(
                options?.AdditionalProperties?.TryGetValue("truncate", out bool t) == true ? t : null);

            var call = Calls++;
            if (call < failures.Length && failures[call] is { } ex) throw ex;

            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                values.Select(_ => new Embedding<float>(new float[] { 1f, 2f })).ToList()));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static async Task<float[][]> Embed(FakeGenerator generator, int maxRetries = 1)
    {
        var service = TestEmbedding.ServiceFor(generator, maxRetries);
        var result = await service.EmbedAsync(
            new EmbeddingTarget("ollama", "embeddinggemma"), EmbedPurpose.Raw, ["some text"]);
        return [.. result];
    }

    [Fact]
    public async Task TheProviderIsAskedNotToTruncate()
    {
        // The whole point. Left to its default, Ollama shortens the input and says nothing.
        var generator = new FakeGenerator();

        await Embed(generator);

        generator.TruncateAsked.ShouldBe([false]);
    }

    [Fact]
    public async Task ARefusalIsReportedRatherThanTruncated()
    {
        // The caller owns the text and can divide it; this layer cannot, and a vector for
        // part of a chunk stored under the whole chunk's id is the loss being avoided.
        var generator = new FakeGenerator(new InvalidOperationException(
            "input length exceeds maximum context length"));

        await Should.ThrowAsync<EmbeddingInputTooLongException>(() => Embed(generator));

        // Asked once, and never asked again with truncation allowed.
        generator.TruncateAsked.ShouldBe([false]);
    }

    [Fact]
    public async Task ARefusalIsNotRetried()
    {
        // Not transient: the same input fails the same way every time, so spending the
        // retry budget on it only makes the answer slower.
        var generator = new FakeGenerator(
            new InvalidOperationException("exceeds context window"),
            new InvalidOperationException("exceeds context window"),
            new InvalidOperationException("exceeds context window"));

        await Should.ThrowAsync<EmbeddingInputTooLongException>(() => Embed(generator));

        generator.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task TheRefusalSaysHowLongTheInputWas()
    {
        // The figure is what someone acts on, and it is the only part of the refusal that
        // survives into the indexer's log when a split is reported.
        var generator = new FakeGenerator(new InvalidOperationException("exceeds context window"));

        var ex = await Should.ThrowAsync<EmbeddingInputTooLongException>(() => Embed(generator));

        ex.Message.ShouldContain("longer than the model's context");
        ex.Message.ShouldContain("chars");
    }

    [Fact]
    public async Task AnOrdinaryFailureStillRetriesUntruncated()
    {
        // A timeout is not a length problem. Quietly switching to truncation because the
        // service was busy would lose text for a reason that had nothing to do with size.
        var generator = new FakeGenerator(new HttpRequestException("connection reset"));

        await Embed(generator);

        generator.TruncateAsked.ShouldBe([false, false]);
    }

    [Theory]
    [InlineData("input length exceeds maximum context length")]
    [InlineData("this model's maximum context length is 2048 tokens")]
    [InlineData("Requested tokens exceed context window of 2048")]
    [InlineData("input is too long for this model")]
    public async Task TheRefusalIsRecognisedHoweverItIsWorded(string message)
    {
        // No provider gives a code for this and each words it differently.
        var generator = new FakeGenerator(new InvalidOperationException(message));

        await Should.ThrowAsync<EmbeddingInputTooLongException>(() => Embed(generator));
    }

    [Theory]
    [InlineData("connection reset by peer")]
    [InlineData("503 Service Unavailable")]
    [InlineData("The operation has timed out")]
    public async Task AnUnrelatedMessageIsNotMistakenForOne(string message)
    {
        var generator = new FakeGenerator(new InvalidOperationException(message));

        await Embed(generator);

        // Retried as the transient failure it is, still asking not to truncate.
        generator.TruncateAsked.ShouldBe([false, false]);
    }
}

/// <summary>
/// An EmbeddingService wired to one fake generator. Kept here rather than shared with
/// EmbeddingProviderTests: two short stubs are cheaper to read than one helper that has to
/// serve two sets of concerns.
/// </summary>
internal static class TestEmbedding
{
    public static EmbeddingService ServiceFor(
        IEmbeddingGenerator<string, Embedding<float>> generator, int maxRetries)
    {
        var options = Options.Create(new DexiconOptions
        {
            Embedding = new EmbeddingOptions { Provider = "ollama" },
            // Backoff is real time, so these keep the budget small: the behaviour under
            // test is which options a retry is made with, not how long it waits.
            Ollama = new OllamaOptions { MaxRetries = maxRetries },
        });

        return new EmbeddingService(
            new OneGenerator(generator), new NoProfiles(), options,
            new MemoryCache(new MemoryCacheOptions()), NullLogger<EmbeddingService>.Instance);
    }

    private sealed class OneGenerator(IEmbeddingGenerator<string, Embedding<float>> generator)
        : IEmbeddingGeneratorFactory
    {
        public IEmbeddingGenerator<string, Embedding<float>> GeneratorFor(EmbeddingTarget target) => generator;

        public IReadOnlyCollection<string> ProviderNames => ["ollama"];

        public EmbeddingProviderOptions Options(string provider) =>
            new() { Kind = EmbeddingProviderKind.Ollama };
    }

    /// <summary>No task framing: text reaches the generator exactly as it was passed.</summary>
    private sealed class NoProfiles : IModelProfiles
    {
        public Task<ModelTemplates> ForAsync(EmbeddingTarget target, CancellationToken ct = default) =>
            Task.FromResult(ModelTemplates.Raw);

        public ModelTemplates? Suggest(string model) => null;
    }
}
