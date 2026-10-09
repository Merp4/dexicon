using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Documents;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// An attachment that waited for the attachment lock behind the removal of its corpus. It resolved the
/// corpus before it waited, and the corpus is gone when it gets the lock.
/// </summary>
[Collection(nameof(AttachmentLockCollection))]
public sealed class AttachingToARemovedCorpusTests
{
    [Fact]
    public async Task AnAttachmentThatWaitedBehindTheRemovalOfItsCorpusIsAnsweredAsAnUnknownCorpus()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await using var attaching = harness.NewContext();
        var documents = harness.NewDocumentService(attaching);
        var stored = await documents.StoreAsync(new MemoryStream("one document"u8.ToArray()), "doc.txt");
        var corpus = await attaching.Corpora.SingleAsync();

        Task<IndexedFile> attachment;
        using (await DocumentService.HoldAttachmentsAsync().FinishesAsync("taking the lock"))
        {
            attachment = Task.Run(() => documents.AttachAsync(corpus, stored.Sha256, "doc.txt"));
            await using var removing = harness.NewContext();
            await removing.Corpora.ExecuteDeleteAsync();
        }

        var refused = await Should.ThrowAsync<ScopeResolutionException>(
            () => attachment.FinishesAsync("the attachment"));
        refused.Message.ShouldContain("removed while this request waited");
        await using var check = harness.NewContext();
        (await check.Sources.CountAsync()).ShouldBe(0, "no upload source is created for a corpus that is gone");
        (await check.Files.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task TheCheckThatTheCorpusIsThereRunsUnderTheLock()
    {
        // A check made before the lock is taken passes for an attachment that then waits behind a removal.
        // While the attachment is held at its check, the lock is its own and nothing else can take it.
        var hold = new HoldOneLookup(sql =>
            sql.Contains("FROM \"corpora\"", StringComparison.Ordinal) && sql.Contains("EXISTS", StringComparison.Ordinal));
        await using var harness = await IndexingHarness.StartAsync(hold, "notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await using var attaching = harness.NewContext();
        var documents = harness.NewDocumentService(attaching);
        var stored = await documents.StoreAsync(new MemoryStream("one document"u8.ToArray()), "doc.txt");
        var corpus = await attaching.Corpora.SingleAsync();
        hold.Armed = true;

        var attachment = Task.Run(() => documents.AttachAsync(corpus, stored.Sha256, "doc.txt"));
        Task<IDisposable> other;
        bool tookTheLockWhileHeld;
        try
        {
            await Gates.ReachedAsync(hold.Reached, "the attachment's check of its corpus");
            other = DocumentService.HoldAttachmentsAsync();
            tookTheLockWhileHeld = await Task.WhenAny(other, Task.Delay(TimeSpan.FromSeconds(3))) == other;
        }
        finally { hold.Release(); }

        using var _ = await other.FinishesAsync("taking the lock once the attachment had finished");
        await attachment.FinishesAsync("the attachment");
        tookTheLockWhileHeld.ShouldBeFalse("the attachment checked its corpus before it held the lock");
    }

    [Fact]
    public async Task AnAttachmentOfACorpusResolvedWithItsSetsUsesTheSetsThatAreThereWhenItHoldsTheLock()
    {
        // The request resolved the corpus with its sets included, and one set was removed after that. The
        // attachment gives a chunk state to each set that is there now.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload, sets: 2);
        await using var attaching = harness.NewContext();
        var documents = harness.NewDocumentService(attaching);
        var stored = await documents.StoreAsync(new MemoryStream("one document"u8.ToArray()), "doc.txt");
        var corpus = await attaching.Corpora.Include(c => c.ChunkSets).SingleAsync();
        corpus.ChunkSets.Count.ShouldBe(2);
        await using (var removing = harness.NewContext())
            (await harness.NewConfiguration(removing)
                .RemoveChunkSetAsync(await removing.Corpora.SingleAsync(), "alt-1", default)).Refusal.ShouldBeNull();

        var attached = await documents.AttachAsync(corpus, stored.Sha256, "doc.txt");

        await using var check = harness.NewContext();
        (await check.FileChunkStates.Where(s => s.FileId == attached.Id).Select(s => s.ChunkSetId).ToListAsync())
            .ShouldBe(["set-1"]);
    }
}
