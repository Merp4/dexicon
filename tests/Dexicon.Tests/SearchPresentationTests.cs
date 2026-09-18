using Dexicon.Core.Search;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// What a search result costs to read, and whether the same book fills the page.
///
/// Measured over eight questions against a 95-book library, five results each: a mean of
/// 40,797 characters per call, about 10,200 tokens, because the chunk is sized for
/// retrieval rather than for reading. Truncating each hit to 1,500 characters takes that to
/// 7,500. Dropping repeated text takes it to 40,716, which is nothing: a dropped duplicate
/// only promotes another chunk of the same size.
///
/// So length is the cost and duplication is the quality problem, and they are fixed
/// separately.
/// </summary>
public sealed class SearchPresentationWindowTests
{
    private static string Lines(int count, string filler = "padding text on a line") =>
        string.Join("\n", Enumerable.Range(0, count).Select(i => $"{filler} {i}"));

    [Fact]
    public void TextInsideTheBudgetIsReturnedUntouched()
    {
        var text = "one\ntwo\nthree";

        SearchPresentation.Window(text, "two", 1500).ShouldBe(text);
    }

    [Fact]
    public void ABudgetOfZeroMeansTheWholeChunk()
    {
        // How a reader comparing two extractions of one title gets the whole thing.
        var text = Lines(400);

        SearchPresentation.Window(text, "padding", 0).ShouldBe(text);
    }

    [Fact]
    public void TheWindowIsCentredOnTheMatchAndNotTakenFromTheHead()
    {
        // The measurement that decided this: with a 1,500-character head, 12% of real hits
        // had their FIRST matching term already past it, so the preview carried none of
        // what the query matched. Here the match is far down a long chunk.
        var text = Lines(300) + "\nthe eventual consistency trade-off is the point\n" + Lines(300);

        var window = SearchPresentation.Window(text, "eventual consistency trade-off", 1500);

        window.ShouldContain("eventual consistency trade-off is the point");
        window.Length.ShouldBeLessThanOrEqualTo(1600);
    }

    [Fact]
    public void AnElidedWindowSaysSo()
    {
        var text = Lines(300) + "\nthe needle sits here\n" + Lines(300);

        var window = SearchPresentation.Window(text, "needle", 1500);

        // Otherwise a window is indistinguishable from a whole chunk, and an agent cannot
        // tell that asking for more would give it more.
        window.ShouldStartWith("…");
        window.ShouldEndWith("…");
    }

    [Fact]
    public void NoMarkerWhereNothingWasCut()
    {
        var text = "the needle sits here\n" + Lines(300);

        var window = SearchPresentation.Window(text, "needle", 1500);

        window.ShouldNotStartWith("…");
        window.ShouldEndWith("…");
    }

    [Fact]
    public void TheDensestRunOfTermsWins()
    {
        // An early incidental mention of one term should not beat the passage that uses
        // several of them, which is the passage the question was about.
        var text = "a passing mention of consistency\n" + Lines(200)
                   + "\nstrong consistency and eventual consistency differ in latency\n" + Lines(200);

        var window = SearchPresentation.Window(text, "strong eventual consistency latency", 1200);

        window.ShouldContain("strong consistency and eventual consistency differ");
    }

    [Fact]
    public void AChunkWithNoMatchingTermStillReturnsSomething()
    {
        // Semantic-only hits match no literal term at all. Returning nothing, or throwing,
        // would drop a result that ranked.
        var text = Lines(300);

        var window = SearchPresentation.Window(text, "zzz nothing here matches", 1000);

        window.Length.ShouldBeGreaterThan(0);
        window.Length.ShouldBeLessThanOrEqualTo(1100);
    }

    [Fact]
    public void OneEnormousLineIsCutRatherThanReturnedWhole()
    {
        // A PDF page extracted as a single line, or a minified file. Growing by lines
        // cannot help, and returning it whole would make the budget a fiction.
        var text = new string('x', 5_000) + " needle " + new string('y', 5_000);

        var window = SearchPresentation.Window(text, "needle", 1000);

        window.ShouldContain("needle");
        window.Length.ShouldBeLessThanOrEqualTo(1100);
    }

    [Fact]
    public void LinesAreKeptWholeRatherThanCutMidSentence()
    {
        var text = Lines(300) + "\nthe needle sits here\n" + Lines(300);

        var window = SearchPresentation.Window(text, "needle", 1500);

        foreach (var line in window.Split('\n'))
        {
            if (line == "…" || line.Length == 0) continue;
            // Every line in the window is a line that was in the text.
            text.ShouldContain(line);
        }
    }
}

/// <summary>
/// One hit per document, when the library holds each title twice.
///
/// The measured corpus returned 3.0 distinct books per 5 results, because most titles are
/// held as both PDF and EPUB. Both copies earn their place on disk — they are how the two
/// extractors get compared — but returning both to one question spends a slot on a book the
/// reader already has.
/// </summary>
public sealed class SearchPresentationDistinctTests
{
    private static SearchHit Hit(string path, float score, string? sourceRoot = "orly/AI") => new()
    {
        CorpusId = "c",
        FilePath = path,
        SourceRoot = sourceRoot,
        Content = "text",
        Score = score,
    };

    [Fact]
    public void TheSameTitleInTwoFormatsIsOneResult()
    {
        var hits = new[]
        {
            Hit("AI Engineering.pdf", 0.9f),
            Hit("AI Engineering.epub", 0.8f),
            Hit("Designing Data-Intensive Applications.pdf", 0.7f),
        };

        var kept = SearchPresentation.DistinctByTitle(hits, 5);

        kept.Select(h => h.FilePath).ShouldBe(
            ["AI Engineering.pdf", "Designing Data-Intensive Applications.pdf"]);
    }

    [Fact]
    public void TheBestScoringCopyIsTheOneKept()
    {
        // Hits arrive ordered, so the first of a title is its best. Keeping the later one
        // would answer with the worse extraction of the same book.
        var hits = new[]
        {
            Hit("AI Engineering.epub", 0.9f),
            Hit("AI Engineering.pdf", 0.4f),
        };

        SearchPresentation.DistinctByTitle(hits, 5).Single().FilePath.ShouldBe("AI Engineering.epub");
    }

    [Fact]
    public void DroppingADuplicatePromotesTheNextDistinctHit()
    {
        // The reason the limit is applied after collapsing rather than before: asking for
        // three and getting two is a worse answer than the one available.
        var hits = new[]
        {
            Hit("One.pdf", 0.9f),
            Hit("One.epub", 0.8f),
            Hit("Two.pdf", 0.7f),
            Hit("Three.pdf", 0.6f),
        };

        SearchPresentation.DistinctByTitle(hits, 3)
            .Select(h => h.FilePath).ShouldBe(["One.pdf", "Two.pdf", "Three.pdf"]);
    }

    [Fact]
    public void TwoSourcesHoldingTheSameFilenameAreTwoDocuments()
    {
        // A file path is relative to its source root, so within a corpus it is not unique.
        // Collapsing on the name alone would hide one book behind another.
        var hits = new[]
        {
            Hit("Logic For Dummies.pdf", 0.9f, sourceRoot: "orly/AI"),
            Hit("Logic For Dummies.pdf", 0.8f, sourceRoot: "orly/Philosophy"),
        };

        SearchPresentation.DistinctByTitle(hits, 5).Count.ShouldBe(2);
    }

    [Fact]
    public void TwoChunksOfOneFileAreStillOneDocument()
    {
        var hits = new[] { Hit("AI Engineering.pdf", 0.9f), Hit("AI Engineering.pdf", 0.8f) };

        SearchPresentation.DistinctByTitle(hits, 5).Count.ShouldBe(1);
    }

    [Fact]
    public void TheSameNameInDifferentFoldersIsNotTheSameDocument()
    {
        var hits = new[] { Hit("a/notes.md", 0.9f), Hit("b/notes.md", 0.8f) };

        SearchPresentation.DistinctByTitle(hits, 5).Count.ShouldBe(2);
    }

    [Fact]
    public void AFileWithNoExtensionIsHandledLikeAnyOther()
    {
        var hits = new[] { Hit("LICENSE", 0.9f), Hit("README.md", 0.8f) };

        SearchPresentation.DistinctByTitle(hits, 5).Count.ShouldBe(2);
    }

    [Fact]
    public void FewerDistinctDocumentsThanAskedForReturnsWhatThereIs()
    {
        var hits = new[] { Hit("One.pdf", 0.9f), Hit("One.epub", 0.8f) };

        SearchPresentation.DistinctByTitle(hits, 5).Count.ShouldBe(1);
    }
}
