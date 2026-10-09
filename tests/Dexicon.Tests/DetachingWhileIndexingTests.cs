using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// A document detached while a pass is indexing the corpus.
///
/// The pass reads the corpus's attachments when it starts and goes through them for as long as embedding
/// takes. A detach in that time deletes a file row, and its per-set chunk state with it, that the pass
/// still tracks. The pass's next save then wrote to rows that were gone and failed on them, again on
/// every retry of the save that records how the job ended, so the job was left Running in the catalogue
/// and the documents after the detached one were not indexed. The save now leaves out a document whose
/// row has gone, and removes the vectors the pass wrote for it.
/// </summary>
public sealed class DetachingWhileIndexingTests
{
    private const string PathOfTheFirst = "one.txt";

    private sealed record Library(IndexedFile First, IndexedFile? Second);

    private static async Task<Library> AttachAsync(IndexingHarness harness, bool two)
    {
        await using var db = harness.NewContext();
        var documents = harness.NewDocumentService(db);
        var corpus = await db.Corpora.SingleAsync();
        var one = await documents.StoreAsync(new MemoryStream("the first document"u8.ToArray()), PathOfTheFirst);
        var first = await documents.AttachAsync(corpus, one.Sha256, PathOfTheFirst);
        if (!two) return new Library(first, null);

        var other = await documents.StoreAsync(new MemoryStream("the second document"u8.ToArray()), "two.txt");
        return new Library(first, await documents.AttachAsync(corpus, other.Sha256, "two.txt"));
    }

    /// <summary>Detaches the file the first time the pass deletes a file's vectors, which is while it indexes it.</summary>
    private static void DetachWhenThePassReachesIt(IndexingHarness harness, IndexedFile file, Func<Task>? then = null)
    {
        var fired = 0;
        harness.Vectors.OnDeleteAsync = async () =>
        {
            if (Interlocked.Exchange(ref fired, 1) == 1)
            {
                if (then is not null) await then();
                return;
            }

            await using var other = harness.NewContext();
            (await harness.NewDocumentService(other).DetachAsync(IndexingHarness.CorpusId, file.Id))
                .ShouldBeTrue("the detach has to have happened during the pass");
        };
    }

    private static async Task<(IndexJob Job, IndexJob Recorded, Corpus Corpus, List<string> Files)> ReadBackAsync(
        IndexingHarness harness, IndexJob returned)
    {
        await using var db = harness.NewContext();
        return (returned,
            await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == returned.Id),
            await db.Corpora.AsNoTracking().SingleAsync(),
            await db.Files.AsNoTracking().Select(f => f.RelativePath).ToListAsync());
    }

    [Fact]
    public async Task ADocumentDetachedWhileAnotherIsIndexedDoesNotFailThePassOrLeaveTheJobRunning()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        var library = await AttachAsync(harness, two: true);
        DetachWhenThePassReachesIt(harness, library.First);

        var job = await harness.RunIndexAsync();

        var read = await ReadBackAsync(harness, job);
        read.Recorded.State.ShouldBe(JobState.Succeeded, "the outcome of the pass is recorded in the catalogue");
        read.Recorded.FinishedUtc.ShouldNotBeNull();
        read.Corpus.State.ShouldBe(CorpusState.Ready);
        read.Files.ShouldBe(["two.txt"]);
        (await harness.StateOfAsync("two.txt")).Status.ShouldBe(FileStatus.Indexed, "the document after it was indexed");
        harness.Vectors.CountFor("two.txt").ShouldBeGreaterThan(0);
        harness.Vectors.CountFor(PathOfTheFirst).ShouldBe(0, "the vectors written for the detached document are removed");
    }

    [Fact]
    public async Task ADocumentDetachedBeforeThePassReachesItIsSkippedAndNotEmbedded()
    {
        // A full pass, so that claiming each document for re-indexing is a change to save. A document new to
        // the pass already reads as pending, claiming it changes nothing, and its row is first missed later.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        var library = await AttachAsync(harness, two: true);
        await harness.RunIndexAsync();
        DetachWhenThePassReachesIt(harness, library.Second!);

        var job = await harness.RunIndexAsync(JobKind.Full);

        var read = await ReadBackAsync(harness, job);
        read.Recorded.State.ShouldBe(JobState.Succeeded);
        read.Files.ShouldBe([PathOfTheFirst]);
        harness.Vectors.CountFor(PathOfTheFirst).ShouldBeGreaterThan(0);
        harness.Vectors.CountFor("two.txt").ShouldBe(0, "the points of a document that is gone are removed, and none are written");
        read.Recorded.FilesDone.ShouldBe(1);
        read.Recorded.FilesSkipped.ShouldBe(1, "the detached document is counted as skipped");
    }

    [Fact]
    public async Task TheOnlyDocumentDetachedWhileItIsIndexedLeavesNoVectorsAndASucceededPass()
    {
        // No document follows, so the save that finds the row gone is the one that ends the source.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        var library = await AttachAsync(harness, two: false);
        DetachWhenThePassReachesIt(harness, library.First);

        var job = await harness.RunIndexAsync();

        var read = await ReadBackAsync(harness, job);
        read.Recorded.State.ShouldBe(JobState.Succeeded);
        read.Files.ShouldBeEmpty();
        harness.Vectors.CountFor(PathOfTheFirst).ShouldBe(0);
    }

    [Fact]
    public async Task VectorsThatCannotBeRemovedForADetachedDocumentAreLeftForTheNextPass()
    {
        // The vector store is unreachable for the clean-up, and the pass does not fail for it. The next pass
        // finds points for a path no row names and removes them.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        var library = await AttachAsync(harness, two: false);
        DetachWhenThePassReachesIt(harness, library.First,
            then: () => throw new InvalidOperationException("the vector store is unreachable"));

        var job = await harness.RunIndexAsync();

        (await ReadBackAsync(harness, job)).Recorded.State.ShouldBe(JobState.Succeeded);
        harness.Vectors.CountFor(PathOfTheFirst).ShouldBeGreaterThan(0, "the clean-up failed, so the points are still there");

        harness.Vectors.OnDeleteAsync = null;
        var next = await harness.RunIndexAsync();

        (await ReadBackAsync(harness, next)).Recorded.State.ShouldBe(JobState.Succeeded);
        harness.Vectors.CountFor(PathOfTheFirst).ShouldBe(0, "the next pass removes points no row names");
    }

    [Fact]
    public async Task APassRemovesTheVectorsOfADocumentThatWasDetachedBeforeItStarted()
    {
        // What a detach leaves when a pass wrote vectors after the detach had deleted them: points for a
        // path that no row names. The reconcile at the start of the next pass of the source removes them.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        var library = await AttachAsync(harness, two: false);
        await harness.RunIndexAsync();
        harness.Vectors.CountFor(PathOfTheFirst).ShouldBeGreaterThan(0);
        await using (var db = harness.NewContext())
            (await harness.NewDocumentService(db).DetachAsync(IndexingHarness.CorpusId, library.First.Id)).ShouldBeTrue();
        harness.Vectors.CountFor(PathOfTheFirst).ShouldBeGreaterThan(0, "a detach that is not the cleanup leaves the points");

        var job = await harness.RunIndexAsync();

        (await ReadBackAsync(harness, job)).Recorded.State.ShouldBe(JobState.Succeeded);
        harness.Vectors.CountFor(PathOfTheFirst).ShouldBe(0);
    }
}
