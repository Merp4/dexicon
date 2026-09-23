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
    private static string Git(string repo, params string[] args)
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

        // Both pipes drained at once. Reading stdout to the end first fills the stderr
        // buffer on any git call that is chatty on it, git blocks writing, stdout never
        // closes, and the test hangs with no CPU. `GitHistoryTests.Git` hit exactly that
        // on a `git add` of 900 files emitting a line-ending warning per file; this copy
        // kept the bug because the fix was made in the other file.
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        p.WaitForExit();
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)}: {stderr}");

        return stdout;
    }

    /// <summary>HEAD's sha and author date, as git reports them.</summary>
    private static (string Sha, DateTime AuthoredUtc) Head(string repo) =>
        (Git(repo, "rev-parse", "HEAD").Trim(),
         DateTimeOffset.Parse(Git(repo, "log", "-1", "--format=%aI").Trim(),
             System.Globalization.CultureInfo.InvariantCulture).UtcDateTime);

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
        second.FilesSkipped.ShouldBe(2);
        second.FilesDone.ShouldBe(0);
        second.ChunksWritten.ShouldBe(0);
    }

    /// <summary>
    /// What makes the refresh above cheap, which its counters cannot show.
    ///
    /// Two checks skip an unchanged unit: one before the read, from the sha and the
    /// source's settings, and one after it, from the document text. Only the first
    /// saves the read, and both leave FilesSkipped at the same number — so the counters
    /// agree whether the patches were fetched or not, and the assertion above passed
    /// while every one of them was being fetched and thrown away.
    ///
    /// The observable difference is the stored hash. The pre-read check can only fire
    /// when what a pass STORED is the value that check computes, so that is what this
    /// asserts, against the same helpers the indexer uses rather than a copy of them.
    /// </summary>
    [Fact]
    public async Task TheStoredHashIsTheOneTheCheapCheckCompares()
    {
        await using var harness = await IndexingHarness.StartAsync("repo");
        Init(harness.SourceDirectory);
        Commit(harness.SourceDirectory, "a.txt", "one\n", "the first change");

        await harness.SeedCorpusAsync(SourceKind.GitHistory);
        await harness.RunIndexAsync();

        await using var db = harness.NewContext();
        var set = await db.ChunkSets.SingleAsync();
        var state = await db.FileChunkStates.SingleAsync(s => s.Status == FileStatus.Indexed);
        var file = await db.Files.SingleAsync(f => f.Id == state.FileId);

        var sha = file.RelativePath.Split('-').Last();
        var options = new GitHistoryOptions();
        var repo = GitHistory.RepositoryIn(harness.DataPath, Path.Combine("workspace", "repo"))!;
        var full = (await GitHistory.EnumerateAsync(repo, options, null, default))
            .Single(c => c.Sha.StartsWith(sha, StringComparison.Ordinal));

        var expected = CorpusIndexer.ChunkingFingerprint(
            set,
            CorpusIndexer.HashContent(full.Sha + '|' + options.ContentFingerprint(null)),
            Dexicon.Core.Embedding.ModelTemplates.Raw);

        state.ContentHash.ShouldBe(expected,
            "a stored hash in any other shape is one the pre-read check can never match");
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

    private static void ShouldBeTheCommit((string? Sha, DateTime? AuthoredUtc) recorded, (string Sha, DateTime AuthoredUtc) commit)
    {
        recorded.Sha.ShouldBe(commit.Sha);
        recorded.AuthoredUtc.ShouldBe(commit.AuthoredUtc);
    }

    private static async Task<(string? Sha, DateTime? AuthoredUtc)> NewestRecordedAsync(IndexingHarness harness)
    {
        await using var db = harness.NewContext();
        var source = await db.Sources.SingleAsync();
        return (source.NewestCommitSha, source.NewestCommitUtc);
    }

    /// <summary>
    /// Where the ref had got to, so a source whose ref stopped moving can be seen to have
    /// stopped. One over a local branch nobody pulled indexed the same 174 commits for
    /// three days, and every count on screen was correct.
    /// </summary>
    [Fact]
    public async Task ThePassRecordsTheNewestCommitAndFollowsItForward()
    {
        await using var harness = await IndexingHarness.StartAsync("repo");
        Init(harness.SourceDirectory);
        Commit(harness.SourceDirectory, "a.txt", "one\n", "the first change");
        Commit(harness.SourceDirectory, "b.txt", "two\n", "the second change");

        await harness.SeedCorpusAsync(SourceKind.GitHistory);
        (await NewestRecordedAsync(harness)).Sha.ShouldBeNull("nothing has read the history yet");

        await harness.RunIndexAsync();
        var first = Head(harness.SourceDirectory);
        ShouldBeTheCommit(await NewestRecordedAsync(harness), first);

        Commit(harness.SourceDirectory, "c.txt", "three\n", "a later change");
        await harness.RunIndexAsync();

        var second = Head(harness.SourceDirectory);
        second.Sha.ShouldNotBe(first.Sha);
        ShouldBeTheCommit(await NewestRecordedAsync(harness), second);
    }

    /// <summary>
    /// A pass that could not read the history saw nothing, so it says nothing. Writing
    /// null here would claim the settings selected no commits, which is a different fact.
    /// </summary>
    [Fact]
    public async Task APassThatCouldNotReadTheHistoryLeavesTheLastOne()
    {
        await using var harness = await IndexingHarness.StartAsync("repo");
        Init(harness.SourceDirectory);
        Commit(harness.SourceDirectory, "a.txt", "one\n", "the first change");

        await harness.SeedCorpusAsync(SourceKind.GitHistory);
        await harness.RunIndexAsync();
        var seen = Head(harness.SourceDirectory);

        Directory.Move(Path.Combine(harness.SourceDirectory, ".git"),
            Path.Combine(harness.SourceDirectory, ".git-away"));

        var job = await harness.RunIndexAsync();

        job.State.ShouldBe(JobState.Degraded, "the source could not be reached");
        ShouldBeTheCommit(await NewestRecordedAsync(harness), seen);
    }

    /// <summary>
    /// Settings that select no commits are an observation, and the record says so.
    /// Keeping the old commit would show a source reading history it no longer reads.
    /// </summary>
    [Fact]
    public async Task SettingsThatSelectNoCommitsRecordNone()
    {
        await using var harness = await IndexingHarness.StartAsync("repo");
        Init(harness.SourceDirectory);
        Commit(harness.SourceDirectory, "a.txt", "one\n", "the first change");

        await harness.SeedCorpusAsync(SourceKind.GitHistory);
        await harness.RunIndexAsync();
        (await NewestRecordedAsync(harness)).Sha.ShouldNotBeNull();

        await using (var db = harness.NewContext())
        {
            var source = await db.Sources.SingleAsync();
            source.GitOptions = new GitHistoryOptions
            {
                Since = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2),
            }.ToJson();
            await db.SaveChangesAsync();
        }

        await harness.RunIndexAsync();

        var none = await NewestRecordedAsync(harness);
        none.Sha.ShouldBeNull();
        none.AuthoredUtc.ShouldBeNull();
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
    /// <summary>
    /// The sweep covers a history source, so the corpus says what it holds before any
    /// commit has been read.
    ///
    /// Adding a source enqueues a sweep, and the sweep walked workspace sources only —
    /// so a new history source reported zero files and zero pending until an index job
    /// reached the front of the queue. That is the case D-32 exists to prevent,
    /// reintroduced for a new kind of source, and the inventory it needs is the cheap
    /// `git log` this feature already runs.
    /// </summary>
    [Fact]
    public async Task ASourceWhosePathIsRefusedDoesNotLoseTheSweep()
    {
        await using var harness = await IndexingHarness.StartAsync("repo");
        await harness.SeedCorpusAsync(SourceKind.GitHistory);

        // A real repository, OUTSIDE the workspace, with the source's root linked to it.
        // A path is resolved on every sweep, so this needs nobody to edit anything: a
        // link target that moves outside the root turns a source that has swept for
        // months into a refusal. Uncaught, that reached the worker pool and lost the
        // whole corpus's sweep, including sources that were fine — and leaving an
        // inventory it could not refresh alone is the one thing this pass promises.
        var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        Init(outside);
        Commit(outside, "a.txt", "one\n", "the first change");

        Directory.Delete(harness.SourceDirectory);   // the harness made it, and it is empty
        Directory.CreateSymbolicLink(harness.SourceDirectory, outside);

        try
        {
            var result = await harness.SweepAsync();

            result.Outcome.ShouldBe(SweepOutcome.Swept, "the sweep finished rather than throwing");
            result.Swept.ShouldBe(0);
        }
        finally
        {
            Directory.Delete(harness.SourceDirectory);
            foreach (var f in Directory.EnumerateFiles(outside, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task ASweepRecordsTheCommitsBeforeAnyAreRead()
    {
        await using var harness = await IndexingHarness.StartAsync("repo");
        Init(harness.SourceDirectory);
        Commit(harness.SourceDirectory, "a.txt", "one\n", "the first change");
        Commit(harness.SourceDirectory, "b.txt", "two\n", "the second change");

        await harness.SeedCorpusAsync(SourceKind.GitHistory);

        var result = await harness.SweepAsync();

        result.Outcome.ShouldBe(SweepOutcome.Swept);
        result.Swept.ShouldBe(2);
        result.Added.ShouldBe(2);

        await using var db = harness.NewContext();
        var states = await db.FileChunkStates.AsNoTracking().ToListAsync();
        states.Count.ShouldBe(2);
        states.ShouldAllBe(s => s.Status == FileStatus.Pending);
        states.ShouldAllBe(s => s.ChunkCount == 0 && s.ContentHash == null);

        (await IndexedPathsAsync(harness)).ShouldAllBe(p => p.StartsWith("commits/", StringComparison.Ordinal));

        // And indexing afterwards fills those rows in rather than duplicating them.
        var job = await harness.RunIndexAsync();
        job.FilesDone.ShouldBe(2);
        (await IndexedPathsAsync(harness)).Count.ShouldBe(2);
    }

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
