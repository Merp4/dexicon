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
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

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
        text.ShouldContain("character limit for this source");
        text.ShouldNotContain("+line 1999", Case.Sensitive);
        text.ShouldContain("    a big change", Case.Sensitive, "the message survives the diff being dropped");
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

        boom.Message.ShouldContain("git log failed");
        File.Exists("/tmp/pwned").ShouldBeFalse();
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

        (baseline with { Ref = "other" }).ContentFingerprint().ShouldBe(baseline.ContentFingerprint());
        (baseline with { MaxCommits = 10 }).ContentFingerprint().ShouldBe(baseline.ContentFingerprint());
        (baseline with { Since = new DateOnly(2020, 1, 1) }).ContentFingerprint()
            .ShouldBe(baseline.ContentFingerprint());

        (baseline with { IncludeDiff = true }).ContentFingerprint().ShouldNotBe(baseline.ContentFingerprint());
        (baseline with { IncludeStat = false }).ContentFingerprint().ShouldNotBe(baseline.ContentFingerprint());
        (baseline with { IncludeMessage = false }).ContentFingerprint().ShouldNotBe(baseline.ContentFingerprint());
        (baseline with { MaxDiffBytes = 1 }).ContentFingerprint().ShouldNotBe(baseline.ContentFingerprint());
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
