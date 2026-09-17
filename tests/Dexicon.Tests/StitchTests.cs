using Dexicon.Mcp;

namespace Dexicon.Tests;

/// <summary>
/// get_context hands an agent the lines around a search hit. Those lines come from
/// chunks that deliberately OVERLAP, so stitching them back together is where the tool
/// either reproduces the file faithfully or quietly invents it.
///
/// Both failure modes are invisible to the caller: duplicated lines look like real
/// repetition in the source, and a silently closed gap looks like contiguous code.
/// </summary>
public sealed class StitchTests
{
    private static string Lines(int from, int to) =>
        string.Join('\n', Enumerable.Range(from, to - from + 1).Select(i => $"line {i}"));

    [Fact]
    public void OverlappingChunksAreJoinedWithoutRepeatingTheSharedLines()
    {
        // Two chunks that share lines 8-10, exactly as the chunker emits them.
        var stitched = DexiconTools.Stitch(
        [
            (1, 10, Lines(1, 10)),
            (8, 20, Lines(8, 20)),
        ]);

        var result = stitched.TrimEnd('\n').Split('\n');
        result.ShouldBe([.. Enumerable.Range(1, 20).Select(i => $"line {i}")]);
    }

    [Fact]
    public void ThreeWayOverlapStillProducesTheFileOnce()
    {
        var stitched = DexiconTools.Stitch(
        [
            (1, 10, Lines(1, 10)),
            (6, 15, Lines(6, 15)),
            (11, 25, Lines(11, 25)),
        ]);

        stitched.TrimEnd('\n').Split('\n')
            .ShouldBe([.. Enumerable.Range(1, 25).Select(i => $"line {i}")]);
    }

    [Fact]
    public void AChunkFullyContainedInAnotherIsDropped()
    {
        var stitched = DexiconTools.Stitch(
        [
            (1, 30, Lines(1, 30)),
            (5, 12, Lines(5, 12)),   // already covered
            (31, 33, Lines(31, 33)),
        ]);

        stitched.TrimEnd('\n').Split('\n')
            .ShouldBe([.. Enumerable.Range(1, 33).Select(i => $"line {i}")]);
    }

    [Fact]
    public void AGapIsMarkedRatherThanClosedSilently()
    {
        // The dangerous case. Line 11 is NOT line 41, and a model reading them adjacent
        // would reason about code that does not exist.
        var stitched = DexiconTools.Stitch(
        [
            (1, 10, Lines(1, 10)),
            (41, 50, Lines(41, 50)),
        ]);

        stitched.ShouldContain("lines 11-40 not indexed");
        stitched.ShouldContain("line 10");
        stitched.ShouldContain("line 41");
    }

    [Fact]
    public void ExactlyAdjacentChunksAreNotTreatedAsAGap()
    {
        // 1-10 then 11-20 is contiguous; a marker here would be noise.
        var stitched = DexiconTools.Stitch([(1, 10, Lines(1, 10)), (11, 20, Lines(11, 20))]);

        stitched.ShouldNotContain("not indexed");
        stitched.TrimEnd('\n').Split('\n')
            .ShouldBe([.. Enumerable.Range(1, 20).Select(i => $"line {i}")]);
    }

    [Fact]
    public void ASingleChunkComesBackVerbatim()
    {
        DexiconTools.Stitch([(1, 3, "alpha\nbeta\ngamma")]).ShouldBe("alpha\nbeta\ngamma\n");
    }

    [Fact]
    public void NoChunksProducesNothing() => DexiconTools.Stitch([]).ShouldBe("");

    /// <summary>
    /// The property that matters most: chunk a file, stitch it back, get the file.
    ///
    /// Chunking and stitching are written independently and each looked right on its own.
    /// This is the only test that holds them to the one thing they jointly promise.
    /// </summary>
    [Theory]
    [InlineData(768, 100, "language-aware")]
    [InlineData(256, 40, "language-aware")]
    [InlineData(512, 64, "blank-line")]
    [InlineData(128, 0, "none")]
    public void ChunkingThenStitchingReproducesTheFile(int size, int overlap, string mode)
    {
        var source = string.Join('\n', Enumerable.Range(1, 400).Select(i =>
            i % 17 == 0 ? "" :
            i % 23 == 0 ? $"## Heading {i}" :
            $"line {i}: some ordinary prose that takes up a reasonable amount of room"));

        var chunks = Dexicon.Core.Indexing.CodeChunker.Chunk("notes.md", source, size, overlap, mode);

        var stitched = DexiconTools.Stitch(
            chunks.OrderBy(c => c.Index).Select(c => (c.StartLine, c.EndLine, c.Content)));

        stitched.ShouldNotContain("not indexed", Case.Sensitive,
            "chunks from one pass tile the file; a gap here means the chunker skipped lines");
        stitched.TrimEnd('\n').ShouldBe(source.TrimEnd('\n'));
    }

    [Fact]
    public void SlicesOfOneLongLineAreAllKept()
    {
        // The chunker splits a line longer than the whole budget, so several chunks can
        // report the SAME single line. Line-based de-overlapping cannot tell those apart
        // because by line each one is "already emitted", and dropped all but the first.
        var stitched = DexiconTools.Stitch(
        [
            (1, 1, "alpha beta gamma"),
            (1, 1, "gamma delta epsilon"),
            (1, 1, "epsilon zeta"),
        ]);

        foreach (var word in new[] { "alpha", "beta", "gamma", "delta", "epsilon", "zeta" })
            stitched.ShouldContain(word);
    }

    [Fact]
    public void TheSharedTextBetweenTwoSlicesAppearsOnce()
    {
        var stitched = DexiconTools.Stitch([(1, 1, "the quick brown fox"), (1, 1, "brown fox jumps over")]);

        stitched.Trim().ShouldBe("the quick brown fox jumps over");
    }

    [Fact]
    public void SlicesThatShareNothingAreBothKeptWhole()
    {
        // A line with no spaces splits with no overlap configured; nothing is shared, and
        // nothing may be silently dropped on the assumption that something was.
        var stitched = DexiconTools.Stitch([(1, 1, "aaaa"), (1, 1, "bbbb")]);

        stitched.Trim().ShouldBe("aaaabbbb");
    }

    [Fact]
    public void SameLineSlicesDoNotSuppressTheNextRealLine()
    {
        var stitched = DexiconTools.Stitch(
        [
            (1, 1, "one two"),
            (1, 1, "two three"),
            (2, 3, "line two\nline three"),
        ]);

        stitched.ShouldContain("one two three");
        stitched.ShouldContain("line three");
    }
}
