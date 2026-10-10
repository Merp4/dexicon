using System.IO.Compression;

namespace Dexicon.Tests;

/// <summary>EPUB bytes built for a test.</summary>
internal static class TestEpubs
{
    private const string EntryName = "OEBPS/ch1.xhtml";

    /// <summary>
    /// A zip whose directory reads and whose one XHTML entry does not: the first bytes of the entry's
    /// deflate data are replaced, so opening the archive works and reading the entry throws
    /// <see cref="InvalidDataException"/>. It has no EPUB manifest, so the extractor takes the archive path.
    /// </summary>
    public static byte[] WithADamagedEntry()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(EntryName, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open());
            writer.Write("<html><body><p>" + string.Join(' ', Enumerable.Repeat("chapter text", 200)) + "</p></body></html>");
        }

        var bytes = buffer.ToArray();

        // The entry's data follows its 30 byte local header, its name and its extra field.
        var start = 30 + BitConverter.ToUInt16(bytes, 26) + BitConverter.ToUInt16(bytes, 28);
        for (var i = 2; i < 12; i++) bytes[start + i] = 0xFF;
        return bytes;
    }

    /// <summary>
    /// A well-formed EPUB 2 with a manifest, a spine and one chapter holding <paramref name="chapterBody"/>,
    /// so the extractor reads it by its manifest and not by the archive.
    /// </summary>
    /// <param name="navigationDepth">How many navigation points nest inside one another in the NCX.</param>
    /// <param name="ncxName">The entry the NCX is stored under; the manifest names it with the NCX media type.</param>
    public static byte[] WithAChapter(string chapterBody, int navigationDepth = 1, string ncxName = "toc.ncx")
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }

            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml",
                "<?xml version=\"1.0\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">"
                + "<rootfiles><rootfile full-path=\"content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
            Add("content.opf",
                "<?xml version=\"1.0\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"2.0\" unique-identifier=\"id\">"
                + "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>t</dc:title><dc:identifier id=\"id\">x</dc:identifier>"
                + "<dc:language>en</dc:language></metadata><manifest><item id=\"ncx\" href=\"" + ncxName + "\" media-type=\"application/x-dtbncx+xml\"/>"
                + "<item id=\"c1\" href=\"c1.xhtml\" media-type=\"application/xhtml+xml\"/></manifest><spine toc=\"ncx\"><itemref idref=\"c1\"/></spine></package>");
            Add(ncxName,
                "<?xml version=\"1.0\"?><ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\" version=\"2005-1\"><head/><docTitle><text>t</text></docTitle>"
                + "<navMap>"
                + string.Concat(Enumerable.Range(1, navigationDepth).Select(i =>
                    $"<navPoint id=\"n{i}\" playOrder=\"{i}\"><navLabel><text>x</text></navLabel><content src=\"c1.xhtml\"/>"))
                + string.Concat(Enumerable.Repeat("</navPoint>", navigationDepth))
                + "</navMap></ncx>");
            Add("c1.xhtml", $"<html xmlns=\"http://www.w3.org/1999/xhtml\"><body>{chapterBody}</body></html>");
        }

        return buffer.ToArray();
    }

    /// <summary>A zip with no manifest and one XHTML entry holding <paramref name="chapterBody"/>, read by the archive path.</summary>
    public static byte[] WithAnEntry(string chapterBody)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("OEBPS/ch1.xhtml").Open());
            writer.Write($"<html><body>{chapterBody}</body></html>");
        }

        return buffer.ToArray();
    }

    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>
    /// A DOCX whose body holds one paragraph "hello" inside <paramref name="nesting"/> pairs of
    /// <c>sdt</c> and <c>sdtContent</c> elements, which nest to any depth.
    /// </summary>
    /// <param name="partName">What the main part is stored under. The relationship names it, so it can be anything.</param>
    /// <param name="encoding">How the part is encoded, with its byte order mark if it has one.</param>
    public static byte[] DocxNestedBy(int nesting, string partName = "word/document.xml", System.Text.Encoding? encoding = null) =>
        DocxWithDocumentPart(
            partName,
            Encode(
                $"<?xml version=\"1.0\"?><w:document xmlns:w=\"{WordNamespace}\"><w:body>"
                + string.Concat(Enumerable.Repeat("<w:sdt><w:sdtContent>", nesting))
                + "<w:p><w:r><w:t>hello</w:t></w:r></w:p>"
                + string.Concat(Enumerable.Repeat("</w:sdtContent></w:sdt>", nesting))
                + "</w:body></w:document>",
                encoding));

    /// <summary>A DOCX package whose <c>word/document.xml</c> is <paramref name="documentXml"/>.</summary>
    public static byte[] DocxWithDocumentXml(string documentXml) =>
        DocxWithDocumentPart("word/document.xml", Encode(documentXml, null));

    /// <summary>A DOCX package whose main document part is stored as <paramref name="partName"/>.</summary>
    public static byte[] DocxWithDocumentPart(string partName, byte[] content) =>
        PackageOfBytes(
            ("[Content_Types].xml", Encode(
                "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
                + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + $"<Override PartName=\"/{partName}\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>", null)),
            ("_rels/.rels", Encode(
                "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + $"<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"{partName}\"/></Relationships>", null)),
            (partName, content));

    /// <summary>
    /// A PPTX with one slide holding the text "hello" in a shape inside <paramref name="nesting"/> nested
    /// group shapes.
    /// </summary>
    /// <param name="slidePart">What the slide is stored under, below <c>ppt/</c>. The relationship names it.</param>
    public static byte[] PptxNestedBy(int nesting, string slidePart = "ppt/slides/slide1.xml")
    {
        var shapes = string.Concat(Enumerable.Repeat("<p:grpSp>", nesting))
                     + "<p:sp><p:txBody><a:p><a:r><a:t>hello</a:t></a:r></a:p></p:txBody></p:sp>"
                     + string.Concat(Enumerable.Repeat("</p:grpSp>", nesting));

        return Package(
            ("[Content_Types].xml",
                "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
                + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + "<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/>"
                + $"<Override PartName=\"/{slidePart}\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/></Types>"),
            ("_rels/.rels",
                "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"ppt/presentation.xml\"/></Relationships>"),
            ("ppt/presentation.xml",
                "<?xml version=\"1.0\"?><p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" "
                + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId1\"/></p:sldIdLst></p:presentation>"),
            ("ppt/_rels/presentation.xml.rels",
                "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + $"<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" Target=\"{slidePart["ppt/".Length..]}\"/></Relationships>"),
            (slidePart,
                "<?xml version=\"1.0\"?><p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" "
                + "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><p:cSld><p:spTree>"
                + shapes + "</p:spTree></p:cSld></p:sld>"));
    }

    /// <summary>
    /// The package with one more entry, <c>junk/unreferenced.xml</c>, that nothing names and that inflates
    /// to <paramref name="inflatedBytes"/> bytes of <c>&lt;r&gt;&lt;a&gt;x&lt;/a&gt;…&lt;/r&gt;</c> from a few
    /// kilobytes per hundred megabytes.
    /// </summary>
    public static byte[] WithAnInflatingEntry(byte[] package, long inflatedBytes)
    {
        using var buffer = new MemoryStream();
        buffer.Write(package);
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Update, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("junk/unreferenced.xml", CompressionLevel.Fastest).Open();
            var chunk = System.Text.Encoding.ASCII.GetBytes("<r>" + string.Concat(Enumerable.Repeat("<a>x</a>", 2_000)) + "</r>");
            var open = System.Text.Encoding.ASCII.GetBytes("<root>");
            entry.Write(open);
            for (long written = open.Length; written < inflatedBytes; written += chunk.Length) entry.Write(chunk);
            entry.Write(System.Text.Encoding.ASCII.GetBytes("</root>"));
        }

        return buffer.ToArray();
    }

    /// <summary>The text as bytes in <paramref name="encoding"/> (UTF-8 without a mark when null), with its byte order mark.</summary>
    public static byte[] Encode(string text, System.Text.Encoding? encoding)
    {
        encoding ??= new System.Text.UTF8Encoding(false);
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
    }

    /// <summary>
    /// An EPUB 3 whose navigation document holds <paramref name="depth"/> lists nested in one another, read
    /// by its manifest.
    /// </summary>
    public static byte[] WithANavigationDocumentNestedBy(int depth) =>
        Package(
            ("mimetype", "application/epub+zip"),
            ("META-INF/container.xml",
                "<?xml version=\"1.0\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">"
                + "<rootfiles><rootfile full-path=\"content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>"),
            ("content.opf",
                "<?xml version=\"1.0\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"id\">"
                + "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>t</dc:title><dc:identifier id=\"id\">x</dc:identifier>"
                + "<dc:language>en</dc:language></metadata><manifest>"
                + "<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>"
                + "<item id=\"c1\" href=\"c1.xhtml\" media-type=\"application/xhtml+xml\"/></manifest><spine><itemref idref=\"c1\"/></spine></package>"),
            ("nav.xhtml",
                "<?xml version=\"1.0\"?><html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><body>"
                + "<nav epub:type=\"toc\"><ol>"
                + string.Concat(Enumerable.Repeat("<li><a href=\"c1.xhtml\">x</a><ol>", depth))
                + string.Concat(Enumerable.Repeat("</ol></li>", depth))
                + "</ol></nav></body></html>"),
            ("c1.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>hello</p></body></html>"));

    /// <summary>A zip of the named text entries.</summary>
    public static byte[] Package(params (string Name, string Content)[] entries) =>
        PackageOfBytes([.. entries.Select(e => (e.Name, Encode(e.Content, null)))]);

    /// <summary>A zip of the named entries.</summary>
    public static byte[] PackageOfBytes(params (string Name, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(content);
            }
        }

        return buffer.ToArray();
    }
}
