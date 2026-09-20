using Dexicon.Core.Embedding;
using Dexicon.Core.Indexing;
using Dexicon.Core.Search;

namespace Dexicon.Tests;

/// <summary>
/// The batch path, driven by a model that refuses.
///
/// ChunkSplitTests covers <c>Split</c> on its own, which leaves the part around it
/// untested: halving to find the offending chunk, recursing into a half that is itself
/// refused, numbering in write order, and the count that comes back. That arithmetic is
/// where the defects were — a tail numbered above every chunk in its own file, and a count
/// taken from the pre-split list — so it is the part that needs holding.
///
/// The fake writer refuses anything over a length, which is exactly what Ollama does with
/// <c>truncate:false</c>, and records what it was asked to store.
/// </summary>
public sealed class DivideAndWriteTests
{
    private static Chunk Chunk(string content, int index, int start = 1, int end = 1) =>
        new()
        {
            CorpusId = "c1",
            ChunkSetId = "s1",
            SourceId = "src1",
            FilePath = "docs/x.md",
            FileHash = "hash",
            StartLine = start,
            EndLine = end,
            ChunkIndex = index,
            Content = content,
        };

    /// <summary>Refuses any batch holding a chunk longer than <paramref name="limit"/>.</summary>
    private sealed class Writer(int limit)
    {
        public List<Chunk> Stored { get; } = [];
        public int Refusals { get; private set; }

        public Task Write(List<Chunk> batch)
        {
            if (batch.Exists(c => c.Content.Length > limit))
            {
                Refusals++;
                throw new EmbeddingInputTooLongException("too long");
            }

            Stored.AddRange(batch);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task AnAcceptedBatchIsNumberedFromTheGivenIndex()
    {
        var writer = new Writer(limit: 100);
        List<Chunk> batch = [Chunk("a", 0), Chunk("b", 1), Chunk("c", 2)];

        var written = await CorpusIndexer.DivideAndWriteAsync(batch, firstIndex: 10, writer.Write);

        written.ShouldBe(3);
        writer.Refusals.ShouldBe(0);
        writer.Stored.Select(c => c.ChunkIndex).ShouldBe([10, 11, 12]);
    }

    [Fact]
    public async Task OneOverlongChunkIsFoundByHalvingRatherThanOneCallEach()
    {
        // Which input was refused is not reported, so the batch is halved until the
        // offender is alone. Eight chunks is three refusals plus the one on the chunk
        // itself, against eight calls if each were tried separately.
        var writer = new Writer(limit: 10);
        List<Chunk> batch = [.. Enumerable.Range(0, 8)
            .Select(i => Chunk(i == 5 ? new string('x', 20) : "short", i))];

        var written = await CorpusIndexer.DivideAndWriteAsync(batch, 0, writer.Write);

        written.ShouldBe(9);                    // the long one became two
        writer.Refusals.ShouldBeLessThan(8);
    }

    [Fact]
    public async Task SplitHalvesLandBetweenTheirNeighbours()
    {
        // The defect this replaced: the tail took the next index above every chunk in the
        // file, so it sorted to the end of its own document and fell outside its own
        // neighbourhood. ContextService selects neighbours by the distance between
        // indices, so the numbering has to be the reading order.
        var writer = new Writer(limit: 10);
        List<Chunk> batch = [Chunk("one", 0), Chunk(new string('x', 20), 1), Chunk("three", 2)];

        await CorpusIndexer.DivideAndWriteAsync(batch, 0, writer.Write);

        var byIndex = writer.Stored.OrderBy(c => c.ChunkIndex).ToList();
        byIndex.Select(c => c.ChunkIndex).ShouldBe([0, 1, 2, 3]);
        byIndex[0].Content.ShouldBe("one");
        (byIndex[1].Content + byIndex[2].Content).ShouldBe(new string('x', 20));
        byIndex[3].Content.ShouldBe("three");
    }

    [Fact]
    public async Task AHalfThatIsStillTooLongSplitsAgainAndKeepsTheNumbersDense()
    {
        // One chunk, sixteen times the limit: it takes repeated splitting, and each level
        // has to offset the right half by however many the left actually needed rather
        // than by one.
        var writer = new Writer(limit: 10);

        var written = await CorpusIndexer.DivideAndWriteAsync([Chunk(new string('x', 160), 0)], 0, writer.Write);

        written.ShouldBeGreaterThan(8);
        writer.Stored.Count.ShouldBe(written);
        writer.Stored.Select(c => c.ChunkIndex).Order().ShouldBe(Enumerable.Range(0, written));
        string.Concat(writer.Stored.OrderBy(c => c.ChunkIndex).Select(c => c.Content))
            .ShouldBe(new string('x', 160));
    }

    [Fact]
    public async Task TheReturnedCountIsWhatWasStored()
    {
        // It used to be the pre-split list, so a file that split reported fewer chunks in
        // the UI than Qdrant held.
        var writer = new Writer(limit: 10);
        List<Chunk> batch = [Chunk("short", 0), Chunk(new string('x', 40), 1)];

        var written = await CorpusIndexer.DivideAndWriteAsync(batch, 0, writer.Write);

        written.ShouldBe(writer.Stored.Count);
        written.ShouldBeGreaterThan(batch.Count);
    }

    [Fact]
    public async Task NoTextIsLostAcrossAWholeRefusedBatch()
    {
        // The property the design rests on, asserted through the batch path rather than on
        // Split alone: everything handed in is stored, once, in order.
        var writer = new Writer(limit: 20);
        List<Chunk> batch = [.. Enumerable.Range(0, 6).Select(i => Chunk(new string((char)('a' + i), 70), i))];
        var original = string.Concat(batch.Select(c => c.Content));

        await CorpusIndexer.DivideAndWriteAsync(batch, 0, writer.Write);

        string.Concat(writer.Stored.OrderBy(c => c.ChunkIndex).Select(c => c.Content)).ShouldBe(original);
    }

    [Fact]
    public async Task AnEmptyBatchWritesNothing()
    {
        var writer = new Writer(limit: 10);

        (await CorpusIndexer.DivideAndWriteAsync([], 7, writer.Write)).ShouldBe(0);

        writer.Stored.ShouldBeEmpty();
    }

    [Fact]
    public async Task AChunkTooSmallToDivideIsReportedRatherThanLooping()
    {
        // Split returns null below two characters, so the filter stops matching and the
        // refusal leaves. A file that cannot be indexed has to say so; spinning here would
        // be the hang this whole change exists to remove.
        var writer = new Writer(limit: 0);

        await Should.ThrowAsync<EmbeddingInputTooLongException>(() =>
            CorpusIndexer.DivideAndWriteAsync([Chunk("x", 0)], 0, writer.Write));
    }
}
