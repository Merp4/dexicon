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

        // The entry's data follows its 30 byte local header and its name (there is no extra field).
        var start = 30 + EntryName.Length;
        for (var i = 0; i < 8; i++) bytes[start + i] = 0xFF;
        return bytes;
    }
}
