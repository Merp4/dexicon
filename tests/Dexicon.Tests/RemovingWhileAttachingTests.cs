using Dexicon.Api;
using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// Removing a source, a chunk set or a corpus while a document is being attached to it.
///
/// An attachment looks for the corpus's upload source and for the document's file, loads the corpus's chunk
/// sets, and adds a file row that names the source and a chunk state for each set. A removal that deleted
/// one of them between the lookup and the save made the save fail on a foreign key. The removals take the
/// lock attaching holds for their catalogue delete, so the second of the two waits for the first.
/// </summary>
[Collection(nameof(AttachmentLockCollection))]
public sealed class RemovingWhileAttachingTests
{
    /// <summary>
    /// How long a removal is given to finish while an attachment holds the lock. A removal that is blocked
    /// stays blocked however slow the runner is, so the wait cannot turn a pass into a failure. It only has
    /// to be long enough that a removal that is not blocked finishes inside it. Whole runs of these tests, setup
    /// included, took 0.25 to 0.6 s on this machine while it was loaded, so 3 s is five times that.
    /// </summary>
    private static readonly TimeSpan HowLongToWait = TimeSpan.FromSeconds(3);

    private static bool IsAFileLookup(string sql) =>
        sql.Contains("FROM \"files\"", StringComparison.Ordinal)
        && sql.Contains("LIMIT 1", StringComparison.Ordinal);

    public enum Removal { Source, ChunkSet, Corpus }

    private static Task<ConfigOutcome<bool>> RemoveAsync(
        CorpusConfiguration config, Corpus corpus, Removal what) =>
        what switch
        {
            Removal.Source => config.RemoveSourceAsync(corpus, IndexingHarness.SourceId, default),
            Removal.ChunkSet => config.RemoveChunkSetAsync(corpus, "alt-1", default),
            _ => config.RemoveCorpusAsync(corpus, default),
        };

    private static int SetsFor(Removal what) => what == Removal.ChunkSet ? 2 : 1;

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

        try
        {
            await Gates.ReachedAsync(hold.Reached, "the held file lookup");
            var removal = RemoveAsync(harness.NewConfiguration(removing), await removing.Corpora.SingleAsync(), Removal.Source);

            (await removal.FinishesAsync("the removal beside a held lookup")).Refusal.ShouldBeNull();
        }
        finally
        {
            hold.Release();
            await lookup.FinishesAsync("the held file lookup");
        }
    }

    [Theory]
    [InlineData(Removal.Source)]
    [InlineData(Removal.ChunkSet)]
    [InlineData(Removal.Corpus)]
    public async Task ARemovalWaitsForAnAttachmentThatHasLookedForItsFileAndTheAttachmentIsSaved(Removal what)
    {
        var hold = new HoldOneLookup(IsAFileLookup);
        await using var harness = await IndexingHarness.StartAsync(hold, "notes");
        await harness.SeedCorpusAsync(SourceKind.Upload, sets: SetsFor(what));
        await using var attaching = harness.NewContext();
        await using var removing = harness.NewContext();
        var documents = harness.NewDocumentService(attaching);
        var stored = await documents.StoreAsync(new MemoryStream("one document"u8.ToArray()), "doc.txt");
        var corpus = await attaching.Corpora.SingleAsync();
        hold.Armed = true;

        var attachment = Task.Run(() => documents.AttachAsync(corpus, stored.Sha256, "doc.txt"));
        Task<ConfigOutcome<bool>> removal;
        bool removedWhileHeld;
        try
        {
            await Gates.ReachedAsync(hold.Reached, "the attachment's file lookup");
            removal = RemoveAsync(harness.NewConfiguration(removing), await removing.Corpora.SingleAsync(), what);
            removedWhileHeld = await Task.WhenAny(removal, Task.Delay(HowLongToWait)) == removal;
        }
        finally { hold.Release(); }

        // The attachment is awaited first: its failure, if any, is what the test is about.
        var attached = await attachment.FinishesAsync("the attachment");
        (await removal.FinishesAsync("the removal")).Refusal.ShouldBeNull();

        removedWhileHeld.ShouldBeFalse("the removal ran inside the attachment's window");
        attached.SourceId.ShouldBe(IndexingHarness.SourceId);
        await using var check = harness.NewContext();
        if (what == Removal.ChunkSet)
        {
            (await check.Files.CountAsync()).ShouldBe(1, "removing a chunk set keeps the document");
            (await check.ChunkSets.CountAsync()).ShouldBe(1);
            (await check.FileChunkStates.CountAsync()).ShouldBe(1, "a chunk state for the set that is left");
        }
        else
        {
            (await check.Files.CountAsync()).ShouldBe(0, "the removal then took the attached document with it");
            (await check.Sources.CountAsync()).ShouldBe(0);
        }
    }

    [Theory]
    [InlineData(Removal.Source)]
    [InlineData(Removal.ChunkSet)]
    [InlineData(Removal.Corpus)]
    public async Task AnAttachmentIsNotHeldBackWhileARemovalDeletesVectors(Removal what)
    {
        // The lock is for the catalogue delete. A removal that held it across the vector store would stop
        // every attachment for as long as the store took to answer.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload, sets: SetsFor(what));
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
        try
        {
            await Gates.ReachedAsync(deleting.Task, "the removal's vector delete");

            await documents.AttachAsync(corpus, second.Sha256, "two.txt")
                .FinishesAsync("the attachment while the removal waits on the vector store");
        }
        finally { letGo.TrySetResult(); }

        (await removal.FinishesAsync("the removal")).Refusal.ShouldBeNull();
    }

    [Theory]
    [InlineData(Removal.Source)]
    [InlineData(Removal.Corpus)]
    public async Task TwoRemovalsOfOneTargetAtTheSameTimeAnswerTheLaterAsGone(Removal what)
    {
        // Both have loaded the target and deleted its vectors before either has deleted its row. The one
        // that takes the lock second finds the row gone, and says so as a later request would.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await using var first = harness.NewContext();
        await using var second = harness.NewContext();
        var documents = harness.NewDocumentService(first);
        var stored = await documents.StoreAsync(new MemoryStream("one document"u8.ToArray()), "doc.txt");
        await documents.AttachAsync(await first.Corpora.SingleAsync(), stored.Sha256, "doc.txt");
        await harness.RunIndexAsync();

        var arrived = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Vectors.OnDeleteAsync = async () =>
        {
            if (Interlocked.Increment(ref arrived) >= 2) both.TrySetResult();
            await Gates.ReachedAsync(both.Task, "the second removal's vector delete");
        };

        var outcomes = await Task.WhenAll(
                RemoveAsync(harness.NewConfiguration(first), await first.Corpora.SingleAsync(), what),
                RemoveAsync(harness.NewConfiguration(second), await second.Corpora.SingleAsync(), what))
            .FinishesAsync("the two removals");

        arrived.ShouldBe(2, "both removals have to have deleted vectors before either deleted its row");
        outcomes.Count(o => o.Refusal is null).ShouldBe(1, "one removal succeeds");
        outcomes.Single(o => o.Refusal is not null).Refusal!.Status.ShouldBe(404);
    }
}
