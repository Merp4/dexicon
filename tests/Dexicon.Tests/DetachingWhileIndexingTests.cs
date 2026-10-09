using Dexicon.Core.Catalog;
using Dexicon.Core.Embedding;
using Dexicon.Core.Indexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dexicon.Tests;

/// <summary>
/// A document detached while a pass is indexing the corpus.
///
/// The pass reads the corpus's attachments when it starts and tracks them for as long as embedding takes.
/// A detach in that time deletes a file row, and its per-set chunk state with it, that the pass still
/// holds changes for. The saves that insert chunk states, claim a document, flush after a document and end
/// the source leave the document out, delete the vectors the pass wrote for it and count it as skipped. The
/// count reconcile at the start and the save that records how the job ended only leave it out, and a later
/// pass removes its points.
/// </summary>
[Collection(nameof(AttachmentLockCollection))]
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

    private static async Task DetachNowAsync(IndexingHarness harness, IndexedFile file)
    {
        await using var other = harness.NewContext();
        (await harness.NewDocumentService(other).DetachAsync(IndexingHarness.CorpusId, file.Id))
            .ShouldBeTrue("the detach has to have happened during the pass");
    }

    /// <summary>Runs <paramref name="act"/> once, as the pass embeds the first document that reaches it.</summary>
    private sealed class EmbedderThatActsOnce(Func<Task> act) : IEmbeddingService
    {
        private readonly IndexingHarness.FixedEmbedder _inner = new();
        private int _acted;

        public async Task<IReadOnlyList<float[]>> EmbedAsync(EmbeddingTarget target, EmbedPurpose purpose,
            IReadOnlyList<string> inputs, string? source = null, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _acted, 1) == 0) await act();
            return await _inner.EmbedAsync(target, purpose, inputs, source, ct);
        }

        public Task<int?> CountTokensAsync(EmbeddingTarget t, string text, CancellationToken ct = default) =>
            _inner.CountTokensAsync(t, text, ct);

        public Task<int> ProbeDimensionsAsync(EmbeddingTarget t, CancellationToken ct = default) =>
            _inner.ProbeDimensionsAsync(t, ct);

        public int KnownDimensions(EmbeddingTarget t) => _inner.KnownDimensions(t);
    }

    [Fact]
    public async Task ADocumentDetachedBeforeTheFirstSaveOfItsChunkStateIsDropped()
    {
        // The pass inserts the chunk state of a document that has none. The document is detached after the
        // pass read it and before that insert, which then names a file row that is gone.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        var library = await AttachAsync(harness, two: false);
        await using (var db = harness.NewContext())
            await db.FileChunkStates.ExecuteDeleteAsync();
        var fired = 0;
        harness.Vectors.OnCountAsync = async () =>
        {
            if (Interlocked.Exchange(ref fired, 1) == 0) await DetachNowAsync(harness, library.First);
        };

        var job = await harness.RunIndexAsync();

        var read = await ReadBackAsync(harness, job);
        read.Recorded.State.ShouldBe(JobState.Succeeded);
        read.Recorded.FinishedUtc.ShouldNotBeNull();
        read.Files.ShouldBeEmpty();
        read.Recorded.FilesSkipped.ShouldBe(1, "the detached document is counted as skipped");
    }

    [Fact]
    public async Task ADocumentDetachedBeforeTheCountReconcileSavesItsStateIsDropped()
    {
        // The reconcile at the start of the pass clears the hash of a document whose points are missing and
        // saves it. The document is detached between the pass reading it and that save.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        var library = await AttachAsync(harness, two: false);
        await harness.RunIndexAsync();
        harness.Vectors.DropSilently(PathOfTheFirst).ShouldBeGreaterThan(0);
        var fired = 0;
        harness.Vectors.OnCountAsync = async () =>
        {
            if (Interlocked.Exchange(ref fired, 1) == 0) await DetachNowAsync(harness, library.First);
        };

        var job = await harness.RunIndexAsync();

        var read = await ReadBackAsync(harness, job);
        read.Recorded.State.ShouldBe(JobState.Succeeded);
        read.Recorded.FinishedUtc.ShouldNotBeNull();
        read.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task ADetachFollowedByACancelStillRecordsHowTheJobEnded()
    {
        // The document is detached after the pass finished with it and before any save of its changes, and
        // the pass is then cancelled. Its changes are still tracked when the outcome of the job is saved.
        using var stop = new CancellationTokenSource();
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        var library = await AttachAsync(harness, two: false);
        harness.Embedder = new EmbedderThatActsOnce(async () =>
        {
            await DetachNowAsync(harness, library.First);
            await stop.CancelAsync();
        });

        var job = await harness.RunIndexAsync(cancel: stop.Token);

        var read = await ReadBackAsync(harness, job);
        read.Recorded.State.ShouldBe(JobState.Cancelled, "the outcome is saved although the pass held changes for a detached document");
        read.Recorded.FinishedUtc.ShouldNotBeNull();
        read.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task ADocumentDetachedAfterItWasEmbeddedIsNotCountedAsIndexed()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        var library = await AttachAsync(harness, two: true);
        harness.Embedder = new EmbedderThatActsOnce(() => DetachNowAsync(harness, library.First));

        var job = await harness.RunIndexAsync();

        var read = await ReadBackAsync(harness, job);
        read.Recorded.State.ShouldBe(JobState.Succeeded);
        read.Files.ShouldBe(["two.txt"]);
        read.Recorded.FilesDone.ShouldBe(1, "the second document");
        read.Recorded.FilesSkipped.ShouldBe(1, "the first, which was detached after it was embedded");
        read.Recorded.FilesFailed.ShouldBe(0);
        read.Recorded.FilesTotal.ShouldBe(read.Recorded.FilesDone + read.Recorded.FilesSkipped + read.Recorded.FilesFailed);
    }

    [Fact]
    public async Task TheClaimOfTheNextDocumentIsSavedAfterADroppedDocumentIsLeftOut()
    {
        // The claim of the second document is the save that finds the first one gone. It has to be saved
        // once the first is left out, or the second is deleted and re-embedded while the catalogue still
        // describes its old vectors.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        var library = await AttachAsync(harness, two: true);
        await harness.RunIndexAsync();

        var deletes = 0;
        FileStatus? statusWhenTheSecondIsDeleted = null;
        harness.Vectors.OnDeleteAsync = async () =>
        {
            switch (Interlocked.Increment(ref deletes))
            {
                case 1:
                    // The first document's own delete: it is detached now.
                    await DetachNowAsync(harness, library.First);
                    break;
                case 3:
                    // Call 2 is the removal of the first document's points. This is the second document's
                    // delete, which follows its claim.
                    statusWhenTheSecondIsDeleted = (await harness.StateOfAsync("two.txt")).Status;
                    break;
            }
        };

        var job = await harness.RunIndexAsync(JobKind.Full);

        deletes.ShouldBeGreaterThanOrEqualTo(3);
        statusWhenTheSecondIsDeleted.ShouldBe(FileStatus.Pending, "its claim was saved before its points were deleted");
        (await ReadBackAsync(harness, job)).Recorded.State.ShouldBe(JobState.Succeeded);
        (await harness.StateOfAsync("two.txt")).Status.ShouldBe(FileStatus.Indexed);
    }

    [Fact]
    public async Task AChunkStateDeletedOnItsOwnFailsThePassAndKeepsTheDocumentAndItsVectors()
    {
        // Only the chunk state is gone: the document is still attached, so this is not a detach. The pass
        // fails on it, the job is recorded as failed with the error, and nothing of the document is deleted.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        var library = await AttachAsync(harness, two: false);
        var fired = 0;
        harness.Vectors.OnDeleteAsync = async () =>
        {
            if (Interlocked.Exchange(ref fired, 1) == 1) return;
            await using var other = harness.NewContext();
            await other.FileChunkStates.Where(s => s.FileId == library.First.Id).ExecuteDeleteAsync();
        };

        var job = await harness.RunIndexAsync();

        var read = await ReadBackAsync(harness, job);
        read.Recorded.State.ShouldBe(JobState.Failed);
        read.Recorded.FinishedUtc.ShouldNotBeNull();
        read.Recorded.Error.ShouldNotBeNull().ShouldContain("removed while the pass ran");
        read.Recorded.Error.ShouldNotContain("fwlink", Case.Insensitive, "the details of the save are in the log");
        read.Files.ShouldBe([PathOfTheFirst], "the document is still attached");
        harness.Vectors.CountFor(PathOfTheFirst).ShouldBeGreaterThan(0, "its vectors are kept");
    }

    [Fact]
    public async Task ACancelWhileTheVectorsOfADroppedDocumentAreDeletedStillRecordsHowTheJobEnded()
    {
        // Both documents are detached. The save that claims the second finds the first one gone, and the
        // cleanup of the first one's points is cancelled, which ends the pass. The second document is still
        // tracked with a pending change for a row that is gone, and the outcome save drops it.
        using var stop = new CancellationTokenSource();
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        var library = await AttachAsync(harness, two: true);
        await harness.RunIndexAsync();

        var deletes = 0;
        harness.Vectors.OnDeleteAsync = async () =>
        {
            switch (Interlocked.Increment(ref deletes))
            {
                case 1:
                    await DetachNowAsync(harness, library.First);
                    await DetachNowAsync(harness, library.Second!);
                    break;
                case 2:
                    await stop.CancelAsync();
                    throw new OperationCanceledException(stop.Token);
            }
        };

        var job = await harness.RunIndexAsync(JobKind.Full, cancel: stop.Token);

        deletes.ShouldBe(2, "the pass ended at the cleanup of the first document's points");
        var read = await ReadBackAsync(harness, job);
        read.Recorded.State.ShouldBe(JobState.Cancelled);
        read.Recorded.FinishedUtc.ShouldNotBeNull();
        read.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task ADetachedDocumentWhoseTextIsMissingIsCountedAsSkippedAndNotFailed()
    {
        // The document is left out by the insert of chunk states, and the pass then goes through it. With no
        // text stored for it, indexing it would count it as failed.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        var library = await AttachAsync(harness, two: false);
        await using (var db = harness.NewContext())
        {
            await db.FileChunkStates.ExecuteDeleteAsync();
            await db.BlobTexts.ExecuteDeleteAsync();
        }

        var fired = 0;
        harness.Vectors.OnCountAsync = async () =>
        {
            if (Interlocked.Exchange(ref fired, 1) == 0) await DetachNowAsync(harness, library.First);
        };

        var job = await harness.RunIndexAsync();

        var read = await ReadBackAsync(harness, job);
        read.Recorded.State.ShouldBe(JobState.Succeeded);
        (read.Recorded.FilesSkipped, read.Recorded.FilesFailed).ShouldBe((1, 0));
    }

    [Fact]
    public async Task TwoDocumentsDetachedBeforeTheInsertOfTheirStatesAreBothDropped()
    {
        // A failed save names one file, so the second is dropped by the round after the first.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        var library = await AttachAsync(harness, two: true);
        await harness.RunIndexAsync();
        harness.Vectors.CountFor(PathOfTheFirst).ShouldBeGreaterThan(0);
        harness.Vectors.CountFor("two.txt").ShouldBeGreaterThan(0);
        await using (var db = harness.NewContext())
            await db.FileChunkStates.ExecuteDeleteAsync();
        var fired = 0;
        harness.Vectors.OnCountAsync = async () =>
        {
            if (Interlocked.Exchange(ref fired, 1) != 0) return;
            await DetachNowAsync(harness, library.First);
            await DetachNowAsync(harness, library.Second!);
        };

        var job = await harness.RunIndexAsync();

        var read = await ReadBackAsync(harness, job);
        read.Recorded.State.ShouldBe(JobState.Succeeded);
        read.Files.ShouldBeEmpty();
        harness.Vectors.CountFor(PathOfTheFirst).ShouldBe(0);
        harness.Vectors.CountFor("two.txt").ShouldBe(0);
        (read.Recorded.FilesDone, read.Recorded.FilesSkipped, read.Recorded.FilesFailed, read.Recorded.FilesTotal)
            .ShouldBe((0, 2, 0, 2));
    }

    [Fact]
    public async Task ADocumentThatFailedToEmbedAndWasDetachedIsDroppedByTheFlushAfterItAndCountedAsSkipped()
    {
        // The pass spends over a second on the document before it fails, so the flush after the document
        // saves the failure the pass holds for a row that is gone. The embedding failure of the job stays.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        var library = await AttachAsync(harness, two: true);
        harness.Embedder = new EmbedderThatActsOnce(async () =>
        {
            await DetachNowAsync(harness, library.First);
            await Task.Delay(TimeSpan.FromMilliseconds(1200));
            throw new EmbeddingUnavailableException("the provider is away");
        });

        var job = await harness.RunIndexAsync();

        var read = await ReadBackAsync(harness, job);
        read.Recorded.State.ShouldBe(JobState.Degraded, "the embedding failure is the job's, and the pass went on");
        read.Files.ShouldBe(["two.txt"]);
        (read.Recorded.FilesDone, read.Recorded.FilesSkipped, read.Recorded.FilesFailed).ShouldBe((1, 1, 0));
    }

    [Fact]
    public async Task AChunkSetRemovedUnderAPassStillRecordsTheJobAsFailed()
    {
        // The pass holds the set and changes its state. The set is deleted before the pass reaches it, so
        // the save names a chunk set and not a file, and the outcome of the job is still recorded.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload, sets: 2);
        await AttachAsync(harness, two: false);
        var ensured = 0;
        harness.Vectors.OnEnsureCollection = () =>
        {
            if (Interlocked.Increment(ref ensured) != 2) return;
            using var other = harness.NewContext();
            other.ChunkSets.Where(s => s.Id == "set-2").ExecuteDelete();
        };

        var job = await harness.RunIndexAsync();

        var read = await ReadBackAsync(harness, job);
        read.Recorded.State.ShouldBe(JobState.Failed);
        read.Recorded.FinishedUtc.ShouldNotBeNull();
        read.Recorded.Error.ShouldNotBeNull().ShouldContain("removed while the pass ran");
        read.Files.ShouldBe([PathOfTheFirst]);
    }

    [Fact]
    public async Task AJobWhoseOwnRowWasDeletedIsNotRecordedAndIsNotRetried()
    {
        // Dropping stale entries is for the rows a pass held. The job's own row is what the outcome is saved
        // to, and when it is gone there is nowhere to record it and nothing to wait for.
        var logs = new RecordingLoggerFactory();
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await AttachAsync(harness, two: false);
        var fired = 0;
        harness.Vectors.OnEnsureCollection = () =>
        {
            if (Interlocked.Exchange(ref fired, 1) != 0) return;
            using var other = harness.NewContext();
            other.Jobs.ExecuteDelete();
        };
        var started = System.Diagnostics.Stopwatch.StartNew();

        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => harness.RunIndexAsync(
            log: logs.CreateLogger<CorpusIndexer>(), saveRetryDelay: TimeSpan.FromSeconds(1)));

        started.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(8), "the retries wait 1, 2, 4 and 8 s");
        logs.Lines.ShouldNotContain(l => l.Contains("trying again", StringComparison.Ordinal));
        logs.Lines.ShouldContain(l => l.Contains("Gave up recording", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUploadSourceDeletedUnderAPassDropsItsDocumentsAndTheJobSucceeds()
    {
        // The rows of the source's documents go with it, and the pass leaves them out as detached documents.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await AttachAsync(harness, two: true);
        var fired = 0;
        harness.Vectors.OnDeleteAsync = async () =>
        {
            if (Interlocked.Exchange(ref fired, 1) != 0) return;
            await using var other = harness.NewContext();
            await other.Sources.ExecuteDeleteAsync();
        };

        var job = await harness.RunIndexAsync();

        var read = await ReadBackAsync(harness, job);
        read.Recorded.State.ShouldBe(JobState.Succeeded);
        read.Files.ShouldBeEmpty();
        (read.Recorded.FilesDone, read.Recorded.FilesFailed).ShouldBe((0, 0));
    }
}
