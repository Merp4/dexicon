using Dexicon.Core.Search;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// Taking lines out of a whole document, rather than out of the chunks that happen to
/// cover them.
///
/// <c>Stitch</c> selects chunks by overlap with the window, so the passage it returns is
/// bounded by chunk edges; a request for one line either side used to come back as forty.
/// A document has no edges, so the arithmetic is the caller's range and nothing else, and
/// it is the arithmetic that decides whether a model reads the lines it asked for.
/// </summary>
public sealed class PassageWindowTests
{
    private static string Doc(int lines) =>
        string.Join('\n', Enumerable.Range(1, lines).Select(i => $"line {i}"));

    [Fact]
    public void AWindowInTheMiddleIsExactlyTheLinesAsked()
    {
        var (text, lo, hi) = Passage.Window(Doc(100), 40, 42);

        text.ShouldBe("line 40\nline 41\nline 42");
        lo.ShouldBe(40);
        hi.ShouldBe(42);
    }

    [Fact]
    public void OneLineIsOneLine()
    {
        var (text, lo, hi) = Passage.Window(Doc(10), 5, 5);

        text.ShouldBe("line 5");
        lo.ShouldBe(5);
        hi.ShouldBe(5);
    }

    [Fact]
    public void AWindowRunningPastTheEndStopsAtTheLastLine()
    {
        var (text, lo, hi) = Passage.Window(Doc(4), 3, 99);

        text.ShouldBe("line 3\nline 4");
        lo.ShouldBe(3);
        hi.ShouldBe(4);   // not 99: the citation reports what is there
    }

    [Fact]
    public void AWindowStartingPastTheEndIsEmptyAndSaysSo()
    {
        var (text, lo, hi) = Passage.Window(Doc(4), 9, 12);

        text.ShouldBe("");
        hi.ShouldBeLessThan(lo);
    }

    [Fact]
    public void TheFirstLineIsReachable()
    {
        Passage.Window(Doc(3), 1, 1).Text.ShouldBe("line 1");
    }

    [Fact]
    public void ALineBelowOneIsClampedRatherThanRefused()
    {
        // before=30 around line 4 asks for line -26. The caller wants the opening.
        var (text, lo, _) = Passage.Window(Doc(3), -26, 2);

        lo.ShouldBe(1);
        text.ShouldBe("line 1\nline 2");
    }

    [Fact]
    public void ADocumentEndingInANewlineDoesNotGainAnEmptyLastLine()
    {
        var (text, _, hi) = Passage.Window("a\nb\n", 1, 10);

        text.ShouldBe("a\nb");
        hi.ShouldBe(2);
    }

    [Fact]
    public void AnEmptyDocumentYieldsNothing()
    {
        var (text, lo, hi) = Passage.Window("", 1, 10);

        text.ShouldBe("");
        hi.ShouldBeLessThan(lo);
    }

    [Fact]
    public void BlankLinesAreLinesAndAreCounted()
    {
        // A paragraph break is a line. Counting it as nothing shifts every number after
        // it, which is how a citation comes to point at the wrong place.
        var (text, lo, hi) = Passage.Window("a\n\n\nd", 2, 3);

        text.ShouldBe("\n");
        lo.ShouldBe(2);
        hi.ShouldBe(3);
    }
}
