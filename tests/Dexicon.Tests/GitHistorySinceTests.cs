using System.Diagnostics;
using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// Setting <c>TZ</c> for the git processes the code under test starts means setting it for
/// this process, so nothing else may run while it is set.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessTimeZoneCollection
{
    public const string Name = "Process time zone";
}

/// <summary>
/// <see cref="GitHistoryOptions.Since"/> is 00:00 UTC on its date. Commits are made either
/// side of that midnight with pinned dates, so neither the clock nor the machine's zone
/// decides the answer.
///
/// Passed to git as a bare date it was neither: git reads a bare date as that date at the
/// current time of day, in its own local zone. Measured at 08:56 UTC, `--since=2026-01-02`
/// dropped commits from 00:30 and 06:00 that day.
/// </summary>
[Collection(ProcessTimeZoneCollection.Name)]
public sealed class GitHistorySinceTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"githist-since-{Guid.NewGuid():N}");
    private readonly string _before;
    private readonly string _after;

    public GitHistorySinceTests()
    {
        Directory.CreateDirectory(_repo);
        Git(null, "init", "--initial-branch=main");
        Git(null, "config", "user.email", "test@example.invalid");
        Git(null, "config", "user.name", "A Test");
        Git(null, "config", "commit.gpgsign", "false");

        _before = CommitAt("before", "2026-01-01T23:59:59Z");
        _after = CommitAt("after", "2026-01-02T00:00:01Z");
    }

    [Fact]
    public async Task SinceIsMidnightUtcOnThatDate()
    {
        (await CommitsSince(new DateOnly(2026, 1, 2))).ShouldBe([_after]);
        (await CommitsSince(new DateOnly(2026, 1, 1))).ShouldBe([_before, _after], ignoreOrder: true);
    }

    [Fact]
    public async Task WhateverZoneGitRunsIn()
    {
        // UTC+14. Read as local midnight, 2 January begins at 10:00 UTC on the 1st and
        // `before` falls inside it. Git on Linux reads its zone from TZ; Git for Windows
        // ignores it (measured with 2.31), so this passes there whatever the argument says.
        var previous = Environment.GetEnvironmentVariable("TZ");
        Environment.SetEnvironmentVariable("TZ", "Etc/GMT-14");
        try
        {
            (await CommitsSince(new DateOnly(2026, 1, 2))).ShouldBe([_after]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TZ", previous);
        }
    }

    private async Task<string[]> CommitsSince(DateOnly since)
    {
        var repo = GitHistory.RepositoryIn(Path.GetDirectoryName(_repo)!, Path.GetFileName(_repo))!;
        var commits = await GitHistory.EnumerateAsync(repo, new GitHistoryOptions { Since = since }, null, default);
        return [.. commits.Select(c => c.Sha)];
    }

    private string CommitAt(string name, string when)
    {
        File.WriteAllText(Path.Combine(_repo, name), name);
        Git(null, "add", name);
        Git(when, "commit", "-m", name);
        return Git(null, "rev-parse", "HEAD").Trim();
    }

    private string Git(string? date, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) info.ArgumentList.Add(a);

        // `--since` reads the committer date, so both are pinned.
        if (date is not null)
        {
            info.Environment["GIT_AUTHOR_DATE"] = date;
            info.Environment["GIT_COMMITTER_DATE"] = date;
        }

        using var p = Process.Start(info)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();

        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed: {stderr.GetAwaiter().GetResult()}");

        return stdout.GetAwaiter().GetResult();
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
    }
}
