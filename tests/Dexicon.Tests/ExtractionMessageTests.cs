using System.Text;
using Dexicon.Core.Extraction;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// The message each extractor gives a file it cannot read: the problem, then what to do, then the reader's own
/// message, labelled. The reader's message comes from a library and changes with its version, so only the part
/// before it is pinned.
/// </summary>
public sealed class ExtractionMessageTests
{
    private static string MessageFor(ITextExtractor extractor, byte[] bytes, string fileName) =>
        Should.Throw<UnreadableDocumentException>(() => extractor.Extract(new MemoryStream(bytes), fileName)).Message;

    [Fact]
    public void ADocxThatIsNotAZipSaysToSaveItAgain() =>
        MessageFor(new DocxTextExtractor(), "not a zip"u8.ToArray(), "a.docx").ShouldStartWith(
            "'a.docx' could not be read as a Word document. It may be damaged or in another format. "
            + "Open it and save it again as .docx. (DOCX reader: ");

    [Fact]
    public void APptxThatIsNotAZipSaysToSaveItAgain() =>
        MessageFor(new PptxTextExtractor(), "not a zip"u8.ToArray(), "a.pptx").ShouldStartWith(
            "'a.pptx' could not be read as a PowerPoint presentation. It may be damaged or in another format. "
            + "Open it and save it again as .pptx. (PPTX reader: ");

    [Fact]
    public void APdfThatWillNotOpenSaysToDownloadItAgain()
    {
        // A trailer, so the check before the parser passes, and nothing a parser can open.
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.4\nnot a pdf body\nstartxref\n0\n%%EOF\n");

        MessageFor(new PdfTextExtractor(), bytes, "a.pdf").ShouldStartWith(
            "'a.pdf' could not be opened as a PDF. It may be damaged or encrypted. "
            + "Try downloading or exporting it again. (PDF reader: ");
    }

    [Fact]
    public void AnEpubWithADamagedEntrySaysToDownloadItAgain() =>
        MessageFor(new EpubTextExtractor(), TestEpubs.WithADamagedEntry(), "a.epub").ShouldStartWith(
            "'a.epub' could not be read as an EPUB. It may be damaged. Try downloading it again. (EPUB reader: ");

    [Fact]
    public void AnEpubThatIsNotAZipSaysSo() =>
        MessageFor(new EpubTextExtractor(), "this is just text"u8.ToArray(), "a.epub").ShouldStartWith(
            "'a.epub' could not be read as an EPUB because it is not a valid zip archive. It may be damaged or in "
            + "another format. Try downloading it again. (EPUB reader: ");

    [Fact]
    public void ATimeoutGetsNoAdvice()
    {
        // A file that was slow to read is not fixed by downloading it again.
        var failure = ExtractionFailures.Of("'a.pdf' could not be opened as a PDF.", "Download it again.", "PDF reader",
            new TimeoutException("Too slow."));

        failure.ShouldNotBeOfType<UnreadableDocumentException>();
        failure.Message.ShouldBe("'a.pdf' could not be opened as a PDF. (PDF reader: Too slow)");
    }

    [Fact]
    public void AVerdictWrappedAroundAnIoErrorGetsTheAdvice()
    {
        // An explicit verdict anywhere in the causes outweighs an I/O error beside it.
        var failure = ExtractionFailures.Of("'a.docx' could not be read.", "Save it again.", null,
            new UnreadableDocumentException("bad part", new IOException("disk")));

        failure.ShouldBeOfType<UnreadableDocumentException>().Message.ShouldBe("'a.docx' could not be read. Save it again.");
    }
}
