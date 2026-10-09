using System.IO.Compression;
using System.Xml;

namespace Dexicon.Core.Extraction;

/// <summary>
/// How deep the XML parts of a package may nest. The readers of DOCX, PPTX and EPUB load a part into a
/// tree by recursion, in the Open XML SDK (<c>OpenXmlElement.Load</c>) and in VersOne.Epub
/// (<c>Epub2NcxReader.ReadNavigationPoint</c>), and the stack overflowed at about 5,000 nested
/// <c>sdt</c> elements in a DOCX and 5,000 nested <c>navPoint</c> elements in an NCX. A stack overflow
/// ends the process and cannot be caught, so the depth is counted with a streaming <see cref="XmlReader"/>,
/// which keeps no call stack, before either library sees the part.
/// </summary>
internal static class XmlNesting
{
    /// <summary>
    /// The deepest element nesting read, the same limit as for HTML. Real documents are a few dozen
    /// levels deep. 1,000 nested <c>sdt</c> elements (2,000 levels) still loaded in the probe, so this
    /// has a margin of several times.
    /// </summary>
    public const int MaxDepth = HtmlText.MaxNesting;

    /// <summary>Parts of a package that the readers parse as XML.</summary>
    private static readonly HashSet<string> XmlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xml", ".rels", ".opf", ".ncx", ".xhtml",
    };

    /// <summary>
    /// Refuses a package any of whose XML parts nests deeper than <see cref="MaxDepth"/>. Every XML part
    /// is read, not only the ones the extractor is known to load, so a part the library reaches by a
    /// relationship cannot be the one that was not checked. The stream is left where it was.
    /// </summary>
    /// <remarks>
    /// A stream that is not a zip archive, an entry that cannot be inflated and a part that is not well
    /// formed XML are left to the reader the extractor calls next, which reports them in its own words.
    /// </remarks>
    /// <exception cref="UnreadableDocumentException">A part nests deeper than the limit.</exception>
    public static void RequireShallowParts(Stream package, string fileName)
    {
        if (!package.CanSeek) return;

        var start = package.Position;
        try
        {
            ZipArchive archive;
            try
            {
                archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
            }
            catch (InvalidDataException)
            {
                return;
            }

            using (archive)
            {
                foreach (var entry in archive.Entries)
                {
                    if (!XmlExtensions.Contains(Path.GetExtension(entry.FullName))) continue;

                    try
                    {
                        using var part = entry.Open();
                        RequireShallow(part, $"{fileName} ({entry.FullName})");
                    }
                    catch (InvalidDataException)
                    {
                        // Data that does not inflate: the reader of the package says so.
                    }
                }
            }
        }
        finally
        {
            package.Position = start;
        }
    }

    /// <summary>Reads the part to its end, or to the first element deeper than the limit.</summary>
    /// <param name="what">The part the message names.</param>
    public static void RequireShallow(Stream part, string what)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        };

        try
        {
            using var reader = XmlReader.Create(part, settings);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Depth >= MaxDepth)
                    throw new UnreadableDocumentException(
                        $"{what} nests XML elements more than {MaxDepth} deep, which is too deep to read.");
            }
        }
        catch (XmlException)
        {
            // Not well formed from here on. The reader that loads the part reports it.
        }
    }
}
