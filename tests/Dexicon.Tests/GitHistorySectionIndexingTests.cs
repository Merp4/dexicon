using Dexicon.Core.Catalog;
using Dexicon.Core.Embedding;
using Dexicon.Core.Indexing;
using Dexicon.Core.Search;
using Microsoft.EntityFrameworkCore;
using static Dexicon.Tests.StashFixture;

namespace Dexicon.Tests;

/// <summary>
/// A history corpus stored in sections, through the pass that indexes it: each chunk of a commit is wholly its
/// message, its stat or its patch and carries that name, and a corpus indexed before sections existed is cut
/// again once.
/// </summary>
public sealed class GitHistorySectionIndexingTests
{
    private static readonly string WithDiff = new GitHistoryOptions { IncludeDiff = true }.ToJson();
    private static readonly string WithDiffAndStashes = new GitHistoryOptions { IncludeDiff = true, IncludeStashes = true }.ToJson();

    /// <summary>A file long enough that its patch is several chunks at the harness's chunk size.</summary>
    private static string LongFile() =>
        string.Join('\n', Enumerable.Range(1, 120).Select(i => $"line {i:000} of a file that is long enough to need several chunks"));

    private static async Task<IReadOnlyList<SearchHit>> ChunksOfAsync(IndexingHarness harness, string path)
    {
        await using var db = harness.NewContext();
        var set = await db.ChunkSets.SingleAsync();
        return await harness.Vectors.GetFileChunksAsync(IndexingHarness.Collection, set.Id, path);
    }

    private static async Task<string> OnlyCommitPathAsync(IndexingHarness harness)
    {
        await using var db = harness.NewContext();
        return await db.Files.Where(f => f.RelativePath.StartsWith("commits/")).Select(f => f.RelativePath).SingleAsync();
    }

    [Fact]
    public async Task EveryStoredChunkOfACommitIsWhollyOneSectionAndSaysWhich()
    {
        await using var harness = await IndexingHarness.StartAsync("repo");
        var repo = harness.SourceDirectory;
        Init(repo);
        Commit(repo, "long.txt", LongFile(), "add a long file\n\nThe reason is in this body.");

        await harness.SeedCorpusAsync(SourceKind.GitHistory, gitOptions: WithDiff);
        var job = await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken);
        job.State.ShouldBe(JobState.Succeeded);

        var chunks = await ChunksOfAsync(harness, await OnlyCommitPathAsync(harness));

        chunks.Select(c => c.Section).Distinct().ShouldBe(["message", "stat", "diff"]);
        chunks.Count(c => c.Section == "diff").ShouldBeGreaterThan(1);
        chunks.Where(c => c.Section == "message").ShouldAllBe(c => !c.Content.Contains("diff --git") && !c.Content.Contains("long.txt"));
        chunks.Where(c => c.Section == "stat").ShouldAllBe(c => !c.Content.Contains("The reason") && !c.Content.Contains("diff --git"));
        chunks.Where(c => c.Section == "diff").ShouldAllBe(c => !c.Content.Contains("The reason") && !c.Content.Contains("Author:"));
        chunks.First(c => c.Section == "message").Content.ShouldContain("The reason is in this body.");
        chunks.First(c => c.Section == "diff").Content.ShouldStartWith("diff --git a/long.txt");
    }

    [Fact]
    public async Task ACommitIndexedBeforeSectionsWereNamedIsCutAgainOnceAndThenLeftAlone()
    {
        await using var harness = await IndexingHarness.StartAsync("repo");
        var repo = harness.SourceDirectory;
        Init(repo);
        Commit(repo, "a.txt", "one\n", "the first change");

        await harness.SeedCorpusAsync(SourceKind.GitHistory);
        await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken);
        var path = await OnlyCommitPathAsync(harness);

        // The key an earlier build stored for this commit: its sha and the source's settings, with no version.
        string earlier;
        await using (var db = harness.NewContext())
        {
            var set = await db.ChunkSets.SingleAsync(TestContext.Current.CancellationToken);
            var state = await db.FileChunkStates.SingleAsync(s => s.Status == FileStatus.Indexed, TestContext.Current.CancellationToken);
            var sha = Head(repo);
            earlier = CorpusIndexer.ChunkingFingerprint(
                set, CorpusIndexer.HashContent(sha + '|' + new GitHistoryOptions().ContentFingerprint(null)),
                ModelTemplates.Raw, path);

            state.ContentHash.ShouldNotBe(earlier, "the key a pass stores now carries the document version");
            state.ContentHash = earlier;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var again = await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken);

        again.FilesDone.ShouldBe(1, "a commit stored under the earlier key is read and chunked again");
        again.ChunksWritten.ShouldBeGreaterThan(0);
        (await ChunksOfAsync(harness, path)).ShouldAllBe(c => c.Section != null);

        var third = await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken);

        third.FilesDone.ShouldBe(0);
        third.FilesSkipped.ShouldBe(1, "and once cut under the current key it is skipped as before");
        third.ChunksWritten.ShouldBe(0);
    }

    [Fact]
    public async Task AStashIsCutInSectionsAndTheDocumentOfAnUntrackedFileIsNot()
    {
        await using var harness = await IndexingHarness.StartAsync("repo");
        var repo = harness.SourceDirectory;
        Init(repo);
        Commit(repo, "a.txt", "base\n", "base");
        Write(repo, "a.txt", "base\nchanged\n");
        Write(repo, "notes.txt", "decide X in plain words\n");
        Stash(repo, "park");

        await harness.SeedCorpusAsync(SourceKind.GitHistory, gitOptions: WithDiffAndStashes);
        await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken);

        await using var db = harness.NewContext();
        var paths = await db.Files.Select(f => f.RelativePath).ToListAsync(TestContext.Current.CancellationToken);

        var stash = await ChunksOfAsync(harness, paths.Single(p => p.StartsWith("stashes/2", StringComparison.Ordinal)));
        stash.Select(c => c.Section).Distinct().ShouldBe(["message", "stat", "diff"]);
        stash[^1].Section.ShouldBe("stat", "the list of untracked files follows the diff and is not part of it");
        stash[^1].Content.ShouldContain("notes.txt");

        var file = await ChunksOfAsync(harness, paths.Single(p => p.StartsWith("stashes/untracked/", StringComparison.Ordinal)));
        file.ShouldAllBe(c => c.Section == null, "a file's text has no message, stat or diff");
    }
}
