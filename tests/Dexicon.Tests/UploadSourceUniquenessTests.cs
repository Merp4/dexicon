using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// A corpus has at most one upload source, created by the first document attached to it.
///
/// Nothing in the schema says so, and the source was looked for and then added with no lock between the
/// two. Two first attachments at the same moment, from two requests with a context each, both found none
/// and each added one. Every upload source is indexed on its own, so nothing failed: the corpus listed
/// two, and a document attached under both was chunked, embedded and returned twice.
/// </summary>
[Collection(nameof(AttachmentLockCollection))]
public sealed class UploadSourceUniquenessTests
{
    private static bool IsTheSourceLookup(string sql) =>
        sql.Contains("FROM \"sources\"", StringComparison.Ordinal)
        && sql.Contains("\"Kind\"", StringComparison.Ordinal)
        && sql.Contains("LIMIT 1", StringComparison.Ordinal);

    private static Task<Source?> LookUpTheUploadSourceAsync(CatalogDbContext db) =>
        db.Sources.FirstOrDefaultAsync(s => s.CorpusId == IndexingHarness.CorpusId && s.Kind == SourceKind.Upload);

    [Fact]
    public async Task TheGateHoldsTwoReadersOfTheUploadSourceAtOnce()
    {
        // The control for the test below: with nothing in the way, the gate does put two lookups inside
        // the window, and both find no source.
        var gate = new HoldTheLookup(TimeSpan.FromSeconds(10), IsTheSourceLookup);
        await using var harness = await IndexingHarness.StartAsync(gate, "notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await using var first = harness.NewContext();
        await using var second = harness.NewContext();
        gate.Armed = true;

        var found = await Task.WhenAll(LookUpTheUploadSourceAsync(first), LookUpTheUploadSourceAsync(second));

        gate.Met.ShouldBeTrue("the gate has to have held both lookups at once");
        found.ShouldAllBe(s => s == null);
    }

    [Fact]
    public async Task TwoFirstAttachmentsToOneCorpusAtTheSameTimeCreateOneUploadSource()
    {
        var gate = new HoldTheLookup(TimeSpan.FromMilliseconds(750), IsTheSourceLookup);
        await using var harness = await IndexingHarness.StartAsync(gate, "notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await using var first = harness.NewContext();
        await using var second = harness.NewContext();
        var firstDocuments = harness.NewDocumentService(first);
        var secondDocuments = harness.NewDocumentService(second);
        var one = await firstDocuments.StoreAsync(new MemoryStream("the first document"u8.ToArray()), "one.txt");
        var two = await secondDocuments.StoreAsync(new MemoryStream("the second document"u8.ToArray()), "two.txt");
        var firstCorpus = await first.Corpora.SingleAsync();
        var secondCorpus = await second.Corpora.SingleAsync();
        gate.Armed = true;

        await Task.WhenAll(
            firstDocuments.AttachAsync(firstCorpus, one.Sha256, "one.txt"),
            secondDocuments.AttachAsync(secondCorpus, two.Sha256, "two.txt"));

        gate.Arrivals.ShouldBe(2, "both attachments have to have looked for the source");
        await using var check = harness.NewContext();
        var uploads = await check.Sources.Where(s => s.Kind == SourceKind.Upload).ToListAsync();
        uploads.Count.ShouldBe(1, "a corpus has one upload source");
        (await check.Files.CountAsync(f => f.SourceId == uploads[0].Id)).ShouldBe(2, "both documents are attached to it");
    }

    [Fact]
    public async Task TwoAttachmentsOfOneDocumentAtTheSameTimeAreOneAttachment()
    {
        // The same lookup-then-add held for the document: "one blob, one attachment per corpus" is a
        // check made before the insert, and two requests could both pass it.
        var gate = new HoldTheLookup(TimeSpan.FromMilliseconds(750), IsTheSourceLookup);
        await using var harness = await IndexingHarness.StartAsync(gate, "notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await using var first = harness.NewContext();
        await using var second = harness.NewContext();
        var firstDocuments = harness.NewDocumentService(first);
        var secondDocuments = harness.NewDocumentService(second);
        var stored = await firstDocuments.StoreAsync(new MemoryStream("one document"u8.ToArray()), "doc.txt");
        var firstCorpus = await first.Corpora.SingleAsync();
        var secondCorpus = await second.Corpora.SingleAsync();
        gate.Armed = true;

        await Task.WhenAll(
            firstDocuments.AttachAsync(firstCorpus, stored.Sha256, "doc.txt"),
            secondDocuments.AttachAsync(secondCorpus, stored.Sha256, "renamed.txt"));

        gate.Arrivals.ShouldBe(2, "both attachments have to have looked for the source");
        await using var check = harness.NewContext();
        (await check.Files.CountAsync(f => f.BlobSha256 == stored.Sha256)).ShouldBe(1, "one attachment of one blob per corpus");
    }
}
