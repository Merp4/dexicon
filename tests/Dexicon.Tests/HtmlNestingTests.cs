using System.Diagnostics;
using System.Text;
using AngleSharp.Html.Parser;
using Dexicon.Core.Extraction;

namespace Dexicon.Tests;

/// <summary>
/// Deeply nested HTML. The text walk used to recurse and overflowed the stack, and the parser's time
/// grows with the square of the depth (100,000 nested divs took 319 s). The walk keeps its own stack, and
/// the parse runs under the extraction clock, which cancels it. No count of tags taken beforehand is
/// exact, so none is used: a deep document that parses within the budget is extracted.
/// </summary>
public sealed class HtmlNestingTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(1);

    /// <summary>Longer than the budget by far, and still short of a test that stalls the run.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(20);

    private static ExtractedText Extract(string html, Encoding? encoding = null, TimeSpan? budget = null)
    {
        encoding ??= new UTF8Encoding(false);
        Stream content = new MemoryStream([.. encoding.GetPreamble(), .. encoding.GetBytes(html)]);
        if (budget is { } clock) content = new DeadlineStream(content, clock, "page.html");

        return new HtmlTextExtractor().Extract(content, "page.html");
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
        var html = $"<html><body>{Repeat("<div></span>", 100_000)}</body></html>";
        var clock = Stopwatch.StartNew();

        Should.Throw<ExtractionTimeoutException>(() => Extract(html, budget: Budget));

        clock.Elapsed.ShouldBeLessThan(Margin, "the parse took longer than the budget allows, so the clock did not cancel it");
    }

    [Fact]
    public void AMillionNestedDivsStopAtTheBudgetWithoutCrashing()
    {
        var html = $"<html><body>{Repeat("<div>", 1_000_000)}</body></html>";
        var clock = Stopwatch.StartNew();

        Should.Throw<ExtractionTimeoutException>(() => Extract(html, budget: Budget));

        clock.Elapsed.ShouldBeLessThan(Margin);
    }

    [Fact]
    public void ADeepDocumentWrittenAsUtf16StopsAtTheBudgetToo()
    {
        // A scan of the bytes as Latin-1 never saw tags written in UTF-16. The parse reads what the text reader decoded.
        var html = $"<html><body>{Repeat("<div></span>", 100_000)}</body></html>";
        var clock = Stopwatch.StartNew();

        Should.Throw<ExtractionTimeoutException>(() => Extract(html, new UnicodeEncoding(false, true), Budget));

        clock.Elapsed.ShouldBeLessThan(Margin);
    }

    [Fact]
    public void ATimeoutFromTheParseCarriesTheCancellationThatEndedIt()
    {
        var html = $"<html><body>{Repeat("<div>", 1_000_000)}</body></html>";

        var thrown = Should.Throw<ExtractionTimeoutException>(() => Extract(html, budget: Budget));

        thrown.InnerException.ShouldBeAssignableTo<OperationCanceledException>();
    }

    // An undefined entity makes the chapter XML that is not well formed, so the XML check of the package
    // leaves it to the parse, as it does a chapter written as HTML.
    private const string NotXml = "<p>&nbsp;</p>";

    private static DeadlineStream Clock(byte[] bytes) => new(new MemoryStream(bytes), Budget, "book.epub");

    [Fact]
    public void AnEpubChapterReadByItsManifestStopsAtTheBudget()
    {
        var epub = TestEpubs.WithAChapter(NotXml + Repeat("<div></span>", 100_000));
        var clock = Stopwatch.StartNew();

        Should.Throw<ExtractionTimeoutException>(() => new EpubTextExtractor().Extract(Clock(epub), "book.epub"));

        clock.Elapsed.ShouldBeLessThan(Margin);
    }

    [Fact]
    public void AnEpubEntryReadFromTheArchiveStopsAtTheBudget()
    {
        var epub = TestEpubs.WithAnEntry(NotXml + Repeat("<div></span>", 100_000));
        var clock = Stopwatch.StartNew();

        Should.Throw<ExtractionTimeoutException>(() => new EpubTextExtractor().Extract(Clock(epub), "book.epub"));

        clock.Elapsed.ShouldBeLessThan(Margin);
    }

    [Fact]
    public void AnEpubChapterThatParsesInTimeIsExtracted()
    {
        var epub = TestEpubs.WithAChapter(NotXml + Repeat("<div>", 600) + "<p>kept</p>");

        new EpubTextExtractor().Extract(Clock(epub), "book.epub").Text.ShouldContain("kept");
    }

    [Fact]
    public void AnEpubEntryThatParsesInTimeIsExtracted()
    {
        var epub = TestEpubs.WithAnEntry(NotXml + Repeat("<div>", 600) + "<p>kept</p>");

        new EpubTextExtractor().Extract(Clock(epub), "book.epub").Text.ShouldContain("kept");
    }
}
