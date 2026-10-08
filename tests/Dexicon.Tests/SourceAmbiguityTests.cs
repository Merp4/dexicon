using Dexicon.Core.Search;
using Dexicon.Core.Vectors;

namespace Dexicon.Tests;

/// <summary>
/// A file path is not a unique name for a file.
///
/// Chunks record `file_path` relative to their SOURCE root, not to the corpus. That is
/// invisible with one source and wrong with two: this repository's own book corpus has
/// sources `manuals/AI` and `manuals/Philosophy`, and BOTH contain "Installation Guide, 2nd
/// Edition.pdf" and "An Invented Handbook.pdf". Two different files, one path,
/// one corpus, one chunk set.
///
/// Everything downstream assumed paths were unique, and `source_id`, written into every
/// point's payload and indexed as a keyword since chunk sets landed, was never read.
/// </summary>
public class SourceAmbiguityTests
{
    private static SearchHit Chunk(string sourceId, int index, int startLine, string content) =>
        new()
        {
            CorpusId = "corpus",
            SourceId = sourceId,
            FilePath = "Installation Guide, 2nd Edition.pdf",
            StartLine = startLine,
            EndLine = startLine + 9,
            ChunkIndex = index,
            Content = content,
            Score = 0f,
        };

    [Fact]
    public void DeletingOneSourceFileDoesNotTakeTheOtherSourcesCopy()
    {
        // The filter decides which points die. Without source_id, refreshing the AI copy
        // deletes the Philosophy copy too, and an incremental refresh only rewrites the
        // file that changed, so the other disappears until a full reindex. Lost content,
        // no error, no log line.
        var scoped = QdrantVectorStore.FileChunksFilter("set-1", "source-ai", "Installation Guide.pdf");

        var keys = scoped.Must
            .Select(c => c.Field?.Key)
            .Where(k => k is not null)
            .ToList();

        keys.ShouldContain("chunk_set_id");
        keys.ShouldContain("file_path");
        keys.ShouldContain("source_id");
    }

    [Fact]
    public void ReadingAFileCanStillSpanEverySourceWhenThatIsAskedFor()
    {
        // The read path keeps the old behaviour available; only the DELETE is unconditional.
        var unscoped = QdrantVectorStore.FileChunksFilter("set-1", null, "Installation Guide.pdf");

        unscoped.Must.Select(c => c.Field?.Key).ShouldNotContain("source_id");
    }

    [Fact]
    public void TwoBooksWithOnePathGroupIntoTwoSources()
    {
        // The property every by-path read relies on to notice the problem at all.
        var chunks = new[]
        {
            Chunk("source-ai", 0, 1, "Propositional logic, as an AI text presents it."),
            Chunk("source-philosophy", 0, 1, "Propositional logic, as a philosophy text presents it."),
            Chunk("source-ai", 1, 11, "Predicate calculus follows."),
        };

        var file = FileSources.Choose(chunks);

        file.SourceCount.ShouldBe(2);
        file.Ambiguous.ShouldBeTrue();
        // Interleaved by chunk index, which is how they used to be stitched.
        chunks.OrderBy(c => c.ChunkIndex).Select(c => c.SourceId).ShouldBe(
            ["source-ai", "source-philosophy", "source-ai"]);
    }

    [Fact]
    public void StitchingOneSourceReadsAsOneDocument()
    {
        // The fix, stated as the property that matters: the passage a model quotes must
        // come from a single file. Mixed, it reads as a coherent argument that no book
        // makes, with line numbers inviting it to be quoted.
        var chunks = new[]
        {
            Chunk("source-ai", 0, 1, "Propositional logic, as an AI text presents it."),
            Chunk("source-philosophy", 0, 1, "Propositional logic, as a philosophy text presents it."),
            Chunk("source-ai", 1, 11, "Predicate calculus follows."),
        };

        var file = FileSources.Choose(chunks);
        var chosen = file.Chunks;

        file.SourceId.ShouldBe("source-ai");
        chosen.Select(c => c.SourceId).Distinct().Count().ShouldBe(1);
        chosen.Count.ShouldBe(2);
        chosen[0].Content.ShouldContain("AI text");
        chosen.ShouldAllBe(c => c.SourceId == "source-ai");
    }

    [Fact]
    public void TheChoiceIsDeterministicWhenSourcesAreTheSameSize()
    {
        // Two copies of equal length must not alternate between calls: an agent that reads
        // a passage twice and gets two different books has no way to notice.
        var a = new[] { Chunk("source-b", 0, 1, "b"), Chunk("source-a", 0, 1, "a") };

        FileSources.Choose(a).SourceId.ShouldBe("source-a");
        FileSources.Choose([.. a.Reverse()]).SourceId.ShouldBe("source-a");
    }

    [Fact]
    public void APathOneSourceHoldsComesBackUnchangedWithNoWarning()
    {
        var chunks = new[] { Chunk("source-ai", 0, 1, "a"), Chunk("source-ai", 1, 11, "b") };

        var file = FileSources.Choose(chunks);

        file.Chunks.ShouldBe(chunks);
        file.SourceCount.ShouldBe(1);
        file.Ambiguous.ShouldBeFalse();
        file.Warning.ShouldBeNull();
    }

    [Fact]
    public void ANoChunkPathHasNoSourceAndNoWarning()
    {
        var file = FileSources.Choose([]);

        file.Chunks.ShouldBeEmpty();
        file.SourceId.ShouldBeNull();
        file.SourceCount.ShouldBe(0);
        file.Warning.ShouldBeNull();
    }

    [Fact]
    public void TheWarningCountsTheSources()
    {
        var chunks = new[]
        {
            Chunk("source-a", 0, 1, "a"), Chunk("source-b", 0, 1, "b"), Chunk("source-c", 0, 1, "c"),
        };

        FileSources.Choose(chunks).Warning.ShouldNotBeNull()
            .ShouldStartWith("3 sources in this corpus contain a file at that path.");
    }

    [Fact]
    public void TwoSourcesHoldingOnePathDoNotDeriveTheSamePointId()
    {
        // The quieter half of the same bug, and the one the delete fix did NOT cover.
        // Point ids were derived from (chunk set, path, index). Two sources of one corpus
        // holding "Installation Guide.pdf" therefore produced IDENTICAL ids for every
        // chunk, and the second source's upsert overwrote the first: one book's vectors
        // gone, both files still listed as indexed, nothing reporting a problem.
        var ai = QdrantVectorStore.DeterministicId("set-1", "source-ai", "Installation Guide.pdf", 0);
        var philosophy = QdrantVectorStore.DeterministicId("set-1", "source-philosophy", "Installation Guide.pdf", 0);

        ai.ShouldNotBe(philosophy);
    }

    [Fact]
    public void TheSameChunkKeepsTheSameIdSoReindexingStaysIdempotent()
    {
        // The property the deterministic id exists for. If it changed per run, every
        // refresh would duplicate every chunk instead of replacing it.
        var first = QdrantVectorStore.DeterministicId("set-1", "source-ai", "a.pdf", 7);
        var second = QdrantVectorStore.DeterministicId("set-1", "source-ai", "a.pdf", 7);

        first.ShouldBe(second);
        Guid.TryParse(first, out _).ShouldBeTrue();
    }

    [Fact]
    public void ChunkIndexStillSeparatesChunksOfOneFile()
    {
        QdrantVectorStore.DeterministicId("set-1", "src", "a.pdf", 0)
            .ShouldNotBe(QdrantVectorStore.DeterministicId("set-1", "src", "a.pdf", 1));
    }

    [Fact]
    public void TwoChunkSetsDoNotShareIdsEither()
    {
        // Pre-existing behaviour worth keeping: a corpus mid-migration holds two sets, and
        // one overwriting the other would corrupt whichever finished second.
        QdrantVectorStore.DeterministicId("set-1", "src", "a.pdf", 0)
            .ShouldNotBe(QdrantVectorStore.DeterministicId("set-2", "src", "a.pdf", 0));
    }
}
