using Dexicon.Core.Catalog;
using Dexicon.Core.Indexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dexicon.Tests;

/// <summary>
/// A pass over a folder adds file rows, and the save that records how the job ended is refused for a while.
/// A file that was never in the catalogue has not vanished from it, so its row is saved when the refusal
/// ends.
/// </summary>
public sealed class WorkspaceFileRowsAfterARefusedSaveTests
{
    [Fact]
    public async Task TheRowsOfFilesThePassAddedSurviveARefusedOutcomeSave()
    {
        var disk = new DiskFull();
        await using var harness = await IndexingHarness.StartAsync(disk, "notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await harness.WriteFileAsync("a.txt", IndexingHarness.Prose("alpha"));
        disk.RefuseWritesTo("\"files\"");
        var freeing = Task.Run(async () =>
        {
            var until = DateTime.UtcNow.AddSeconds(20);
            while (disk.WritesRefused < 3 && DateTime.UtcNow < until) await Task.Delay(10);
            disk.Free();
        });

        await harness.RunIndexAsync(saveRetryDelay: TimeSpan.FromMilliseconds(300));
        await freeing;

        disk.WritesRefused.ShouldBeGreaterThanOrEqualTo(3, "the window has to have been open");
        await using var db = harness.NewContext();
        (await db.Files.CountAsync()).ShouldBe(1, "the file the pass embedded keeps its row");
        (await db.FileChunkStates.CountAsync()).ShouldBe(1);
    }
}
