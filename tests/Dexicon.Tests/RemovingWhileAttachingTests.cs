using Dexicon.Api;
using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// Removing a source or a corpus while a document is being attached to it.
///
/// An attachment looks for the corpus's upload source and for the document's file, and adds a file row
/// that names the source. A removal that deleted the source between the lookup and the save left the save
/// naming a row that was gone, and it failed on the foreign key. Both removals now take the lock
/// attaching holds for their catalogue delete, so the second of the two waits for the first.
/// </summary>
public sealed class RemovingWhileAttachingTests
{
    private static readonly TimeSpan HowLongToWait = TimeSpan.FromSeconds(1.5);

    private static bool IsAFileLookup(string sql) =>
        sql.Contains("FROM \"files\"", StringComparison.Ordinal)
        && sql.Contains("LIMIT 1", StringComparison.Ordinal);

    public enum Removal { Source, Corpus }

    private static Task<ConfigOutcome<bool>> RemoveAsync(
        CorpusConfiguration config, Corpus corpus, Removal what) =>
        what == Removal.Source
            ? config.RemoveSourceAsync(corpus, IndexingHarness.SourceId, default)
            : config.RemoveCorpusAsync(corpus, default);

    [Fact]
    public async Task ARemovalIsNotHeldBackByAnAttachmentLookupThatNothingSerialises()
    {
        // The control for the tests below: a file lookup that is held outside the attachment lock does
        // not stop a removal, so a removal that waits in the tests below waits on the lock.
        var hold = new HoldOneLookup(IsAFileLookup);
        await using var harness = await IndexingHarness.StartAsync(hold, "notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await using var looking = harness.NewContext();
        await using var removing = harness.NewContext();
        hold.Armed = true;
        var lookup = looking.Files.FirstOrDefaultAsync(f => f.SourceId == IndexingHarness.SourceId);
        await hold.Reached;

        try
        {
            var removal = RemoveAsync(harness.NewConfiguration(removing), await removing.Corpora.SingleAsync(), Removal.Source);

            (await Task.WhenAny(removal, Task.Delay(HowLongToWait))).ShouldBeSameAs(removal,
                "nothing but the attachment lock makes a removal wait for a held lookup");
            (await removal).Refusal.ShouldBeNull();
        }
        finally
        {
            hold.Release();
            await lookup;
        }
    }

    [Theory]
    [InlineData(Removal.Source)]
    [InlineData(Removal.Corpus)]
    public async Task ARemovalWaitsForAnAttachmentThatHasLookedForItsFileAndTheAttachmentIsSaved(Removal what)
    {
        var hold = new HoldOneLookup(IsAFileLookup);
        await using var harness = await IndexingHarness.StartAsync(hold, "notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await using var attaching = harness.NewContext();
        await using var removing = harness.NewContext();
        var documents = harness.NewDocumentService(attaching);
        var stored = await documents.StoreAsync(new MemoryStream("one document"u8.ToArray()), "doc.txt");
        var corpus = await attaching.Corpora.SingleAsync();
        hold.Armed = true;

        var attachment = Task.Run(() => documents.AttachAsync(corpus, stored.Sha256, "doc.txt"));
        await hold.Reached;

        Task<ConfigOutcome<bool>> removal;
        bool removedWhileHeld;
        try
        {
            removal = RemoveAsync(harness.NewConfiguration(removing), await removing.Corpora.SingleAsync(), what);
            removedWhileHeld = await Task.WhenAny(removal, Task.Delay(HowLongToWait)) == removal;
        }
        finally { hold.Release(); }

        // The attachment is awaited first: its failure, if any, is what the test is about.
        var attached = await attachment;
        (await removal).Refusal.ShouldBeNull();

        removedWhileHeld.ShouldBeFalse("the removal ran inside the attachment's window");
        attached.SourceId.ShouldBe(IndexingHarness.SourceId);
        await using var check = harness.NewContext();
        (await check.Files.CountAsync()).ShouldBe(0, "the removal then took the attached document with it");
        (await check.Sources.CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(Removal.Source)]
    [InlineData(Removal.Corpus)]
    public async Task AnAttachmentIsNotHeldBackWhileARemovalDeletesVectors(Removal what)
    {
        // The lock is for the catalogue delete. A removal that held it across the vector store would stop
        // every attachment for as long as the store took to answer.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await using var attaching = harness.NewContext();
        await using var removing = harness.NewContext();
        var documents = harness.NewDocumentService(attaching);
        var first = await documents.StoreAsync(new MemoryStream("the first document"u8.ToArray()), "one.txt");
        var second = await documents.StoreAsync(new MemoryStream("the second document"u8.ToArray()), "two.txt");
        var corpus = await attaching.Corpora.SingleAsync();
        await documents.AttachAsync(corpus, first.Sha256, "one.txt");
        await harness.RunIndexAsync();
        harness.Vectors.CountFor("one.txt").ShouldBeGreaterThan(0, "the removal has vectors to delete");

        var deleting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var letGo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Vectors.OnDeleteAsync = async () =>
        {
            deleting.TrySetResult();
            await letGo.Task;
        };

        var removal = Task.Run(async () =>
            await RemoveAsync(harness.NewConfiguration(removing), await removing.Corpora.SingleAsync(), what));
        await deleting.Task;

        try
        {
            var attachment = documents.AttachAsync(corpus, second.Sha256, "two.txt");

            (await Task.WhenAny(attachment, Task.Delay(TimeSpan.FromSeconds(10)))).ShouldBeSameAs(attachment,
                "the removal is waiting on the vector store and holds nothing an attachment needs");
            await attachment;
        }
        finally { letGo.TrySetResult(); }

        (await removal).Refusal.ShouldBeNull();
    }
}
