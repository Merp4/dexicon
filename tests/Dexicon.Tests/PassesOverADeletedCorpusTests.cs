using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dexicon.Tests;

/// <summary>
/// A corpus deleted while a pass runs takes its jobs with it, so there is no row to record the outcome on. The
/// pass does not throw: the save that records the outcome stops tracking the corpus and the job, and says so in
/// the log.
/// </summary>
[Collection(nameof(AttachmentLockCollection))]
public sealed class PassesOverADeletedCorpusTests
{
    private static async Task RunAsync(IndexingHarness harness, RecordingLoggerFactory logs)
    {
        harness.Vectors.OnEnsureCollection = () =>
        {
            using var other = harness.NewContext();
            other.Corpora.ExecuteDelete();
        };

        var job = await harness.RunIndexAsync(log: logs.CreateLogger<Dexicon.Core.Indexing.CorpusIndexer>());

        job.State.ShouldBe(JobState.Failed, "the pass reports its own failure to its caller");
        await using var db = harness.NewContext();
        (await db.Corpora.CountAsync(), await db.Jobs.CountAsync()).ShouldBe((0, 0));
        logs.Lines.ShouldContain(l => l.Contains("rows changed or went missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACorpusDeletedUnderAFolderPassThrowsNothingAndLogsTheRowsItDropped()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await harness.WriteFileAsync("a.txt", IndexingHarness.Prose("alpha"));

        await RunAsync(harness, new RecordingLoggerFactory());
    }

    [Fact]
    public async Task ACorpusDeletedUnderAnUploadPassThrowsNothingAndLogsTheRowsItDropped()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await using (var db = harness.NewContext())
        {
            var documents = harness.NewDocumentService(db);
            var stored = await documents.StoreAsync(new MemoryStream("one document"u8.ToArray()), "doc.txt");
            await documents.AttachAsync(await db.Corpora.SingleAsync(), stored.Sha256, "doc.txt");
        }

        await RunAsync(harness, new RecordingLoggerFactory());
    }

    [Fact]
    public async Task AChunkSetDeletedUnderAFolderPassLeavesTheFileRowAndRecordsTheJobAsFailed()
    {
        // The set is deleted after the pass loaded it. The file the pass walked is under a source that is
        // still there, so its row stays.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace, sets: 2);
        await harness.WriteFileAsync("a.txt", IndexingHarness.Prose("alpha"));
        var ensured = 0;
        harness.Vectors.OnEnsureCollection = () =>
        {
            if (Interlocked.Increment(ref ensured) != 1) return;
            using var other = harness.NewContext();
            other.ChunkSets.Where(s => s.Id == "set-1").ExecuteDelete();
        };

        var job = await harness.RunIndexAsync();

        await using var db = harness.NewContext();
        (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id)).State.ShouldBe(JobState.Failed);
        (await db.Files.Select(f => f.RelativePath).ToListAsync()).ShouldBe(["a.txt"], "its source is still there");
        (await db.FileChunkStates.CountAsync(s => s.ChunkSetId == "set-1")).ShouldBe(0);
    }
}
