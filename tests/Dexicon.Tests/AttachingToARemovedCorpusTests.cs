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
    public async Task AnAttachmentThatWaitedBehindTheRemovalOfItsUploadSourceCreatesTheSourceAgain()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await using var attaching = harness.NewContext();
        var documents = harness.NewDocumentService(attaching);
        var stored = await documents.StoreAsync(new MemoryStream("one document"u8.ToArray()), "doc.txt");
        var corpus = await attaching.Corpora.SingleAsync();

        Task<IndexedFile> attachment;
        using (await DocumentService.HoldAttachmentsAsync().FinishesAsync("taking the lock"))
        {
            attachment = Task.Run(() => documents.AttachAsync(corpus, stored.Sha256, "doc.txt"));
            await using var removing = harness.NewContext();
            await removing.Sources.ExecuteDeleteAsync();
        }

        var attached = await attachment.FinishesAsync("the attachment");
        await using var check = harness.NewContext();
        (await check.Sources.SingleAsync()).Id.ShouldBe(attached.SourceId);
        attached.SourceId.ShouldNotBe(IndexingHarness.SourceId, "the source that was removed is not the one the file names");
    }
}
