using System.IO.Compression;
using System.Text;
using Dexicon.Core.Extraction;

namespace Dexicon.Tests;

/// <summary>
/// EPUBs that a strict parser refuses and every e-reader opens.
///
/// Indexing a real shelf of 19 books from one publisher produced one hard failure on the first
/// pass: "Incorrect EPUB manifest: item with ID = 'img_cover' is not unique". The book
/// was fine. The manifest listed a cover image twice, which the spec forbids and
/// publishers ship anyway, and the error it raised was reported to the user as
/// "DRM-protected books cannot be read", which was not true and sent them looking for a
/// problem they did not have.
/// </summary>
public class EpubSalvageTests
{
    /// <param name="duplicateId">Reproduces the manifest defect found on the real shelf.</param>
    private static MemoryStream Epub(
        bool duplicateId = false, bool encrypted = false, bool nav = true,
        params (string Path, string Html)[] documents)
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Write(string path, string content)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), Encoding.UTF8);
                writer.Write(content);
            }

            Write("mimetype", "application/epub+zip");
            Write("META-INF/container.xml",
                """
                <?xml version="1.0"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
                </container>
                """);

            // EPUB 3 requires a navigation document, and VersOne enforces it. Without one
            // even a well-formed book falls to the salvage path, which would make the
            // "a readable manifest is still preferred" test pass for the wrong reason.
            if (nav) Write("OEBPS/nav.xhtml",
                """
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
                  <body><nav epub:type="toc"><ol><li><a href="ch1.xhtml">One</a></li></ol></nav></body>
                </html>
                """);

            var items = new StringBuilder();
            var spine = new StringBuilder();
            if (nav) items.Append("""<item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>""");
            for (var i = 0; i < documents.Length; i++)
            {
                items.Append($"""<item id="c{i}" href="{Path.GetFileName(documents[i].Path)}" media-type="application/xhtml+xml"/>""");
                spine.Append($"""<itemref idref="c{i}"/>""");
            }

            // The defect: one id, twice.
            if (duplicateId)
                items.Append("""<item id="c0" href="cover.png" media-type="image/png"/>""");

            Write("OEBPS/content.opf",
                $"""
                <?xml version="1.0"?>
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>A Book</dc:title><dc:identifier id="id">x</dc:identifier></metadata>
                  <manifest>{items}</manifest>
                  <spine>{spine}</spine>
                </package>
                """);

            foreach (var (path, html) in documents)
                Write(path, $"<html><body>{html}</body></html>");

            if (encrypted)
                Write("META-INF/encryption.xml", """<encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container"/>""");
        }

        buffer.Position = 0;
        return buffer;
    }

    [Fact]
    public void A_duplicate_manifest_id_does_not_lose_the_book()
    {
        using var epub = Epub(
            duplicateId: true,
            documents:
            [
                ("OEBPS/ch1.xhtml", "<p>The first chapter is about coupling.</p>"),
                ("OEBPS/ch2.xhtml", "<p>The second chapter is about cohesion.</p>"),
            ]);

        var text = new EpubTextExtractor().Extract(epub, "broken-manifest.epub");

        text.Text.ShouldContain("coupling");
        text.Text.ShouldContain("cohesion");
    }

    [Fact]
    public void Salvaged_text_still_carries_units_so_a_citation_can_point_somewhere()
    {
        // Without units a chunk from a 400-page book cites nothing a reader can find.
        using var epub = Epub(
            duplicateId: true,
            documents:
            [
                ("OEBPS/ch1.xhtml", "<p>The first chapter is about coupling.</p>"),
                ("OEBPS/ch2.xhtml", "<p>The second chapter is about cohesion.</p>"),
            ]);

        var text = new EpubTextExtractor().Extract(epub, "broken-manifest.epub");

        // Not an exact count: salvage reads every XHTML in the archive, front matter and
        // navigation included, because without a manifest there is nothing that says which
        // files are chapters. What must hold is that each unit points at its own text.
        text.Units.ShouldNotBeEmpty();
        text.Units[0].StartOffset.ShouldBe(0);
        text.Units.Select(u => u.StartOffset).ShouldBeInOrder();

        foreach (var unit in text.Units)
            unit.StartOffset.ShouldBeLessThan(text.Text.Length);

        // The unit must start where its chapter starts, or the citation is off by a chapter.
        var second = text.Units.First(u => text.Text[u.StartOffset..].StartsWith("The second chapter"));
        second.Label.ShouldBe("ch2");
    }

    [Fact]
    public void A_readable_manifest_is_still_preferred()
    {
        // The salvage path loses reading order and the title, so it must stay a fallback.
        using var epub = Epub(documents: [("OEBPS/ch1.xhtml", "<p>Perfectly ordinary.</p>")]);

        var text = new EpubTextExtractor().Extract(epub, "fine.epub");

        text.Title.ShouldBe("A Book");
        text.Text.ShouldContain("Perfectly ordinary");
    }

    [Fact]
    public void An_encrypted_book_says_DRM_because_that_is_what_it_is()
    {
        using var epub = Epub(
            duplicateId: true, encrypted: true,
            documents: [("OEBPS/ch1.xhtml", "<p>Unreachable.</p>")]);

        var failure = Should.Throw<ExtractionFailedException>(
            () => new EpubTextExtractor().Extract(epub, "drm.epub"));

        failure.Message.ShouldContain("DRM");
    }

    [Fact]
    public void A_malformed_book_does_not_say_DRM()
    {
        // The whole point. Blaming DRM for a manifest defect sent people looking for a
        // problem they did not have.
        // A broken manifest AND nothing readable inside: no chapters, no navigation.
        using var epub = Epub(duplicateId: true, nav: false);

        var failure = Should.Throw<ExtractionFailedException>(
            () => new EpubTextExtractor().Extract(epub, "empty.epub"));

        failure.Message.ShouldNotContain("DRM");
        failure.Message.ShouldContain("manifest");
    }

    [Fact]
    public void Something_that_is_not_a_zip_is_not_reported_as_a_book_problem()
    {
        using var notAnEpub = new MemoryStream("this is just text"u8.ToArray());

        Should.Throw<ExtractionFailedException>(
            () => new EpubTextExtractor().Extract(notAnEpub, "fake.epub"));
    }
}
