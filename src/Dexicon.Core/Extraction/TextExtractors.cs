using System.Text;
using AngleSharp.Dom;
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

public static class ExtractorVersions
{
    /// <summary>
    /// Bumped whenever extraction OUTPUT changes, so cached text is re-extracted rather
    /// than trusted forever.
    ///
    /// Extraction is cached per blob, and rightly so — a 437-page PDF costs ~1.8 s and
    /// its bytes never change. But the CODE changes, and without a version the cache is
    /// permanent: a library ingested before a fix keeps the broken text invisibly, and no
    /// reindex repairs it, because reindexing re-chunks the cached text rather than
    /// re-reading the file.
    ///
    /// 2: HTML and EPUB keep block structure — one block per line — instead of
    ///    collapsing a whole chapter onto a single unsplittable line.
    /// </summary>
    public const int Current = 2;
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
                HtmlText.AppendBlocks(doc.Body, sb);
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

        var sb = new StringBuilder();
        HtmlText.AppendBlocks(doc.Body, sb);
        return new ExtractedText(sb.ToString().Trim(), [], doc.Title);
    }
}

/// <summary>
/// Turns an HTML body into text that keeps its BLOCK STRUCTURE, one block per line.
///
/// AngleSharp's <c>TextContent</c> is the obvious thing to reach for and it is wrong
/// here: it concatenates every descendant text node with no separators, so a chapter
/// comes back as a single line tens of thousands of characters long. The chunker splits
/// on line boundaries, so it could not split at all — a 578,000-character EPUB produced
/// 18 chunks averaging 32,000 characters, each of which the embedding model silently
/// truncated at its context limit. The book reported itself as indexed while most of it
/// was nowhere in the index.
///
/// Newlines are not cosmetic: they are what makes the text chunkable, and what makes a
/// line number in a search hit mean anything.
/// </summary>
internal static class HtmlText
{
    /// <summary>Elements whose text is markup machinery, not content.</summary>
    private static readonly HashSet<string> Skipped =
        new(StringComparer.Ordinal) { "script", "style", "noscript", "template", "head" };

    /// <summary>
    /// Block-level elements, as HTML renders them: each one starts on a new line.
    /// Inline elements (em, a, span, code…) deliberately are NOT here — breaking a line
    /// mid-sentence at every &lt;em&gt; would be as wrong as not breaking at all.
    /// </summary>
    private static readonly HashSet<string> Blocks =
        new(StringComparer.Ordinal)
        {
            "p", "div", "section", "article", "aside", "header", "footer", "main", "nav",
            "h1", "h2", "h3", "h4", "h5", "h6",
            "ul", "ol", "li", "dl", "dt", "dd",
            "blockquote", "pre", "figure", "figcaption", "hr",
            "table", "thead", "tbody", "tfoot", "tr", "td", "th",
            "form", "fieldset", "address", "body",
        };

    public static void AppendBlocks(IElement? root, StringBuilder sb)
    {
        if (root is null) return;
        Walk(root, sb);
        EndLine(sb);
    }

    private static void Walk(INode node, StringBuilder sb)
    {
        foreach (var child in node.ChildNodes)
        {
            switch (child)
            {
                case IText text:
                    AppendCollapsed(text.Data, sb);
                    break;

                case IElement el when Skipped.Contains(el.LocalName):
                    break;

                // <br> is an explicit line break even though it is an inline element.
                case IElement { LocalName: "br" }:
                    EndLine(sb);
                    break;

                case IElement el:
                    var block = Blocks.Contains(el.LocalName);
                    if (block) EndLine(sb);
                    Walk(el, sb);
                    if (block) EndLine(sb);
                    break;
            }
        }
    }

    /// <summary>
    /// Collapses runs of whitespace to a single space, the way HTML rendering does.
    /// Source indentation is not content, and leaving it in inflates every chunk.
    /// </summary>
    private static void AppendCollapsed(string text, StringBuilder sb)
    {
        // Seeded from what is already in the buffer, so collapsing works ACROSS text
        // nodes: "A " followed by an inline element whose own text starts with a space
        // must still come out as one space.
        var lastWasSpace = sb.Length == 0 || sb[^1] is '\n' or ' ';
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace) { sb.Append(' '); lastWasSpace = true; }
            }
            else { sb.Append(ch); lastWasSpace = false; }
        }
    }

    /// <summary>Ends the current line, without leaving a run of blank ones.</summary>
    private static void EndLine(StringBuilder sb)
    {
        while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
    }
}
