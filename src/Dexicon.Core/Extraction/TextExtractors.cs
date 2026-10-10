using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace Dexicon.Core.Extraction;

/// <summary>
/// The result of pulling text out of a file. <paramref name="Units"/> carries the
/// format's natural provenance unit (page for PDF, slide for PPTX, chapter for EPUB)
/// so a citation can say "p. 34" instead of "chunk 87".
/// </summary>
public sealed record ExtractedText(string Text, IReadOnlyList<ExtractedUnit> Units, string? Title = null)
{
    public static readonly ExtractedText Empty = new(string.Empty, []);

    /// <summary>
    /// The text with every U+0000 removed and the units' offsets moved to match. SQLite ends a
    /// text value at the first one, so a cached document that held one was cut there while its
    /// row recorded the length of the whole. Some PDFs hold them by the thousand (maths-heavy
    /// ones in particular), and a plain-text upload or any other format can. They carry
    /// nothing, so this is applied once, to whatever an extractor returns, before the text is
    /// stored. A workspace file read as plain text or code is never stored, so the cut does
    /// not apply to it and its text is indexed as read.
    /// </summary>
    public ExtractedText WithoutNul()
    {
        if (Text.IndexOf('\0') < 0) return this;

        // One pass over the text, with the units in order of offset: each unit moves back by
        // the number of NULs that came before it.
        var moved = new ExtractedUnit[Units.Count];
        var removed = 0;
        var at = 0;
        foreach (var (unit, i) in Units.Select((u, i) => (u, i)).OrderBy(x => x.u.StartOffset))
        {
            var to = Math.Clamp(unit.StartOffset, at, Text.Length);
            removed += Text.AsSpan(at, to - at).Count('\0');
            at = to;
            moved[i] = unit with { StartOffset = Math.Max(0, unit.StartOffset - removed) };
        }

        return this with { Text = Text.Replace("\0", string.Empty), Units = moved };
    }
}

/// <param name="Number">1-based page / slide / chapter number.</param>
/// <param name="StartOffset">Character offset into <see cref="ExtractedText.Text"/>.</param>
public sealed record ExtractedUnit(int Number, int StartOffset, string? Label = null);

/// <summary>
/// Raised when a file is a recognised format that cannot be read: encrypted, DRM'd,
/// or structurally broken. Distinct from "produced no text", which is not an error.
/// </summary>
/// <remarks>
/// Not sealed, so <see cref="ExtractionTimeoutException"/> can be one of these. Every
/// extractor's catch-all is filtered on this type, and a timeout that was a sibling
/// rather than a subtype was re-wrapped by the DOCX and PPTX ones and reported as a
/// corrupt file. Making the relationship carry the exemption means a new extractor gets
/// it by copying the filter its neighbours already use.
/// </remarks>
public class ExtractionFailedException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// A file that cannot be read because of what is in it: truncated, corrupt, or malformed in a
/// way the parser rejects. Reading the same bytes again fails the same way, so the indexer
/// settles the file and leaves it alone until it changes or the extractor does.
///
/// A plain <see cref="ExtractionFailedException"/> is any other failure to read, and is tried
/// again on the next pass: the mount answered badly, the process was short of memory. The
/// extractor is where that is known, since it is the code that can tell a parser's verdict
/// from an I/O error, so it chooses which to throw (<see cref="ExtractionFailures.Of"/>).
/// </summary>
public sealed class UnreadableDocumentException(string message, Exception? inner = null)
    : ExtractionFailedException(message, inner)
{
    /// <summary>
    /// Set by <see cref="ExtractionFailures.Of"/> when the verdict was made from an exception type that
    /// signals a fault in the code that read the file (such as a null reference, an invalid operation, an
    /// argument) and not a malformed file. A new upload still records the verdict. A stored document
    /// that has good cached text keeps it instead, because a fault in a library says nothing certain
    /// about bytes that read well before.
    /// </summary>
    public bool Unexpected { get; init; }
}

internal static class ExtractionFailures
{
    /// <summary>
    /// The exception for a parser that threw about a file's bytes. Anything environmental is
    /// not a verdict on the file: an I/O error, a refused permission, memory or a timeout.
    /// Whatever else a parser throws while reading bytes it has been given is, however it is
    /// worded: an invalid colour space, a page it could not parse, a distance that overflows.
    ///
    /// Every exception in the tree of causes is looked at, not only the top: a parser catches what the
    /// stream throws and rethrows its own, and the extraction deadline throws from a read, so a
    /// timeout that arrived wrapped as "failed to parse the page" would otherwise be taken for a
    /// corrupt file, and a file that was only slow would stay failed. An <see cref="AggregateException"/>
    /// is walked through all its members. The rule, in order:
    /// <list type="number">
    /// <item>A timeout (<see cref="ExtractionTimeoutException"/> or <see cref="TimeoutException"/>)
    /// anywhere makes it environmental.</item>
    /// <item>An <see cref="UnreadableDocumentException"/> anywhere is an explicit verdict from code that
    /// read the file, and makes it a verdict.</item>
    /// <item>Otherwise any environmental member makes it environmental: an I/O error other than
    /// <see cref="EndOfStreamException"/>, a refused permission, memory, or another extraction failure.
    /// End of stream while reading a stored blob means the bytes end early, which is a verdict.</item>
    /// <item>Otherwise it is a verdict.</item>
    /// </list>
    ///
    /// The cost of being wrong in each direction is not the same, and this chooses the cheaper
    /// one for the cases it can see. A mount that returns short reads could make a good file
    /// read as corrupt for one pass; the file then stays settled as failed until it changes,
    /// or a full pass reads it again.
    /// </summary>
    public static ExtractionFailedException Of(string message, Exception cause)
    {
        bool timeout = false, verdict = false, environmental = false;
        foreach (var member in Members(cause))
        {
            switch (member)
            {
                case ExtractionTimeoutException or TimeoutException:
                    timeout = true;
                    break;
                case UnreadableDocumentException:
                    verdict = true;
                    break;
                case EndOfStreamException:
                    break;
                case OperationCanceledException:
                    // A cancellation says nothing about the bytes: a library gave up, or the caller did.
                    environmental = true;
                    break;
                case IOException or UnauthorizedAccessException or OutOfMemoryException or ExtractionFailedException:
                    environmental = true;
                    break;
            }
        }

        if (timeout || (environmental && !verdict))
            return new ExtractionFailedException(message, cause);

        return new UnreadableDocumentException(message, cause) { Unexpected = !verdict && IsFault(cause) };
    }

    /// <summary>The exception, then everything under it, an aggregate's members included.</summary>
    private static IEnumerable<Exception> Members(Exception root)
    {
        var pending = new Stack<Exception>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            yield return current;
            if (current is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            else if (current.InnerException is { } next)
                pending.Push(next);
        }
    }

    /// <summary>
    /// Whether what the code caught is a type that points at a fault in the code that read the file. A
    /// parser's own exception for a malformed file (an invalid-data, format or parser-specific type) is
    /// not one. A wrapper that only carries another exception (<see cref="TargetInvocationException"/>,
    /// <see cref="TypeInitializationException"/>, <see cref="AggregateException"/>) is taken apart, and
    /// any member that is a fault makes it one: keeping the text of a stored document is the cheaper
    /// mistake.
    /// </summary>
    private static bool IsFault(Exception cause)
    {
        var pending = new Stack<Exception>();
        pending.Push(cause);
        while (pending.Count > 0)
        {
            var caught = pending.Pop();
            switch (caught)
            {
                case AggregateException aggregate:
                    foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
                    break;
                case TargetInvocationException or TypeInitializationException when caught.InnerException is { } carried:
                    pending.Push(carried);
                    break;
                case NullReferenceException or InvalidOperationException or ArgumentException
                    or IndexOutOfRangeException or KeyNotFoundException or InvalidCastException
                    or ArithmeticException or NotImplementedException or NotSupportedException:
                    return true;
            }
        }

        return false;
    }
}

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
    /// Extraction is cached per blob, since a 437-page PDF costs ~1.8 s and its bytes
    /// never change. The code changes, however, and without a version the cache is
    /// permanent: a library ingested before a fix keeps the broken text invisibly, and no
    /// reindex repairs it, because reindexing re-chunks the cached text rather than
    /// re-reading the file.
    ///
    /// 2: HTML and EPUB keep block structure, one block per line, instead of
    ///    collapsing a whole chapter onto a single unsplittable line.
    /// 3: An EPUB whose manifest will not parse is salvaged from the archive instead of
    ///    failing. Books that cached as a failure now have text.
    /// 4: PDFs are read by layout rather than by content-stream order, so a paragraph is
    ///    one line as it already is for EPUB and HTML, and columns no longer interleave.
    /// 5: A page number alone in a PDF's top or bottom margin is furniture, not text, and
    ///    is dropped. About one extracted block in ten was one.
    /// 6: Whitespace inside an HTML or EPUB <c>&lt;pre&gt;</c> is kept, so a code listing arrives with
    ///    its lines and indentation instead of as one line.
    /// </summary>
    public const int Current = 6;
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
/// PdfPig (Apache-2.0). Text layer only: there is no OCR, and a scanned PDF is
/// reported as empty with a reason rather than producing nothing without explanation.
///
/// Text is read by layout, not by the order operators appear in the content stream.
/// Content order follows the file, which on a two-column page or a table means reading
/// across the columns rather than down them, and in ordinary prose means breaking at
/// every visual line ending. Measured over a 424-page book against the EPUB of the same
/// title, the EPUB being the control because its extractor emits one block per line:
///
///   content order   674,149 chars   14,954 lines   mean 43
///   layout          659,789 chars    5,559 lines   mean 118
///   EPUB            631,576 chars    5,727 lines   mean 109
///
/// The character counts barely move, so this is not about losing or gaining text. The
/// chunker splits on lines and treats a blank line as a PDF's boundary, and content order
/// handed it a paragraph spread over ten short lines. Layout costs about 1.2x the time:
/// 2,444 ms against 2,122 ms for that book, 5.8 ms a page.
/// </summary>
public sealed partial class PdfTextExtractor : ITextExtractor
{
    public bool CanHandle(string extension) => extension == ".pdf";

    public ExtractedText Extract(Stream content, string fileName)
    {
        // PdfPig reads a seekable stream directly, so hand it the file.
        //
        // This used to copy the whole PDF into a MemoryStream and then call ToArray() on
        // it: a growing buffer that doubles to as much as twice the file, plus a second
        // full-size array. A 128 MB book cost nearly 400 MB of raw bytes before a single
        // page was parsed, which is most of the reason the size cap was where it was.
        // PdfPig itself streams, so none of that was buying anything.
        Stream source = content;
        MemoryStream? buffered = null;

        if (!content.CanSeek)
        {
            // An upload arriving over the wire. Pre-sized where the length is known, so it
            // does not double its way up to twice the file.
            buffered = content.CanRead && content.Length > 0
                ? new MemoryStream((int)Math.Min(content.Length, int.MaxValue))
                : new MemoryStream();
            content.CopyTo(buffered);
            buffered.Position = 0;
            source = buffered;
        }

        RequireTrailer(source, fileName);

        PdfDocument document;
        try
        {
            document = PdfDocument.Open(source);
        }
        catch (Exception ex) when (ex is not ExtractionFailedException)
        {
            buffered?.Dispose();
            throw ExtractionFailures.Of(
                $"'{fileName}' could not be opened as a PDF. It may be encrypted or corrupt: {ex.Message}", ex);
        }

        try
        {
            using var _ = document;
            var sb = new StringBuilder();
            var units = new List<ExtractedUnit>();

            foreach (var page in document.GetPages())
            {
                units.Add(new ExtractedUnit(page.Number, sb.Length, $"Page {page.Number}"));
                foreach (var block in ReadInLayoutOrder(page))
                    sb.Append(block).Append('\n');
            }

            var title = document.Information?.Title;
            return new ExtractedText(sb.ToString(), units,
                string.IsNullOrWhiteSpace(title) ? null : title);
        }
        catch (Exception ex) when (ex is not (ExtractionFailedException or OperationCanceledException))
        {
            // A page that would not parse, found after the document opened: an invalid colour
            // space, a broken page tree, a layout that overflows. The same kind of failure as
            // one at open and reported the same way, where it escaped as a bare parser
            // exception and was logged as "Failed to index", every pass. Whether it is the file
            // or the mount that failed is decided by the cause. Output for a file that
            // extracts is unchanged, so ExtractorVersions.Current is not bumped.
            throw ExtractionFailures.Of(
                $"'{fileName}' could not be read as a PDF. It may be corrupt: {ex.Message}", ex);
        }
        finally
        {
            buffered?.Dispose();
        }
    }

    /// <summary>
    /// Reject a PDF whose trailer is missing before handing it to PdfPig.
    ///
    /// A conforming PDF ends with <c>startxref</c>, a byte offset and <c>%%EOF</c>. A
    /// download cut short has none of them, and PdfPig's response to a missing
    /// cross-reference table is to rebuild it by scanning the file backwards for object
    /// markers. That scan re-reads a 4 KB block to advance a single byte, so its cost is
    /// quadratic in file size and paid at the mount's latency: on a 68 MB truncated PDF
    /// over a 9p bind mount it ran for hours without finishing.
    ///
    /// The cost here is one read of the last 4 KB. Measured over 1,983 PDFs, 1,981 carry
    /// both <c>startxref</c> and <c>%%EOF</c> within that window and the two that carry
    /// neither are truncated downloads, one ending mid-dictionary at exactly 68 MiB. 4 KB
    /// rather than the 1 KB the specification implies, because appended signatures and
    /// incremental updates leave junk after the marker.
    ///
    /// This changes no output for a file that already extracted, so
    /// <see cref="ExtractorVersions.Current"/> is deliberately not bumped: the cache
    /// holds text for files that parsed, and these never produced any.
    /// </summary>
    private static void RequireTrailer(Stream source, string fileName)
    {
        // Not seekable cannot happen here (the caller buffers first), but Length on a
        // stream that does not support it throws, and a guard must not be the thing that
        // breaks the path it guards.
        if (!source.CanSeek) return;

        const int TailBytes = 4096;
        var length = source.Length;
        if (length == 0)
            throw new UnreadableDocumentException($"'{fileName}' is empty.");

        var take = (int)Math.Min(TailBytes, length);
        var tail = new byte[take];

        source.Position = length - take;
        source.ReadExactly(tail, 0, take);
        source.Position = 0;

        // Both keywords, not just %%EOF. Those five bytes can appear inside a stream, a
        // comment or a string, so on their own they are not evidence of a trailer, and a
        // file cut mid-stream could carry them and still reach the recovery scan. The
        // trailer is `startxref`, an offset, then `%%EOF`, so requiring the keyword that
        // names the cross-reference table tests for the thing actually needed.
        //
        // Free, measured: across 1,983 PDFs, 1,981 carry both within their last 4 KB, the
        // two that carry neither are the truncated downloads, and not one file has %%EOF
        // without startxref. Requiring both rejects nothing that the looser check accepted.
        var span = tail.AsSpan();
        if (span.IndexOf("%%EOF"u8) >= 0 && span.IndexOf("startxref"u8) >= 0) return;

        throw new UnreadableDocumentException(
            $"'{fileName}' has no PDF trailer (startxref and %%EOF) in its last {take:N0} "
            + $"bytes, so it is truncated rather than merely unusual. Its {length:N0} bytes "
            + "were not read: a PDF with no cross-reference table can only be recovered by "
            + "scanning it backwards a byte at a time, which costs hours on a file this size.");
    }

    /// <summary>
    /// One line per text block, in reading order.
    ///
    /// Docstrum groups words into blocks by the spacing between them, which is what
    /// separates a table cell or a column from its neighbour; the reading-order detector
    /// then puts those blocks in the order a person would read them. A block's own line
    /// breaks are collapsed, because they are where the text met the right margin rather
    /// than where a thought ended.
    /// </summary>
    private static IEnumerable<string> ReadInLayoutOrder(Page page)
    {
        // A page with no text layer has no letters, and the segmenter is not defined on
        // an empty set. Yielding nothing is what surfaces as `status: empty`.
        if (page.Letters.Count == 0) yield break;

        var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters);
        var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);

        foreach (var block in UnsupervisedReadingOrderDetector.Instance.Get(blocks))
        {
            var line = block.Text.ReplaceLineEndings(" ").Trim();
            if (line.Length == 0) continue;
            if (IsPageNumber(line, block.BoundingBox.Centroid.Y, page.Height)) continue;

            yield return line;
        }
    }

    /// <summary>
    /// A page number alone in a margin, which is furniture rather than text.
    ///
    /// Measured over this corpus: 99 bare numbers per 1,000 extracted blocks in PDFs
    /// against 13 in the EPUBs of the same books. Each is a line of its own, so a passage
    /// spanning a page break extracts as "…end of the section. 247 Chapter 9 begins…",
    /// which appears in no edition of the book.
    ///
    /// Position decides, not the digits: a bare number in the body of a page is a table
    /// cell, a numbered list item or a line of code, so only the top and bottom margins
    /// are considered.
    ///
    /// Removing them put the formats level. Over paired ranking comparisons, where one
    /// query matches both the PDF and the EPUB of a title, EPUB ranked higher in 29 of 42
    /// pairs beforehand (exact binomial p = 0.0195) and in 22 of 43 afterwards (p = 1.0).
    /// </summary>
    private static bool IsPageNumber(string line, double centreY, double pageHeight)
    {
        if (line.Length > 8 || pageHeight <= 0) return false;

        // Arabic for the body, lowercase roman for front matter. Anchored, so "Chapter 4"
        // and "4 ways to fail" both stay.
        if (!PageNumberPattern().IsMatch(line)) return false;

        // PdfPig's origin is the bottom-left, so the bottom margin is a low Y and the top
        // margin a high one.
        var margin = pageHeight * 0.08;
        return centreY <= margin || centreY >= pageHeight - margin;
    }

    [GeneratedRegex(@"^(\d{1,4}|[ivxlcdm]{1,7})$", RegexOptions.CultureInvariant)]
    private static partial Regex PageNumberPattern();
}

public sealed class DocxTextExtractor : ITextExtractor
{
    public bool CanHandle(string extension) => extension == ".docx";

    public ExtractedText Extract(Stream content, string fileName)
    {
        try
        {
            // The package is read through the extraction clock already: content is the DeadlineStream.
            XmlNesting.RequireShallowParts(content, fileName);
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
            throw ExtractionFailures.Of($"'{fileName}' is not a readable .docx: {ex.Message}", ex);
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
            XmlNesting.RequireShallowParts(content, fileName);
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
            throw ExtractionFailures.Of($"'{fileName}' is not a readable .pptx: {ex.Message}", ex);
        }
    }
}

/// <param name="maxTags">The most tags a chapter may hold; see <see cref="HtmlText.MaxTags"/>.</param>
public sealed class EpubTextExtractor(int maxTags = HtmlText.MaxTags) : ITextExtractor
{
    public bool CanHandle(string extension) => extension == ".epub";

    public ExtractedText Extract(Stream content, string fileName)
    {
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);

        // Each attempt gets its own stream over the same bytes. VersOne disposes the
        // stream it is given on some failure paths, so reusing one means the fallback
        // reads a closed stream and reports ObjectDisposedException instead of the book.
        var bytes = buffer.ToArray();
        var deadline = content as DeadlineStream;
        XmlNesting.RequireShallowParts(new MemoryStream(bytes), fileName, deadline);

        try
        {
            using var forManifest = new MemoryStream(bytes);
            return ReadWithManifest(forManifest, deadline, maxTags);
        }
        catch (Exception ex) when (ex is not ExtractionFailedException)
        {
            // A real shelf is full of books that no reader complains about and a strict
            // parser refuses: duplicate manifest IDs, a missing TOC, a spine that names a
            // file that is not there. Falling back to the archive reads those, in a worse
            // order and without chapter titles, which is enormously better than not at all.
            using var forArchive = new MemoryStream(bytes);
            return ReadFromArchive(forArchive, fileName, ex, deadline, maxTags);
        }
    }

    /// <summary>The good path: the manifest gives real reading order and a title.</summary>
    private static ExtractedText ReadWithManifest(MemoryStream buffer, DeadlineStream? deadline, int maxTags)
    {
        var book = VersOne.Epub.EpubReader.ReadBook(buffer);
        var sb = new StringBuilder();
        var units = new List<ExtractedUnit>();
        var parser = new HtmlParser();
        var number = 1;

        foreach (var file in book.ReadingOrder)
        {
            units.Add(new ExtractedUnit(number, sb.Length, $"Chapter {number}"));
            HtmlText.RequireFewTags(file.Content, $"Chapter {number}", maxTags);
            using var doc = HtmlText.Parse(parser, file.Content, deadline);
            HtmlText.AppendBlocks(doc.Body, sb);
            number++;
        }

        return new ExtractedText(sb.ToString(), units, book.Title);
    }

    /// <summary>
    /// The salvage path. An EPUB is a zip of XHTML, so the documents can be read without
    /// the manifest that failed to parse. Entry order stands in for reading order: it is
    /// usually the authoring order and is nearly always alphabetical by chapter.
    /// </summary>
    private static ExtractedText ReadFromArchive(
        MemoryStream buffer, string fileName, Exception cause, DeadlineStream? deadline, int maxTags)
    {
        using var zip = OpenArchive(buffer, fileName, cause);

        // Encryption is declared, not guessed. This is the one case where naming DRM is
        // correct, and the reason the old message applied it to every malformed book.
        if (zip.Entries.Any(e => e.FullName.Equals("META-INF/encryption.xml", StringComparison.OrdinalIgnoreCase)))
            throw new UnreadableDocumentException(
                $"'{fileName}' is encrypted. DRM-protected books cannot be read.", cause);

        var documents = zip.Entries
            .Where(e => e.Name.Length > 0)
            .Where(e => Path.GetExtension(e.Name).ToLowerInvariant() is ".xhtml" or ".html" or ".htm")
            .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        var units = new List<ExtractedUnit>();
        var parser = new HtmlParser();
        var number = 1;

        try
        {
            foreach (var entry in documents)
            {
                using var bytes = new MemoryStream();
                using (var stream = entry.Open()) stream.CopyTo(bytes);
                HtmlText.RequireFewTags(bytes.GetBuffer().AsSpan(0, (int)bytes.Length), entry.FullName, maxTags);
                bytes.Position = 0;
                using var doc = HtmlText.Parse(parser, bytes, deadline);
                var before = sb.Length;
                HtmlText.AppendBlocks(doc.Body, sb);

                // A cover page or a stylesheet wrapper contributes nothing; recording a unit
                // for it would put chapter markers where there is no text.
                if (sb.Length > before)
                {
                    units.Add(new ExtractedUnit(number, before, Path.GetFileNameWithoutExtension(entry.Name)));
                    number++;
                }
            }
        }
        catch (Exception ex) when (ex is not ExtractionFailedException)
        {
            // An archive whose directory reads and whose entry data does not (a damaged deflate block,
            // a failed checksum) throws InvalidDataException from the entry stream, outside the
            // handler that wraps the other formats.
            throw ExtractionFailures.Of($"'{fileName}' is not a readable .epub: {ex.Message}", ex);
        }

        if (sb.Length == 0)
            throw new UnreadableDocumentException(
                $"'{fileName}' could not be read: its manifest is unreadable ({cause.Message}) " +
                "and the archive holds no readable XHTML.", cause);

        return new ExtractedText(sb.ToString(), units);
    }

    private static ZipArchive OpenArchive(MemoryStream buffer, string fileName, Exception cause)
    {
        try
        {
            return new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            // Not a zip at all, so not an EPUB. Report the original parse failure, which
            // is the more informative of the two.
            throw new UnreadableDocumentException(
                $"'{fileName}' is not a readable .epub: {cause.Message}", ex);
        }
    }
}

/// <param name="maxTags">The most tags a document may hold; see <see cref="HtmlText.MaxTags"/>.</param>
public sealed class HtmlTextExtractor(int maxTags = HtmlText.MaxTags) : ITextExtractor
{
    public bool CanHandle(string extension) => extension is ".html" or ".htm";

    public ExtractedText Extract(Stream content, string fileName)
    {
        // Every .html and .htm file reaches this extractor, from an upload or from a
        // workspace tree (ExtractorRegistry.For). The text is chunked as prose; the
        // language-aware boundary patterns in LanguageMap apply to Razor, Vue and Svelte.
        try
        {
            using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var parser = new HtmlParser();
            var markup = reader.ReadToEnd();
            HtmlText.RequireFewTags(markup, fileName, maxTags);
            using var doc = HtmlText.Parse(parser, markup, content as DeadlineStream);

            var sb = new StringBuilder();
            HtmlText.AppendBlocks(doc.Body, sb);
            return new ExtractedText(sb.ToString().Trim(), [], doc.Title);
        }
        catch (Exception ex) when (ex is not ExtractionFailedException)
        {
            // An I/O error reading the file reaches the caller as a failure that is not a verdict on it.
            throw ExtractionFailures.Of($"'{fileName}' is not a readable HTML document: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// Turns an HTML body into text that keeps its BLOCK STRUCTURE, one block per line.
///
/// AngleSharp's <c>TextContent</c> is the obvious thing to reach for and it is wrong
/// here: it concatenates every descendant text node with no separators, so a chapter
/// comes back as a single line tens of thousands of characters long. The chunker splits
/// on line boundaries, so it could not split at all: a 578,000-character EPUB produced
/// 18 chunks averaging 32,000 characters, each of which the embedding model truncated at
/// its context limit without reporting it. The book reported itself as indexed while most of it
/// was nowhere in the index.
///
/// Newlines are not cosmetic: they are what makes the text chunkable, and what makes a
/// line number in a search hit mean anything.
/// </summary>
internal static class HtmlText
{
    /// <summary>
    /// The most tags a document or a chapter may hold. The parser is bounded in time by the extraction clock
    /// and in memory by this: a parse costs 290 to 560 bytes of working set per tag (measured on flat
    /// paragraphs, spans, list items and anchors with four attributes: 361 to 769 MB at the limit, with the
    /// markup itself), 100 MB of paragraphs reached 10 GB before the clock ended it, and a
    /// real 12 MB single-page specification holds about 300,000 tags. Counted as the
    /// less-than signs, which is exact for text and an upper bound for tags.
    /// </summary>
    public const int MaxTags = 1_000_000;

    /// <summary>Refuses text with more than <paramref name="maxTags"/> tags.</summary>
    /// <param name="what">What the message names: a file or a chapter.</param>
    /// <exception cref="UnreadableDocumentException">There are more tags than allowed.</exception>
    public static void RequireFewTags(string markup, string what, int maxTags)
    {
        if (markup.AsSpan().Count('<') > maxTags) throw TooManyTags(what, maxTags);
    }

    /// <summary>
    /// The same for bytes, whatever their encoding: the byte 0x3C is the less-than sign in UTF-8 and the
    /// low byte of it in UTF-16, and a byte that is part of another character only makes the count higher.
    /// </summary>
    public static void RequireFewTags(ReadOnlySpan<byte> markup, string what, int maxTags)
    {
        if (markup.Count((byte)0x3C) > maxTags) throw TooManyTags(what, maxTags);
    }

    private static UnreadableDocumentException TooManyTags(string what, int maxTags) =>
        new($"{what} contains more than {maxTags:N0} tags, which is more than can be read.");

    /// <summary>
    /// Parses under the extraction clock. AngleSharp builds the tree in time that grows with the square
    /// of the nesting (100,000 nested divs took 319 s here), and no count of tags taken beforehand
    /// matches what the parser makes of them: it closes elements an end tag does not name, ignores end
    /// tags with nothing to close, and treats attribute values, raw text and SVG by rules of its own. So
    /// the bound is the time. The parser stops within about 50 ms of the token being cancelled, which
    /// was measured on 100,000 and 1,000,000 levels of nesting and on a 21 MB flat document.
    /// </summary>
    /// <param name="deadline">
    /// The clock of the stream the extractor was given. Without one, as when the budget is 0, the parse is
    /// not bounded.
    /// </param>
    /// <exception cref="ExtractionTimeoutException">The budget passed before the tree was built.</exception>
    public static IHtmlDocument Parse(HtmlParser parser, string markup, DeadlineStream? deadline) =>
        Parsed(token => parser.ParseDocumentAsync(markup, token), deadline);

    /// <inheritdoc cref="Parse(HtmlParser, string, DeadlineStream?)"/>
    public static IHtmlDocument Parse(HtmlParser parser, Stream markup, DeadlineStream? deadline) =>
        Parsed(token => parser.ParseDocumentAsync(markup, token), deadline);

    private static readonly TimeSpan MaxCancelDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private static IHtmlDocument Parsed(Func<CancellationToken, Task<IHtmlDocument>> parse, DeadlineStream? deadline)
    {
        using var cancel = new CancellationTokenSource();
        // CancelAfter takes no more than about 49 days. A budget longer than that is as good as none.
        if (deadline is not null)
            cancel.CancelAfter(deadline.Remaining > MaxCancelDelay ? Timeout.InfiniteTimeSpan : deadline.Remaining);

        try
        {
            return parse(cancel.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException ex) when (deadline is not null && cancel.IsCancellationRequested)
        {
            throw deadline.TimedOut(ex);
        }
    }

    /// <summary>Elements whose text is markup machinery, not content.</summary>
    private static readonly HashSet<string> Skipped =
        new(StringComparer.Ordinal) { "script", "style", "noscript", "template", "head" };

    /// <summary>
    /// Block-level elements, as HTML renders them: each one starts on a new line.
    /// Inline elements (em, a, span, code…) are intentionally excluded: breaking a line
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
        Walk(root, sb, preformatted: false);
        EndLine(sb);
    }

    /// <param name="preformatted">
    /// Inside a <c>pre</c>, where whitespace is content rather than markup layout.
    ///
    /// Collapsing it everywhere turned a code listing into one line: `if (a) {`, four
    /// spaces, `b();` and `}` came out as `if (a) { b(); }`. The listing stays findable
    /// that way, which is why this went unnoticed, but it is unreadable in a search result
    /// and the chunker splits on lines, so a long listing was one line it could not split.
    /// </param>
    private static void Walk(INode root, StringBuilder sb, bool preformatted)
    {
        // An explicit stack: a document nested a few thousand deep overflowed the call stack, which
        // kills the process and cannot be caught. Each frame is a node, the index of the child to visit
        // next, whether the node is inside a pre, and whether its end closes a block.
        var stack = new Stack<(INode Node, int Next, bool Pre, bool Block)>();
        stack.Push((root, 0, preformatted, false));

        while (stack.Count > 0)
        {
            var (node, next, pre, block) = stack.Pop();
            var children = node.ChildNodes;
            if (next >= children.Length)
            {
                if (block) EndLine(sb);
                continue;
            }

            stack.Push((node, next + 1, pre, block));

            switch (children[next])
            {
                case IText text:
                    if (pre) AppendVerbatim(text.Data, sb);
                    else AppendCollapsed(text.Data, sb);
                    break;

                case IElement el when Skipped.Contains(el.LocalName):
                    break;

                // <br> is an explicit line break even though it is an inline element.
                case IElement { LocalName: "br" }:
                    EndLine(sb);
                    break;

                case IElement el:
                    var isBlock = Blocks.Contains(el.LocalName);
                    if (isBlock) EndLine(sb);

                    // Inherited, so the <code> inside a <pre> is preformatted too, which
                    // is how a listing is marked up nearly everywhere.
                    stack.Push((el, 0, pre || el.LocalName == "pre", isBlock));
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

    /// <summary>
    /// Text exactly as written, for a <c>pre</c>: indentation and line breaks included.
    ///
    /// No line-ending normalisation, because the HTML parser has already done it. A
    /// CRLF in the source reaches here as a single LF, which is what the spec requires
    /// of a parser and what <c>CarriageReturnsDoNotDoubleTheLines</c> holds it to. The
    /// first version of this handled CR itself, and that code was unreachable.
    /// </summary>
    private static void AppendVerbatim(string text, StringBuilder sb) => sb.Append(text);

    /// <summary>Ends the current line, without leaving a run of blank ones.</summary>
    private static void EndLine(StringBuilder sb)
    {
        while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
    }
}
