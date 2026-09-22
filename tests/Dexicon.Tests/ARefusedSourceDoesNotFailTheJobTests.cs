using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// A source whose path is refused ends the pass Degraded, as a missing mount does, rather
/// than failing it. A path is resolved on every pass, so this needs nobody to edit the
/// source: one created through a link before D-35 is refused on its next refresh.
/// Uncaught, the refusal failed the whole job at that source, and the sources after it
/// were not indexed.
/// </summary>
public sealed class ARefusedSourceDoesNotFailTheJobTests
{
    [Fact]
    public async Task AWorkspaceSourceKeepsWhatItHadAndTheOthersStillIndex()
    {
        await using var harness = await IndexingHarness.StartAsync("a", "b");
        await harness.WriteFileAsync("a.txt", "alpha", source: 0);
        await harness.WriteFileAsync("b.txt", "bravo", source: 1);
        await harness.SeedCorpusAsync(SourceKind.Workspace);

        (await harness.RunIndexAsync()).State.ShouldBe(JobState.Succeeded);

        // `a` becomes a link to the same content, as a source created through one is.
        var real = Path.Combine(harness.DataPath, "workspace", "real-a");
        Directory.Move(harness.SourceDirectories[0], real);
        Directory.CreateSymbolicLink(harness.SourceDirectories[0], real);
        await harness.WriteFileAsync("b2.txt", "charlie", source: 1);

        var job = await harness.RunIndexAsync();

        job.State.ShouldBe(JobState.Degraded);
        job.Error.ShouldNotBeNull().ShouldContain("Links are not followed");

        await using var db = harness.NewContext();
        (await db.Corpora.FirstAsync()).State.ShouldBe(CorpusState.Unavailable);

        var states = await db.FileChunkStates.AsNoTracking().Include(s => s.File).ToListAsync();
        states.Where(s => s.File!.SourceId == IndexingHarness.SourceIdFor(0))
            .ShouldHaveSingleItem().Status.ShouldBe(FileStatus.Indexed);
        states.Where(s => s.File!.SourceId == IndexingHarness.SourceIdFor(1))
            .Select(s => s.File!.RelativePath).Order(StringComparer.Ordinal)
            .ShouldBe(["b.txt", "b2.txt"]);
    }

    [Fact]
    public async Task SoDoesAGitHistorySource()
    {
        await using var harness = await IndexingHarness.StartAsync("repo");
        var real = Path.Combine(harness.DataPath, "workspace", "real");
        Directory.CreateDirectory(real);
        Directory.Delete(harness.SourceDirectory);
        Directory.CreateSymbolicLink(harness.SourceDirectory, real);
        await harness.SeedCorpusAsync(SourceKind.GitHistory);

        var job = await harness.RunIndexAsync();

        job.State.ShouldBe(JobState.Degraded);
        job.Error.ShouldNotBeNull().ShouldContain("Links are not followed");
    }
}
