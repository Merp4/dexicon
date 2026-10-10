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
    public async Task AnUnusableDexiconignoreLeavesItsSourceUnindexedAndTheJobDegradedWithTheLineNamed()
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

    [Fact]
    public async Task ASkippedGitignoreLineIsLoggedOncePerPassHoweverManyChunkSetsWalkTheSource()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.WriteFileAsync(".gitignore", $"{Faulty}\n");
        await harness.WriteFileAsync("kept.md", IndexingHarness.Prose("kept"));
        await harness.SeedCorpusAsync(SourceKind.Workspace, sets: 3);
        var log = new RecordingLoggerFactory();

        await harness.RunIndexAsync(log: new Logger<CorpusIndexer>(log));

        log.Lines.Count(l => l.Contains(".gitignore line 1", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task TheNoticeThatAFileStoppedBeingReadIsLoggedWhateverElseWasOmitted()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.WriteFileAsync(".gitignore", string.Concat(Enumerable.Repeat($"{Faulty}\n", 1_500)) + "secret.md\n");
        await harness.WriteFileAsync("secret.md", IndexingHarness.Prose("secret"));
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        var log = new RecordingLoggerFactory();

        await harness.RunIndexAsync(log: new Logger<CorpusIndexer>(log));

        log.Lines.ShouldContain(
            "Source notes: .gitignore has 1,000 lines that cannot be used; the rest of the file was not read, so no rule after them applies");
        log.Lines.ShouldContain("Source notes: 980 more ignore-file warnings were counted and are not listed");
    }

    [Fact]
    public async Task NoCountOfOmittedWarningsIsLoggedWhenNoneWereOmitted()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.WriteFileAsync(".gitignore", $"{Faulty}\n");
        await harness.WriteFileAsync("kept.md", IndexingHarness.Prose("kept"));
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        var log = new RecordingLoggerFactory();

        await harness.RunIndexAsync(log: new Logger<CorpusIndexer>(log));

        log.Lines.ShouldContain(l => l.Contains(".gitignore line 1", StringComparison.Ordinal));
        log.Lines.ShouldNotContain(l => l.Contains("more ignore-file warnings", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABadGitignoreInADirectoryAMoreSpecificSourceOwnsIsReportedByThatSourceOnly()
    {
        await using var harness = await IndexingHarness.StartAsync("outer", "outer/inner");
        await harness.WriteFileAsync("top.md", IndexingHarness.Prose("top"), source: 0);
        await harness.WriteFileAsync(".gitignore", $"{Faulty}\n", source: 1);
        await harness.WriteFileAsync("deep.md", IndexingHarness.Prose("deep"), source: 1);
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        var log = new RecordingLoggerFactory();

        var job = await harness.RunIndexAsync(log: new Logger<CorpusIndexer>(log));

        job.State.ShouldBe(JobState.Succeeded);
        var reported = log.Lines.Where(l => l.Contains(".gitignore line 1", StringComparison.Ordinal)).ToList();
        reported.ShouldHaveSingleItem().ShouldStartWith("Source outer/inner: ");
    }

    [Fact]
    public async Task ABadDexiconignoreInADirectoryAMoreSpecificSourceOwnsFailsThatSourceAndNotTheOuterOne()
    {
        await using var harness = await IndexingHarness.StartAsync("outer", "outer/inner");
        await harness.WriteFileAsync("top.md", IndexingHarness.Prose("top"), source: 0);
        await harness.WriteFileAsync(".dexiconignore", $"{Faulty}\n", source: 1);
        await harness.WriteFileAsync("deep.md", IndexingHarness.Prose("deep"), source: 1);
        await harness.SeedCorpusAsync(SourceKind.Workspace);

        var job = await harness.RunIndexAsync();

        job.Error.ShouldNotBeNull();
        job.Error.ShouldContain("Source 'outer/inner' was not indexed because .dexiconignore line 1");
        job.Error.ShouldNotContain("Source 'outer' ");
        (await harness.StateOfAsync("top.md", sourceId: IndexingHarness.SourceIdFor(0))).Status.ShouldBe(FileStatus.Indexed);
    }

    [Fact]
    public async Task ALinkedDexiconignoreLeavesItsSourceUnindexedAndNamesTheLink()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.WriteFileAsync("one.md", IndexingHarness.Prose("one"));
        var target = Path.Combine(harness.DataPath, "outside-rules");
        await File.WriteAllTextAsync(target, "one.md\n");
        File.CreateSymbolicLink(Path.Combine(harness.SourceDirectory, ".dexiconignore"), target);
        await harness.SeedCorpusAsync(SourceKind.Workspace);

        var job = await harness.RunIndexAsync();

        job.State.ShouldBe(JobState.Degraded);
        job.Error.ShouldNotBeNull();
        job.Error.ShouldContain("Source 'notes' was not indexed because .dexiconignore is a link, and links are not followed");
        await using var db = harness.NewContext();
        (await db.Files.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task TheSameBadLineInTwoSourcesIsLoggedForEachAndAgainOnTheNextPass()
    {
        await using var harness = await IndexingHarness.StartAsync("one", "two");
        await harness.WriteFileAsync(".gitignore", $"{Faulty}\n", source: 0);
        await harness.WriteFileAsync(".gitignore", $"{Faulty}\n", source: 1);
        await harness.WriteFileAsync("a.md", IndexingHarness.Prose("a"), source: 0);
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        var log = new RecordingLoggerFactory();

        await harness.RunIndexAsync(log: new Logger<CorpusIndexer>(log));
        log.Lines.Count(l => l.Contains(".gitignore line 1", StringComparison.Ordinal)).ShouldBe(2, "one for each source");
        log.Lines.ShouldContain(l => l.StartsWith("Source one: ", StringComparison.Ordinal));
        log.Lines.ShouldContain(l => l.StartsWith("Source two: ", StringComparison.Ordinal));

        await harness.RunIndexAsync(log: new Logger<CorpusIndexer>(log));
        log.Lines.Count(l => l.Contains(".gitignore line 1", StringComparison.Ordinal)).ShouldBe(4, "the next pass reports again");
    }

    [Fact]
    public async Task AFailedIgnoreFileIsLoggedOnceHoweverManyChunkSetsWalkTheSource()
    {
        await using var harness = await IndexingHarness.StartAsync("faulty");
        await harness.WriteFileAsync(".dexiconignore", $"{Faulty}\n");
        await harness.WriteFileAsync("one.md", IndexingHarness.Prose("one"));
        await harness.SeedCorpusAsync(SourceKind.Workspace, sets: 3);
        var log = new RecordingLoggerFactory();

        var job = await harness.RunIndexAsync(log: new Logger<CorpusIndexer>(log));

        log.Lines.Count(l => l.Contains("was not indexed because", StringComparison.Ordinal)).ShouldBe(1);
        job.Error.ShouldNotBeNull();
        job.Error.Split("was not indexed because").Length.ShouldBe(2);
    }

    [Fact]
    public async Task WhatTheWalkSkippedBeforeAFailureIsLoggedWithIt()
    {
        await using var harness = await IndexingHarness.StartAsync("faulty");
        await harness.WriteFileAsync(".gitignore", $"{Faulty}\n");
        await harness.WriteFileAsync(".dexiconignore", $"{Faulty}\n");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        var log = new RecordingLoggerFactory();

        await harness.RunIndexAsync(log: new Logger<CorpusIndexer>(log));

        log.Lines.ShouldContain(l => l.Contains(".gitignore line 1", StringComparison.Ordinal) && l.Contains("the line was skipped", StringComparison.Ordinal));
        log.Lines.ShouldContain(l => l.Contains(".dexiconignore line 1", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("source", "excludeGlobs[0]")]
    [InlineData("corpus", "corpus default excludeGlobs[0]")]
    [InlineData("source include", "includeGlobs[0]")]
    [InlineData("corpus include", "corpus default includeGlobs[0]")]
    [InlineData("corpus include, source with its own exclude list", "corpus default includeGlobs[0]")]
    [InlineData("corpus exclude, source with its own include list", "corpus default excludeGlobs[0]")]
    public async Task AnEntryOfAStoredListIsNamedByWhereItCameFrom(string holder, string expected)
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.WriteFileAsync("one.md", IndexingHarness.Prose("one"));
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await using (var db = harness.NewContext())
        {
            var source = await db.Sources.SingleAsync();
            var corpus = await db.Corpora.SingleAsync();
            var faulty = SourceFilters.Store([Faulty]);
            var fine = SourceFilters.Store(["*.log"]);
            switch (holder)
            {
                case "source": source.ExcludeGlobs = faulty; break;
                case "corpus": corpus.DefaultExcludeGlobs = faulty; break;
                case "source include": source.IncludeGlobs = faulty; break;
                case "corpus include": corpus.DefaultIncludeGlobs = faulty; break;
                case "corpus include, source with its own exclude list":
                    corpus.DefaultIncludeGlobs = faulty;
                    source.ExcludeGlobs = fine;
                    break;
                case "corpus exclude, source with its own include list":
                    corpus.DefaultExcludeGlobs = faulty;
                    source.IncludeGlobs = SourceFilters.Store(["*.md"]);
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(holder), holder, "an unknown holder");
            }

            await db.SaveChangesAsync();
        }

        var job = await harness.RunIndexAsync();

        job.Error.ShouldNotBeNull();
        job.Error.ShouldContain($"Source 'notes' was not indexed because {expected} ('[z-a]') cannot be compiled");
    }

    [Fact]
    public async Task ASweepLogsAFailedIgnoreFileOnceUntilTheReasonChanges()
    {
        await using var harness = await IndexingHarness.StartAsync("faulty");
        await harness.WriteFileAsync(".dexiconignore", $"{Faulty}\n");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        var log = new RecordingLoggerFactory();
        var sweeperLog = new Logger<CorpusSweeper>(log);

        await harness.SweepAsync(sweeperLog);
        await harness.SweepAsync(sweeperLog);
        log.Lines.Count(l => l.Contains("was not walked because", StringComparison.Ordinal)).ShouldBe(1);

        await harness.WriteFileAsync(".dexiconignore", "[y-b]\n");
        await harness.SweepAsync(sweeperLog);
        log.Lines.Count(l => l.Contains("was not walked because", StringComparison.Ordinal)).ShouldBe(2);

        await harness.WriteFileAsync(".dexiconignore", "*.log\n");
        await harness.SweepAsync(sweeperLog);
        await harness.WriteFileAsync(".dexiconignore", "[y-b]\n");
        await harness.SweepAsync(sweeperLog);
        log.Lines.Count(l => l.Contains("was not walked because", StringComparison.Ordinal)).ShouldBe(3, "a source that came right and failed again is reported again");
    }

    [Fact]
    public void TheDiscoveryWalkIsGivenTheTokenAndStopsOnIt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "a.md"), "hello");
            var source = new Source { Id = "s", CorpusId = "c", Kind = SourceKind.Workspace, RootPath = string.Empty };
            var corpus = new Corpus { Id = "c", Name = "c", Sources = { source } };
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Should.Throw<OperationCanceledException>(
                () => WorkspaceDiscovery.Walk(corpus, source, root, new Dexicon.Core.Configuration.IndexingOptions(), cts.Token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
