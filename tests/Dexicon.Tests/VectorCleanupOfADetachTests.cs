using Dexicon.Core.Catalog;
using Dexicon.Core.Documents;
using Dexicon.Infrastructure;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dexicon.Tests;

/// <summary>
/// A document changed between the cleanup deleting its vectors and the detach deleting its row.
///
/// The cleanup reads the file, deletes the vectors under the name it read, and then detaches the row. An
/// attachment that renames or replaces the document in between, followed by a pass that indexes it, writes
/// vectors for a document whose row is then deleted, and nothing names them. The cleanup deletes the
/// vectors again for the file the detach removed.
/// </summary>
[Collection(nameof(AttachmentLockCollection))]
public sealed class VectorCleanupOfADetachTests
{
    private static readonly byte[] TheBytes = "the document as it was first attached"u8.ToArray();

    /// <summary>
    /// Attaches and indexes "a.txt", then runs the cleanup with <paramref name="meanwhile"/> acting once the
    /// cleanup has deleted the vectors it read the name for. Returns whether the cleanup detached it.
    /// </summary>
    private static async Task<bool> DetachWhileAsync(
        IndexingHarness harness, Func<DocumentService, Corpus, Task> meanwhile,
        CancellationToken caller = default)
    {
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await using var db = harness.NewContext();
        var documents = harness.NewDocumentService(db);
        var corpus = await db.Corpora.SingleAsync(CancellationToken.None);
        var stored = await documents.StoreAsync(new MemoryStream(TheBytes), "a.txt", CancellationToken.None);
        var attached = await documents.AttachAsync(corpus, stored.Sha256, "a.txt", CancellationToken.None);
        await harness.RunIndexAsync(cancel: CancellationToken.None);
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
            .RemoveAttachmentAsync(corpus, attached.Id, documents, caller);

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

    /// <summary>Cancels the caller's token once a save that deleted a file row has committed.</summary>
    private sealed class CancelWhenAFileRowIsDeleted(CancellationTokenSource caller) : SaveChangesInterceptor
    {
        private int _deleting;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<IndexedFile>().Any(e => e.State == EntityState.Deleted))
                Volatile.Write(ref _deleting, 1);

            return ValueTask.FromResult(result);
        }

        public override async ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _deleting, 0) == 1) await caller.CancelAsync();
            return result;
        }
    }

    [Fact]
    public async Task TheDeleteAfterTheRowIsGoneRunsWhenTheCallerHasGoneAndDoesNotUseItsToken()
    {
        // The caller leaves once the row is deleted. The points written meanwhile are still deleted, with a
        // token of the cleanup's own.
        using var caller = new CancellationTokenSource();
        var leaving = new CancelWhenAFileRowIsDeleted(caller);
        await using var harness = await IndexingHarness.StartAsync(leaving, "notes");

        var removed = await DetachWhileAsync(harness, (_, _) => Task.CompletedTask, caller.Token);

        removed.ShouldBeTrue();
        caller.IsCancellationRequested.ShouldBeTrue("the caller left after the row was deleted");
        var tokens = harness.Vectors.DeleteTokens;
        tokens.Count.ShouldBeGreaterThanOrEqualTo(2);
        // The last delete is the cleanup's own, after the row. The ones before it include the passes' and the first.
        tokens[^1].IsCancellationRequested.ShouldBeFalse("the delete after the row is not");
        tokens[^1].ShouldNotBe(caller.Token);
    }

    /// <summary>The cleanup of one attached document, with the store and the log a test wants to watch.</summary>
    private static async Task<bool> CleanUpAsync(IndexingHarness harness, ILoggerFactory logs, string fileName,
        TimeSpan? timeout = null)
    {
        await using var db = harness.NewContext();
        var documents = harness.NewDocumentService(db);
        var corpus = await db.Corpora.SingleAsync();
        // A name that breaks the rule for names, a line break among them, can only belong to a document stored
        // before the rule, so it is given to the row after the attachment.
        var storedAs = DocumentService.FileNameProblem(fileName) is null ? fileName : "legacy.txt";
        var stored = await documents.StoreAsync(new MemoryStream(TheBytes), storedAs);
        var attached = await documents.AttachAsync(corpus, stored.Sha256, storedAs);
        if (storedAs != fileName)
        {
            attached.RelativePath = fileName;
            await db.SaveChangesAsync();
        }

        var cleanup = new VectorStoreCleanup(db, harness.Vectors, logs.CreateLogger<VectorStoreCleanup>())
        {
            SecondDeleteTimeout = timeout ?? TimeSpan.FromMinutes(5),
        };

        return await cleanup.RemoveAttachmentAsync(corpus, attached.Id, documents, CancellationToken.None)
            .FinishesAsync("the cleanup");
    }

    [Fact]
    public void TheDeletesAfterTheRowAreGivenThirtySecondsByDefault()
    {
        var cleanup = new VectorStoreCleanup(null!, null!, NullLogger<VectorStoreCleanup>.Instance);

        cleanup.SecondDeleteTimeout.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADeleteAfterTheRowIsGoneThatDoesNotAnswerIsGivenUpAndTheOtherSetsAreNotTried(bool throwsRpcCancelled)
    {
        // A gRPC client that is not set to throw OperationCanceledException reports a cancelled call as an
        // RpcException, so the timeout is recognised by the token and not by the exception type.
        var logs = new RecordingLoggerFactory();
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload, sets: 2);
        harness.Vectors.OnFileDeleteTokenAsync = async (token, deletes) =>
        {
            if (deletes != 3) return;

            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) when (throwsRpcCancelled)
            {
                throw new RpcException(new Status(StatusCode.Cancelled, "Call canceled by the client."));
            }
        };

        var removed = await CleanUpAsync(harness, logs, "a.txt", timeout: TimeSpan.FromMilliseconds(300));

        removed.ShouldBeTrue("the row was deleted");
        await using var check = harness.NewContext();
        (await check.Files.CountAsync()).ShouldBe(0);
        harness.Vectors.DeleteTokens.Count.ShouldBe(3, "two deletes before the row and the one that timed out; the second set is not tried");
        logs.Lines.ShouldContain(l => l.Contains("timed out", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANameThatBreaksTheLineIsLoggedOnOneLineWhenTheTimeoutIsReported()
    {
        var logs = new RecordingLoggerFactory();
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        harness.Vectors.OnFileDeleteTokenAsync = async (token, deletes) =>
        {
            if (deletes == 2) await Task.Delay(Timeout.Infinite, token);
        };

        await CleanUpAsync(harness, logs, "two\nlines.txt", timeout: TimeSpan.FromMilliseconds(300));

        var line = logs.Lines.Single(l => l.Contains("lines.txt", StringComparison.Ordinal));
        line.ShouldNotContain("\n");
        line.ShouldNotContain("\r");
    }

    [Fact]
    public async Task TheNameOfADocumentIsLoggedOnOneLineWhenADeleteFails()
    {
        var logs = new RecordingLoggerFactory();
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        harness.Vectors.OnFileDeleteTokenAsync = (_, deletes) =>
            deletes == 2 ? throw new InvalidOperationException("the vector store is unreachable") : Task.CompletedTask;

        await CleanUpAsync(harness, logs, "two\nlines.txt");

        var line = logs.Lines.Single(l => l.Contains("lines.txt", StringComparison.Ordinal));
        line.ShouldNotContain("\n");
        line.ShouldNotContain("\r");
    }

    [Fact]
    public async Task TheNameOfAChunkSetIsLoggedOnOneLineWhenADeleteFails()
    {
        var logs = new RecordingLoggerFactory();
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await using (var db = harness.NewContext())
            await db.ChunkSets.ExecuteUpdateAsync(u => u.SetProperty(s => s.Name, "set\nname"));
        harness.Vectors.OnFileDeleteTokenAsync = (_, deletes) =>
            deletes == 2 ? throw new InvalidOperationException("the vector store is unreachable") : Task.CompletedTask;

        await CleanUpAsync(harness, logs, "a.txt");

        var line = logs.Lines.Single(l => l.Contains("name", StringComparison.Ordinal) && l.Contains("set", StringComparison.Ordinal)
            && l.Contains("Could not delete", StringComparison.Ordinal));
        line.ShouldNotContain("\n");
        line.ShouldNotContain("\r");
    }
}
