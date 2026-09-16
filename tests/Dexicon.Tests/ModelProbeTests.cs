using Dexicon.Core.Embedding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dexicon.Tests;

/// <summary>
/// The model probe measures what a model will actually accept, without indexing.
///
/// It exists because of a bug that nothing could detect: an EPUB produced chunks
/// averaging 32,000 characters, the embedding model silently truncated every one of them,
/// and roughly 95% of the book was in no index anywhere while the corpus, the job and the
/// file all reported success. A truncating model returns a perfectly good vector for the
/// part it read.
///
/// The probe works because truncation, though silent, is EMPIRICALLY VISIBLE: change only
/// the end of an input and see whether the vector moves. These tests stand in a fake
/// model with a known limit and check the probe finds it.
/// </summary>
public sealed class ModelProbeTests
{
    private static readonly EmbeddingTarget Target = new("test", "fake");

    [Theory]
    [InlineData(2_000)]
    [InlineData(8_192)]
    [InlineData(32_768)]
    public async Task FindsATruncatingModelsRealLimit(int limit)
    {
        var model = new TruncatingModel(limit, errorsOnOverflow: false);

        var capabilities = await new ModelProbe(model, NullLogger<ModelProbe>.Instance).RunAsync(Target);

        capabilities.TruncatesSilently.ShouldBeTrue();
        capabilities.MaxInputChars.ShouldNotBeNull();

        // Bisection stops at a 256-character bracket, so the answer lands just under the
        // true limit. Under, never over: a budget above the real limit is the failure
        // this whole exercise is about.
        capabilities.MaxInputChars!.Value.ShouldBeLessThanOrEqualTo(limit);
        capabilities.MaxInputChars!.Value.ShouldBeGreaterThan(limit - 512);
    }

    [Fact]
    public async Task TheRecommendationLeavesHeadroomUnderTheLimit()
    {
        var model = new TruncatingModel(12_000, errorsOnOverflow: false);

        var capabilities = await new ModelProbe(model, NullLogger<ModelProbe>.Instance).RunAsync(Target);

        // The measurement is in characters and the model counts tokens; dense text hits
        // the same token limit in fewer characters. The headroom is what absorbs that,
        // so a recommendation at exactly the measured limit would be wrong for code.
        capabilities.RecommendedChunkChars.ShouldBeLessThan(capabilities.MaxInputChars!.Value);
        capabilities.RecommendedChunkTokens
            .ShouldBe(capabilities.RecommendedChunkChars / Dexicon.Core.Indexing.CodeChunker.CharsPerToken);
    }

    [Fact]
    public async Task AModelThatErrorsIsReportedAsSaferThanOneThatTruncates()
    {
        // Erroring is the good behaviour: the failure is visible where it happens, rather
        // than surfacing months later as content that was never searchable.
        var model = new TruncatingModel(8_000, errorsOnOverflow: true);

        var capabilities = await new ModelProbe(model, NullLogger<ModelProbe>.Instance).RunAsync(Target);

        capabilities.TruncatesSilently.ShouldBeFalse();
        capabilities.Summary.ShouldContain("rejects more");
    }

    [Fact]
    public async Task AModelWithNoPracticalLimitIsSaidToHaveNone()
    {
        var model = new TruncatingModel(int.MaxValue, errorsOnOverflow: false);

        var capabilities = await new ModelProbe(model, NullLogger<ModelProbe>.Instance).RunAsync(Target);

        capabilities.MaxInputChars.ShouldBeNull();
        capabilities.TruncatesSilently.ShouldBeFalse();
        capabilities.Summary.ShouldContain("No practical limit");
    }

    [Fact]
    public async Task TheProbeReportsDimensionsAndItsOwnCost()
    {
        var model = new TruncatingModel(8_000, errorsOnOverflow: false, dimensions: 384);

        var capabilities = await new ModelProbe(model, NullLogger<ModelProbe>.Instance).RunAsync(Target);

        capabilities.Dimensions.ShouldBe(384);

        // Bisecting a 256KB range to a 256-char bracket is ten steps at two calls each,
        // plus the dimension probe and the overflow check. Bounded, and worth stating:
        // this runs against a live model and someone pays for it.
        capabilities.EmbedCalls.ShouldBeInRange(10, 40);
    }

    [Fact]
    public async Task IndexesNothing()
    {
        // The point of the feature. Everything it learns comes from embedding throwaway
        // filler — no corpus, no chunk set, no document.
        var model = new TruncatingModel(8_000, errorsOnOverflow: false);

        await new ModelProbe(model, NullLogger<ModelProbe>.Instance).RunAsync(Target);

        model.Inputs.ShouldAllBe(s => s.Contains("alpha beta gamma") || s == "dimension probe");
    }

    /// <summary>
    /// A model that reads the first <c>limit</c> characters and ignores the rest — the
    /// behaviour every embedding model tested so far actually has.
    /// </summary>
    private sealed class TruncatingModel(int limit, bool errorsOnOverflow, int dimensions = 768)
        : IEmbeddingService
    {
        public List<string> Inputs { get; } = [];

        public int KnownDimensions(EmbeddingTarget target) => 0;

        public Task<int> ProbeDimensionsAsync(EmbeddingTarget target, CancellationToken ct = default) =>
            Task.FromResult(dimensions);

        public Task<IReadOnlyList<float[]>> EmbedAsync(
            EmbeddingTarget target, EmbedPurpose purpose, IReadOnlyList<string> inputs,
            CancellationToken ct = default)
        {
            // The probe must always ask for Raw: a task template would add characters of
            // its own and shift every measurement by the length of a prefix.
            purpose.ShouldBe(EmbedPurpose.Raw, "the probe measures the model, not a document");

            var vectors = new List<float[]>();

            foreach (var input in inputs)
            {
                Inputs.Add(input);

                if (input.Length > limit && errorsOnOverflow)
                    throw new EmbeddingUnavailableException($"input of {input.Length} exceeds {limit}");

                // Only the part within the limit reaches the model, so only that part can
                // affect the vector: identical prefix, identical vector.
                //
                // Seeded pseudo-random rather than "hash in element zero", which was the
                // first attempt and was useless — one huge dominant component makes the
                // cosine similarity of ANY two vectors about 1.0, so every input looked
                // truncated. A real embedding spreads meaning across all the dimensions,
                // and the fake has to as well or it tests nothing.
                var visible = input.Length <= limit ? input : input[..limit];
                var rng = new Random(visible.GetHashCode(StringComparison.Ordinal));
                var vector = new float[dimensions];
                for (var i = 0; i < dimensions; i++) vector[i] = (float)(rng.NextDouble() - 0.5);
                vectors.Add(vector);
            }

            return Task.FromResult<IReadOnlyList<float[]>>(vectors);
        }
    }
}
