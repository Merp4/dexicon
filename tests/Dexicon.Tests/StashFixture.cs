using System.Diagnostics;

namespace Dexicon.Tests;

/// <summary>
/// A repository built with the real git, for the stash tests. Both pipes are drained at once:
/// a <c>git add</c> that warns about line endings fills stderr, and reading stdout to the end
/// first leaves git blocked writing it.
/// </summary>
internal static class StashFixture
{
    public static string Git(string repo, params string[] args)
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
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();

        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr.GetAwaiter().GetResult()}");

        return stdout.GetAwaiter().GetResult();
    }

    public static void Init(string repo)
    {
        Git(repo, "init", "--initial-branch=main");
        Git(repo, "config", "user.email", "test@example.invalid");
        Git(repo, "config", "user.name", "A Test");
        Git(repo, "config", "commit.gpgsign", "false");
        // Bytes as written, so a size in a test is the size of the string.
        Git(repo, "config", "core.autocrlf", "false");
    }

    public static void Write(string repo, string path, string content)
    {
        var full = Path.Combine(repo, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public static void Commit(string repo, string path, string content, string message)
    {
        Write(repo, path, content);
        Git(repo, "add", path);
        Git(repo, "commit", "-m", message);
    }

    /// <summary>Stashes with <c>-u</c>, or <c>-a</c> for ignored files too, and returns the stash commit.</summary>
    public static string Stash(string repo, string message, bool all = false)
    {
        Git(repo, "stash", "push", all ? "--all" : "--include-untracked", "-m", message);
        return Git(repo, "rev-parse", "stash@{0}").Trim();
    }

    public static string Head(string repo) => Git(repo, "rev-parse", "HEAD").Trim();

    /// <summary>Removes a repository, whose objects git leaves read-only.</summary>
    public static void Delete(string repo)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(repo, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(repo, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
