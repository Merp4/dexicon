using System.Diagnostics;
using Dexicon.Core.Catalog;
using Dexicon.Core.Indexing;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// A repository's history indexed as a corpus, through the same pass that indexes files.
///
/// The properties worth pinning are the ones that make it usable rather than merely
/// possible: a commit becomes a searchable document, a refresh over a tip that has not
/// moved reads nothing, a new commit is picked up, and changing what a document contains
/// re-indexes the history rather than leaving a corpus half cut one way and half the
/// other.
/// </summary>
public sealed class GitHistoryIndexingTests
{
    private static void Git(string repo, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) info.ArgumentList.Add(a);

        using var p = Process.Start(info)!;
        p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)}: {stderr}");
    }

    private static void Init(string repo)
    {
        Git(repo, "init", "--initial-branch=main");
        Git(repo, "config", "user.email", "test@example.invalid");
        Git(repo, "config", "user.name", "A Test");
        Git(repo, "config", "commit.gpgsign", "false");
    }

    private static void Commit(string repo, string path, string content, string message)
    {
        File.WriteAllText(Path.Combine(repo, path), content);
        Git(repo, "add", path);
        Git(repo, "commit", "-m", message);
    }

    /// <summary>Every path the pass wrote vectors for, which for this source is a commit each.</summary>
    private static async Task<List<string>> IndexedPathsAsync(IndexingHarness harness)
    {
        await using var db = harness.NewContext();
        var paths = await db.Files.Select(f => f.RelativePath).ToListAsync();
        paths.Sort(StringComparer.Ordinal);
        return paths;
    }

    [Fact]
    public async Task EachCommitBecomesADocument()
    {
        await using var harness = await IndexingHarness.StartAsync("repo");
        Init(harness.SourceDirectory);
        Commit(harness.SourceDirectory, "a.txt", "one\n", "the first change");
        Commit(harness.SourceDirectory, "b.txt", "two\n", "the second change");

        await harness.SeedCorpusAsync(SourceKind.GitHistory);
        var job = await harness.RunIndexAsync();

        job.State.ShouldBe(JobState.Succeeded);
        job.FilesTotal.ShouldBe(2);
        job.FilesDone.ShouldBe(2);
        job.ChunksWritten.ShouldBeGreaterThan(0);

        var paths = await IndexedPathsAsync(harness);
        paths.Count.ShouldBe(2);
        paths.ShouldAllBe(p => p.StartsWith("commits/", StringComparison.Ordinal));
        paths.ShouldAllBe(p => harness.Vectors.CountFor(p) > 0);
    }

    /// <summary>
    /// The point of enumerating separately. A commit cannot change, so a refresh over a
    /// tip that has not moved settles every one of them against the catalogue and asks
    /// git for no bodies at all.
    /// </summary>
    [Fact]
    public async Task ARefreshOverAnUnmovedTipReadsNothing()
    {
        await using var harness = await IndexingHarness.StartAsync("repo");
        Init(harness.SourceDirectory);
        Commit(harness.SourceDirectory, "a.txt", "one\n", "the first change");
        Commit(harness.SourceDirectory, "b.txt", "two\n", "the second change");

        await harness.SeedCorpusAsync(SourceKind.GitHistory);
        await harness.RunIndexAsync();

        var second = await harness.RunIndexAsync();

        second.State.ShouldBe(JobState.Succeeded);
        second.FilesSkipped.ShouldBe(2, "an immutable commit is settled without being read");
        second.FilesDone.ShouldBe(0);
        second.ChunksWritten.ShouldBe(0);
    }

    [Fact]
    public async Task ANewCommitIsPickedUpAndTheOldOnesAreNot()
    {
        await using var harness = await IndexingHarness.StartAsync("repo");
        Init(harness.SourceDirectory);
        Commit(harness.SourceDirectory, "a.txt", "one\n", "the first change");

        await harness.SeedCorpusAsync(SourceKind.GitHistory);
        await harness.RunIndexAsync();

        Commit(harness.SourceDirectory, "b.txt", "two\n", "a later change");
        var second = await harness.RunIndexAsync();

        second.FilesDone.ShouldBe(1, "only the commit that was not there last time");
        second.FilesSkipped.ShouldBe(1);
        (await IndexedPathsAsync(harness)).Count.ShouldBe(2);
    }

    /// <summary>
    /// Turning the diff on changes what every document says, so every one of them is
    /// stale. The alternative is a corpus half cut one way and half the other, with
    /// nothing saying which half is which.
    /// </summary>
    [Fact]
    public async Task ChangingWhatADocumentHoldsReIndexesTheHistory()
    {
        await using var harness = await IndexingHarness.StartAsync("repo");
        Init(harness.SourceDirectory);
        Commit(harness.SourceDirectory, "a.txt", "the quick brown fox\n", "the first change");

        await harness.SeedCorpusAsync(SourceKind.GitHistory);
        await harness.RunIndexAsync();

        await using (var db = harness.NewContext())
        {
            var source = await db.Sources.FirstAsync();
            source.GitOptions = new GitHistoryOptions { IncludeDiff = true }.ToJson();
            await db.SaveChangesAsync();
        }

        var second = await harness.RunIndexAsync();

        second.FilesDone.ShouldBe(1, "the document is different, so the commit is stale");
        second.FilesSkipped.ShouldBe(0);
    }

    /// <summary>
    /// A directory that is not a repository is an operational condition, not a failure
    /// that removes what was indexed before.
    /// </summary>
    [Fact]
    public async Task ADirectoryWithNoRepositoryIsUnavailableRatherThanFailed()
    {
        await using var harness = await IndexingHarness.StartAsync("repo");
        await harness.WriteFileAsync("a.txt", "not a repository");

        await harness.SeedCorpusAsync(SourceKind.GitHistory);
        var job = await harness.RunIndexAsync();

        job.Error.ShouldNotBeNull();
        job.Error.ShouldContain("not a git repository");
        (await IndexedPathsAsync(harness)).ShouldBeEmpty();

        // And the outcome survives the end of the pass. It did not: the corpus was set
        // Unavailable by the source handler and then overwritten with Ready, and the job
        // said Succeeded, so a caller polling the job for success was told yes while
        // nothing had been indexed.
        job.State.ShouldBe(JobState.Degraded, "the pass ran and one source could not be reached");

        await using var db = harness.NewContext();
        var corpus = await db.Corpora.FirstAsync();
        corpus.State.ShouldBe(CorpusState.Unavailable);
        corpus.LastIndexedUtc.ShouldBeNull("nothing was indexed, so nothing was indexed at");
    }
}
