using System.Diagnostics;
using System.Text;
using AngleSharp.Html.Parser;
using Dexicon.Core.Extraction;

namespace Dexicon.Tests;

/// <summary>
/// Deeply nested and very large HTML. The text walk used to recurse and overflowed the stack, and the
/// parser's time grows with the square of the depth. The walk keeps its own stack, the parse runs under the
/// extraction clock, which cancels it, and the number of tags is limited because the parser's memory grows
/// with it. No count of nested tags taken beforehand is exact, so none is used: a deep document that
/// parses within the budget is extracted.
/// </summary>
public sealed class HtmlNestingTests
{
    /// <summary>
    /// A budget the inputs below overrun by orders of magnitude: 200,000 levels take minutes to parse.
    /// Faster hardware or a faster parser changes nothing.
    /// </summary>
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(100);

    /// <summary>For the timeout that has to come from the parse and not from reading the markup.</summary>
    private static readonly TimeSpan Parse = TimeSpan.FromMilliseconds(500);

    /// <summary>A budget a document that parses in time cannot spend.</summary>
    private static readonly TimeSpan Roomy = TimeSpan.FromSeconds(10);

    private const int Levels = 200_000;

    /// <summary>Longer than any budget above by far, and still short of a test that stalls the run.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(20);

    private static ExtractedText Extract(
        string html, Encoding? encoding = null, TimeSpan? budget = null, HtmlTextExtractor? extractor = null)
    {
        encoding ??= new UTF8Encoding(false);
        Stream content = new MemoryStream([.. encoding.GetPreamble(), .. encoding.GetBytes(html)]);
        if (budget is { } clock) content = new DeadlineStream(content, clock, "page.html");

        return BoundedCalls.Within(
            BoundedCalls.Generous, () => (extractor ?? new HtmlTextExtractor()).Extract(content, "page.html"));
    }

    private static string Repeat(string markup, int times) => string.Concat(Enumerable.Repeat(markup, times));

    [Fact]
    public void TheWalkOfADeepTreeDoesNotUseTheCallStack()
    {
        // The walk recursed once per level and overflowed at about 3,100 levels.
        using var document = new HtmlParser().ParseDocument($"<html><body>{Repeat("<div>", 6_000)}text</body></html>");
        var text = new StringBuilder();

        HtmlText.AppendBlocks(document.Body, text);

        text.ToString().ShouldBe("text\n");
    }

    [Fact]
    public void DeepMarkupThatParsesInTimeIsExtracted()
    {
        Extract($"<html><body>{Repeat("<div>", 3_000)}text</body></html>", budget: TimeSpan.FromSeconds(60)).Text.ShouldBe("text");
    }

    [Theory]
    [InlineData("<a href=x>l", 600)]
    [InlineData("<h1>l", 600)]
    [InlineData("<p><b>l", 2_000)]
    [InlineData("<form><div>l", 700)]
    [InlineData("<div></span>l", 2_000)]
    public void MarkupAHandWrittenCountOfTagsGotWrongIsExtracted(string unit, int times)
    {
        // Each of these made a scan of the tags either miss the depth or invent it. The parser decides.
        Extract($"<html><body>{Repeat(unit, times)}</body></html>", budget: TimeSpan.FromSeconds(60)).Text.ShouldContain("l");
    }

    [Fact]
    public void ADeepStrayEndTagDocumentStopsAtTheBudget()
    {
        var html = $"<html><body>{Repeat("<div></span>", Levels)}</body></html>";
        var clock = Stopwatch.StartNew();

        Should.Throw<ExtractionTimeoutException>(() => Extract(html, budget: Short));

        clock.Elapsed.ShouldBeLessThan(Margin, "the parse took longer than the budget allows, so the clock did not cancel it");
    }

    [Fact]
    public void NineHundredThousandNestedDivsStopAtTheBudgetWithoutCrashing()
    {
        var html = $"<html><body>{Repeat("<div>", 900_000)}</body></html>";
        var clock = Stopwatch.StartNew();

        Should.Throw<ExtractionTimeoutException>(() => Extract(html, budget: Short));

        clock.Elapsed.ShouldBeLessThan(Margin);
    }

    [Fact]
    public void ADeepDocumentWrittenAsUtf16StopsAtTheBudgetToo()
    {
        // A scan of the bytes as Latin-1 never saw tags written in UTF-16. The parse reads what the text reader decoded.
        var html = $"<html><body>{Repeat("<div></span>", Levels)}</body></html>";
        var clock = Stopwatch.StartNew();

        Should.Throw<ExtractionTimeoutException>(() => Extract(html, new UnicodeEncoding(false, true), Short));

        clock.Elapsed.ShouldBeLessThan(Margin);
    }

    [Fact]
    public void ATimeoutFromTheParseCarriesTheCancellationThatEndedIt()
    {
        var html = $"<html><body>{Repeat("<div>", 900_000)}</body></html>";

        var thrown = Should.Throw<ExtractionTimeoutException>(() => Extract(html, budget: Parse));

        thrown.InnerException.ShouldBeAssignableTo<OperationCanceledException>();
    }

    [Fact]
    public void ABudgetBeyondWhatTheCancellationTimerTakesIsNoBudget()
    {
        // CancelAfter takes up to about 49 days. A budget of 5,000,000 s made every HTML file unreadable.
        var content = new DeadlineStream(
            new MemoryStream("<html><body><p>kept</p></body></html>"u8.ToArray()), TimeSpan.FromSeconds(5_000_000), "page.html");

        new HtmlTextExtractor().Extract(content, "page.html").Text.ShouldBe("kept");
    }

    // The memory bound: the parse costs 290 to 560 bytes per tag, so the tags are counted before it starts.

    private static string Tags(int count) => $"<html><body>{Repeat("<p>x", count - 4)}</body></html>";

    [Fact]
    public void ADocumentWithExactlyTheMostTagsIsExtracted()
    {
        Extract(Tags(100), extractor: new HtmlTextExtractor(maxTags: 100)).Text.ShouldContain("x");
    }

    [Fact]
    public void ADocumentWithOneTagMoreIsRefused()
    {
        var thrown = Should.Throw<UnreadableDocumentException>(
            () => Extract(Tags(101), extractor: new HtmlTextExtractor(maxTags: 100)));

        thrown.Message.ShouldBe("page.html contains more than 100 tags, which is more than can be read.");
        thrown.Unexpected.ShouldBeFalse();
    }

    [Fact]
    public void TheTagsOfADocumentWrittenAsUtf16AreCountedToo()
    {
        Should.Throw<UnreadableDocumentException>(
            () => Extract(Tags(101), new UnicodeEncoding(false, true), extractor: new HtmlTextExtractor(maxTags: 100)));
    }

    [Fact]
    public void TheDefaultLimitIsAMillionTags()
    {
        HtmlText.MaxTags.ShouldBe(1_000_000);
    }

    // An undefined entity makes the chapter XML that is not well formed, so the XML check of the package
    // leaves it to the parse, as it does a chapter written as HTML.
    private const string NotXml = "<p>&nbsp;</p>";

    private static ExtractedText Within(byte[] epub, TimeSpan budget, int maxTags = HtmlText.MaxTags) =>
        BoundedCalls.Within(
            BoundedCalls.Generous,
            () => new EpubTextExtractor(maxTags).Extract(new DeadlineStream(new MemoryStream(epub), budget, "book.epub"), "book.epub"));

    [Fact]
    public void AnEpubChapterReadByItsManifestStopsAtTheBudget()
    {
        var epub = TestEpubs.WithAChapter(NotXml + Repeat("<div></span>", Levels));
        var clock = Stopwatch.StartNew();

        Should.Throw<ExtractionTimeoutException>(() => Within(epub, Short));

        clock.Elapsed.ShouldBeLessThan(Margin);
    }

    [Fact]
    public void AnEpubEntryReadFromTheArchiveStopsAtTheBudget()
    {
        var epub = TestEpubs.WithAnEntry(NotXml + Repeat("<div></span>", Levels));
        var clock = Stopwatch.StartNew();

        Should.Throw<ExtractionTimeoutException>(() => Within(epub, Short));

        clock.Elapsed.ShouldBeLessThan(Margin);
    }

    [Fact]
    public void AnEpubChapterThatParsesInTimeIsExtracted()
    {
        var epub = TestEpubs.WithAChapter(NotXml + Repeat("<div>", 600) + "<p>kept</p>");

        Within(epub, Roomy).Text.ShouldContain("kept");
    }

    [Fact]
    public void AnEpubEntryThatParsesInTimeIsExtracted()
    {
        var epub = TestEpubs.WithAnEntry(NotXml + Repeat("<div>", 600) + "<p>kept</p>");

        Within(epub, Roomy).Text.ShouldContain("kept");
    }

    // The chapter file holds four tags of its own (html and body, opened and closed).

    [Fact]
    public void AnEpubChapterReadByItsManifestWithExactlyTheMostTagsIsExtracted()
    {
        var epub = TestEpubs.WithAChapter("&nbsp;" + Repeat("<p>x", 96));

        Within(epub, Roomy, maxTags: 100).Text.ShouldContain("x");
    }

    [Fact]
    public void AnEpubChapterReadByItsManifestWithOneTagMoreIsRefused()
    {
        var epub = TestEpubs.WithAChapter("&nbsp;" + Repeat("<p>x", 97));

        Should.Throw<UnreadableDocumentException>(() => Within(epub, Roomy, maxTags: 100))
            .Message.ShouldBe("Chapter 1 contains more than 100 tags, which is more than can be read.");
    }

    [Fact]
    public void AnEpubEntryReadFromTheArchiveWithExactlyTheMostTagsIsExtracted()
    {
        var epub = TestEpubs.WithAnEntry("&nbsp;" + Repeat("<p>x", 96));

        Within(epub, Roomy, maxTags: 100).Text.ShouldContain("x");
    }

    [Fact]
    public void AnEpubEntryReadFromTheArchiveWithOneTagMoreIsRefused()
    {
        var epub = TestEpubs.WithAnEntry("&nbsp;" + Repeat("<p>x", 97));

        Should.Throw<UnreadableDocumentException>(() => Within(epub, Roomy, maxTags: 100))
            .Message.ShouldBe("OEBPS/ch1.xhtml contains more than 100 tags, which is more than can be read.");
    }
}
