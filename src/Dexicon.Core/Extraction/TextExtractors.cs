using System.Text;
using AngleSharp.Html.Parser;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Dexicon.Core.Extraction;

/// <summary>
/// The result of pulling text out of a file. <paramref name="Units"/> carries the
/// format's natural provenance unit — page for PDF, slide for PPTX, chapter for EPUB —
/// so a citation can say "p. 34" instead of "chunk 87".
/// </summary>
public sealed record ExtractedText(string Text, IReadOnlyList<ExtractedUnit> Units, string? Title = null)
{
    public static readonly ExtractedText Empty = new(string.Empty, []);
}

/// <param name="Number">1-based page / slide / chapter number.</param>
/// <param name="StartOffset">Character offset into <see cref="ExtractedText.Text"/>.</param>
public sealed record ExtractedUnit(int Number, int StartOffset, string? Label = null);

/// <summary>
/// Raised when a file is a recognised format that cannot be read — encrypted, DRM'd,
/// or structurally broken. Distinct from "produced no text", which is not an error.
/// </summary>
public sealed class ExtractionFailedException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Bytes to text, per media type. The seam that keeps OCR a future registration
/// rather than a rewrite: nothing downstream assumes the text came from a text layer.
/// </summary>
public interface ITextExtractor
{
    bool CanHandle(string extension);
    ExtractedText Extract(Stream content, string fileName);
}

public static class ExtractorRegistry
{
    private static readonly ITextExtractor[] Extractors =
    [
        new PdfTextExtractor(),
        new DocxTextExtractor(),
        new PptxTextExtractor(),
        new EpubTextExtractor(),
        new HtmlTextExtractor(),
    ];

    /// <summary>Null means "treat it as plain text", which is the right answer for code.</summary>
    public static ITextExtractor? For(string relativePath)
    {
        var ext = Path.GetExtension(relativePath).ToLowerInvariant();
        return Array.Find(Extractors, e => e.CanHandle(ext));
    }

    public static bool IsDocumentFormat(string relativePath) => For(relativePath) is not null;
}

/// <summary>
/// PdfPig (Apache-2.0). Text layer only — there is no OCR, and a scanned PDF is
/// reported as empty with a reason rather than silently producing nothing.
/// </summary>
public sealed class PdfTextExtractor : ITextExtractor
{
    public bool CanHandle(string extension) => extension == ".pdf";

    public ExtractedText Extract(Stream content, string fileName)
    {
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);

        PdfDocument document;
        try
        {
            document = PdfDocument.Open(buffer.ToArray());
        }
        catch (Exception ex)
        {
            throw new ExtractionFailedException(
                $"'{fileName}' could not be opened as a PDF. It may be encrypted or corrupt: {ex.Message}", ex);
        }

        using (document)
        {
            var sb = new StringBuilder();
            var units = new List<ExtractedUnit>();

            foreach (var page in document.GetPages())
            {
                units.Add(new ExtractedUnit(page.Number, sb.Length, $"Page {page.Number}"));
                var text = ContentOrderTextExtractor.GetText(page);
                if (!string.IsNullOrWhiteSpace(text)) sb.Append(text).Append('\n');
            }

            var title = document.Information?.Title;
            return new ExtractedText(sb.ToString(), units,
                string.IsNullOrWhiteSpace(title) ? null : title);
        }
    }
}

public sealed class DocxTextExtractor : ITextExtractor
{
    public bool CanHandle(string extension) => extension == ".docx";

    public ExtractedText Extract(Stream content, string fileName)
    {
        try
        {
            using var doc = WordprocessingDocument.Open(content, false);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body is null) return ExtractedText.Empty;

            var sb = new StringBuilder();
            foreach (var para in body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
            {
                var text = para.InnerText;
                if (!string.IsNullOrWhiteSpace(text)) sb.Append(text).Append('\n');
            }

            return new ExtractedText(sb.ToString(), []);
        }
        catch (Exception ex) when (ex is not ExtractionFailedException)
        {
            throw new ExtractionFailedException($"'{fileName}' is not a readable .docx: {ex.Message}", ex);
        }
    }
}

public sealed class PptxTextExtractor : ITextExtractor
{
    public bool CanHandle(string extension) => extension == ".pptx";

    public ExtractedText Extract(Stream content, string fileName)
    {
        try
        {
            using var doc = PresentationDocument.Open(content, false);
            var parts = doc.PresentationPart?.SlideParts?.ToList();
            if (parts is null or { Count: 0 }) return ExtractedText.Empty;

            var sb = new StringBuilder();
            var units = new List<ExtractedUnit>();
            var number = 1;

            foreach (var slide in parts)
            {
                units.Add(new ExtractedUnit(number, sb.Length, $"Slide {number}"));
                var text = slide.Slide?.InnerText;
                if (!string.IsNullOrWhiteSpace(text)) sb.Append(text).Append('\n');

                // Speaker notes are frequently where the actual argument lives.
                var notes = slide.NotesSlidePart?.NotesSlide?.InnerText;
                if (!string.IsNullOrWhiteSpace(notes)) sb.Append("[notes] ").Append(notes).Append('\n');

                number++;
            }

            return new ExtractedText(sb.ToString(), units);
        }
        catch (Exception ex) when (ex is not ExtractionFailedException)
        {
            throw new ExtractionFailedException($"'{fileName}' is not a readable .pptx: {ex.Message}", ex);
        }
    }
}

public sealed class EpubTextExtractor : ITextExtractor
{
    public bool CanHandle(string extension) => extension == ".epub";

    public ExtractedText Extract(Stream content, string fileName)
    {
        try
        {
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            buffer.Position = 0;

            var book = VersOne.Epub.EpubReader.ReadBook(buffer);
            var sb = new StringBuilder();
            var units = new List<ExtractedUnit>();
            var parser = new HtmlParser();
            var number = 1;

            foreach (var file in book.ReadingOrder)
            {
                units.Add(new ExtractedUnit(number, sb.Length, $"Chapter {number}"));
                using var doc = parser.ParseDocument(file.Content);
                var text = doc.Body?.TextContent;
                if (!string.IsNullOrWhiteSpace(text)) sb.Append(text.Trim()).Append('\n');
                number++;
            }

            return new ExtractedText(sb.ToString(), units, book.Title);
        }
        catch (Exception ex) when (ex is not ExtractionFailedException)
        {
            throw new ExtractionFailedException(
                $"'{fileName}' is not a readable .epub. DRM-protected books cannot be read: {ex.Message}", ex);
        }
    }
}

public sealed class HtmlTextExtractor : ITextExtractor
{
    public bool CanHandle(string extension) => extension is ".html" or ".htm";

    public ExtractedText Extract(Stream content, string fileName)
    {
        // NOTE: this handles HTML *documents* reached as uploads. HTML found inside a
        // workspace tree is template SOURCE and is indexed as code — see LanguageMap.
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var parser = new HtmlParser();
        using var doc = parser.ParseDocument(reader.ReadToEnd());

        foreach (var node in doc.QuerySelectorAll("script, style, noscript").ToList())
            node.Remove();

        return new ExtractedText(doc.Body?.TextContent?.Trim() ?? string.Empty, [], doc.Title);
    }
}
