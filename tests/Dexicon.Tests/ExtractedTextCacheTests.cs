using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Documents;
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
/// returned text would pass on a cache that never hits, which is the failure this is here
/// to catch.
/// </summary>
public sealed class ExtractedTextCacheTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("filetext-").FullName;
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"filetext-{Guid.NewGuid():N}.db");

    private class Counting(string text, IReadOnlyList<ExtractedUnit>? units = null,
                           string? title = null) : ITextExtractor
    {
        public int Calls { get; private set; }

        public bool CanHandle(string extension) => true;

        public ExtractedText Extract(Stream content, string fileName)
        {
            Calls++;

            // Drained, because the real ones read the stream and the deadline wrapper
            // only fires on a read. An extractor that ignores its input would hide a
            // caller that opened the wrong file.
            content.CopyTo(Stream.Null);
            return new ExtractedText(text, units ?? [], title);
        }
    }

    /// <summary>Stands in for two extractors, which is two types rather than two instances.</summary>
    private sealed class CountingA(string text) : Counting(text);

    private sealed class CountingB(string text) : Counting(text);

    private CatalogDbContext Db()
    {
        var db = new CatalogDbContext(
            new DbContextOptionsBuilder<CatalogDbContext>().UseSqlite($"Data Source={_db}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    private static ExtractedTextCache Cache(CatalogDbContext db) =>
        new(db, new IndexingLimits(Options.Create(new DexiconOptions())),
            NullLogger<ExtractedTextCache>.Instance);

    /// <summary>A file on disk, and the two paths the reader would pass for it.</summary>
    private (string Full, string Relative) File(string name, string bytes)
    {
        var full = Path.Combine(_dir, name);
        System.IO.File.WriteAllText(full, bytes);
        return (full, name);
    }

    private static Task<ReadText> Read(
        ExtractedTextCache cache, (string Full, string Relative) file, ITextExtractor? extractor) =>
        cache.ReadAsync(file.Full, file.Relative, extractor, timeoutSeconds: 300, CancellationToken.None);

    [Fact]
    public async Task TheSecondPassOverAnUnchangedFileDoesNotRunTheExtractor()
    {
        await using var db = Db();
        var cache = Cache(db);
        var extractor = new Counting("page one");
        var file = File("a.pdf", "%PDF-1.7 whatever");

        var first = await Read(cache, file, extractor);
        var second = await Read(cache, file, extractor);

        extractor.Calls.ShouldBe(1);
        first.Text.Text.ShouldBe("page one");
        second.Text.Text.ShouldBe("page one");

        // The hash the file's per-set row is stamped with, so the text is reachable by path.
        second.Sha256.ShouldBe(first.Sha256);
        second.Sha256.Length.ShouldBe(64);
    }

    [Fact]
    public async Task UnitsAndTitleSurviveTheRoundTrip()
    {
        await using var db = Db();
        var cache = Cache(db);
        var extractor = new Counting("body",
            [new ExtractedUnit(1, 0), new ExtractedUnit(2, 4, "Chapter 2")], "A Book");
        var file = File("b.epub", "PK zip bytes");

        await Read(cache, file, extractor);
        var cached = (await Read(cache, file, extractor)).Text;

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
        var cache = Cache(db);
        var extractor = new Counting("text");

        await Read(cache, File("c.pdf", "one"), extractor);
        await Read(cache, File("c.pdf", "two"), extractor);

        extractor.Calls.ShouldBe(2);
        (await db.FileTexts.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task TwoFilesWithIdenticalBytesShareOneRow()
    {
        await using var db = Db();
        var cache = Cache(db);
        var extractor = new Counting("same");

        await Read(cache, File("d.pdf", "identical"), extractor);
        await Read(cache, File("e.pdf", "identical"), extractor);

        extractor.Calls.ShouldBe(1);
        (await db.FileTexts.CountAsync()).ShouldBe(1);
    }

    /// <summary>
    /// Which extractor runs is decided by extension, so the same bytes under two
    /// extensions are two different parses. DOCX, PPTX and EPUB are all zip containers,
    /// and a rename moves a file between them; keyed on the bytes alone, the second file
    /// would be handed the first's text and recorded as indexed.
    /// </summary>
    [Fact]
    public async Task TheSameBytesUnderTwoExtractorsAreTwoRows()
    {
        await using var db = Db();
        var cache = Cache(db);

        // Two TYPES, because the extractor's type name is what the row records and what
        // the lookup matches on. Two instances of one class are one extractor.
        var epub = new CountingA("read as an epub");
        var docx = new CountingB("read as a docx");
        var bytes = "PK identical zip bytes";

        await Read(cache, File("a.epub", bytes), epub);
        var second = await Read(cache, File("a.docx", bytes), docx);

        docx.Calls.ShouldBe(1);   // not served the epub's text
        second.Text.Text.ShouldBe("read as a docx");
        (await db.FileTexts.CountAsync()).ShouldBe(2);
    }

    /// <summary>
    /// A row stamped by a LATER build than this one. Overwriting it would make a rollback
    /// and the version it rolled back from take turns re-extracting the same library, each
    /// undoing the other's work every pass.
    /// </summary>
    [Fact]
    public async Task TextFromANewerExtractorIsKeptRatherThanDowngraded()
    {
        await using var db = Db();
        var cache = Cache(db);
        var file = File("f.pdf", "bytes");

        await Read(cache, file, new Counting("current text"));

        var ahead = await db.FileTexts.SingleAsync();
        ahead.ExtractorVersion = ExtractorVersions.Current + 1;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var older = new Counting("would be a downgrade");
        var result = await Read(cache, file, older);

        older.Calls.ShouldBe(0);
        result.Text.Text.ShouldBe("current text");
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
        var cache = Cache(db);
        var file = File("g.pdf", "bytes");

        await Read(cache, file, new Counting("old text"));

        var stale = await db.FileTexts.SingleAsync();
        stale.ExtractorVersion = ExtractorVersions.Current - 1;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var fresh = new Counting("new text");
        var result = await Read(cache, file, fresh);

        fresh.Calls.ShouldBe(1);
        result.Text.Text.ShouldBe("new text");

        var rows = await db.FileTexts.AsNoTracking().ToListAsync();
        rows.Count.ShouldBe(1);   // overwritten, not added beside
        rows[0].Text.ShouldBe("new text");
        rows[0].ExtractorVersion.ShouldBe(ExtractorVersions.Current);
        rows[0].ExtractedChars.ShouldBe("new text".Length);
    }

    /// <summary>
    /// A scanned PDF costs the same to re-read as a readable one and yields nothing
    /// either time, so "produced no text" is cached too. The reason is stored with it,
    /// which is what the Files list shows in a row's status detail.
    /// </summary>
    [Fact]
    public async Task AFileThatYieldsNoTextIsCachedWithItsReason()
    {
        await using var db = Db();
        var cache = Cache(db);
        var extractor = new Counting("   ");
        var file = File("h.pdf", "scanned");

        await Read(cache, file, extractor);
        await Read(cache, file, extractor);

        extractor.Calls.ShouldBe(1);
        var row = await db.FileTexts.AsNoTracking().SingleAsync();
        row.EmptyReason.ShouldBe("no extractable text content");
        row.ExtractedChars.ShouldBe(3);
    }

    /// <summary>
    /// A row holds a whole document's text and the caller's context can live as long as a
    /// job, so a tracked entity per file would hold the library in memory at once.
    /// </summary>
    [Fact]
    public async Task NoRowIsLeftInTheChangeTracker()
    {
        await using var db = Db();
        var cache = Cache(db);

        await Read(cache, File("i.pdf", "one"), new Counting("written"));
        await Read(cache, File("i.pdf", "one"), new Counting("read back"));

        db.ChangeTracker.Entries<FileText>().ShouldBeEmpty();
    }

    /// <summary>
    /// Plain text and code have no extractor, and their text is not cached: reading the
    /// file IS the extraction, so a cache would hold a second copy of the tree. The hash
    /// is still taken, because the file's per-set row carries it either way.
    /// </summary>
    [Fact]
    public async Task AFileWithNoExtractorIsReadButNotCached()
    {
        await using var db = Db();
        var cache = Cache(db);

        var read = await Read(cache, File("a.cs", "class A { }"), extractor: null);

        read.Text.Text.ShouldBe("class A { }");
        read.Extractor.ShouldBeNull();
        read.Sha256.Length.ShouldBe(64);
        (await db.FileTexts.CountAsync()).ShouldBe(0);
    }

    /// <summary>
    /// A job cancelled while every extraction permit is held must not leave its reader
    /// stuck.
    ///
    /// The permit was taken with a blocking Wait(), which no token can cancel, so the
    /// waiting reader stayed blocked and the reader's own cancellation cleanup could not
    /// run. Worst with ExtractionTimeoutSeconds=0, where the parse holding the permit has
    /// no bound either: nothing would ever have freed it.
    /// </summary>
    [Fact]
    public async Task CancellingWhileEveryExtractionPermitIsHeldDoesNotBlock()
    {
        await using var db = Db();
        using var limits = new IndexingLimits(Options.Create(new DexiconOptions
        {
            Indexing = new IndexingOptions { MaxConcurrentExtractions = 1 },
        }));
        var cache = new ExtractedTextCache(db, limits, NullLogger<ExtractedTextCache>.Instance);

        // The only permit, held by something else for the duration.
        await limits.Extractions.WaitAsync();

        using var stopping = new CancellationTokenSource();
        var extractor = new Counting("never reached");
        var reading = cache.ReadAsync(
            File("blocked.pdf", "bytes").Full, "blocked.pdf", extractor,
            timeoutSeconds: 0, stopping.Token);

        // Long enough that the hash and the cache lookup are certainly done and the call
        // is parked on the permit. Without this the cancellation lands on the catalogue
        // query instead, and the test passes with the blocking wait still in place -
        // which it did, until the control showed it.
        await Task.Delay(500);
        extractor.Calls.ShouldBe(0, "the permit should not have been granted");

        await stopping.CancelAsync();

        // WHICH task finished, not that one did: an elapsed timeout completes
        // successfully too, so only comparing identities can fail.
        var finished = await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(15)));
        finished.ShouldBeSameAs(reading, "the wait for a permit has to carry the job's token");
        await Should.ThrowAsync<OperationCanceledException>(async () => await reading);

        limits.Extractions.Release();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        try { System.IO.File.Delete(_db); } catch (IOException) { }
    }
}
