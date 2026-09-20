using Dexicon.Core.Extraction;
using Shouldly;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Dexicon.Tests;

/// <summary>
/// Extraction is a synchronous call into PdfPig or the OpenXML readers, none of which
/// take a cancellation token. An index job's own token therefore could not interrupt one,
/// and a single file held a corpus for over two hours with two refresh jobs queued behind
/// it; only restarting the container ended it.
///
/// The budget is enforced on the file's reads rather than reported to the caller, because
/// reporting it would have left the thread exactly where it was. These tests fix that
/// distinction: the read throws, so the stack unwinds and the thread comes back.
/// </summary>
public sealed class ExtractionDeadlineTests
{
    private static readonly TimeSpan Expired = TimeSpan.Zero;
    private static readonly TimeSpan Generous = TimeSpan.FromMinutes(5);

    private static MemoryStream Bytes(int n = 4096) => new(new byte[n]);

    [Fact]
    public void ReadsPassThroughWhileThereIsTimeLeft()
    {
        using var inner = Bytes();
        var stream = new DeadlineStream(inner, Generous, "book.pdf");

        stream.Read(new byte[128]).ShouldBe(128);
        stream.Expired.ShouldBeFalse();
    }

    [Fact]
    public void AReadPastTheDeadlineThrows()
    {
        using var inner = Bytes();
        var stream = new DeadlineStream(inner, Expired, "book.pdf");

        var ex = Should.Throw<ExtractionTimeoutException>(() => stream.Read(new byte[128]));

        ex.Message.ShouldContain("book.pdf");
        stream.Expired.ShouldBeTrue();
    }

    [Fact]
    public void ItKeepsThrowingSoALibraryThatCatchesCannotResume()
    {
        // PdfPig's recovery path catches broadly. A one-shot throw would be swallowed and
        // the scan would carry on, which is the failure this is meant to end.
        using var inner = Bytes();
        var stream = new DeadlineStream(inner, Expired, "book.pdf");

        for (var i = 0; i < 5; i++)
            Should.Throw<ExtractionTimeoutException>(() => stream.Read(new byte[8]));
    }

    [Fact]
    public void SeekingPastTheDeadlineThrowsToo()
    {
        // The backward scan seeks as much as it reads. Guarding only Read would let it
        // spin on Seek without ever being stopped.
        using var inner = Bytes();
        var stream = new DeadlineStream(inner, Expired, "book.pdf");

        Should.Throw<ExtractionTimeoutException>(() => stream.Seek(0, SeekOrigin.Begin));
        Should.Throw<ExtractionTimeoutException>(() => stream.Position = 10);
    }

    [Fact]
    public void TheInnerStreamIsLeftOpenForItsOwner()
    {
        // The indexer opens the file in a `using` and wraps it here. Disposing it from the
        // wrapper too would close it twice.
        using var inner = Bytes();
        var stream = new DeadlineStream(inner, Generous, "book.pdf");

        stream.Dispose();

        Should.NotThrow(() => inner.Position = 0);
    }

    [Fact]
    public void LengthAndSeekabilityAreVisibleBecausePdfPigRequiresThem()
    {
        // PdfPig reads a seekable stream and asks for its length up front. A wrapper that
        // hid either would fail every PDF rather than only the slow ones.
        using var inner = Bytes(1234);
        var stream = new DeadlineStream(inner, Generous, "book.pdf");

        stream.CanSeek.ShouldBeTrue();
        stream.CanRead.ShouldBeTrue();
        stream.CanWrite.ShouldBeFalse();
        stream.Length.ShouldBe(1234);
    }

    /// <summary>
    /// Every extractor filters its catch-all on <see cref="ExtractionFailedException"/>.
    /// A timeout that was a sibling of that type rather than a subtype was re-wrapped by
    /// DOCX and PPTX and arrived as "is not a readable .docx", which is both untrue and
    /// routed to the wrong branch of the indexer. Measured by making the type a sibling
    /// again: those two cases fail and EPUB does not, because its handler salvages rather
    /// than rethrows. EPUB is covered here so that stays true rather than staying luck.
    ///
    /// The budget is spent before the first read, so what the bytes contain does not
    /// matter: the reader's first Seek or Read into the archive is the one that throws.
    /// </summary>
    [Theory]
    [InlineData("docx")]
    [InlineData("pptx")]
    [InlineData("epub")]
    public void ATimeoutIsNotReportedAsAnUnreadableFile(string format)
    {
        ITextExtractor extractor = format switch
        {
            "docx" => new DocxTextExtractor(),
            "pptx" => new PptxTextExtractor(),
            _ => new EpubTextExtractor(),
        };
        var name = $"book.{format}";

        using var inner = Bytes();
        var stream = new DeadlineStream(inner, Expired, name);

        var ex = Should.Throw<ExtractionTimeoutException>(() => extractor.Extract(stream, name));

        ex.Message.ShouldNotContain("not a readable");
        ex.Message.ShouldContain(name);
    }

    [Fact]
    public void ATimeoutEscapesExtractionRatherThanReadingAsACorruptFile()
    {
        // The PDF extractor turns anything thrown during open into "may be encrypted or
        // corrupt". A timeout is neither, and reporting it that way would send whoever
        // reads the job looking for a damaged file instead of a slow mount or a budget
        // set too low.
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(600, 400).AddText("Anything at all.", 12, new PdfPoint(60, 200), font);

        using var inner = new MemoryStream(builder.Build());
        var stream = new DeadlineStream(inner, Expired, "slow.pdf");

        Should.Throw<ExtractionTimeoutException>(() =>
            new PdfTextExtractor().Extract(stream, "slow.pdf"));
    }
}
