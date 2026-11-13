using Dexicon.Core.Catalog;
using Dexicon.Core.Indexing;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using static Dexicon.Tests.StashFixture;

namespace Dexicon.Tests;

/// <summary>
/// Stashes indexed as a corpus, through the pass that indexes commits.
///
/// What is worth pinning is what separates a stash from a commit: it appears and disappears
/// with the reflog and not with a ref, one untracked file in many stashes is one document,
/// and switching the setting on reads no commit again.
/// </summary>
public sealed class GitStashIndexingTests
{
    private static readonly string StashesOn = new GitHistoryOptions { IncludeStashes = true }.ToJson();

    private static async Task<List<string>> PathsAsync(IndexingHarness harness)
    {
        await using var db = harness.NewContext();
        var paths = await db.Files.Select(f => f.RelativePath).ToListAsync();
        paths.Sort(StringComparer.Ordinal);
        return paths;
    }

    private static async Task SetOptionsAsync(IndexingHarness harness, GitHistoryOptions options)
    {
        await using var db = harness.NewContext();
        var source = await db.Sources.SingleAsync();
        source.GitOptions = options.ToJson();
        await db.SaveChangesAsync();
    }

    private static string UntrackedPath(string repo, string stash, string file) =>
        $"stashes/untracked/{Git(repo, "rev-parse", $"{stash}^3:{file}").Trim()[..12]}/{file}";

    [Fact]
    public async Task StashesAreNotIndexedUnlessAskedFor()
    {
        await using var harness = await IndexingHarness.StartAsync("repo");
        Init(harness.SourceDirectory);
        Commit(harness.SourceDirectory, "a.txt", "base\n", "base");
        Write(harness.SourceDirectory, "notes.md", "decide X\n");
        Stash(harness.SourceDirectory, "park");

        await harness.SeedCorpusAsync(SourceKind.GitHistory);
        await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken);

        (await PathsAsync(harness)).ShouldAllBe(p => p.StartsWith("commits/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStashAndItsUntrackedFileBecomeDocuments()
    {
        await using var harness = await IndexingHarness.StartAsync("repo");
        var repo = harness.SourceDirectory;
        Init(repo);
        Commit(repo, "a.txt", "base\n", "base");
        Write(repo, "a.txt", "base\nchanged\n");
        Write(repo, "notes.md", "decide X\n");
        Stash(repo, "park");

        await harness.SeedCorpusAsync(SourceKind.GitHistory, gitOptions: StashesOn);
        var job = await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken);

        job.State.ShouldBe(JobState.Succeeded);
        job.FilesTotal.ShouldBe(3);
        job.FilesDone.ShouldBe(3);

        var paths = await PathsAsync(harness);
        paths.Count(p => p.StartsWith("commits/", StringComparison.Ordinal)).ShouldBe(1);
        paths.Count(p => p.StartsWith("stashes/2", StringComparison.Ordinal)).ShouldBe(1);
        paths.ShouldContain(UntrackedPath(repo, "stash@{0}", "notes.md"));
        paths.ShouldAllBe(p => harness.Vectors.CountFor(p) > 0);
    }

    /// <summary>
    /// A commit's key is its sha, its document version and the source's settings that decide its text, and the
    /// stash setting is none of them, so turning it on indexes the stashes and reads no commit again.
    /// </summary>
    [Fact]
    public async Task TurningStashesOnReadsNoCommitAgain()
    {
        await using var harness = await IndexingHarness.StartAsync("repo");
        var repo = harness.SourceDirectory;
        Init(repo);
        Commit(repo, "a.txt", "base\n", "base");
        Write(repo, "notes.md", "decide X\n");
        Stash(repo, "park");

        await harness.SeedCorpusAsync(SourceKind.GitHistory);
        (await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken)).FilesDone.ShouldBe(1);

        await SetOptionsAsync(harness, new GitHistoryOptions { IncludeStashes = true });
        var second = await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken);

        second.FilesDone.ShouldBe(2, "the stash and its file");
        second.FilesSkipped.ShouldBe(1, "the commit, unchanged");
    }

    [Fact]
    public async Task ARefreshOverUnchangedStashesReadsNothing()
    {
        await using var harness = await IndexingHarness.StartAsync("repo");
        var repo = harness.SourceDirectory;
        Init(repo);
        Commit(repo, "a.txt", "base\n", "base");
        Write(repo, "notes.md", "decide X\n");
        Stash(repo, "park");

        await harness.SeedCorpusAsync(SourceKind.GitHistory, gitOptions: StashesOn);
        await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken);
        var second = await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken);

        second.FilesDone.ShouldBe(0);
        second.FilesSkipped.ShouldBe(3);
        second.ChunksWritten.ShouldBe(0);
    }

    [Fact]
    public async Task ADroppedStashLeavesTheIndexWithItsDocuments()
    {
        await using var harness = await IndexingHarness.StartAsync("repo");
        var repo = harness.SourceDirectory;
        Init(repo);
        Commit(repo, "a.txt", "base\n", "base");
        Write(repo, "notes.md", "decide X\n");
        Stash(repo, "park");
        var untracked = UntrackedPath(repo, "stash@{0}", "notes.md");

        await harness.SeedCorpusAsync(SourceKind.GitHistory, gitOptions: StashesOn);
        await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken);
        harness.Vectors.CountFor(untracked).ShouldBeGreaterThan(0);

        Git(repo, "stash", "drop");
        await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken);

        var paths = await PathsAsync(harness);
        paths.ShouldAllBe(p => p.StartsWith("commits/", StringComparison.Ordinal));
        harness.Vectors.CountFor(untracked).ShouldBe(0);
    }

    [Fact]
    public async Task TurningStashesOffRemovesThem()
    {
        await using var harness = await IndexingHarness.StartAsync("repo");
        var repo = harness.SourceDirectory;
        Init(repo);
        Commit(repo, "a.txt", "base\n", "base");
        Write(repo, "notes.md", "decide X\n");
        Stash(repo, "park");

        await harness.SeedCorpusAsync(SourceKind.GitHistory, gitOptions: StashesOn);
        await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken);
        (await PathsAsync(harness)).Count.ShouldBe(3);

        await SetOptionsAsync(harness, new GitHistoryOptions());
        await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken);

        (await PathsAsync(harness)).ShouldAllBe(p => p.StartsWith("commits/", StringComparison.Ordinal));
    }

    /// <summary>
    /// One document for a file held by two stashes, named for the oldest. Dropping the oldest
    /// keeps the document and re-reads it under the next holder; the dropped stash's own
    /// document goes.
    /// </summary>
    [Fact]
    public async Task AFileInTwoStashesIsOneDocumentThatFollowsItsOldestHolder()
    {
        await using var harness = await IndexingHarness.StartAsync("repo");
        var repo = harness.SourceDirectory;
        Init(repo);
        Commit(repo, "a.txt", "base\n", "base");
        Write(repo, "notes.md", "decide X\n");
        Stash(repo, "one");
        Write(repo, "notes.md", "decide X\n");
        Stash(repo, "two");
        var untracked = UntrackedPath(repo, "stash@{0}", "notes.md");

        await harness.SeedCorpusAsync(SourceKind.GitHistory, gitOptions: StashesOn);
        var first = await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken);

        first.FilesDone.ShouldBe(4, "the commit, two stashes, and one document for the file");
        (await PathsAsync(harness)).Count(p => p.StartsWith("stashes/untracked/", StringComparison.Ordinal)).ShouldBe(1);

        // stash@{1} is the oldest.
        Git(repo, "stash", "drop", "stash@{1}");
        var second = await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken);

        second.FilesDone.ShouldBe(1, "the file, now held by the stash that is left");
        second.FilesSkipped.ShouldBe(2, "the commit and the remaining stash");
        var paths = await PathsAsync(harness);
        paths.Count.ShouldBe(3);
        paths.ShouldContain(untracked);
        harness.Vectors.CountFor(untracked).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task AFileLeftOutIsARowWithItsReason()
    {
        await using var harness = await IndexingHarness.StartAsync("repo");
        var repo = harness.SourceDirectory;
        Init(repo);
        Commit(repo, "a.txt", "base\n", "base");
        File.WriteAllBytes(Path.Combine(repo, "pic.dat"), [1, 2, 0, 3, 4]);
        Stash(repo, "park");

        await harness.SeedCorpusAsync(SourceKind.GitHistory, gitOptions: StashesOn);
        var job = await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken);

        var state = await harness.StateOfAsync(UntrackedPath(repo, "stash@{0}", "pic.dat"));
        state.Status.ShouldBe(FileStatus.Skipped);
        state.StatusDetail.ShouldStartWith("binary content");
        job.FilesSkipped.ShouldBe(1);
    }

    [Fact]
    public async Task TheSweepRecordsTheStashesBeforeAnythingIsRead()
    {
        await using var harness = await IndexingHarness.StartAsync("repo");
        var repo = harness.SourceDirectory;
        Init(repo);
        Commit(repo, "a.txt", "base\n", "base");
        Write(repo, "notes.md", "decide X\n");
        Stash(repo, "park");

        await harness.SeedCorpusAsync(SourceKind.GitHistory, gitOptions: StashesOn);
        var result = await harness.SweepAsync();

        result.Swept.ShouldBe(3, "the commit, the stash and the file");
        result.Added.ShouldBe(3);
        (await PathsAsync(harness)).Count.ShouldBe(3);
    }
}
