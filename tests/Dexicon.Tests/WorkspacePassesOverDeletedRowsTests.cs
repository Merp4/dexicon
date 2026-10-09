using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// A pass over a folder, and a source deleted while it runs. The pass has added the file rows of what it
/// walked, and the rows name a source that is gone, so the save fails on the foreign key and not on a
/// conflict. The job is recorded as failed.
/// </summary>
[Collection(nameof(AttachmentLockCollection))]
public sealed class WorkspacePassesOverDeletedRowsTests
{
    [Fact]
    public async Task ASourceDeletedBeforeTheFirstSaveOfItsFilesLeavesTheJobRecordedAsFailed()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await harness.WriteFileAsync("a.txt", IndexingHarness.Prose("alpha"));
        harness.Vectors.OnEnsureCollection = () =>
        {
            using var other = harness.NewContext();
            other.Sources.ExecuteDelete();
        };

        var job = await harness.RunIndexAsync();

        await using var db = harness.NewContext();
        var row = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        row.State.ShouldBe(JobState.Failed);
        row.FinishedUtc.ShouldNotBeNull();
        row.Error.ShouldNotBeNull().ShouldContain("removed while the pass ran");
        (await db.Files.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task ASourceDeletedWhileTheReconcileHoldsAFileItRemovesDoesNotLeaveTheOutcomeUnsaved()
    {
        // The reconcile of a folder marks the chunk state and the row of a file that left the disk as deleted.
        // They stay tracked after the save fails, and a state that is Deleted is not stopped being tracked
        // with its file, so a loop that only forgot the file would try the same save for ever.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await harness.WriteFileAsync("a.md", IndexingHarness.Prose("alpha"));
        await harness.WriteFileAsync("b.md", IndexingHarness.Prose("beta"));
        await harness.RunIndexAsync(JobKind.Full);
        File.Delete(Path.Combine(harness.SourceDirectory, "a.md"));
        var fired = 0;
        harness.Vectors.OnDeleteAsync = async () =>
        {
            if (Interlocked.Exchange(ref fired, 1) != 0) return;
            await using var other = harness.NewContext();
            await other.Sources.ExecuteDeleteAsync();
        };

        var job = await harness.RunIndexAsync().FinishesAsync("the pass and the record of its outcome");

        await using var db = harness.NewContext();
        var row = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        row.State.ShouldNotBe(JobState.Running, "the outcome is recorded");
        row.FinishedUtc.ShouldNotBeNull();
        fired.ShouldBe(1, "the source has to have been deleted during the reconcile");
    }
}
