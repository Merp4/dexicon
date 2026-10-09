using Dexicon.Core.Catalog;
using Dexicon.Core.Documents;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// The same bytes uploaded twice at the same moment are stored once, and both uploads are answered.
///
/// A blob is looked for by its hash and, when it is not there, extracted and added. Extraction takes
/// as long as the file, so two uploads of one new file both found nothing, both extracted, and the
/// second save failed on the blob's primary key. That reached the caller as a 500, with the file in
/// no corpus and no job queued. Found by driving four uploads of one file at once against a running
/// build, where three of the four failed.
/// </summary>
public sealed class ConcurrentStoreOfOneDocumentTests
{
    private static readonly byte[] TheSameBytes = "one document, uploaded by two requests at the same moment"u8.ToArray();

    private static bool IsTheBlobLookup(string sql) =>
        sql.Contains("FROM \"blobs\"", StringComparison.Ordinal)
        && sql.Contains("\"Sha256\"", StringComparison.Ordinal)
        && sql.Contains("LIMIT 1", StringComparison.Ordinal);

    private static Task<Blob?> LookUpTheBlobAsync(CatalogDbContext db, string sha) =>
        db.Blobs.FirstOrDefaultAsync(b => b.Sha256 == sha);

    [Fact]
    public async Task TheGateHoldsTwoLookupsOfOneBlobAtOnce()
    {
        // The control for the test below: with nothing in the way, the gate does put two lookups inside
        // the window, and both find no blob.
        var gate = new HoldTheLookup(TimeSpan.FromSeconds(10), IsTheBlobLookup);
        await using var harness = await IndexingHarness.StartAsync(gate, "notes");
        await using var first = harness.NewContext();
        await using var second = harness.NewContext();
        gate.Armed = true;

        var found = await Task.WhenAll(LookUpTheBlobAsync(first, "abc"), LookUpTheBlobAsync(second, "abc"));

        gate.Met.ShouldBeTrue("the gate has to have held both lookups at once");
        found.ShouldAllBe(b => b == null);
    }

    [Fact]
    public async Task TwoUploadsOfTheSameBytesAtTheSameTimeStoreOneBlobAndAnswerBoth()
    {
        var gate = new HoldTheLookup(TimeSpan.FromSeconds(10), IsTheBlobLookup);
        await using var harness = await IndexingHarness.StartAsync(gate, "notes");
        await using var first = harness.NewContext();
        await using var second = harness.NewContext();
        var firstDocuments = harness.NewDocumentService(first);
        var secondDocuments = harness.NewDocumentService(second);
        gate.Armed = true;

        var stored = await Task.WhenAll(
            firstDocuments.StoreAsync(new MemoryStream(TheSameBytes), "one.txt"),
            secondDocuments.StoreAsync(new MemoryStream(TheSameBytes), "two.txt"));

        gate.Met.ShouldBeTrue("both uploads have to have looked before either saved");
        stored.Count(s => s.AlreadyExisted).ShouldBe(1, "the upload that lost reports the blob it found");
        stored[1].Sha256.ShouldBe(stored[0].Sha256);
        stored.ShouldAllBe(s => s.ExtractedChars > 0, "the text of the blob that was kept is reported to both");
        stored.Select(s => s.ExtractedChars).Distinct().Count().ShouldBe(1);

        await using var check = harness.NewContext();
        (await check.Blobs.CountAsync()).ShouldBe(1, "one blob for one set of bytes");
        (await check.BlobTexts.CountAsync()).ShouldBe(1, "extracted once");
        first.ChangeTracker.Entries().ShouldBeEmpty("the loser's pending rows are dropped, not left to fail the next save");
        second.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task TheUploadThatLostCanStillAttachTheDocument()
    {
        // What the upload endpoint does next: attach what was stored, on the same context.
        var gate = new HoldTheLookup(TimeSpan.FromSeconds(10), IsTheBlobLookup);
        await using var harness = await IndexingHarness.StartAsync(gate, "notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await using var first = harness.NewContext();
        await using var second = harness.NewContext();
        var firstDocuments = harness.NewDocumentService(first);
        var secondDocuments = harness.NewDocumentService(second);
        var firstCorpus = await first.Corpora.SingleAsync();
        var secondCorpus = await second.Corpora.SingleAsync();
        gate.Armed = true;

        async Task UploadAsync(DocumentService documents, Corpus corpus, string name)
        {
            var stored = await documents.StoreAsync(new MemoryStream(TheSameBytes), name);
            await documents.AttachAsync(corpus, stored.Sha256, name);
        }

        await Task.WhenAll(UploadAsync(firstDocuments, firstCorpus, "one.txt"), UploadAsync(secondDocuments, secondCorpus, "two.txt"));

        gate.Met.ShouldBeTrue("both uploads have to have looked before either saved");
        await using var check = harness.NewContext();
        (await check.Blobs.CountAsync()).ShouldBe(1);
        (await check.Files.CountAsync()).ShouldBe(1, "one corpus holds one blob once, whatever name it came under");
    }

    [Theory]
    [InlineData(19, 1555, true)]   // SQLITE_CONSTRAINT_PRIMARYKEY: the blob's own key
    [InlineData(19, 2067, true)]   // SQLITE_CONSTRAINT_UNIQUE
    [InlineData(19, 787, false)]   // SQLITE_CONSTRAINT_FOREIGNKEY
    [InlineData(19, 1299, false)]  // SQLITE_CONSTRAINT_NOTNULL
    [InlineData(5, 5, false)]      // SQLITE_BUSY
    [InlineData(13, 13, false)]    // SQLITE_FULL
    public void OnlyAKeyAlreadyTakenIsADuplicateKey(int code, int extendedCode, bool expected)
    {
        var failure = new DbUpdateException("save failed", new SqliteException("SQLite Error", code, extendedCode));

        failure.IsDuplicateKey().ShouldBe(expected);
    }

    [Fact]
    public void AFailureWithNoDatabaseErrorBehindItIsNotADuplicateKey()
    {
        new DbUpdateException("save failed").IsDuplicateKey().ShouldBeFalse();
        new DbUpdateException("save failed", new InvalidOperationException()).IsDuplicateKey().ShouldBeFalse();
    }
}
