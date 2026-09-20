using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// Skipping a directory the walk would only have thrown away.
///
/// A sweep of a real repository enumerated 240,704 files to keep 26,998, because every
/// directory was walked and the files filtered afterwards; `.git`, `node_modules` and a
/// database's data directory were all stat'd in full across the mount.
///
/// Pruning is only allowed where it is EQUIVALENT to filtering, and negation is what makes
/// that conditional. The repository that prompted this carries `!.vscode/launch.json` while
/// `.vscode/` is always excluded, so pruning on the directory alone would stop indexing a
/// file that is indexed today. These fix that boundary, because getting it wrong is silent.
/// </summary>
public sealed class DirectoryPruningTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"prune-{Guid.NewGuid():N}");

    public DirectoryPruningTests() => Directory.CreateDirectory(_root);

    private void Write(string relative, string content = "hello")
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private List<string> Walk(bool useGitignore = true) =>
        WorkspaceWalker.Walk(_root, useGitignore, null, null, 1_000_000)
            .Files.Select(f => f.RelativePath).OrderBy(p => p, StringComparer.Ordinal).ToList();

    [Fact]
    public void AnAlwaysExcludedDirectoryContributesNothing()
    {
        Write("src/app.cs");
        Write("node_modules/pkg/index.js");
        Write("node_modules/pkg/deep/nested/more.js");
        Write(".git/config");

        Walk().ShouldBe(["src/app.cs"]);
    }

    [Fact]
    public void ANegatedFileUnderAnExcludedDirectoryIsStillFound()
    {
        // The case that makes pruning conditional rather than automatic. `.vscode/` is
        // always excluded and this repository deliberately keeps one file from it, so a
        // walk that skipped the directory outright would stop indexing a file it indexes
        // today, with nothing to show for it.
        Write(".gitignore", "!.vscode/launch.json\n");
        Write(".vscode/settings.json");
        Write(".vscode/launch.json");
        Write("src/app.cs");

        Walk().ShouldBe([".gitignore", ".vscode/launch.json", "src/app.cs"]);
    }

    [Fact]
    public void ANegationThatAppliesAtAnyDepthKeepsEveryDirectoryWalked()
    {
        // `!*.md` has no interior slash, so it can re-include a file anywhere and nothing
        // can be skipped on its account.
        Write(".gitignore", "!*.md\n");
        Write("node_modules/pkg/readme.md");
        Write("node_modules/pkg/index.js");
        Write("src/app.cs");

        Walk().ShouldContain("node_modules/pkg/readme.md");
        Walk().ShouldNotContain("node_modules/pkg/index.js");
    }

    [Fact]
    public void ASiblingOfANegatedDirectoryIsStillSkipped()
    {
        // The whole point: one narrow negation must not disable pruning everywhere. The
        // repository this came from excludes `data/` and keeps `data/sessions/`, while the
        // bulk of the tree is a sibling of it.
        Write(".gitignore", "data/\n!data/sessions/\n");
        Write("data/sessions/a.json");
        Write("data/mariadb/ibdata1");
        Write("src/app.cs");

        Walk().ShouldBe([".gitignore", "data/sessions/a.json", "src/app.cs"]);
    }

    [Theory]
    // A negation reaching into the directory, so it cannot be skipped.
    [InlineData("!.vscode/launch.json", ".vscode", true)]
    [InlineData("!data/sessions/", "data", true)]
    // The directory sits inside the negation's scope.
    [InlineData("!keep/", "keep/inner", true)]
    // Applies at any depth, so nothing is prunable.
    [InlineData("!*.md", "node_modules", true)]
    // Unrelated, so the directory can go.
    [InlineData("!data/sessions/", "node_modules", false)]
    [InlineData("!.vscode/launch.json", "data/mariadb", false)]
    public void MayReincludeBeneathAnswersConservatively(string pattern, string directory, bool expected)
    {
        var rules = new IgnoreRuleSet();
        rules.AddPatterns([pattern], "test");

        rules.MayReincludeBeneath(directory).ShouldBe(expected);
    }

    [Fact]
    public void WithNoNegationsNothingBlocksPruning()
    {
        var rules = new IgnoreRuleSet();
        rules.AddPatterns(["node_modules/", "bin/", "*.dll"], "test");

        rules.MayReincludeBeneath("node_modules").ShouldBeFalse();
        rules.MayReincludeBeneath("anything/at/all").ShouldBeFalse();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a handle the OS has not let go of yet */ }
    }
}
