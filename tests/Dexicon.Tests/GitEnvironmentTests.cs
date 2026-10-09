using System.Diagnostics;
using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// Setting a GIT_ variable for the git processes the code under test starts means setting it for this
/// process, so nothing else may run while it is set.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessGitEnvironmentCollection
{
    public const string Name = "Process git environment";
}

/// <summary>
/// No GIT_ variable of the service reaches git, whatever it is called. Git reads them from its environment, and
/// each of these changes what a call returns or whether it works: the four that set how a pathspec is read,
/// <c>GIT_DIFF_OPTS</c> (more patch context than the pinned configuration asks for), <c>GIT_DIR</c> and
/// <c>GIT_WORK_TREE</c> (another repository), <c>GIT_CONFIG_COUNT</c> with its keys and values (any key the
/// pins do not name), <c>GIT_EXTERNAL_DIFF</c> and <c>GIT_PAGER</c>.
/// </summary>
[Collection(ProcessGitEnvironmentCollection.Name)]
public sealed class GitEnvironmentTests : IDisposable
{
    private static readonly string[] Variables =
    [
        "GIT_LITERAL_PATHSPECS", "GIT_GLOB_PATHSPECS", "GIT_NOGLOB_PATHSPECS", "GIT_ICASE_PATHSPECS",
        "GIT_DIFF_OPTS", "GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0", "GIT_DIR", "GIT_WORK_TREE",
        "GIT_EXTERNAL_DIFF", "GIT_PAGER", "GIT_TRACE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY",
    ];

    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"githist-env-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> _saved = Variables.ToDictionary(v => v, Environment.GetEnvironmentVariable);

    public GitEnvironmentTests()
    {
        Directory.CreateDirectory(_repo);
        Git("init", "--quiet", "--initial-branch=main");
        Git("config", "user.email", "test@example.invalid");
        Git("config", "user.name", "A Test");
        Git("config", "commit.gpgsign", "false");
    }

    private void Git(params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) info.ArgumentList.Add(a);

        using var p = Process.Start(info)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed");
    }

    private void Commit(string path, string content, string message)
    {
        var full = Path.Combine(_repo, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        Git("add", path);
        Git("commit", "-m", message);
    }

    private GitRepository Repo() =>
        GitHistory.RepositoryIn(Path.GetDirectoryName(_repo)!, Path.GetFileName(_repo))
        ?? throw new InvalidOperationException($"{_repo} was not created");

    [Fact]
    public void NoGitVariableOfTheServiceReachesGitExceptThePinsThatSayHowItRuns()
    {
        // Set here so the removal is exercised: a process started without them would pass whether or not the code removes them.
        foreach (var variable in Variables) Environment.SetEnvironmentVariable(variable, "1");

        var info = GitHistory.StartInfo(Repo(), ["log"], redirectStdin: false);

        foreach (var variable in Variables)
            info.Environment.ContainsKey(variable).ShouldBeFalse(variable);

        info.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).Order()
            .ShouldBe(["GIT_OPTIONAL_LOCKS", "GIT_TERMINAL_PROMPT"]);
        info.Environment["GIT_TERMINAL_PROMPT"].ShouldBe("0");
        info.Environment["GIT_OPTIONAL_LOCKS"].ShouldBe("0");
    }

    [Fact]
    public void AGitVariableIsRemovedWhateverTheCaseOfItsName()
    {
        Environment.SetEnvironmentVariable("git_Diff_Opts", "--unified=9");

        try
        {
            var info = GitHistory.StartInfo(Repo(), ["log"], redirectStdin: false);

            info.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).Order()
                .ShouldBe(["GIT_OPTIONAL_LOCKS", "GIT_TERMINAL_PROMPT"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("git_Diff_Opts", null);
        }
    }

    [Fact]
    public async Task AnIncludeMatchesAsItIsWrittenWhenTheServiceIsStartedWithLiteralPathspecs()
    {
        Commit("docs/a.md", "one", "touch docs");

        // With the variable, git matches `:(glob)docs/*.md` as a literal name and selects nothing.
        Environment.SetEnvironmentVariable("GIT_LITERAL_PATHSPECS", "1");

        var commits = await GitHistory.EnumerateAsync(Repo(), new GitHistoryOptions(), [":(glob)docs/*.md"], default);

        commits.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ACallReadsTheRepositoryItWasGivenWhenTheServiceIsStartedWithAGitDir()
    {
        Commit("a.txt", "one", "first");

        // With the variable every call fails: it names a repository that is not there.
        Environment.SetEnvironmentVariable("GIT_DIR", Path.Combine(_repo, "no-such-git-dir"));

        (await GitHistory.EnumerateAsync(Repo(), new GitHistoryOptions(), null, default)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ADocumentHoldsThePinnedPatchContextWhenTheServiceIsStartedWithDiffOptions()
    {
        File.WriteAllText(Path.Combine(_repo, "f.txt"), string.Join('\n', Enumerable.Range(1, 40).Select(i => $"line {i}")) + "\n");
        Git("add", "f.txt");
        Git("commit", "-m", "first");
        File.WriteAllText(Path.Combine(_repo, "f.txt"),
            string.Join('\n', Enumerable.Range(1, 40).Select(i => i == 20 ? "changed" : $"line {i}")) + "\n");
        Git("commit", "-am", "second");
        var options = new GitHistoryOptions { IncludeDiff = true };
        var commits = await GitHistory.EnumerateAsync(Repo(), options, null, default);

        // With the variable the patch carries nine lines of context either side of the change, not the pinned three.
        Environment.SetEnvironmentVariable("GIT_DIFF_OPTS", "--unified=9");

        var read = new Dictionary<string, string>();
        await foreach (var (sha, text) in GitHistory.ReadAsync(Repo(), options, [commits[0].Sha], null))
            read[sha] = text;

        // Context lines start with a space. The hunk header repeats the line before the hunk, so the text alone is no test.
        var context = read[commits[0].Sha].Split('\n').Where(l => l.StartsWith(" line ", StringComparison.Ordinal)).ToList();
        context.ShouldContain(" line 17");
        context.ShouldNotContain(" line 16");
    }

    public void Dispose()
    {
        foreach (var (variable, value) in _saved) Environment.SetEnvironmentVariable(variable, value);

        try
        {
            foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_repo, recursive: true);
        }
        catch (IOException) { /* a temp directory left behind is not a test failure */ }
    }
}
