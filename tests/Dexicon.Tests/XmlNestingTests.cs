using System.Diagnostics;
using System.Text;
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

    // The libraries load a part by the relationship or manifest that names it, so it can be called anything.
    // Entries are tried as XML by what is in them.

    [Theory]
    [InlineData("word/document.dat")]
    [InlineData("word/document.txt")]
    [InlineData("word/DOCUMENT.XmL")]
    [InlineData("word/document")]
    public void ADocxPartCalledAnythingIsCheckedAtTheLimitAndPastIt(string partName)
    {
        Extract(new DocxTextExtractor(), TestEpubs.DocxNestedBy(DocxPairsAtTheLimit, partName), "a.docx").Text.ShouldBe("hello\n");

        Should.Throw<UnreadableDocumentException>(
                () => Extract(new DocxTextExtractor(), TestEpubs.DocxNestedBy(20_000, partName), "a.docx"))
            .Message.ShouldContain($"a.docx ({partName}) nests XML elements more than");
    }

    [Theory]
    [InlineData("ppt/slides/slide1.dat")]
    [InlineData("ppt/slides/SLIDE1.Txt")]
    public void APptxSlideCalledAnythingIsCheckedAtTheLimitAndPastIt(string slidePart)
    {
        Extract(new PptxTextExtractor(), TestEpubs.PptxNestedBy(PptxGroupsAtTheLimit, slidePart), "a.pptx").Text.ShouldContain("hello");

        Should.Throw<UnreadableDocumentException>(
                () => Extract(new PptxTextExtractor(), TestEpubs.PptxNestedBy(20_000, slidePart), "a.pptx"))
            .Message.ShouldContain($"a.pptx ({slidePart}) nests XML elements more than");
    }

    [Fact]
    public void AnEpubNavigationFileCalledDotTxtIsCheckedAtTheLimitAndPastIt()
    {
        Extract(new EpubTextExtractor(), TestEpubs.WithAChapter("<p>kept</p>", NavigationPointsAtTheLimit, "toc.txt"), "book.epub")
            .Text.ShouldContain("kept");

        Should.Throw<UnreadableDocumentException>(
                () => Extract(new EpubTextExtractor(), TestEpubs.WithAChapter("<p>kept</p>", 20_000, "toc.txt"), "book.epub"))
            .Message.ShouldContain("book.epub (toc.txt) nests XML elements more than");
    }

    public static TheoryData<string> Encodings() => new() { "utf-8-bom", "utf-16le-bom", "utf-16be-bom", "utf-16le" };

    private static Encoding EncodingOf(string name) => name switch
    {
        "utf-8-bom" => new UTF8Encoding(true),
        "utf-16le-bom" => new UnicodeEncoding(false, true),
        "utf-16be-bom" => new UnicodeEncoding(true, true),
        _ => new UnicodeEncoding(false, false),
    };

    [Theory]
    [MemberData(nameof(Encodings))]
    public void APartWithAByteOrderMarkOrWrittenInUtf16IsReadByWhatIsInIt(string encoding)
    {
        // The reader finds the encoding from the first bytes, as the SDK's does. Seen as Latin-1 or by an
        // extension, these were never looked at.
        Extract(new DocxTextExtractor(), TestEpubs.DocxNestedBy(DocxPairsAtTheLimit, "word/document.dat", EncodingOf(encoding)), "a.docx")
            .Text.ShouldBe("hello\n");

        Should.Throw<UnreadableDocumentException>(
                () => Extract(new DocxTextExtractor(), TestEpubs.DocxNestedBy(20_000, "word/document.dat", EncodingOf(encoding)), "a.docx"))
            .Message.ShouldContain("nests XML elements more than");
    }

    // The budgets. The reading of an entry nothing refers to is held to the time and the bytes of the whole package.

    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(30);

    private const long TwoHundredMegabytes = 200L * 1024 * 1024;

    [Theory]
    [InlineData("docx")]
    [InlineData("pptx")]
    [InlineData("epub")]
    public void AnEntryThatInflatesToHundredsOfMegabytesIsStoppedByTheExtractionClock(string format)
    {
        var (extractor, package, name) = format switch
        {
            "docx" => ((ITextExtractor)new DocxTextExtractor(), TestEpubs.DocxNestedBy(3), "a.docx"),
            "pptx" => (new PptxTextExtractor(), TestEpubs.PptxNestedBy(3), "a.pptx"),
            _ => (new EpubTextExtractor(), TestEpubs.WithAChapter("<p>kept</p>"), "book.epub"),
        };
        var bomb = TestEpubs.WithAnInflatingEntry(package, TwoHundredMegabytes);
        var clock = Stopwatch.StartNew();

        // 200 ms is not enough to read 200 MB of XML at any speed this reader has.
        var thrown = Should.Throw<ExtractionTimeoutException>(
            () => extractor.Extract(new DeadlineStream(new MemoryStream(bomb), TimeSpan.FromMilliseconds(200), name), name));

        // Raised while the package was being scanned, not by a later parse that found the clock already out.
        thrown.StackTrace.ShouldContain(nameof(XmlNesting));
        clock.Elapsed.ShouldBeLessThan(Margin);
    }

    [Fact]
    public void TheXmlOfAPackageIsHeldToATotalOfInflatedBytes()
    {
        var bomb = TestEpubs.WithAnInflatingEntry(TestEpubs.DocxNestedBy(3), 3L * 1024 * 1024);

        var thrown = Should.Throw<UnreadableDocumentException>(
            () => XmlNesting.RequireShallowParts(new MemoryStream(bomb), "a.docx", budgetBytes: 1024 * 1024));

        thrown.Message.ShouldBe("a.docx holds XML that inflates to more than 1 MiB, which is too much to read.");
    }

    [Fact]
    public void TheBudgetIsSharedByTheEntriesOfAPackageAndNotKeptPerEntry()
    {
        // Each of these inflates to under half the budget, so a budget kept per entry would let all three
        // through.
        var package = TestEpubs.WithAnInflatingEntry(
            TestEpubs.WithAnInflatingEntry(TestEpubs.WithAnInflatingEntry(TestEpubs.DocxNestedBy(3), 400_000), 400_000), 400_000);

        Should.Throw<UnreadableDocumentException>(
            () => XmlNesting.RequireShallowParts(new MemoryStream(package), "a.docx", budgetBytes: 1_000_000));
    }

    [Fact]
    public void AnEpubWhoseXmlInflatesPastTheFullBudgetIsRefusedWithoutBeingLoaded()
    {
        // The budget as extractors apply it: 256 MiB. The bytes of an EPUB are in memory by then, so the
        // extraction clock does not see this reading, and the budget has to.
        var bomb = TestEpubs.WithAnInflatingEntry(
            TestEpubs.WithAChapter("<p>kept</p>"), XmlNesting.TotalBudgetBytes + 1024 * 1024);
        var clock = Stopwatch.StartNew();

        Should.Throw<UnreadableDocumentException>(() => new EpubTextExtractor().Extract(new MemoryStream(bomb), "book.epub"))
            .Message.ShouldContain("inflates to more than 256 MiB");

        clock.Elapsed.ShouldBeLessThan(Margin);
    }
}
