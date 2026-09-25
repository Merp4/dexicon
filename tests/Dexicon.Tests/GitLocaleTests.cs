using System.Diagnostics;
using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// Setting <c>LANGUAGE</c> for the git processes the code under test starts means setting
/// it for this process, so nothing else may run while it is set.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessLanguageCollection
{
    public const string Name = "Process language";
}

/// <summary>
/// Every git call runs in the C locale, whatever language the service was started in.
///
/// Read from how the process is started rather than from what git prints: neither git this
/// was measured on, the image's 2.54.0 and Git for Windows 2.31.1, has translations, so a
/// test of the output would pass with or without the pin and prove nothing.
/// </summary>
[Collection(ProcessLanguageCollection.Name)]
public sealed class GitLocaleTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"githist-locale-{Guid.NewGuid():N}");
    private readonly string? _language = Environment.GetEnvironmentVariable("LANGUAGE");

    public GitLocaleTests()
    {
        Directory.CreateDirectory(_repo);
        using var init = Process.Start(new ProcessStartInfo("git", ["init", "--quiet", _repo])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        init.WaitForExit();
    }

    private GitRepository Repo() =>
        GitHistory.RepositoryIn(Path.GetDirectoryName(_repo)!, Path.GetFileName(_repo))
        ?? throw new InvalidOperationException($"{_repo} was not created");

    [Fact]
    public void EveryCallRunsInTheCLocale()
    {
        // Set here so the removal is exercised: a process started with no LANGUAGE would
        // pass this whether or not the code removes it.
        Environment.SetEnvironmentVariable("LANGUAGE", "de");

        var info = GitHistory.StartInfo(Repo(), ["log"], redirectStdin: false);

        info.Environment["LC_ALL"].ShouldBe("C");
        info.Environment.ContainsKey("LANGUAGE").ShouldBeFalse(
            "gettext prefers LANGUAGE to LC_ALL for messages unless the locale is C");
    }

    /// <summary>
    /// <c>safe.directory</c> names the one repository, per call. Without it every history
    /// source reports unavailable in the shipped container, where the mount belongs to
    /// another user, while every other test passes, because a test runs as the owner.
    /// </summary>
    [Fact]
    public void EveryCallTrustsOnlyTheRepositoryItReads()
    {
        var repo = Repo();

        var info = GitHistory.StartInfo(repo, ["log"], redirectStdin: false);

        info.ArgumentList.ShouldContain("safe.directory=" + repo.FullPath);
        info.ArgumentList.ShouldNotContain("safe.directory=*");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("LANGUAGE", _language);
        try
        {
            foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_repo, recursive: true);
        }
        catch (IOException) { /* a temp directory left behind is not a test failure */ }
    }
}
