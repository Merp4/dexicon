using System.Text;
using Dexicon.Core.Catalog;
using Dexicon.Core.Extraction;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// The text of some PDFs holds U+0000. SQLite ends a text value at the first one, so the cached
/// copy of such a document was cut there while its row recorded the length of the whole: the
/// next pass read the cache, found text that no longer matched what had been indexed, and
/// indexed the head of the document in place of all of it.
///
/// Measured on a live catalogue: 161 of 1,956 cached PDFs read back shorter than they were
/// written, keeping 29% of their text on average and, for eight, none of it.
/// </summary>
public sealed class PdfTextWithNulTests
{
    private static readonly DateTime Stamp = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A PDF written by hand, because the builder refuses a character its font lacks and
    /// compresses what it writes. Each string is a page's content stream.
    /// </summary>
    private static byte[] Pdf(params string[] pages)
    {
        var ms = new MemoryStream();
        var offsets = new List<int>();
        void Write(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        void Obj(string body) { offsets.Add((int)ms.Length); Write($"{offsets.Count} 0 obj\n{body}\nendobj\n"); }

        var first = 4;
        var kids = string.Join(" ", pages.Select((_, i) => $"{first + 2 * i} 0 R"));
        Write("%PDF-1.4\n");
        Obj("<</Type/Catalog/Pages 2 0 R>>");
        Obj($"<</Type/Pages/Kids[{kids}]/Count {pages.Length}>>");
        Obj("<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>");
        foreach (var (content, i) in pages.Select((c, i) => (c, i)))
        {
            Obj($"<</Type/Page/Parent 2 0 R/MediaBox[0 0 600 400]/Contents {first + 2 * i + 1} 0 R/Resources<</Font<</F1 3 0 R>>>>>>");
            Obj($"<</Length {content.Length}>>\nstream\n{content}\nendstream");
        }

        var xref = (int)ms.Length;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Write($"{o:D10} 00000 n \n");
        Write($"trailer\n<</Root 1 0 R/Size {offsets.Count + 1}>>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>A page of words with a NUL code shown between two of them, as a maths font produces.</summary>
    private static string PageWithNul(string before, string after) =>
        $"BT /F1 12 Tf 60 200 Td ({before} ) Tj <00> Tj ( {after}) Tj ET";

    private static ExtractedText Extract(byte[] pdf) =>
        new PdfTextExtractor().Extract(new MemoryStream(pdf), "paper.pdf");

    [Fact]
    public void A_pdf_whose_text_holds_NUL_is_extracted_without_it()
    {
        var text = Extract(Pdf(PageWithNul("alpha beta", "gamma delta"))).Text;

        text.ShouldNotContain('\0');
        text.ShouldBe("alpha beta  gamma delta\n", "the words either side are kept, and only the NUL goes");
    }

    [Fact]
    public void A_units_offset_is_into_the_text_that_was_kept()
    {
        var extracted = Extract(Pdf(
            PageWithNul("first page", "ends here"),
            "BT /F1 12 Tf 60 200 Td (second page begins) Tj ET"));

        var second = extracted.Units.Single(u => u.Number == 2);
        extracted.Text[second.StartOffset..].ShouldStartWith("second page begins");
    }

    [Fact]
    public async Task A_pdf_whose_text_holds_NUL_is_cached_whole()
    {
        await using var harness = await IndexingHarness.StartAsync();
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await WriteAsync(harness, Pdf(PageWithNul(Words(40), Words(40))));

        await harness.RunIndexAsync();

        var row = await CachedRowAsync(harness);
        row.Text.Length.ShouldBe(row.ExtractedChars, "what the table returns is as long as what was written");
        row.Text.ShouldNotContain('\0');
    }

    [Fact]
    public async Task A_cached_row_that_reads_back_short_is_extracted_again_and_the_file_is_left_alone()
    {
        await using var harness = await IndexingHarness.StartAsync();
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await WriteAsync(harness, Pdf(PageWithNul(Words(40), Words(40))));

        var first = await harness.RunIndexAsync();
        first.FilesDone.ShouldBe(1);
        var chunks = harness.Vectors.CountFor("paper.pdf");
        chunks.ShouldBeGreaterThan(0);
        var whole = (await CachedRowAsync(harness)).Text;

        // What a cut leaves: the head of the document, with the length of the whole recorded.
        await using (var db = harness.NewContext())
            await db.FileTexts.ExecuteUpdateAsync(u => u.SetProperty(t => t.Text, whole[..20]));

        var second = await harness.RunIndexAsync();

        var row = await CachedRowAsync(harness);
        row.Text.ShouldBe(whole, "the damaged row was extracted again and replaced");
        second.FilesDone.ShouldBe(0, "the text is the text that was indexed, so nothing is chunked again");
        second.FilesSkipped.ShouldBe(1);
        harness.Vectors.CountFor("paper.pdf").ShouldBe(chunks);
    }

    private static string Words(int n) => string.Join(' ', Enumerable.Range(0, n).Select(i => $"word{i}"));

    private static async Task WriteAsync(IndexingHarness harness, byte[] pdf)
    {
        var path = Path.Combine(harness.SourceDirectory, "paper.pdf");
        await File.WriteAllBytesAsync(path, pdf);
        File.SetLastWriteTimeUtc(path, Stamp);
    }

    private static async Task<FileText> CachedRowAsync(IndexingHarness harness)
    {
        await using var db = harness.NewContext();
        return await db.FileTexts.AsNoTracking().SingleAsync();
    }
}
