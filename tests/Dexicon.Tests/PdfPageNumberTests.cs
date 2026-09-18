using Dexicon.Core.Extraction;
using Shouldly;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Dexicon.Tests;

/// <summary>
/// The page numbers printed on a PDF's pages end up in the extracted text, one line each.
///
/// Counted over the books in this corpus: 99 bare numbers per 1,000 extracted blocks from
/// the PDFs against 13 from the EPUBs of the same titles. They are not inert. A passage
/// carried across a page break extracts as "...the section ends here. 247 Chapter 9 opens
/// with...", so the chunk holds a sentence no edition of the book contains, and that chunk
/// is what gets embedded.
///
/// Position is what separates a page number from a number: the same digits in the body of
/// a page are a table cell, a list item or a line of code, and those have to survive.
/// </summary>
public sealed class PdfPageNumberTests
{
    private const double Width = 600, Height = 400;

    /// <summary>
    /// A page of prose with whatever furniture the test asks for. Y is measured from the
    /// bottom, so 12 is the footer and 384 the header; the margin rule covers 8% of the
    /// height at each end, which is 32 points here.
    /// </summary>
    private static MemoryStream Page(params (string Text, double X, double Y)[] items)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(Width, Height);

        foreach (var (text, x, y) in items)
            page.AddText(text, 12, new PdfPoint(x, y), font);

        return new MemoryStream(builder.Build());
    }

    private static List<string> Lines(Stream pdf) =>
        new PdfTextExtractor().Extract(pdf, "fixture.pdf").Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

    [Theory]
    [InlineData("247", 12)]      // footer, the common case
    [InlineData("8", 384)]       // header, which some publishers use instead
    [InlineData("xiv", 12)]      // front matter, numbered in lowercase roman
    [InlineData("1024", 12)]     // a long book still has a page number, not a constant
    public void ABareNumberInAMarginIsDropped(string furniture, double y)
    {
        using var pdf = Page(("The section ends here.", 40, 200), (furniture, 300, y));

        var lines = Lines(pdf);

        lines.ShouldContain("The section ends here.");
        lines.ShouldNotContain(furniture);
    }

    [Fact]
    public void ABareNumberInTheBodyIsKept()
    {
        // Same digits, same page, 200 points up: a table cell or a numbered list item.
        // Dropping this to save furniture would lose content, so the rule is position.
        using var pdf = Page(("247", 300, 200));

        Lines(pdf).ShouldContain("247");
    }

    [Fact]
    public void TextThatMerelyContainsANumberIsKept()
    {
        // The pattern is anchored, so a running foot survives even though it sits exactly
        // where a page number would. It is furniture too, but it carries the chapter title,
        // and removing titles is a different change with a different risk.
        //
        // Alone on the page so that the block is the footer. Give this fixture a body line
        // at the same left margin and Docstrum merges the two into one block, because its
        // spacing estimate comes from the words present and two items is not much to go on.
        using var pdf = Page(("Chapter 4", 40, 12));

        Lines(pdf).ShouldContain("Chapter 4");
    }

    [Fact]
    public void AFootnoteInTheBottomMarginIsKept()
    {
        // Footnotes share the bottom margin with the page number and open with a number,
        // so position alone would take them. The pattern is anchored, which is the only
        // thing keeping this line: it is short enough to pass the length guard and it
        // starts with a digit.
        using var pdf = Page(("1 Ibid.", 40, 12));

        Lines(pdf).ShouldContain("1 Ibid.");
    }

    [Fact]
    public void AWordMadeOfRomanNumeralLettersIsKeptInTheBody()
    {
        // "mix" is a valid roman numeral and an ordinary English word. In a margin it is
        // furniture; in the body it is prose, and the position test is what tells them
        // apart without a dictionary.
        using var pdf = Page(("mix", 300, 200));

        Lines(pdf).ShouldContain("mix");
    }

    [Fact]
    public void ThePageStillReadsAsOneRunOfProse()
    {
        // The point of the removal: what the chunker sees across a page boundary. With the
        // number in, these two sentences are separated by a line reading "247".
        using var pdf = Page(("The section ends here.", 40, 220),
                             ("Chapter nine opens with a question.", 40, 190),
                             ("247", 300, 12));

        var lines = Lines(pdf);

        lines.ShouldNotContain("247");
        string.Join(" ", lines).ShouldNotContain("247");
    }
}
