using Dexicon.Core.Extraction;

namespace Dexicon.Tests;

/// <summary>
/// How deep the XML parts of a DOCX, PPTX or EPUB may nest. The libraries that load them recurse, and
/// the process ended with a stack overflow at about 5,000 nested elements. The depth is counted with a
/// streaming reader before they see the part.
/// </summary>
public sealed class XmlNestingTests
{
    // document.xml: the document is depth 0, the body 1, each pair of wrappers two more, then p, r and t.
    // The deepest element is at depth 1 + 2 * pairs + 3, and a depth of MaxDepth - 1 or less is accepted.
    private const int DocxPairsAtTheLimit = (XmlNesting.MaxDepth - 5) / 2;

    // slide1.xml: sld, cSld and spTree are depths 0 to 2, then the group shapes, then sp, txBody, a:p, a:r and
    // a:t. The deepest element is at depth 2 + groups + 5.
    private const int PptxGroupsAtTheLimit = XmlNesting.MaxDepth - 1 - 7;

    // toc.ncx: ncx is depth 0, navMap 1, the nested navPoints, then navLabel and text. The deepest element is
    // at depth 1 + points + 2.
    private const int NavigationPointsAtTheLimit = XmlNesting.MaxDepth - 1 - 3;

    // nav.xhtml: html is depth 0, body 1, nav 2, the outer list 3, then each nesting adds an li and an ol, and the
    // innermost li holds an a. The deepest element is at depth 3 + 2 * lists.
    private const int NavigationListsAtTheLimit = (XmlNesting.MaxDepth - 1 - 3) / 2;

    private static ExtractedText Extract(ITextExtractor extractor, byte[] bytes, string name) =>
        extractor.Extract(new MemoryStream(bytes), name);

    [Fact]
    public void ADocxNestedAtTheLimitIsExtracted()
    {
        Extract(new DocxTextExtractor(), TestEpubs.DocxNestedBy(DocxPairsAtTheLimit), "a.docx").Text.ShouldBe("hello\n");
    }

    [Fact]
    public void ADocxNestedOneLevelPastTheLimitIsAnUnreadableDocument()
    {
        var thrown = Should.Throw<UnreadableDocumentException>(
            () => Extract(new DocxTextExtractor(), TestEpubs.DocxNestedBy(DocxPairsAtTheLimit + 1), "a.docx"));

        thrown.Message.ShouldBe(
            $"a.docx (word/document.xml) nests XML elements more than {XmlNesting.MaxDepth} deep, which is too deep to read.");
    }

    [Fact]
    public void ADocxNestedAHundredThousandDeepIsRefusedWithoutLoadingIt()
    {
        // The Open XML SDK overflowed the stack at about 5,000.
        Should.Throw<UnreadableDocumentException>(
            () => Extract(new DocxTextExtractor(), TestEpubs.DocxNestedBy(100_000), "a.docx"))
            .Message.ShouldContain("too deep");
    }

    [Fact]
    public void ADocxWhoseXmlIsNotWellFormedIsLeftToTheSdkToReport()
    {
        var bytes = TestEpubs.DocxWithDocumentXml("<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p>");

        Should.Throw<UnreadableDocumentException>(() => Extract(new DocxTextExtractor(), bytes, "a.docx"))
            .Message.ShouldStartWith("'a.docx' is not a readable .docx");
    }

    [Fact]
    public void BytesThatAreNotAZipAreLeftToTheSdkToReport()
    {
        Should.Throw<UnreadableDocumentException>(
                () => Extract(new DocxTextExtractor(), "not a zip"u8.ToArray(), "a.docx"))
            .Message.ShouldStartWith("'a.docx' is not a readable .docx");
    }

    [Fact]
    public void ADocxIsStillReadAfterTheCheckHasReadThePackage()
    {
        // The check reads every XML part. The reader that follows opens the package as a zip.
        var stream = new MemoryStream(TestEpubs.DocxNestedBy(3));

        new DocxTextExtractor().Extract(stream, "a.docx").Text.ShouldBe("hello\n");
    }

    [Fact]
    public void APptxNestedAtTheLimitIsExtracted()
    {
        Extract(new PptxTextExtractor(), TestEpubs.PptxNestedBy(PptxGroupsAtTheLimit), "a.pptx").Text.ShouldContain("hello");
    }

    [Fact]
    public void APptxNestedOneLevelPastTheLimitIsAnUnreadableDocument()
    {
        var thrown = Should.Throw<UnreadableDocumentException>(
            () => Extract(new PptxTextExtractor(), TestEpubs.PptxNestedBy(PptxGroupsAtTheLimit + 1), "a.pptx"));

        thrown.Message.ShouldContain("a.pptx (ppt/slides/slide1.xml) nests XML elements more than");
    }

    [Fact]
    public void APptxNestedAHundredThousandDeepIsRefusedWithoutLoadingIt()
    {
        Should.Throw<UnreadableDocumentException>(
            () => Extract(new PptxTextExtractor(), TestEpubs.PptxNestedBy(100_000), "a.pptx"))
            .Message.ShouldContain("too deep");
    }

    [Fact]
    public void AnEpubNavigationFileNestedAtTheLimitIsExtracted()
    {
        var epub = TestEpubs.WithAChapter("<p>kept</p>", NavigationPointsAtTheLimit);

        Extract(new EpubTextExtractor(), epub, "book.epub").Text.ShouldContain("kept");
    }

    [Fact]
    public void AnEpubNavigationFileNestedOneLevelPastTheLimitIsAnUnreadableDocument()
    {
        var epub = TestEpubs.WithAChapter("<p>kept</p>", NavigationPointsAtTheLimit + 1);

        Should.Throw<UnreadableDocumentException>(() => Extract(new EpubTextExtractor(), epub, "book.epub"))
            .Message.ShouldContain("book.epub (toc.ncx) nests XML elements more than");
    }

    [Fact]
    public void AnEpubNavigationFileNestedAHundredThousandDeepIsRefusedWithoutLoadingIt()
    {
        // VersOne.Epub overflowed the stack at about 5,000 navigation points.
        var epub = TestEpubs.WithAChapter("<p>kept</p>", 100_000);

        Should.Throw<UnreadableDocumentException>(() => Extract(new EpubTextExtractor(), epub, "book.epub"))
            .Message.ShouldContain("too deep");
    }

    [Fact]
    public void AnEpub3NavigationDocumentNestedAtTheLimitIsExtracted()
    {
        var epub = TestEpubs.WithANavigationDocumentNestedBy(NavigationListsAtTheLimit);

        Extract(new EpubTextExtractor(), epub, "book.epub").Text.ShouldContain("hello");
    }

    [Fact]
    public void AnEpub3NavigationDocumentNestedOneLevelPastTheLimitIsAnUnreadableDocument()
    {
        var epub = TestEpubs.WithANavigationDocumentNestedBy(NavigationListsAtTheLimit + 1);

        Should.Throw<UnreadableDocumentException>(() => Extract(new EpubTextExtractor(), epub, "book.epub"))
            .Message.ShouldContain("book.epub (nav.xhtml) nests XML elements more than");
    }

    [Fact]
    public void AnEpub3NavigationDocumentNestedAHundredThousandDeepIsRefusedWithoutLoadingIt()
    {
        // VersOne.Epub overflowed the stack at about 5,000 nested lists.
        var epub = TestEpubs.WithANavigationDocumentNestedBy(100_000);

        Should.Throw<UnreadableDocumentException>(() => Extract(new EpubTextExtractor(), epub, "book.epub"))
            .Message.ShouldContain("too deep");
    }
}
