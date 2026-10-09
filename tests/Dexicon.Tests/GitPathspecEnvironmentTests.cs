using System.Diagnostics;
using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// Setting a pathspec variable for the git processes the code under test starts means setting it for this
/// process, so nothing else may run while it is set.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessPathspecEnvironmentCollection
{
    public const string Name = "Process pathspec environment";
}

/// <summary>
/// Every git call reads pathspecs by their own text, whatever the service's environment sets. Git reads
/// <c>GIT_LITERAL_PATHSPECS</c>, <c>GIT_GLOB_PATHSPECS</c>, <c>GIT_NOGLOB_PATHSPECS</c> and
/// <c>GIT_ICASE_PATHSPECS</c> from the environment, and each changes what every pathspec means.
/// </summary>
[Collection(ProcessPathspecEnvironmentCollection.Name)]
public sealed class GitPathspecEnvironmentTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"githist-pathspec-env-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> _saved = GitHistory.PathspecEnvironment
        .ToDictionary(v => v, Environment.GetEnvironmentVariable);

    public GitPathspecEnvironmentTests()
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

    private GitRepository Repo() =>
        GitHistory.RepositoryIn(Path.GetDirectoryName(_repo)!, Path.GetFileName(_repo))
        ?? throw new InvalidOperationException($"{_repo} was not created");

    [Fact]
    public void NoPathspecVariableOfTheServiceReachesGit()
    {
        // Set here so the removal is exercised: a process started without them would pass whether or not the code removes them.
        foreach (var variable in GitHistory.PathspecEnvironment) Environment.SetEnvironmentVariable(variable, "1");

        var info = GitHistory.StartInfo(Repo(), ["log"], redirectStdin: false);

        GitHistory.PathspecEnvironment.Length.ShouldBe(4);
        foreach (var variable in GitHistory.PathspecEnvironment)
            info.Environment.ContainsKey(variable).ShouldBeFalse(variable);
    }

    [Fact]
    public async Task AnIncludeMatchesAsItIsWrittenWhenTheServiceIsStartedWithLiteralPathspecs()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "docs"));
        File.WriteAllText(Path.Combine(_repo, "docs", "a.md"), "one");
        Git("add", "docs/a.md");
        Git("commit", "-m", "touch docs");

        // With the variable, git matches `:(glob)docs/*.md` as a literal name and selects nothing.
        Environment.SetEnvironmentVariable("GIT_LITERAL_PATHSPECS", "1");

        var commits = await GitHistory.EnumerateAsync(Repo(), new GitHistoryOptions(), [":(glob)docs/*.md"], default);

        commits.ShouldHaveSingleItem();
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
