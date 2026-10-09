using Dexicon.Core.Catalog;
using Dexicon.Core.Documents;
using Dexicon.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dexicon.Tests;

/// <summary>
/// A document changed between the cleanup deleting its vectors and the detach deleting its row.
///
/// The cleanup reads the file, deletes the vectors under the name it read, and then detaches the row. An
/// attachment that renamed or replaced the document in between, followed by a pass that indexed it, left
/// vectors for a document whose row was then deleted. Nothing named them, so search returned a document that
/// had been detached until a later pass removed points for a path no row names. The cleanup now deletes
/// the vectors again for the file the detach removed.
/// </summary>
public sealed class VectorCleanupOfADetachTests
{
    private static readonly byte[] TheBytes = "the document as it was first attached"u8.ToArray();

    /// <summary>
    /// Attaches and indexes "a.txt", then runs the cleanup with <paramref name="meanwhile"/> acting once the
    /// cleanup has deleted the vectors it read the name for. Returns whether the cleanup detached it.
    /// </summary>
    private static async Task<bool> DetachWhileAsync(
        IndexingHarness harness, Func<DocumentService, Corpus, Task> meanwhile)
    {
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await using var db = harness.NewContext();
        var documents = harness.NewDocumentService(db);
        var corpus = await db.Corpora.SingleAsync();
        var stored = await documents.StoreAsync(new MemoryStream(TheBytes), "a.txt");
        var attached = await documents.AttachAsync(corpus, stored.Sha256, "a.txt");
        await harness.RunIndexAsync();
        harness.Vectors.CountFor("a.txt").ShouldBeGreaterThan(0, "there are vectors to clean up");

        var fired = 0;
        harness.Vectors.AfterFileDeleteAsync = async () =>
        {
            // The pass inside this runs deletes of its own.
            if (Interlocked.Exchange(ref fired, 1) == 1) return;

            await using var other = harness.NewContext();
            await meanwhile(harness.NewDocumentService(other), await other.Corpora.SingleAsync());
            await harness.RunIndexAsync();
        };

        var removed = await new VectorStoreCleanup(db, harness.Vectors, NullLogger<VectorStoreCleanup>.Instance)
            .RemoveAttachmentAsync(corpus, attached.Id, documents, CancellationToken.None);

        fired.ShouldBe(1, "the change has to have happened between the cleanup's two steps");
        return removed;
    }

    [Fact]
    public async Task VectorsWrittenUnderTheNewNameOfADocumentRenamedDuringTheCleanupAreDeleted()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");

        var removed = await DetachWhileAsync(harness, async (documents, corpus) =>
        {
            var stored = await documents.StoreAsync(new MemoryStream(TheBytes), "a.txt");
            await documents.AttachAsync(corpus, stored.Sha256, "b.txt");
        });

        removed.ShouldBeTrue();
        await using var check = harness.NewContext();
        (await check.Files.CountAsync()).ShouldBe(0, "the document is detached");
        harness.Vectors.CountFor("b.txt").ShouldBe(0, "the pass indexed it under the new name before the row went");
        harness.Vectors.CountFor("a.txt").ShouldBe(0);
    }

    [Fact]
    public async Task VectorsWrittenForNewBytesOfADocumentReplacedDuringTheCleanupAreDeleted()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");

        var removed = await DetachWhileAsync(harness, async (documents, corpus) =>
        {
            var stored = await documents.StoreAsync(new MemoryStream("the replacement"u8.ToArray()), "replacement.txt");
            await documents.AttachAsync(corpus, stored.Sha256, "a.txt");
        });

        removed.ShouldBeTrue();
        await using var check = harness.NewContext();
        (await check.Files.CountAsync()).ShouldBe(0);
        harness.Vectors.CountFor("a.txt").ShouldBe(0, "the pass indexed the new bytes before the row went");
    }

    [Fact]
    public async Task VectorsWrittenByAPassForTheSameDocumentDuringTheCleanupAreDeleted()
    {
        // Nothing about the document changes. The pass finds its points gone, which it reads as damage,
        // and writes them again before the row is deleted.
        await using var harness = await IndexingHarness.StartAsync("notes");

        var removed = await DetachWhileAsync(harness, (_, _) => Task.CompletedTask);

        removed.ShouldBeTrue();
        await using var check = harness.NewContext();
        (await check.Files.CountAsync()).ShouldBe(0);
        harness.Vectors.CountFor("a.txt").ShouldBe(0);
    }

    [Fact]
    public async Task ADeleteOfTheVectorsThatFailsAfterTheRowIsGoneDoesNotUndoTheDetach()
    {
        // The vector store goes away between the cleanup's two deletes. The row is deleted by then, and the
        // call answers that it was detached: the points left are removed by a later pass.
        await using var harness = await IndexingHarness.StartAsync("notes");

        var removed = await DetachWhileAsync(harness, (_, _) =>
        {
            harness.Vectors.DeletesThrow = true;
            return Task.CompletedTask;
        });

        removed.ShouldBeTrue();
        await using var check = harness.NewContext();
        (await check.Files.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task ADocumentThatIsNotThereIsNotDetachedAndTheCleanupDeletesNothing()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await using var db = harness.NewContext();
        var deletes = 0;
        harness.Vectors.AfterFileDeleteAsync = () =>
        {
            Interlocked.Increment(ref deletes);
            return Task.CompletedTask;
        };

        var removed = await new VectorStoreCleanup(db, harness.Vectors, NullLogger<VectorStoreCleanup>.Instance).RemoveAttachmentAsync(
            await db.Corpora.SingleAsync(), "no-such-file", harness.NewDocumentService(db), CancellationToken.None);

        removed.ShouldBeFalse();
        deletes.ShouldBe(0);
    }
}
