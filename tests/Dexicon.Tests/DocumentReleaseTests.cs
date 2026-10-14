using Dexicon.Api;
using Dexicon.Core.Catalog;
using Dexicon.Core.Documents;
using Dexicon.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dexicon.Tests;

/// <summary>
/// A stored document that no corpus holds is deleted: its blob row, its extracted text and its bytes. A
/// detach is a delete, and uploading the same bytes again is how a document comes back. The release happens
/// when the last attachment goes (a detach, a replacement by name, the removal of an upload source or a
/// corpus) and once at startup for any left over.
/// </summary>
public sealed class DocumentReleaseTests
{
    private static async Task<IndexingHarness> StartAsync()
    {
        var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        return harness;
    }

    private static async Task<Corpus> CorpusAsync(CatalogDbContext db, string id) =>
        await db.Corpora.Include(c => c.Sources).Include(c => c.ChunkSets).SingleAsync(c => c.Id == id);

    private static async Task<Corpus> AddCorpusAsync(IndexingHarness harness, CatalogDbContext db, string name)
    {
        var made = (await harness.NewConfiguration(db).CreateCorpusAsync(new CreateCorpusRequest(name), default)).Value!;
        return await CorpusAsync(db, made.Id);
    }

    private static async Task<string> StoreAsync(DocumentService documents, string text, string name = "a.txt") =>
        (await documents.StoreAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)), name)).Sha256;

    private static async Task<(bool Blob, bool Text, bool Bytes)> HeldAsync(
        IndexingHarness harness, DocumentService documents, string sha)
    {
        await using var check = harness.NewContext();
        return (await check.Blobs.AnyAsync(b => b.Sha256 == sha),
            await check.BlobTexts.AnyAsync(t => t.Sha256 == sha),
            File.Exists(documents.PathFor(sha)));
    }

    [Fact]
    public async Task TheLastDetachDeletesTheDocumentItsTextAndItsBytes()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var documents = harness.NewDocumentService(db);
        var sha = await StoreAsync(documents, "held by one corpus");
        var file = await documents.AttachAsync(await CorpusAsync(db, IndexingHarness.CorpusId), sha, "a.txt");
        (await HeldAsync(harness, documents, sha)).ShouldBe((true, true, true), "the premise");

        (await documents.DetachAsync(IndexingHarness.CorpusId, file.Id)).ShouldBeTrue();

        (await HeldAsync(harness, documents, sha)).ShouldBe((false, false, false));
    }

    [Fact]
    public async Task ADocumentAnotherCorpusHoldsStaysUntilItsLastDetach()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var documents = harness.NewDocumentService(db);
        var papers = await AddCorpusAsync(harness, db, "papers");
        var sha = await StoreAsync(documents, "held by two corpora");
        var inNotes = await documents.AttachAsync(await CorpusAsync(db, IndexingHarness.CorpusId), sha, "a.txt");
        var inPapers = await documents.AttachAsync(papers, sha, "a.txt");

        (await documents.DetachAsync(IndexingHarness.CorpusId, inNotes.Id)).ShouldBeTrue();
        (await HeldAsync(harness, documents, sha)).ShouldBe((true, true, true), "papers still holds it");

        (await documents.DetachAsync(papers.Id, inPapers.Id)).ShouldBeTrue();
        (await HeldAsync(harness, documents, sha)).ShouldBe((false, false, false));
    }

    [Fact]
    public async Task ReplacingADocumentByNameDeletesTheBytesItReplaced()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var documents = harness.NewDocumentService(db);
        var corpus = await CorpusAsync(db, IndexingHarness.CorpusId);
        var first = await StoreAsync(documents, "the first version");
        await documents.AttachAsync(corpus, first, "a.txt");
        var second = await StoreAsync(documents, "the second version");

        await documents.AttachAsync(corpus, second, "a.txt");

        (await HeldAsync(harness, documents, first)).ShouldBe((false, false, false), "nothing holds the replaced bytes");
        (await HeldAsync(harness, documents, second)).ShouldBe((true, true, true));
    }

    [Fact]
    public async Task RemovingAnUploadSourceDeletesTheDocumentsNoOtherCorpusHolds()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var documents = harness.NewDocumentService(db);
        var notes = await CorpusAsync(db, IndexingHarness.CorpusId);
        var papers = await AddCorpusAsync(harness, db, "papers");
        var alone = await StoreAsync(documents, "only notes holds this", "alone.txt");
        var shared = await StoreAsync(documents, "notes and papers hold this", "shared.txt");
        await documents.AttachAsync(notes, alone, "alone.txt");
        await documents.AttachAsync(notes, shared, "shared.txt");
        await documents.AttachAsync(papers, shared, "shared.txt");
        var source = await db.Sources.SingleAsync(s => s.CorpusId == notes.Id && s.Kind == SourceKind.Upload);

        (await harness.NewConfiguration(db).RemoveSourceAsync(notes, source.Id, default)).Refusal.ShouldBeNull();

        (await HeldAsync(harness, documents, alone)).ShouldBe((false, false, false));
        (await HeldAsync(harness, documents, shared)).ShouldBe((true, true, true), "papers still holds it");
    }

    [Fact]
    public async Task RemovingACorpusDeletesTheDocumentsNoOtherCorpusHolds()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var documents = harness.NewDocumentService(db);
        var notes = await CorpusAsync(db, IndexingHarness.CorpusId);
        var papers = await AddCorpusAsync(harness, db, "papers");
        var alone = await StoreAsync(documents, "only notes holds this", "alone.txt");
        var shared = await StoreAsync(documents, "notes and papers hold this", "shared.txt");
        await documents.AttachAsync(notes, alone, "alone.txt");
        await documents.AttachAsync(notes, shared, "shared.txt");
        await documents.AttachAsync(papers, shared, "shared.txt");

        (await harness.NewConfiguration(db).RemoveCorpusAsync(notes, default)).Refusal.ShouldBeNull();

        (await HeldAsync(harness, documents, alone)).ShouldBe((false, false, false));
        (await HeldAsync(harness, documents, shared)).ShouldBe((true, true, true), "papers still holds it");
    }

    [Fact]
    public async Task StartupDeletesTheStoredDocumentsNoCorpusHoldsAndKeepsTheRest()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var documents = harness.NewDocumentService(db);
        var held = await StoreAsync(documents, "held by notes", "held.txt");
        await documents.AttachAsync(await CorpusAsync(db, IndexingHarness.CorpusId), held, "held.txt");
        var leftOver = await StoreAsync(documents, "stored and never attached", "left.txt");

        await Bootstrapper.ReleaseUnheldDocumentsAsync(db, harness.Settings.Value, NullLogger.Instance);

        (await HeldAsync(harness, documents, leftOver)).ShouldBe((false, false, false));
        (await HeldAsync(harness, documents, held)).ShouldBe((true, true, true));
    }

    [Fact]
    public async Task AReleaseNeverDeletesADocumentAFileRowStillHolds()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var documents = harness.NewDocumentService(db);
        var sha = await StoreAsync(documents, "held");
        await documents.AttachAsync(await CorpusAsync(db, IndexingHarness.CorpusId), sha, "a.txt");

        var deleted = await DocumentService.ReleaseAsync(db, harness.Settings.Value.Storage.BlobRoot, [sha, "no-such-hash"]);

        deleted.ShouldBe(0);
        (await HeldAsync(harness, documents, sha)).ShouldBe((true, true, true));
    }

    [Fact]
    public async Task ACatalogueThatRefusesTheDeleteLeavesTheDocumentAndTheDetachStands()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var documents = harness.NewDocumentService(db);
        var sha = await StoreAsync(documents, "kept because the catalogue refuses");
        var file = await documents.AttachAsync(await CorpusAsync(db, IndexingHarness.CorpusId), sha, "a.txt");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER refuse_blob_delete BEFORE DELETE ON blobs BEGIN SELECT RAISE(ABORT, 'refused'); END");
        var logs = new RecordingLoggerFactory();
        var detaching = harness.NewDocumentService(db, new Logger<DocumentService>(logs));

        (await detaching.DetachAsync(IndexingHarness.CorpusId, file.Id)).ShouldBeTrue();

        (await HeldAsync(harness, documents, sha)).ShouldBe((true, true, true), "the delete was refused");
        await using var check = harness.NewContext();
        (await check.Files.AnyAsync(f => f.Id == file.Id)).ShouldBeFalse("the detach itself was saved");
        logs.Levels.ShouldBe([LogLevel.Warning]);
        logs.Exceptions.ShouldHaveSingleItem().ShouldBeAssignableTo<System.Data.Common.DbException>();
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER refuse_blob_delete");
        await Bootstrapper.ReleaseUnheldDocumentsAsync(db, harness.Settings.Value, NullLogger.Instance);
        (await HeldAsync(harness, documents, sha)).ShouldBe((false, false, false), "the next start deletes it");
    }

    [Fact]
    public async Task AttachingADocumentTheLastDetachDeletedMeanwhileSaysSoAndAttachesNothing()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var documents = harness.NewDocumentService(db);
        var corpus = await CorpusAsync(db, IndexingHarness.CorpusId);
        var papers = await AddCorpusAsync(harness, db, "papers");
        var sha = await StoreAsync(documents, "deleted between the lookup and the attach");
        var held = await documents.AttachAsync(papers, sha, "a.txt");
        await documents.DetachAsync(papers.Id, held.Id);

        var thrown = await Should.ThrowAsync<DocumentGoneException>(() => documents.AttachAsync(corpus, sha, "a.txt"));

        thrown.Message.ShouldContain("Upload it again");
        await using var check = harness.NewContext();
        (await check.Files.CountAsync(f => f.BlobSha256 == sha)).ShouldBe(0);
    }

    [Fact]
    public async Task UploadingTheSameBytesAgainAfterTheLastDetachStoresAndExtractsThemAgain()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var documents = harness.NewDocumentService(db);
        var corpus = await CorpusAsync(db, IndexingHarness.CorpusId);
        var sha = await StoreAsync(documents, "come back");
        var file = await documents.AttachAsync(corpus, sha, "a.txt");
        await documents.DetachAsync(corpus.Id, file.Id);

        var again = await documents.StoreAsync(new MemoryStream("come back"u8.ToArray()), "a.txt");

        again.Sha256.ShouldBe(sha);
        again.AlreadyExisted.ShouldBeFalse("the document was deleted, so it is stored as new");
        await documents.AttachAsync(corpus, again.Sha256, "a.txt");
        (await HeldAsync(harness, documents, sha)).ShouldBe((true, true, true));
    }
}
