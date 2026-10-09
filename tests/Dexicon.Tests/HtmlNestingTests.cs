using System.Text;
using AngleSharp.Html.Parser;
using Dexicon.Core.Extraction;

namespace Dexicon.Tests;

/// <summary>
/// How deep an HTML document may nest. A document nested a few thousand deep overflowed the stack in
/// the text walk, which ends the process and cannot be caught, and the parser's time grows with the
/// square of the depth. Nesting is counted from the markup before a tree is built.
/// </summary>
public sealed class HtmlNestingTests
{
    private static ExtractedText Extract(string html) =>
        new HtmlTextExtractor().Extract(new MemoryStream(Encoding.UTF8.GetBytes(html)), "page.html");

    private static string Divs(int depth) =>
        $"<html><body>{string.Concat(Enumerable.Repeat("<div>", depth))}text{string.Concat(Enumerable.Repeat("</div>", depth))}</body></html>";

    [Fact]
    public void MarkupNestedAtTheLimitIsExtracted()
    {
        Extract(Divs(HtmlText.MaxNesting)).Text.ShouldBe("text");
    }

    [Fact]
    public void MarkupNestedOneDeeperThanTheLimitIsAnUnreadableDocument()
    {
        var thrown = Should.Throw<UnreadableDocumentException>(() => Extract(Divs(HtmlText.MaxNesting + 1)));

        thrown.Message.ShouldBe($"page.html nests elements more than {HtmlText.MaxNesting} deep, which is too deep to read.");
        thrown.Unexpected.ShouldBeFalse();
    }

    [Fact]
    public void MarkupNestedAHundredThousandDeepIsRefusedBeforeAnyTreeIsBuilt()
    {
        // Parsing this took 150 s on the machine that measured it, and walking the tree overflowed the stack.
        Should.Throw<UnreadableDocumentException>(() => Extract(Divs(100_000)))
            .Message.ShouldContain("too deep");
    }

    [Fact]
    public void TheWalkOfADeepTreeDoesNotUseTheCallStack()
    {
        // Past the limit the extractor refuses, so the walk is called on a tree built here. It recursed once
        // per level and overflowed at about 3,100 levels.
        using var document = new HtmlParser().ParseDocument(Divs(6_000));
        var text = new StringBuilder();

        HtmlText.AppendBlocks(document.Body, text);

        text.ToString().ShouldBe("text\n");
    }

    [Fact]
    public void UnclosedParagraphsAndTableCellsAreNotDeepNesting()
    {
        // End tags may be left out of these, so the parser closes them and a long page of them is flat.
        var html = "<html><body>" + string.Concat(Enumerable.Repeat("<p>line ", 3_000))
                   + "<table><tr>" + string.Concat(Enumerable.Repeat("<td>cell", 3_000)) + "</table></body></html>";

        Extract(html).Text.ShouldContain("line");
    }

    [Fact]
    public void TagsInAScriptOrACommentAreNotCounted()
    {
        var html = "<html><body><script>" + string.Concat(Enumerable.Repeat("<div>", 2_000)) + "</script>"
                   + "<!-- " + string.Concat(Enumerable.Repeat("<div>", 2_000)) + " --><p>kept</p></body></html>";

        Extract(html).Text.ShouldBe("kept");
    }

    [Fact]
    public void QuotedAttributeValuesThatHoldTagsAreNotCounted()
    {
        var html = $"<html><body><a title=\"{string.Concat(Enumerable.Repeat("<div>", 2_000))}\">link</a></body></html>";

        Extract(html).Text.ShouldBe("link");
    }

    [Fact]
    public void SelfClosingElementsInsideSvgAreNotNesting()
    {
        var html = "<html><body><svg>" + string.Concat(Enumerable.Repeat("<path d=\"M0 0\"/>", 2_000)) + "</svg><p>kept</p></body></html>";

        Extract(html).Text.ShouldBe("kept");
    }

    [Fact]
    public void ASlashOnADivDoesNotCloseItSoSelfClosingDivsNest()
    {
        // The parser ignores the slash on an HTML element, so <div/> opens a div.
        var html = "<html><body>" + string.Concat(Enumerable.Repeat("<div/>", HtmlText.MaxNesting + 1)) + "</body></html>";

        Should.Throw<UnreadableDocumentException>(() => Extract(html));
    }

    // An undefined entity makes the chapter XML that is not well formed, so the XML check of the package
    // leaves it to the HTML check, as it does a chapter written as HTML.
    private const string NotXml = "<p>&nbsp;</p>";

    [Fact]
    public void AnEpubChapterReadByItsManifestIsRefusedWhenItNestsTooDeep()
    {
        var epub = TestEpubs.WithAChapter(NotXml + string.Concat(Enumerable.Repeat("<div>", HtmlText.MaxNesting + 1)));

        var thrown = Should.Throw<UnreadableDocumentException>(
            () => new EpubTextExtractor().Extract(new MemoryStream(epub), "book.epub"));

        thrown.Message.ShouldContain("Chapter 1 nests elements more than");
    }

    [Fact]
    public void AnEpubChapterReadByItsManifestAtTheLimitIsExtracted()
    {
        var epub = TestEpubs.WithAChapter(
            NotXml + string.Concat(Enumerable.Repeat("<div>", HtmlText.MaxNesting - 1)) + "<p>kept</p>");

        new EpubTextExtractor().Extract(new MemoryStream(epub), "book.epub").Text.ShouldContain("kept");
    }

    [Fact]
    public void AnEpubEntryReadFromTheArchiveIsRefusedWhenItNestsTooDeep()
    {
        var epub = TestEpubs.WithAnEntry(NotXml + string.Concat(Enumerable.Repeat("<div>", HtmlText.MaxNesting + 1)));

        var thrown = Should.Throw<UnreadableDocumentException>(
            () => new EpubTextExtractor().Extract(new MemoryStream(epub), "book.epub"));

        thrown.Message.ShouldContain("OEBPS/ch1.xhtml nests elements more than");
    }
}
