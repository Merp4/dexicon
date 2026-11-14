using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Dexicon.Core.Extraction;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// PdfPig 0.1.16 splits a page's letters into buckets by text orientation (horizontal and
/// the three rotations) and groups each bucket into words on a parallel loop, appending the
/// words to one list in the order the buckets finish. A page with both horizontal and
/// rotated text (a margin label, an axis title, a sideways table) therefore returned the
/// same words in a different order from one extraction to the next, and the blocks built
/// from them followed. The characters were identical, so nothing failed: the chunk counts
/// of a corpus drifted by a few between fresh indexes.
///
/// The fixture is generated here as raw PDF syntax, because the page builder in PdfPig has
/// no way to write rotated text. Each page has horizontal lines in two columns and a few
/// lines rotated by 90, 180 or 270 degrees.
/// </summary>
public sealed class PdfExtractionDeterminismTests
{
    private const int Runs = 200;

    private static readonly string[] Vocabulary =
    [
        "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india",
        "juliet", "kilo", "lima", "mike", "november", "oscar",
    ];

    /// <summary>
    /// A Helvetica PDF with one content stream per page. Horizontal lines sit in two
    /// columns; rotated lines are placed at random and rotated by one of
    /// <paramref name="angles"/> through the text matrix.
    /// </summary>
    internal static byte[] Pdf(int pages, int seed, int horizontal, int rotated, int[] angles)
    {
        var random = new Random(seed);
        var kids = new StringBuilder();
        for (var p = 0; p < pages; p++) kids.Append(CultureInfo.InvariantCulture, $"{4 + (2 * p)} 0 R ");

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{kids}] /Count {pages} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        for (var p = 0; p < pages; p++)
        {
            var content = new StringBuilder("BT\n");
            for (var i = 0; i < horizontal + rotated; i++)
            {
                var angle = i < horizontal ? 0 : angles[random.Next(angles.Length)];
                var radians = angle * Math.PI / 180;
                var cos = (int)Math.Round(Math.Cos(radians));
                var sin = (int)Math.Round(Math.Sin(radians));
                var x = i < horizontal ? 54 + (random.Next(0, 2) * 280) : 30 + random.Next(0, 540);
                var y = i < horizontal ? 740 - (i * 13) : 100 + random.Next(0, 600);
                var line = string.Join(' ', Enumerable.Range(0, 3 + random.Next(3)).Select(_ => Vocabulary[random.Next(Vocabulary.Length)]));
                content.Append(CultureInfo.InvariantCulture, $"/F1 9 Tf {cos} {sin} {-sin} {cos} {x} {y} Tm ({line}) Tj\n");
            }

            content.Append("ET\n");
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + (2 * p)} 0 R >>");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}endstream");
        }

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    [Fact]
    public void ConcurrentExtractionsOfOnePdfWithRotatedTextReturnOneText()
    {
        var bytes = Pdf(pages: 13, seed: 2, horizontal: 50, rotated: 12, angles: [90, 180, 270]);
        var texts = new ConcurrentBag<string>();

        Parallel.For(0, Runs, new ParallelOptions { MaxDegreeOfParallelism = 4 }, _ =>
        {
            using var stream = new MemoryStream(bytes);
            texts.Add(new PdfTextExtractor().Extract(stream, "fixture.pdf").Text);
        });

        texts.Count.ShouldBe(Runs);
        texts.Select(t => t.Length).Distinct().Count().ShouldBe(1, "the characters never differed, only their order");
        texts.Distinct().Count().ShouldBe(1, $"{Runs} extractions of the same bytes gave different block orders");
    }

    [Fact]
    public void SequentialExtractionsOfOnePdfWithRotatedTextReturnOneText()
    {
        var bytes = Pdf(pages: 13, seed: 1, horizontal: 50, rotated: 6, angles: [90, 270]);
        var texts = new HashSet<string>();

        for (var i = 0; i < 60; i++)
        {
            using var stream = new MemoryStream(bytes);
            texts.Add(new PdfTextExtractor().Extract(stream, "fixture.pdf").Text);
        }

        texts.Count.ShouldBe(1);
    }
}
