using System.Diagnostics;
using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// Reading a repository's history as one document per commit.
///
/// Against a real repository built in the test rather than against canned output. What
/// this code has to get right is the shape of what git actually prints — where the stat
/// starts, what a merge looks like, what an empty body leaves behind — and canned output
/// is a record of what we believed git prints.
/// </summary>
public sealed class GitHistoryTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"githist-{Guid.NewGuid():N}");

    public GitHistoryTests()
    {
        Directory.CreateDirectory(_repo);
        Git("init", "--initial-branch=main");
        Git("config", "user.email", "test@example.invalid");
        Git("config", "user.name", "A Test");
        Git("config", "commit.gpgsign", "false");
    }

    private string Git(params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) info.ArgumentList.Add(a);

        using var p = Process.Start(info)!;

        // Both pipes drained at once, which is the deadlock the code under test comments
        // about and this helper originally walked into. `git add` over 900 files emits a
        // line-ending warning per file to STDERR; reading stdout to the end first filled
        // that buffer, git blocked writing it, stdout never closed, and the test hung
        // until it was killed.
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        p.WaitForExit();
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");

        return stdout;
    }

    private void Commit(string path, string content, string message)
    {
        var full = Path.Combine(_repo, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        Git("add", path);
        Git("commit", "-m", message);
    }

    private Task<IReadOnlyList<GitCommit>> EnumerateAsync(GitHistoryOptions? options = null,
        IReadOnlyList<string>? paths = null) =>
        GitHistory.EnumerateAsync(_repo, options ?? new GitHistoryOptions(), paths, default);

    private async Task<Dictionary<string, string>> ReadAsync(
        GitHistoryOptions options, IReadOnlyList<GitCommit> commits, IReadOnlyList<string>? paths = null)
    {
        var read = new Dictionary<string, string>(StringComparer.Ordinal);
        await foreach (var (sha, text) in
                       GitHistory.ReadAsync(_repo, options, [.. commits.Select(c => c.Sha)], paths))
            read[sha] = text;
        return read;
    }

    [Fact]
    public async Task ADirectoryThatIsNotARepositoryIsNotOne()
    {
        (await GitHistory.IsRepositoryAsync(_repo, default)).ShouldBeTrue();

        var plain = Path.Combine(Path.GetTempPath(), $"plain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(plain);
        try
        {
            (await GitHistory.IsRepositoryAsync(plain, default)).ShouldBeFalse();
            (await GitHistory.IsRepositoryAsync(Path.Combine(plain, "nope"), default)).ShouldBeFalse();
        }
        finally { Directory.Delete(plain, recursive: true); }
    }

    /// <summary>
    /// A subdirectory of a repository is not a repository.
    ///
    /// `rev-parse --is-inside-work-tree` says true from `/repo/src`, and a source
    /// accepted there would have walked the whole of `/repo`: every commit of the parent
    /// indexed under a source the operator scoped to one folder, including commits that
    /// never touched it.
    /// </summary>
    [Fact]
    public async Task ASubdirectoryOfARepositoryIsNotOne()
    {
        Commit("src/a.txt", "one", "first");

        (await GitHistory.IsRepositoryAsync(_repo, default)).ShouldBeTrue();
        (await GitHistory.IsRepositoryAsync(Path.Combine(_repo, "src"), default)).ShouldBeFalse();
    }

    /// <summary>
    /// A commit message is arbitrary text, and it used to be parsed out of the same
    /// stream as git's own output. A body holding the record separator split a record
    /// in two and the fragment was dropped, which loses a commit — and a commit the
    /// reader drops is one the shared reconcile sees as vanished and deletes the
    /// vectors of. A body line beginning `diff --git ` was read as the start of a patch.
    /// </summary>
    [Fact]
    public async Task ACommitMessageThatLooksLikeGitOutputIsStillAMessage()
    {
        var hostile = "Not a patch:\ndiff --git a/x b/x\n and a stat | line\n and a separator";

        File.WriteAllText(Path.Combine(_repo, "a.txt"), "one");
        Git("add", "a.txt");
        Git("commit", "-m", "the subject", "-m", hostile);

        var commits = await EnumerateAsync();
        commits.ShouldHaveSingleItem();

        var text = (await ReadAsync(new GitHistoryOptions { IncludeDiff = true }, commits))[commits[0].Sha];

        text.ShouldContain("Not a patch:", Case.Sensitive);
        text.ShouldContain("and a separator", Case.Sensitive, "the body survives the separator inside it");
        text.ShouldContain("+one", Case.Sensitive, "and the real patch is still there");
    }

    [Fact]
    public async Task TheInventoryIsShasDatesAndSubjectsNewestFirst()
    {
        Commit("a.txt", "one", "first change");
        Commit("b.txt", "two", "second change");

        var commits = await EnumerateAsync();

        commits.Count.ShouldBe(2);
        commits[0].Subject.ShouldBe("second change", "newest first, as git log gives them");
        commits[1].Subject.ShouldBe("first change");
        commits.ShouldAllBe(c => c.Sha.Length == 40);
        commits[0].AuthorDate.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    /// <summary>
    /// Dated first, so the file list reads chronologically rather than by hash.
    /// </summary>
    [Fact]
    public async Task ThePathIsTheDateAndTheShortSha()
    {
        Commit("a.txt", "one", "first change");
        var commit = (await EnumerateAsync())[0];

        commit.RelativePath.ShouldBe($"commits/{commit.AuthorDate:yyyy-MM-dd}-{commit.Sha[..12]}");
    }

    [Fact]
    public async Task TheDocumentReadsLikeGitShow()
    {
        Commit("a.txt", "one\n", "a subject line");

        var commits = await EnumerateAsync();
        var text = (await ReadAsync(new GitHistoryOptions(), commits))[commits[0].Sha];

        text.ShouldStartWith($"commit {commits[0].Sha}");
        text.ShouldContain("Author: A Test <test@example.invalid>");
        text.ShouldContain("    a subject line");
        text.ShouldContain("a.txt", Case.Sensitive, "the stat names the file that changed");
    }

    [Fact]
    public async Task TheMessageBodyIsKept()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "src"));
        File.WriteAllText(Path.Combine(_repo, "src", "x.txt"), "x");
        Git("add", "src/x.txt");
        Git("commit", "-m", "the subject", "-m", "The body explains why,\nover two lines.");

        var commits = await EnumerateAsync();
        var text = (await ReadAsync(new GitHistoryOptions(), commits))[commits[0].Sha];

        text.ShouldContain("    the subject");
        text.ShouldContain("    The body explains why,");
        text.ShouldContain("    over two lines.");
    }

    [Fact]
    public async Task TheDiffIsLeftOutUnlessItIsAskedFor()
    {
        Commit("a.txt", "the quick brown fox\n", "add a line");
        var commits = await EnumerateAsync();

        var without = (await ReadAsync(new GitHistoryOptions(), commits))[commits[0].Sha];
        var with = (await ReadAsync(new GitHistoryOptions { IncludeDiff = true }, commits))[commits[0].Sha];

        without.ShouldNotContain("the quick brown fox", Case.Sensitive,
            "the stat says which files changed, not what they now say");
        with.ShouldContain("+the quick brown fox");
        with.ShouldContain("diff --git");
    }

    [Fact]
    public async Task AStatCanBeLeftOutToo()
    {
        Commit("a.txt", "one\n", "add a line");
        var commits = await EnumerateAsync();

        var text = (await ReadAsync(
            new GitHistoryOptions { IncludeStat = false }, commits))[commits[0].Sha];

        text.ShouldContain("    add a line");
        text.ShouldNotContain("1 file changed");
    }

    /// <summary>
    /// A patch over the limit is reported, not cut. A diff truncated mid-hunk reads as a
    /// complete change that did something other than what it did.
    /// </summary>
    [Fact]
    public async Task AnOversizedDiffIsStatedRatherThanCut()
    {
        Commit("big.txt", string.Join('\n', Enumerable.Range(0, 2_000).Select(i => $"line {i}")), "a big change");
        var commits = await EnumerateAsync();

        var text = (await ReadAsync(
            new GitHistoryOptions { IncludeDiff = true, MaxDiffBytes = 500 }, commits))[commits[0].Sha];

        text.ShouldContain("not included");
        text.ShouldContain("byte limit for this source");
        text.ShouldNotContain("+line 1999", Case.Sensitive);
        text.ShouldContain("    a big change", Case.Sensitive, "the message survives the diff being dropped");
        text.ShouldContain("big.txt", Case.Sensitive, "and so does the stat, which is the cheap half");
    }

    /// <summary>
    /// The cap bounds the read, not only the document.
    ///
    /// Reading a batch whole and measuring afterwards is not a bound: by then the patch
    /// is allocated, and one commit carrying a vendored tree could exhaust the indexer
    /// before the limit dropped it. git is killed at a ceiling instead, the batch is
    /// halved to find which commit did it, and that one is read again without a patch.
    ///
    /// Two commits, so the halving runs as well as the fallback.
    /// </summary>
    [Fact]
    public async Task APatchTooLargeToReadIsNeverRead()
    {
        Commit("small.txt", "one\n", "a small change");

        // Over the read ceiling, which is the stat's own allowance plus the diff cap.
        // Long lines rather than many, so git has less to diff for the same bytes.
        var wide = new string('x', 100);
        Commit("big.txt", string.Join('\n', Enumerable.Range(0, 20_000).Select(i => $"{i} {wide}")),
            "a huge change");

        var commits = await EnumerateAsync();
        commits.Count.ShouldBe(2);

        // Below the smallest thing git will emit for the big commit, so the ceiling is
        // certain to be hit rather than merely likely.
        var read = await ReadAsync(new GitHistoryOptions { IncludeDiff = true, MaxDiffBytes = 2_000 }, commits);

        read.Count.ShouldBe(2, "the batch is halved, so the other commit is not lost with it");

        var big = read[commits[0].Sha];
        big.ShouldContain("    a huge change", Case.Sensitive);
        big.ShouldContain("not included");
        big.ShouldNotContain(wide, Case.Sensitive, "no part of the patch reached the document");
        big.ShouldContain("big.txt", Case.Sensitive, "the stat survives, which is the point of keeping it");

        // Which path dropped it, and the distinction is the whole test. A patch read
        // and then measured says how large it was — "diff of 1,234,567 bytes" — and a
        // patch never read cannot. Without this the assertions above pass whether the
        // ceiling fired or the document cap did, which is the same test twice.
        big.ShouldNotContain("diff of ", Case.Sensitive,
            "a size here would mean the patch was read before being dropped");

        read[commits[1].Sha].ShouldContain("+one", Case.Sensitive,
            "a small commit in the same batch still gets its patch");
    }

    /// <summary>
    /// A commit that touches many files keeps its stat, and the source does not fail.
    ///
    /// The ceiling measured the stat against the diff's budget and then handed the
    /// stat-only retry that same budget, so a commit whose stat alone exceeded it made
    /// the retry throw as well — losing the one part the cap promises to keep, and
    /// failing the whole source with it. The stat has its own allowance now.
    ///
    /// Note what this does NOT claim: that a large stat can coexist with a small patch.
    /// It cannot, because a stat line implies a diff header, so the patch is always the
    /// larger of the two. The reachable half of the problem is the retry.
    /// </summary>
    [Fact]
    public async Task ACommitThatTouchesManyFilesKeepsItsStat()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "many"));
        for (var i = 0; i < 900; i++)
            File.WriteAllText(Path.Combine(_repo, "many", $"f{i}.txt"), "a\n");

        Git("add", "many");
        Git("commit", "-m", "touch many files");

        var commits = await EnumerateAsync();

        var text = (await ReadAsync(
            new GitHistoryOptions { IncludeDiff = true, MaxDiffBytes = 32_000 }, commits))[commits[0].Sha];

        text.ShouldContain("900 files changed", Case.Sensitive,
            "the stat is kept whatever happens to the patch");
        text.ShouldContain("    touch many files", Case.Sensitive);
        text.ShouldContain("not included", Case.Sensitive, "and the patch is over its own limit");
    }

    [Fact]
    public async Task MergesAreLeftOutUnlessTheyAreAskedFor()
    {
        Commit("a.txt", "one", "base");
        Git("checkout", "-b", "side");
        Commit("b.txt", "two", "on the side");
        Git("checkout", "main");
        Commit("c.txt", "three", "on main");
        Git("merge", "--no-ff", "side", "-m", "merge the side branch");

        var without = await EnumerateAsync();
        var with = await EnumerateAsync(new GitHistoryOptions { IncludeMerges = true });

        without.ShouldNotContain(c => c.Subject == "merge the side branch");
        with.ShouldContain(c => c.Subject == "merge the side branch");
    }

    [Fact]
    public async Task TheWalkCanBeBoundedByCountAndByDate()
    {
        Commit("a.txt", "one", "first");
        Commit("b.txt", "two", "second");
        Commit("c.txt", "three", "third");

        (await EnumerateAsync(new GitHistoryOptions { MaxCommits = 2 })).Count.ShouldBe(2);

        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));
        (await EnumerateAsync(new GitHistoryOptions { Since = tomorrow })).ShouldBeEmpty();
    }

    /// <summary>
    /// The source's own filters become pathspecs, so they mean "whose history" and narrow
    /// the diff at the same time.
    /// </summary>
    [Fact]
    public async Task PathspecsSelectWhoseHistoryIsIndexed()
    {
        Commit("src/a.txt", "one", "touch src");
        Commit("docs/b.txt", "two", "touch docs");

        var all = await EnumerateAsync();
        var srcOnly = await EnumerateAsync(paths: ["src"]);

        all.Count.ShouldBe(2);
        srcOnly.ShouldHaveSingleItem().Subject.ShouldBe("touch src");
    }

    /// <summary>
    /// The ref decides which commits, and must not be able to become a git option: a
    /// value starting with a dash arrives after `--end-of-options`.
    /// </summary>
    [Fact]
    public async Task ARefThatLooksLikeAnOptionIsTreatedAsARef()
    {
        Commit("a.txt", "one", "first");

        var boom = await Should.ThrowAsync<GitHistoryException>(
            EnumerateAsync(new GitHistoryOptions { Ref = "--output=/tmp/pwned" }));

        boom.Message.ShouldContain("not a usable ref");
        File.Exists("/tmp/pwned").ShouldBeFalse();

        // What the check admits, stated, so that narrowing it later cannot quietly break
        // ordinary use — and what it refuses, so widening it cannot quietly stop refusing.
        foreach (var ok in new[] { "HEAD", "main", "release/1.0", "v1.2.3", "HEAD~3", "HEAD^", "@{u}" })
            GitHistory.IsAcceptableRef(ok).ShouldBeTrue(ok);

        foreach (var no in new[] { "--upload-pack=x", "-n", "a b", "a;b", "a$(x)", "main..other", "", "  " })
            GitHistory.IsAcceptableRef(no).ShouldBeFalse(no);
    }

    /// <summary>
    /// The fingerprint decides whether an indexed commit has to be read again. It covers
    /// what the document SAYS, and deliberately not which commits are selected: a moved
    /// tip must not re-index the history behind it.
    /// </summary>
    [Fact]
    public void TheFingerprintCoversContentAndNotSelection()
    {
        var baseline = new GitHistoryOptions();

        // Selection, not content: these choose WHICH commits are indexed. Including one
        // would re-read and re-embed every commit in the repository the first time it
        // changed, for documents not one of which had moved.
        (baseline with { Ref = "other" }).ContentFingerprint().ShouldBe(baseline.ContentFingerprint());
        (baseline with { MaxCommits = 10 }).ContentFingerprint().ShouldBe(baseline.ContentFingerprint());
        (baseline with { Since = new DateOnly(2020, 1, 1) }).ContentFingerprint()
            .ShouldBe(baseline.ContentFingerprint());
        (baseline with { IncludeMerges = true }).ContentFingerprint()
            .ShouldBe(baseline.ContentFingerprint(), "turning merges on ADDS documents; it alters none");

        // The cap matters only when there is a patch for it to cap.
        (baseline with { MaxDiffBytes = 1 }).ContentFingerprint()
            .ShouldBe(baseline.ContentFingerprint(), "no diff, so no cap to apply");
        (baseline with { IncludeDiff = true, MaxDiffBytes = 1 }).ContentFingerprint()
            .ShouldNotBe((baseline with { IncludeDiff = true }).ContentFingerprint());

        (baseline with { IncludeDiff = true }).ContentFingerprint().ShouldNotBe(baseline.ContentFingerprint());
        (baseline with { IncludeStat = false }).ContentFingerprint().ShouldNotBe(baseline.ContentFingerprint());
        (baseline with { IncludeMessage = false }).ContentFingerprint().ShouldNotBe(baseline.ContentFingerprint());

        // Pathspecs reach git, so they decide what the stat lists and what the patch
        // holds: the same commit under a narrower filter is a different document. Their
        // order does not, because the same set is the same filter.
        baseline.ContentFingerprint(["src"]).ShouldNotBe(baseline.ContentFingerprint());
        baseline.ContentFingerprint(["src", "docs"]).ShouldBe(baseline.ContentFingerprint(["docs", "src"]));

        // A pathspec is a caller's text and may hold any character, so a separator alone
        // cannot encode the list: joined on a comma these two are the same string, and
        // they are different filters. One of them would skip a commit whose stat had
        // been cut to the other's paths.
        baseline.ContentFingerprint(["a,b"]).ShouldNotBe(baseline.ContentFingerprint(["a", "b"]));
        baseline.ContentFingerprint(["a;b"]).ShouldNotBe(baseline.ContentFingerprint(["a", "b"]));
        baseline.ContentFingerprint(["2:ab"]).ShouldNotBe(baseline.ContentFingerprint(["ab"]));
    }

    [Fact]
    public void OptionsRoundTripThroughJson()
    {
        var options = new GitHistoryOptions
        {
            Ref = "release/1.0", IncludeDiff = true, MaxDiffBytes = 1234,
            IncludeMerges = true, MaxCommits = 50, Since = new DateOnly(2026, 1, 2),
        };

        GitHistoryOptions.FromJson(options.ToJson()).ShouldBe(options);
        GitHistoryOptions.FromJson(null).ShouldBe(new GitHistoryOptions());
        GitHistoryOptions.FromJson("   ").ShouldBe(new GitHistoryOptions());
    }

    public void Dispose()
    {
        try
        {
            // git leaves read-only objects behind, which Directory.Delete will not remove.
            foreach (var file in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_repo, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
