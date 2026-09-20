using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Extraction;
using Dexicon.Core.Indexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// Extraction for workspace files, cached against the bytes it came from.
///
/// Uploads have had this since <c>blob_texts</c>. Workspace files did not, and the
/// staleness check could not add it: the fingerprint it compares is a hash of the
/// EXTRACTED text, so deciding a file was unchanged required extracting it first. Every
/// refresh therefore re-opened and re-parsed every PDF in the tree, whether or not
/// anything had touched it, and threw the text away again after chunking.
///
/// The counting extractor below is what makes that observable. A test that only compared
/// returned text would pass on a cache that never hits, which is the failure this is
/// here to catch.
/// </summary>
public sealed class ExtractedTextCacheTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("filetext-").FullName;
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"filetext-{Guid.NewGuid():N}.db");

    private sealed class Counting(string text, IReadOnlyList<ExtractedUnit>? units = null,
                                  string? title = null) : ITextExtractor
    {
        public int Calls { get; private set; }

        public bool CanHandle(string extension) => true;

        public ExtractedText Extract(Stream content, string fileName)
        {
            Calls++;

            // Drained, because the real ones read the stream and the deadline wrapper
            // only fires on a read. An extractor that ignores its input would hide a
            // caller that opens the wrong file.
            content.CopyTo(Stream.Null);
            return new ExtractedText(text, units ?? [], title);
        }
    }

    private CatalogDbContext Db()
    {
        var db = new CatalogDbContext(
            new DbContextOptionsBuilder<CatalogDbContext>().UseSqlite($"Data Source={_db}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    /// <summary>
    /// Everything but the catalog, the options and the logger is null: the method under
    /// test reaches none of it, and standing up a vector store and an embedding provider
    /// to check a cache would test neither.
    /// </summary>
    private static CorpusIndexer Indexer(CatalogDbContext db) =>
        new(db, null!, null!, null!, null!, null!,
            Options.Create(new DexiconOptions()), NullLogger<CorpusIndexer>.Instance);

    private WorkspaceWalker.Candidate File(string name, string bytes)
    {
        var full = Path.Combine(_dir, name);
        System.IO.File.WriteAllText(full, bytes);
        return new WorkspaceWalker.Candidate(full, name, new FileInfo(full).Length);
    }

    [Fact]
    public async Task TheSecondPassOverAnUnchangedFileDoesNotRunTheExtractor()
    {
        await using var db = Db();
        var indexer = Indexer(db);
        var extractor = new Counting("page one");
        var file = File("a.pdf", "%PDF-1.7 whatever");

        var first = await indexer.ExtractCachedAsync(extractor, file, default);
        var second = await indexer.ExtractCachedAsync(extractor, file, default);

        extractor.Calls.ShouldBe(1);
        first.Text.ShouldBe("page one");
        second.Text.ShouldBe("page one");
    }

    [Fact]
    public async Task UnitsAndTitleSurviveTheRoundTrip()
    {
        await using var db = Db();
        var indexer = Indexer(db);
        var extractor = new Counting("body",
            [new ExtractedUnit(1, 0), new ExtractedUnit(2, 4, "Chapter 2")], "A Book");
        var file = File("b.epub", "PK zip bytes");

        await indexer.ExtractCachedAsync(extractor, file, default);
        var cached = await indexer.ExtractCachedAsync(extractor, file, default);

        extractor.Calls.ShouldBe(1);
        cached.Title.ShouldBe("A Book");
        cached.Units.Count.ShouldBe(2);
        cached.Units[1].Number.ShouldBe(2);
        cached.Units[1].StartOffset.ShouldBe(4);
        cached.Units[1].Label.ShouldBe("Chapter 2");
    }

    [Fact]
    public async Task ChangedBytesAreExtractedAgain()
    {
        await using var db = Db();
        var indexer = Indexer(db);
        var extractor = new Counting("text");

        await indexer.ExtractCachedAsync(extractor, File("c.pdf", "one"), default);
        await indexer.ExtractCachedAsync(extractor, File("c.pdf", "two"), default);

        extractor.Calls.ShouldBe(2);
        (await db.FileTexts.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task TwoFilesWithIdenticalBytesShareOneRow()
    {
        await using var db = Db();
        var indexer = Indexer(db);
        var extractor = new Counting("same");

        await indexer.ExtractCachedAsync(extractor, File("d.pdf", "identical"), default);
        await indexer.ExtractCachedAsync(extractor, File("e.pdf", "identical"), default);

        extractor.Calls.ShouldBe(1);
        (await db.FileTexts.CountAsync()).ShouldBe(1);
    }

    /// <summary>
    /// The reason the version column exists. Without it a library ingested before an
    /// extractor fix keeps the broken text forever, because reindexing re-chunks the
    /// cached text rather than re-reading the file.
    /// </summary>
    [Fact]
    public async Task TextFromAnOlderExtractorIsReplacedRatherThanTrusted()
    {
        await using var db = Db();
        var indexer = Indexer(db);
        var file = File("f.pdf", "bytes");

        await indexer.ExtractCachedAsync(new Counting("old text"), file, default);

        var stale = await db.FileTexts.SingleAsync();
        stale.ExtractorVersion = ExtractorVersions.Current - 1;
        await db.SaveChangesAsync();

        var fresh = new Counting("new text");
        var result = await indexer.ExtractCachedAsync(fresh, file, default);

        fresh.Calls.ShouldBe(1);
        result.Text.ShouldBe("new text");

        var rows = await db.FileTexts.AsNoTracking().ToListAsync();
        rows.Count.ShouldBe(1);   // overwritten, not added beside
        rows[0].Text.ShouldBe("new text");
        rows[0].ExtractorVersion.ShouldBe(ExtractorVersions.Current);
        rows[0].ExtractedChars.ShouldBe("new text".Length);
    }

    /// <summary>
    /// A scanned PDF costs the same to re-read as a readable one and yields nothing
    /// either time, so "produced no text" is cached too, with the reason it will be
    /// shown by.
    /// </summary>
    [Fact]
    public async Task AFileThatYieldsNoTextIsCachedWithItsReason()
    {
        await using var db = Db();
        var indexer = Indexer(db);
        var extractor = new Counting("   ");
        var file = File("g.pdf", "scanned");

        await indexer.ExtractCachedAsync(extractor, file, default);
        await indexer.ExtractCachedAsync(extractor, file, default);

        extractor.Calls.ShouldBe(1);
        var row = await db.FileTexts.AsNoTracking().SingleAsync();
        row.EmptyReason.ShouldBe("no extractable text content");
        row.ExtractedChars.ShouldBe(3);
    }

    /// <summary>
    /// A row holds a whole document's text and the indexer's context lives as long as the
    /// job, so a tracked entity per file would hold the library in memory at once.
    /// </summary>
    [Fact]
    public async Task NoRowIsLeftInTheChangeTracker()
    {
        await using var db = Db();
        var indexer = Indexer(db);

        await indexer.ExtractCachedAsync(new Counting("written"), File("h.pdf", "one"), default);
        await indexer.ExtractCachedAsync(new Counting("read back"), File("h.pdf", "one"), default);

        db.ChangeTracker.Entries<FileText>().ShouldBeEmpty();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        try { System.IO.File.Delete(_db); } catch (IOException) { }
    }
}
