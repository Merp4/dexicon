using Dexicon.Core.Catalog;
using Dexicon.Core.Embedding;
using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// A chunk has to fit the model that reads it, and it did not.
///
/// The chunker sizes in tokens and enforces in characters, converting with a flat 4. The
/// probe measures the real ratio for each model and the real context, and neither reached
/// the chunker. This library ran a 2,065-token chunk size against a model that reads 2,048
/// tokens, converting at 4 where the measured ratio is 3.8: 8,260 characters asked of a
/// model whose context is nearer 7,780 of them. Ollama returns a vector for the part it
/// read, so the tail of every full-size chunk was embedded by nothing and 235 warnings in
/// one re-index were the only sign.
///
/// Both corrections are clamps in the same direction. A set already inside them is
/// untouched, which is the case for the 768-token default on every model here.
/// </summary>
public sealed class ChunkBudgetTests
{
    private static ChunkSet Set(int size, int overlap = 100) => new()
    {
        Id = "set", CorpusId = "corpus", Name = "default",
        ChunkSize = size, ChunkOverlap = overlap,
        BoundaryMode = "blank-line",
        EmbeddingProvider = "ollama", EmbeddingModel = "embeddinggemma",
        EmbeddingDimensions = 768, CollectionName = "c",
    };

    private static EmbeddingModelMeasurement Measured(int? context, double? ratio) => new()
    {
        Provider = "ollama", Model = "embeddinggemma",
        ContextTokens = context, CharsPerToken = ratio,
        Dimensions = 768, MeasuredUtc = DateTime.UtcNow,
    };

    [Fact]
    public void ASizeAboveTheModelsContextIsCappedBelowIt()
    {
        // The configuration this shipped with, against the model it shipped against.
        Set(2065).Options(Measured(2048, 3.8)).ChunkSizeTokens
            .ShouldBe(CodeChunker.UsableContext(2048));
    }

    [Fact]
    public void TheCapLeavesMarginRatherThanLandingOnTheCeiling()
    {
        // The property, stated without the constant. Capping AT the context was the first
        // answer: it stopped a chunk being larger than the model and still truncated about
        // 1.5% of them, because the ratio that sizes the chunk is an estimate and a chunk
        // aimed at the ceiling goes over whenever the estimate is a little high.
        var capped = Set(100_000).Options(Measured(2048, 3.8)).ChunkSizeTokens;

        capped.ShouldBeLessThan(2048);
        capped.ShouldBeGreaterThan(1365, "two thirds was measured as much worse for retrieval");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public void ATinyContextStillLeavesAChunkToBuild(int context)
    {
        // Nine tenths of a very small number rounds to nothing, and a budget of zero
        // characters is a chunker that produces no chunks and a file that indexes as empty.
        // The margin is a reduction, never a cancellation.
        CodeChunker.UsableContext(context).ShouldBeGreaterThan(0);

        var o = Set(2065).Options(Measured(context, 3.8));
        o.ChunkSizeTokens.ShouldBeGreaterThan(0);
        o.OverlapTokens.ShouldBeLessThan(o.ChunkSizeTokens);
        Should.NotThrow(() => CodeChunker.Chunk("a.txt", Prose(5_000), o));
    }

    [Fact]
    public void ASizeInsideTheContextIsLeftAlone()
    {
        // The clamp is a ceiling, not a target. The default must come through untouched.
        Set(768).Options(Measured(2048, 3.8)).ChunkSizeTokens.ShouldBe(768);
    }

    [Fact]
    public void TheConversionUsesTheMeasuredRatioNotTheConstant()
    {
        var o = Set(768).Options(Measured(2048, 3.8));

        o.CharsPerToken.ShouldBe(3.8);
        // 2,918 characters, where the flat 4 asked for 3,072 of a model that reads 2,918.
        CodeChunker.Chunk("a.txt", Prose(20_000), o)
            .Max(c => c.Content.Length).ShouldBeLessThanOrEqualTo((int)(768 * 3.8));
    }

    [Fact]
    public void AnUnmeasuredModelKeepsTheConfiguredSizeAndTheConstant()
    {
        // Nothing to reconcile against, so nothing is changed: what every set had before
        // any of this was measured.
        var o = Set(2065).Options(null);

        o.ChunkSizeTokens.ShouldBe(2065);
        o.CharsPerToken.ShouldBe(CodeChunker.CharsPerToken);
    }

    [Fact]
    public void AMeasurementMissingEitherNumberFallsBackForThatNumberOnly()
    {
        // A provider that reports no token counts leaves both null, and a partial row must
        // not take the other field down with it.
        Set(2065).Options(Measured(2048, null)).CharsPerToken.ShouldBe(CodeChunker.CharsPerToken);
        Set(2065).Options(Measured(null, 3.8)).ChunkSizeTokens.ShouldBe(2065);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ARatioThatIsNotAMeasurementIsIgnored(double nonsense)
    {
        // It reaches a multiply that sizes every chunk in the corpus, and zero would size
        // them all at nothing.
        Set(768).Options(Measured(2048, nonsense)).CharsPerToken.ShouldBe(CodeChunker.CharsPerToken);
    }

    [Fact]
    public void TheOverlapComesDownWithTheSize()
    {
        // The chunker rejects an overlap at or above the chunk size. Clamping the size
        // without the overlap would turn a legal set into one that throws mid-index.
        var o = Set(4000, overlap: 3000).Options(Measured(2048, 3.8));

        o.OverlapTokens.ShouldBeLessThan(o.ChunkSizeTokens);
        Should.NotThrow(() => CodeChunker.Chunk("a.txt", Prose(30_000), o));
    }

    [Fact]
    public void NoChunkExceedsWhatTheModelReads()
    {
        // The property the whole change exists for, asserted end to end on real chunking.
        const int context = 2048;
        const double ratio = 3.8;
        var o = Set(2065).Options(Measured(context, ratio));

        var chunks = CodeChunker.Chunk("book.txt", Prose(200_000), o);

        chunks.ShouldNotBeEmpty();
        chunks.Max(c => c.Content.Length).ShouldBeLessThanOrEqualTo((int)(context * ratio));
    }

    [Fact]
    public void ReprobingAModelIntoADifferentBudgetRechunksTheCorpus()
    {
        // The budget is part of the staleness key. Without it a re-probe that changes the
        // size leaves every chunk in place at the old one, sized for a number no longer in
        // force, and an incremental refresh reports there is nothing to do.
        var set = Set(2065);

        var before = CorpusIndexer.ChunkingFingerprint(
            set, "blob", ModelTemplates.Raw, set.Options(Measured(2048, 3.8)));
        var after = CorpusIndexer.ChunkingFingerprint(
            set, "blob", ModelTemplates.Raw, set.Options(Measured(1024, 3.8)));

        after.ShouldNotBe(before);
    }

    [Fact]
    public void TheRatioAloneChangesTheFingerprint()
    {
        var set = Set(768);

        CorpusIndexer.ChunkingFingerprint(set, "blob", ModelTemplates.Raw, set.Options(Measured(2048, 3.8)))
            .ShouldNotBe(CorpusIndexer.ChunkingFingerprint(
                set, "blob", ModelTemplates.Raw, set.Options(Measured(2048, 4.2))));
    }

    /// <summary>Prose in paragraphs, so the blank-line boundary mode has somewhere to cut.</summary>
    private static string Prose(int length)
    {
        const string para = "The indexer reads a folder and splits the text it finds into "
                          + "chunks small enough for the embedding model to read in one go. "
                          + "Nothing leaves the machine unless a hosted provider is chosen.\n\n";
        var sb = new System.Text.StringBuilder();
        while (sb.Length < length) sb.Append(para);
        return sb.ToString();
    }
}
