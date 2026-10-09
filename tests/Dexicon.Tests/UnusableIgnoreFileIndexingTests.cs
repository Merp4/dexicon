using Dexicon.Core.Catalog;
using Dexicon.Core.Indexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// What an index pass and a sweep do with an ignore file line that cannot be compiled
/// (<see cref="UnusableIgnoreFileLineTests"/> covers the walk alone). A <c>.dexiconignore</c> line fails the
/// walk of its source, which is reported like a source that could not be reached: the job reads degraded with the
/// file and the line in its error, nothing already indexed is removed, and the corpus's other sources are still
/// indexed. A <c>.gitignore</c> line is skipped, logged by the pass, and the job succeeds.
/// </summary>
public sealed class UnusableIgnoreFileIndexingTests
{
    private const string Faulty = "[z-a]";

    [Fact]
    public async Task AnUnusableDexiconignoreLineFailsOnlyItsSourceAndNamesTheLine()
    {
        await using var harness = await IndexingHarness.StartAsync("faulty", "fine");
        await harness.WriteFileAsync(".dexiconignore", $"*.log\n{Faulty}\n", source: 0);
        await harness.WriteFileAsync("one.md", IndexingHarness.Prose("one"), source: 0);
        await harness.WriteFileAsync("two.md", IndexingHarness.Prose("two"), source: 1);
        await harness.SeedCorpusAsync(SourceKind.Workspace);

        var job = await harness.RunIndexAsync();

        job.State.ShouldBe(JobState.Degraded, "one source could not be walked and the other was");
        job.Error.ShouldNotBeNull();
        job.Error.ShouldContain(
            "Source 'faulty' was not indexed because .dexiconignore line 2 ('[z-a]') cannot be compiled (reversed character range)");

        (await harness.StateOfAsync("two.md", sourceId: IndexingHarness.SourceIdFor(1))).Status.ShouldBe(FileStatus.Indexed);

        await using var db = harness.NewContext();
        (await db.Files.CountAsync(f => f.SourceId == IndexingHarness.SourceIdFor(0)))
            .ShouldBe(0, "nothing is indexed from a source whose exclusions cannot be read");
        (await db.Corpora.FirstAsync()).State.ShouldBe(CorpusState.Unavailable);
    }

    [Fact]
    public async Task WhatAnEarlierPassIndexedStaysWhenALaterDexiconignoreCannotBeRead()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.WriteFileAsync("one.md", IndexingHarness.Prose("one"));
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        (await harness.RunIndexAsync()).State.ShouldBe(JobState.Succeeded);
        var held = harness.Vectors.CountFor("one.md");
        held.ShouldBeGreaterThan(0);

        await harness.WriteFileAsync(".dexiconignore", $"{Faulty}\n");
        var job = await harness.RunIndexAsync();

        job.State.ShouldBe(JobState.Degraded);
        harness.Vectors.CountFor("one.md").ShouldBe(held, "a walk that failed is not evidence the file is gone");
        (await harness.StateOfAsync("one.md")).Status.ShouldBe(FileStatus.Indexed);
    }

    [Fact]
    public async Task AnUnusableGitignoreLineIsLoggedAndTheJobSucceeds()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.WriteFileAsync(".gitignore", $"{Faulty}\nskipped.md\n");
        await harness.WriteFileAsync("kept.md", IndexingHarness.Prose("kept"));
        await harness.WriteFileAsync("skipped.md", IndexingHarness.Prose("skipped"));
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        var log = new RecordingLoggerFactory();

        var job = await harness.RunIndexAsync(log: new Logger<CorpusIndexer>(log));

        job.State.ShouldBe(JobState.Succeeded);
        job.Error.ShouldBeNull();
        (await harness.StateOfAsync("kept.md")).Status.ShouldBe(FileStatus.Indexed);

        await using var db = harness.NewContext();
        (await db.Files.AnyAsync(f => f.RelativePath == "skipped.md")).ShouldBeFalse("the rest of the file applied");

        log.Lines.ShouldContain(
            "Source notes: .gitignore line 1 ('[z-a]') cannot be compiled (reversed character range); the line was skipped");
    }

    [Fact]
    public async Task ASweepLeavesASourceWithAnUnusableDexiconignoreAloneAndSweepsTheOthers()
    {
        await using var harness = await IndexingHarness.StartAsync("faulty", "fine");
        await harness.WriteFileAsync(".dexiconignore", $"{Faulty}\n", source: 0);
        await harness.WriteFileAsync("one.md", IndexingHarness.Prose("one"), source: 0);
        await harness.WriteFileAsync("two.md", IndexingHarness.Prose("two"), source: 1);
        await harness.SeedCorpusAsync(SourceKind.Workspace);

        var result = await harness.SweepAsync();

        result.Outcome.ShouldBe(SweepOutcome.Swept);
        await using var db = harness.NewContext();
        var swept = await db.Files.AsNoTracking().ToListAsync();
        swept.ShouldAllBe(f => f.SourceId == IndexingHarness.SourceIdFor(1));
        swept.Select(f => f.RelativePath).ShouldContain("two.md");
    }
}
