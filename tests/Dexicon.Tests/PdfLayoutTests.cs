using Dexicon.Core.Extraction;
using Shouldly;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Dexicon.Tests;

/// <summary>
/// A PDF's content stream is written in whatever order the producer chose, which on a
/// two-column page is often left line, right line, left line. Reading it in that order
/// interleaves the columns into text that is grammatical nonsense but looks like prose,
/// so nothing downstream can detect it: it embeds, it chunks, and it comes back as a
/// search hit.
///
/// The same ordering breaks ordinary prose more quietly. Content order ends a line where
/// the text met the right margin, so a paragraph arrives as ten short lines. The chunker
/// splits on lines and uses a blank line as a PDF's boundary, so it had nothing to work
/// with. Measured over a real 424-page book, content order gave 14,954 lines averaging 43
/// characters where the EPUB of the same title gave 5,727 averaging 109.
///
/// The fixture is built here rather than committed, so the assertion is about the
/// extractor and not about one publisher's file.
/// </summary>
public sealed class PdfLayoutTests
{
    /// <summary>
    /// A page with two columns, written to the content stream a line at a time across
    /// both of them: the order that produces interleaving.
    /// </summary>
    private static MemoryStream TwoColumnPdf()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(600, 400);

        string[] left = ["Alpha one", "Alpha two", "Alpha three", "Alpha four"];
        string[] right = ["Beta one", "Beta two", "Beta three", "Beta four"];

        for (var i = 0; i < left.Length; i++)
        {
            var y = 340 - (i * 24);
            page.AddText(left[i], 12, new UglyToad.PdfPig.Core.PdfPoint(40, y), font);
            page.AddText(right[i], 12, new UglyToad.PdfPig.Core.PdfPoint(340, y), font);
        }

        return new MemoryStream(builder.Build());
    }

    private static string Extract(Stream pdf) =>
        new PdfTextExtractor().Extract(pdf, "fixture.pdf").Text;

    [Fact]
    public void ColumnsAreReadDownRatherThanAcross()
    {
        using var pdf = TwoColumnPdf();

        var text = Extract(pdf);

        // Every Alpha before every Beta. Read across the page instead and the first Beta
        // arrives before the last Alpha.
        var lastAlpha = text.LastIndexOf("Alpha four", StringComparison.Ordinal);
        var firstBeta = text.IndexOf("Beta one", StringComparison.Ordinal);

        lastAlpha.ShouldBeGreaterThan(-1, "the left column has to be there at all");
        firstBeta.ShouldBeGreaterThan(-1, "so does the right");
        lastAlpha.ShouldBeLessThan(firstBeta, "the columns are interleaved");
    }

    [Fact]
    public void AColumnArrivesAsOneLineNotOnePerVisualRow()
    {
        using var pdf = TwoColumnPdf();

        var lines = Extract(pdf)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        // Grouped into blocks, so each column is a line rather than four. This is what
        // gives the chunker the same shape it already gets from EPUB and HTML.
        lines.Count.ShouldBe(2);
        lines[0].ShouldBe("Alpha one Alpha two Alpha three Alpha four");
        lines[1].ShouldBe("Beta one Beta two Beta three Beta four");
    }

    [Fact]
    public void APageWithNoTextLayerYieldsNothingRatherThanThrowing()
    {
        // A scan has no letters. The segmenter is not defined on an empty set, and the
        // caller reports `status: empty` from the absence of text, so this must not throw.
        var builder = new PdfDocumentBuilder();
        builder.AddPage(600, 400);
        using var blank = new MemoryStream(builder.Build());

        Extract(blank).Trim().ShouldBeEmpty();
    }
}
