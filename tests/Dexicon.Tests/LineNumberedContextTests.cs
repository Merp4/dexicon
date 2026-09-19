using Dexicon.Core.Search;

namespace Dexicon.Tests;

/// <summary>
/// get_context can number the lines it returns.
///
/// It exists because the passage is what a model reads before quoting or editing, and the
/// alternative is counting lines down from the header: arithmetic over a passage that has
/// had overlap removed from it, which is precisely the sum it gets wrong. A number that is
/// wrong is worse than no number, so every case below is about the number matching the
/// file rather than matching the position in the output.
/// </summary>
public sealed class LineNumberedContextTests
{
    private static string Lines(int from, int to) =>
        string.Join('\n', Enumerable.Range(from, to - from + 1).Select(i => $"line {i}"));

    [Fact]
    public void NumbersAreTheFilesOwn_NotThePositionInTheOutput()
    {
        // The passage starts at line 40. Numbering from 1 would be confidently wrong.
        var stitched = Passage.Stitch([(40, 44, Lines(40, 44))], lineNumbers: true);

        stitched.TrimEnd('\n').Split('\n').ShouldBe(
        [
            "40: line 40",
            "41: line 41",
            "42: line 42",
            "43: line 43",
            "44: line 44",
        ]);
    }

    [Fact]
    public void OverlapRemovalDoesNotShiftTheNumbering()
    {
        // The second chunk repeats lines 8-10. They are dropped, and line 11 must still be
        // numbered 11, which is the off-by-one this file exists to guard.
        var stitched = Passage.Stitch(
        [
            (1, 10, Lines(1, 10)),
            (8, 20, Lines(8, 20)),
        ],
            lineNumbers: true);

        var result = stitched.TrimEnd('\n').Split('\n');

        result.Length.ShouldBe(20);
        result[0].ShouldBe("1: line 1");
        result[7].ShouldBe("8: line 8");
        result[10].ShouldBe("11: line 11");
        result[19].ShouldBe("20: line 20");
    }

    [Fact]
    public void AGapIsAnnouncedWithoutBeingNumbered()
    {
        // The marker stands for lines that are NOT there. Numbering it would give a line
        // number to a line that does not exist.
        var stitched = Passage.Stitch(
        [
            (1, 5, Lines(1, 5)),
            (20, 24, Lines(20, 24)),
        ],
            lineNumbers: true);

        stitched.ShouldContain("… lines 6-19 not indexed …");
        stitched.ShouldNotContain(": … lines");
        stitched.ShouldContain("20: line 20");
    }

    [Fact]
    public void NumberingIsOffByDefault()
    {
        // The corpus-file resource reconstructs a file and shares this code. A file read
        // back should be the file, not a listing of it.
        Passage.Stitch([(1, 2, Lines(1, 2))]).ShouldBe("line 1\nline 2\n");
    }

    [Fact]
    public void SlicesOfOneOverlongLineShareTheOneNumber()
    {
        // A line longer than the whole chunk budget is split across chunks, each honestly
        // reporting the same line. It is one line, so it gets one number, not one per
        // slice, which would invent lines that are not in the file.
        var stitched = Passage.Stitch(
        [
            (1, 1, "the quick brown fox"),
            (1, 1, "brown fox jumps over"),
        ],
            lineNumbers: true);

        stitched.Trim().ShouldBe("1: the quick brown fox jumps over");
    }
}
