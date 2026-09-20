using Dexicon.Core.Extraction;
using Shouldly;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Dexicon.Tests;

/// <summary>
/// A PDF whose download was cut short has no cross-reference table, and PdfPig's answer to
/// that is to rebuild one by scanning the file backwards for object markers, re-reading a
/// 4 KB block for every byte it steps.
///
/// In memory that is merely slow. Over a bind mount each of those reads is a round trip:
/// measured at about 6,000 a second against a 68 MB truncated PDF, which is 3.3 hours for
/// the one file, with the index job and everything queued behind it stopped for the
/// duration. The file in question ends mid-dictionary at exactly 68 MiB and was never
/// going to parse.
///
/// So the trailer is checked first. These tests fix the two properties that matter: a
/// conforming file is untouched, and a truncated one is refused after reading its tail
/// rather than after reading all of it.
/// </summary>
public sealed class TruncatedPdfTests
{
    private static byte[] ValidPdf(string text = "The quick brown fox jumps over the lazy dog.")
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(600, 400).AddText(text, 12, new PdfPoint(60, 200), font);
        return builder.Build();
    }

    /// <summary>Counts what the extractor actually reads, so "it did not scan" is measured.</summary>
    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = base.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }

        public override int Read(Span<byte> buffer)
        {
            var n = base.Read(buffer);
            BytesRead += n;
            return n;
        }
    }

    [Fact]
    public void AConformingPdfIsStillRead()
    {
        // The control. A guard that rejects the corpus is worse than the hang it prevents,
        // and 1,802 of the 1,804 PDFs measured carry %%EOF within their last 2 KB.
        var extracted = new PdfTextExtractor().Extract(new MemoryStream(ValidPdf()), "fine.pdf");

        extracted.Text.ShouldContain("quick brown fox");
    }

    [Fact]
    public void ATruncatedPdfIsRefusedRatherThanParsed()
    {
        var bytes = ValidPdf();
        var truncated = bytes[..(bytes.Length * 4 / 5)];

        var ex = Should.Throw<ExtractionFailedException>(() =>
            new PdfTextExtractor().Extract(new MemoryStream(truncated), "cut-short.pdf"));

        ex.Message.ShouldContain("%%EOF");
        ex.Message.ShouldContain("cut-short.pdf");
    }

    [Fact]
    public void TheRefusalCostsTheTailRatherThanTheFile()
    {
        // The whole point. A 5 MB file with no trailer must cost one read of its end, not
        // a walk back through all of it. Padding a valid PDF reproduces the shape of the
        // real case: plausible bytes, no %%EOF anywhere near the end.
        var padded = new byte[5 * 1024 * 1024];
        ValidPdf().CopyTo(padded, 0);
        var stream = new CountingStream(padded);

        Should.Throw<ExtractionFailedException>(() =>
            new PdfTextExtractor().Extract(stream, "padded.pdf"));

        stream.BytesRead.ShouldBeLessThanOrEqualTo(8 * 1024);
    }

    [Fact]
    public void TheMessageNamesTheSizeSoTheFileCanBeFound()
    {
        // A run that reported "extraction failed" and nothing else gave nobody anything to
        // act on. The size is what identifies a truncated download: the one found here was
        // exactly 68 MiB, cut on a buffer boundary.
        var padded = new byte[3_000_000];
        ValidPdf().CopyTo(padded, 0);

        var ex = Should.Throw<ExtractionFailedException>(() =>
            new PdfTextExtractor().Extract(new MemoryStream(padded), "book.pdf"));

        ex.Message.ShouldContain("3,000,000");
    }

    [Fact]
    public void AnEmptyFileIsRefusedWithoutMentioningTheTrailer()
    {
        // Zero bytes has no tail to read, and "no %%EOF in its last 0 bytes" is not a
        // sentence worth emitting.
        var ex = Should.Throw<ExtractionFailedException>(() =>
            new PdfTextExtractor().Extract(new MemoryStream(), "nothing.pdf"));

        ex.Message.ShouldContain("empty");
        ex.Message.ShouldNotContain("%%EOF");
    }

    [Fact]
    public void APdfSmallerThanTheTailWindowIsReadNormally()
    {
        // The window is 4 KB and a one-page PDF is smaller than that, so the whole file is
        // the tail. An off-by-one here would reject every short document.
        var bytes = ValidPdf("Short.");
        bytes.Length.ShouldBeLessThan(4096);

        Should.NotThrow(() => new PdfTextExtractor().Extract(new MemoryStream(bytes), "short.pdf"));
    }
}
