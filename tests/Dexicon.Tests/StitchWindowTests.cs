using Dexicon.Core.Search;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// `before` and `after` on get_context are documented as lines. They used to select which
/// chunks overlapped the window and then return those chunks whole, so a request for one
/// line either side of a hit returned forty: the whole chunk that contained it. An agent
/// budgeting its context asked for three lines and paid for a chunk.
///
/// Chunks are still selected by overlap, because that is how the lines are found. What
/// changed is that the stitched passage is then trimmed to the window.
/// </summary>
public sealed class StitchWindowTests
{
    private static string Lines(int from, int to) =>
        string.Join('\n', Enumerable.Range(from, to - from + 1).Select(i => $"line {i}"));

    private static string[] Emitted(string stitched) =>
        stitched.TrimEnd('\n').Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void AWindowTrimsToTheLinesAsked()
    {
        var stitched = Passage.Stitch([(1, 40, Lines(1, 40))], window: (18, 22));

        Emitted(stitched).ShouldBe(["line 18", "line 19", "line 20", "line 21", "line 22"]);
    }

    [Fact]
    public void WithoutAWindowNothingIsTrimmed()
    {
        // The default has to stay as it was: Stitch is also used where the whole passage
        // is wanted.
        var stitched = Passage.Stitch([(1, 40, Lines(1, 40))]);

        Emitted(stitched).Length.ShouldBe(40);
    }

    [Fact]
    public void TrimmingKeepsTheRealLineNumbers()
    {
        // The numbers are the file's, not the passage's. Counting down from the top of a
        // trimmed passage is the arithmetic this exists to prevent.
        var stitched = Passage.Stitch(
            [(1, 40, Lines(1, 40))], lineNumbers: true, window: (18, 20));

        Emitted(stitched).ShouldBe(["18: line 18", "19: line 19", "20: line 20"]);
    }

    [Fact]
    public void AWindowSpanningTwoOverlappingChunksStillDeOverlaps()
    {
        var stitched = Passage.Stitch(
        [
            (1, 10, Lines(1, 10)),
            (8, 20, Lines(8, 20)),
        ], window: (9, 12));

        Emitted(stitched).ShouldBe(["line 9", "line 10", "line 11", "line 12"]);
    }

    [Fact]
    public void AGapOutsideTheWindowIsNotAnnounced()
    {
        // The passage the caller asked for is contiguous; a gap elsewhere in the file is
        // not their problem and the marker would be noise.
        var stitched = Passage.Stitch(
        [
            (1, 10, Lines(1, 10)),
            (20, 30, Lines(20, 30)),
        ], window: (2, 5));

        stitched.ShouldNotContain("not indexed");
        Emitted(stitched).ShouldBe(["line 2", "line 3", "line 4", "line 5"]);
    }

    [Fact]
    public void AGapInsideTheWindowIsAnnouncedAndClamped()
    {
        // Still disclosed, because the caller's passage really does have a hole in it,
        // but reported as the part of the hole they asked for.
        var stitched = Passage.Stitch(
        [
            (1, 10, Lines(1, 10)),
            (20, 30, Lines(20, 30)),
        ], window: (9, 21));

        stitched.ShouldContain("… lines 11-19 not indexed …");
        Emitted(stitched).ShouldContain("line 9");
        Emitted(stitched).ShouldContain("line 20");
        Emitted(stitched).ShouldNotContain("line 22");
    }

    [Fact]
    public void AWindowOutsideTheChunksEmitsNothing()
    {
        var stitched = Passage.Stitch([(1, 10, Lines(1, 10))], window: (50, 60));

        Emitted(stitched).ShouldBeEmpty();
    }
}
