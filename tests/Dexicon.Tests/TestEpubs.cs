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
    public static byte[] WithAChapter(string chapterBody)
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
                + "<dc:language>en</dc:language></metadata><manifest><item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/>"
                + "<item id=\"c1\" href=\"c1.xhtml\" media-type=\"application/xhtml+xml\"/></manifest><spine toc=\"ncx\"><itemref idref=\"c1\"/></spine></package>");
            Add("toc.ncx",
                "<?xml version=\"1.0\"?><ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\" version=\"2005-1\"><head/><docTitle><text>t</text></docTitle>"
                + "<navMap><navPoint id=\"n1\" playOrder=\"1\"><navLabel><text>x</text></navLabel><content src=\"c1.xhtml\"/></navPoint></navMap></ncx>");
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
}
