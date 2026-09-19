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

        // The measurement is in characters and the model counts tokens. Headroom absorbs
        // the part of that gap a single ratio cannot describe, since prose, code and CJK
        // do not share one, so a recommendation at the measured limit would be wrong for
        // the densest of them.
        capabilities.RecommendedChunkChars.ShouldBeLessThan(capabilities.MaxInputChars!.Value);

        // The token figure never exceeds the model's own context, counted in tokens. It
        // used to be the character budget divided by a ratio measured on other text, which
        // applied a density correction a second time and put the recommendation ABOVE the
        // context. That is the property; it used to be bought with a third of the context
        // held back, and it is now bought by the chunker capping and converting with the
        // measured ratio, which costs nothing in retrieval.
        var contextTokens = await model.CountTokensAsync(Target, new string('x', capabilities.MaxInputChars!.Value));
        capabilities.RecommendedChunkTokens.ShouldBeLessThanOrEqualTo(contextTokens!.Value);
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
        // filler: no corpus, no chunk set, no document.
        var model = new TruncatingModel(8_000, errorsOnOverflow: false);

        await new ModelProbe(model, NullLogger<ModelProbe>.Instance).RunAsync(Target);

        model.Inputs.ShouldAllBe(s => s.Contains("alpha beta gamma") || s == "dimension probe");
    }

    [Fact]
    public async Task MeasuresCharactersPerTokenRatherThanAssumingFour()
    {
        // The chunker has always divided by a flat 4. That is a fair average for English
        // prose and wrong in the direction that hurts for code and CJK, which reach the
        // same token limit in far fewer characters, so a "768 token" chunk of minified
        // JavaScript can be two or three times that, and the model truncates it without
        // reporting it. The probe now asks the model's own tokenizer.
        var model = new TruncatingModel(limit: 4_000, errorsOnOverflow: false);

        var caps = await new ModelProbe(model, NullLogger<ModelProbe>.Instance).RunAsync(Target);

        caps.CharsPerToken.ShouldNotBeNull();
        caps.CharsPerToken.Value.ShouldBe(3.0, 0.35);
        caps.CharsPerToken.Value.ShouldNotBe(Dexicon.Core.Indexing.CodeChunker.CharsPerToken);
    }

    [Fact]
    public async Task TheTokenRecommendationIsCountedInTokens()
    {
        // A chunk budget is in tokens and the limit the model enforces is in tokens, so
        // the conversion has no business being in the middle of it.
        var model = new TruncatingModel(limit: 4_000, errorsOnOverflow: false);

        var caps = await new ModelProbe(model, NullLogger<ModelProbe>.Instance).RunAsync(Target);

        // Most of the context, with the rest left as margin: the chunker converts to
        // characters with a measured ratio, and a chunk aimed at the ceiling goes over
        // whenever that estimate is a little high.
        var contextTokens = (await model.CountTokensAsync(Target, new string('x', caps.MaxInputChars!.Value)))!.Value;
        caps.RecommendedChunkTokens.ShouldBe(Dexicon.Core.Indexing.CodeChunker.UsableContext(contextTokens));
    }

    [Fact]
    public async Task TheRecommendationSurvivesATokenizerThatIsNotUniform()
    {
        // The regression. The character ceiling is measured on the probe's filler, four
        // repeated words, which tokenizes about as well as text ever does; chars-per-token
        // is averaged over prose, code and JSON, which is far denser. Dividing the first by
        // the second applied a density correction twice in opposite directions and the
        // headroom cancelled: against `embeddinggemma` it recommended 2,065 tokens for a
        // 2,048-token context, and about 4% of real embeds were clamped.
        //
        // The old stub could not show this, because it charged three characters a token for
        // every input alike. A real tokenizer does not.
        var model = new WordishModel(limit: 12_000);

        var caps = await new ModelProbe(model, NullLogger<ModelProbe>.Instance).RunAsync(Target);

        var contextTokens = (await model.CountTokensAsync(Target, Filler(caps.MaxInputChars!.Value)))!.Value;

        // The recommendation has to fit the context, whatever the text. That is what the
        // old arithmetic broke, and it is the part that matters: a recommendation above the
        // context is a chunk size nothing can honour.
        caps.RecommendedChunkTokens.ShouldBeLessThanOrEqualTo(contextTokens);

        // And the old arithmetic would not have. Kept as an assertion rather than a comment
        // so the bug cannot quietly return.
        var oldWay = (int)(caps.RecommendedChunkChars / caps.CharsPerToken!.Value);
        oldWay.ShouldBeGreaterThan(caps.RecommendedChunkTokens);
    }

    /// <summary>The probe's own filler, so a test can count tokens of the same text it measured.</summary>
    private static string Filler(int length)
    {
        const string word = "alpha beta gamma delta ";
        var text = new System.Text.StringBuilder(length + word.Length);
        while (text.Length < length) text.Append(word);
        return text.ToString(0, length);
    }

    /// <summary>
    /// A tokenizer whose density depends on the text, the way every real one does: a word
    /// is a token, and each run of punctuation is another. Repetitive prose comes out near
    /// six characters a token; JSON comes out near two.
    /// </summary>
    private sealed class WordishModel(int limit, int dimensions = 768) : IEmbeddingService
    {
        public int KnownDimensions(EmbeddingTarget target) => 0;

        public Task<int> ProbeDimensionsAsync(EmbeddingTarget target, CancellationToken ct = default) =>
            Task.FromResult(dimensions);

        public Task<int?> CountTokensAsync(
            EmbeddingTarget target, string text, CancellationToken ct = default) =>
            Task.FromResult<int?>(Math.Max(1, Tokens(text)));

        private static int Tokens(string text)
        {
            var words = text.Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
            var punctuation = text.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c));
            return words + punctuation;
        }

        public Task<IReadOnlyList<float[]>> EmbedAsync(
            EmbeddingTarget target, EmbedPurpose purpose, IReadOnlyList<string> inputs, string? source = null,
            CancellationToken ct = default)
        {
            var vectors = new List<float[]>();

            foreach (var input in inputs)
            {
                // Truncated at the limit, silently, exactly as Ollama does by default.
                var seen = input.Length <= limit ? input : input[..limit];
                var rng = new Random(seen.GetHashCode(StringComparison.Ordinal));
                vectors.Add([.. Enumerable.Range(0, dimensions).Select(_ => (float)rng.NextDouble())]);
            }

            return Task.FromResult<IReadOnlyList<float[]>>(vectors);
        }
    }

    [Fact]
    public async Task SaysNothingRatherThanGuessingWhenTheProviderReportsNoTokens()
    {
        // OpenAI reports usage; a provider that does not must not be given a made-up
        // ratio, because a measurement and an assumption look identical once stored.
        var model = new SilentAboutTokens();

        var caps = await new ModelProbe(model, NullLogger<ModelProbe>.Instance).RunAsync(Target);

        caps.CharsPerToken.ShouldBeNull();
    }

    /// <summary>A model that embeds happily and never reports a token count.</summary>
    private sealed class SilentAboutTokens : IEmbeddingService
    {
        public int KnownDimensions(EmbeddingTarget target) => 0;

        public Task<int> ProbeDimensionsAsync(EmbeddingTarget target, CancellationToken ct = default) =>
            Task.FromResult(768);

        public Task<int?> CountTokensAsync(
            EmbeddingTarget target, string text, CancellationToken ct = default) =>
            Task.FromResult<int?>(null);

        public Task<IReadOnlyList<float[]>> EmbedAsync(
            EmbeddingTarget target, EmbedPurpose purpose, IReadOnlyList<string> inputs, string? source = null,
            CancellationToken ct = default)
        {
            var rng = new Random(7);
            return Task.FromResult<IReadOnlyList<float[]>>(
                [.. inputs.Select(_ => Enumerable.Range(0, 768).Select(_ => (float)rng.NextDouble()).ToArray())]);
        }
    }

    /// <summary>
    /// A model that reads the first <c>limit</c> characters and ignores the rest, which
    /// is the behaviour of every embedding model tested so far.
    /// </summary>
    private sealed class TruncatingModel(int limit, bool errorsOnOverflow, int dimensions = 768)
        : IEmbeddingService
    {
        public List<string> Inputs { get; } = [];

        public int KnownDimensions(EmbeddingTarget target) => 0;

        public Task<int> ProbeDimensionsAsync(EmbeddingTarget target, CancellationToken ct = default) =>
            Task.FromResult(dimensions);

        /// <summary>
        /// A fixed three characters a token, which is nothing like any real tokenizer and
        /// is exactly the point: the probe must REPORT what it measured rather than
        /// substitute the 4 the chunker assumes.
        /// </summary>
        public Task<int?> CountTokensAsync(
            EmbeddingTarget target, string text, CancellationToken ct = default) =>
            Task.FromResult<int?>(Math.Max(1, text.Length / 3));

        public Task<IReadOnlyList<float[]>> EmbedAsync(
            EmbeddingTarget target, EmbedPurpose purpose, IReadOnlyList<string> inputs, string? source = null,
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
                // first attempt and was useless: one dominant component makes the cosine
                // similarity of any two vectors about 1.0, so every input looked
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
