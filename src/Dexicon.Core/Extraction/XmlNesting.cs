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
    /// The deepest element nesting read. Real documents are a few dozen levels deep, and 1,000 nested
    /// <c>sdt</c> elements (2,000 levels) still loaded in the probe, so this has a margin of several times.
    /// </summary>
    public const int MaxDepth = 512;

    /// <summary>
    /// The most XML that is read, summed over the parts of one package, once inflated: 256 MiB, which the
    /// reader takes about four seconds to scan at the 60 MB/s it was measured at. The XML of the 61 real
    /// EPUB, DOCX and PPTX files the check was run on scanned in 2 to 125 ms each. A package with more is
    /// not a document: a 5.7 MB one was measured inflating to 1 GiB in a single part.
    /// </summary>
    public const long TotalBudgetBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Refuses a package any of whose parts nests XML elements deeper than <see cref="MaxDepth"/>, or whose
    /// XML inflates to more than <paramref name="budgetBytes"/>, or whose reading outlasts
    /// <paramref name="deadline"/>.
    /// </summary>
    /// <remarks>
    /// Every entry is tried as XML, whatever its name: the libraries load a part by the relationship or
    /// manifest that names it, so a part can be called anything. An entry that is not XML fails at the
    /// reader's first read, having cost a few kilobytes. The readers that follow open the package with a
    /// zip reader, which seeks to the offsets it needs, so the position is not restored. A stream that is
    /// not a zip archive, an entry that cannot be inflated and a part that is not well formed XML are left
    /// to the reader the extractor calls next, which reports them in its own words.
    /// </remarks>
    /// <param name="package">The archive. A stream that cannot seek is not read.</param>
    /// <param name="fileName">What the messages call the file.</param>
    /// <param name="deadline">The extraction clock, checked at every read of an entry.</param>
    /// <param name="budgetBytes">Total inflated bytes allowed across the entries.</param>
    /// <exception cref="UnreadableDocumentException">A part nests too deep, or the XML inflates to too much.</exception>
    /// <exception cref="ExtractionTimeoutException">The deadline passed.</exception>
    public static void RequireShallowParts(
        Stream package, string fileName, DeadlineStream? deadline = null, long budgetBytes = TotalBudgetBytes)
    {
        // A zip reader over a stream that cannot seek reads all of it into memory, which would leave
        // nothing for the reader that follows.
        if (!package.CanSeek) return;

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            return;
        }

        var meter = new Meter(budgetBytes, deadline, fileName);
        using (archive)
        {
            foreach (var entry in archive.Entries)
            {
                // A directory entry has no data.
                if (entry.Name.Length == 0) continue;

                try
                {
                    using var part = new MeteredStream(entry.Open(), meter);
                    RequireShallow(part, $"{fileName} ({entry.FullName})");
                }
                catch (InvalidDataException)
                {
                    // Data that does not inflate: the reader of the package says so.
                }
            }
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

    /// <summary>What is left of the budget and the clock, shared by the entries of one package.</summary>
    private sealed class Meter(long budgetBytes, DeadlineStream? deadline, string fileName)
    {
        private long _used;

        public void Charge(int bytes)
        {
            deadline?.ThrowIfExpired();

            _used += bytes;
            if (_used > budgetBytes)
                throw new UnreadableDocumentException(
                    $"{fileName} holds XML that inflates to more than {budgetBytes / (1024 * 1024):N0} MiB, which is too much to read.");
        }
    }

    /// <summary>Passes reads through and charges what they return to the <see cref="Meter"/>.</summary>
    private sealed class MeteredStream(Stream inner, Meter meter) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Charged(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Charged(inner.Read(buffer));

        private int Charged(int read)
        {
            meter.Charge(read);
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
