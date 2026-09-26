using System.Text.RegularExpressions;
using Dexicon.Api;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// The command a new key comes with, and every copy of it in the docs, registers Dexicon at
/// Claude Code's user scope. Its project and local scopes are keyed by the literal
/// working-directory string, so a server added from one shell can be missing from a session
/// started in another. The command and all five documented copies once left the scope out.
/// </summary>
public sealed class McpAddCommandTests
{
    [Fact]
    public void TheAccessDialogsCommandRegistersAtUserScope()
    {
        var command = SystemEndpoints.McpAddCommand("dex_example");

        command.ShouldContain("claude mcp add --transport http dexicon http://localhost:8477/mcp");
        command.ShouldContain("--header \"Authorization: Bearer dex_example\"");
        command.ShouldEndWith("--scope user");
    }

    [Fact]
    public void EveryDocumentedAddCommandNamesItsScope()
    {
        var root = RepoRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md")
            .Append(Path.Combine(root, "README.md"));

        // Fenced blocks only: prose that mentions the command in passing records a run, and
        // is not something to paste.
        var fence = new Regex(@"```[a-z]*\r?\n(.*?)```", RegexOptions.Singleline);
        var blocks = files
            .SelectMany(f => fence.Matches(File.ReadAllText(f))
                .Select(m => (File: Path.GetRelativePath(root, f), Text: m.Groups[1].Value)))
            .Where(b => b.Text.Contains("claude mcp add", StringComparison.Ordinal))
            .ToList();

        // The scan's reach: README, docs/06, 08, 09 and 12 each hold one.
        blocks.Count.ShouldBeGreaterThanOrEqualTo(5, string.Join(", ", blocks.Select(b => b.File)));

        blocks.Where(b => !b.Text.Contains("--scope", StringComparison.Ordinal))
            .Select(b => b.File).ShouldBeEmpty("a documented `claude mcp add` with no --scope");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Dexicon.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
