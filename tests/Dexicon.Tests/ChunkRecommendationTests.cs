using Dexicon.Core.Catalog;
using Dexicon.Core.Embedding;
using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// What the probe tells a new corpus to use for a chunk size.
///
/// The UI puts this number straight into the chunk size field, so it is the size most
/// corpora will be built at. It was two thirds of the model's context, headroom against a
/// chunker that converted tokens to characters with a flat 4 and had nothing checking the
/// result. That conversion now uses the measured ratio and the size is capped at the
/// context, so the headroom is enforced; recommending it as well charged for it twice.
///
/// The cost was measured on a 96-book library. Two thirds is 1,365 tokens on this model,
/// which the measured ratio makes 5,187 characters, and a run at 5,460 characters retrieved
/// much worse than one at 7,480 while 7,480 and 8,260 were indistinguishable. The
/// recommendation pointed below a configuration already rejected on evidence.
/// </summary>
public sealed class ChunkRecommendationTests
{
    private static ChunkSet Set(int size) => new()
    {
        Id = "s", CorpusId = "c", Name = "default", ChunkSize = size, ChunkOverlap = 100,
        BoundaryMode = "blank-line", EmbeddingProvider = "ollama",
        EmbeddingModel = "m", EmbeddingDimensions = 768, CollectionName = "c",
    };

    private static EmbeddingModelMeasurement Measured(int context, double ratio) => new()
    {
        Provider = "ollama", Model = "m", ContextTokens = context,
        CharsPerToken = ratio, Dimensions = 768, MeasuredUtc = DateTime.UtcNow,
    };

    [Fact]
    public void TheRecommendationIsTheContext()
    {
        ModelProbe.RecommendedTokens(contextTokens: 2048, budgetChars: 7850, charsPerToken: 3.8)
            .ShouldBe(2048);
    }

    [Fact]
    public void TakingTheRecommendationNeedsNoClampAfterwards()
    {
        // The property that makes recommending the whole context safe: a set built from
        // this number is already inside the cap, so it indexes without a warning and
        // without a budget quietly smaller than the one configured.
        var recommended = ModelProbe.RecommendedTokens(2048, 7850, 3.8);

        Set(recommended).Options(Measured(2048, 3.8)).ChunkSizeTokens.ShouldBe(recommended);
    }

    [Fact]
    public void ItNoLongerPointsBelowWhatWasMeasuredAsWorse()
    {
        // 5,460 characters retrieved much worse than 7,480 on the measured library. The
        // recommendation has to land above that, in characters, which is the unit the
        // model actually sees.
        const double ratio = 3.8;
        var chars = ModelProbe.RecommendedTokens(2048, 7850, ratio) * ratio;

        chars.ShouldBeGreaterThan(5460);
    }

    [Fact]
    public void WithoutATokenCountTheCharacterEstimateKeepsItsHeadroom()
    {
        // No context means no cap, so nothing else is guarding this number and the two
        // thirds is the only headroom there is.
        ModelProbe.RecommendedTokens(contextTokens: null, budgetChars: 7850, charsPerToken: 3.8)
            .ShouldBe((int)(7850 / 3.8));
    }

    [Fact]
    public void WithoutARatioEitherItFallsBackToTheConstant()
    {
        ModelProbe.RecommendedTokens(contextTokens: null, budgetChars: 8000, charsPerToken: null)
            .ShouldBe(8000 / CodeChunker.CharsPerToken);
    }
}
