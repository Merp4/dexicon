using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// A line in an ignore file that cannot be compiled, such as the class <c>[z-a]</c>, threw from
/// <see cref="IgnoreRuleSet.AddPatterns"/> on every pass over a tree holding it. Whose file it is decides what
/// happens now: <c>.gitignore</c> and <c>.git/info/exclude</c> are git's, and git applies nothing for a line it
/// cannot read, so the line is skipped and reported while the file's other lines apply. <c>.dexiconignore</c> is
/// Dexicon's own and is written to keep content out of the index, so skipping a line would index what it meant
/// to exclude, and the walk fails instead, naming the file, the line number and the line. An include or exclude
/// list entry that cannot be used also fails the walk, naming the list.
/// </summary>
public sealed class UnusableIgnoreFileLineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"unusable-line-{Guid.NewGuid():N}");

    public UnusableIgnoreFileLineTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string content = "hello")
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private WorkspaceWalker.WalkResult Walk(
        bool useGitignore = true, string[]? include = null, string[]? exclude = null) =>
        WorkspaceWalker.Walk(_root, useGitignore, include, exclude, 1_000_000);

    private static List<string> Names(WorkspaceWalker.WalkResult walk) =>
        [.. walk.Files.Select(f => f.RelativePath).OrderBy(p => p, StringComparer.Ordinal)];

    [Fact]
    public void AnUnusableGitignoreLineIsSkippedAndTheRestOfTheFileApplies()
    {
        Write(".gitignore", "*.log\n[z-a]\nsecret.txt\n");
        Write("a.log");
        Write("secret.txt");
        Write("keep.txt");

        var walk = Walk();

        Names(walk).ShouldBe([".gitignore", "keep.txt"]);
        walk.Warnings.ShouldBe([".gitignore line 2 ('[z-a]') cannot be compiled (reversed character range)"]);
    }

    [Fact]
    public void ANestedGitignoreIsNamedByItsPathFromTheRoot()
    {
        Write("sub/.gitignore", "# comment\n\n[z-a]\nsecret.txt\n");
        Write("sub/secret.txt");
        Write("sub/keep.txt");

        var walk = Walk();

        Names(walk).ShouldBe(["sub/.gitignore", "sub/keep.txt"]);
        walk.Warnings.ShouldBe(["sub/.gitignore line 3 ('[z-a]') cannot be compiled (reversed character range)"]);
    }

    [Fact]
    public void AnUnusableLineInTheLocalGitExcludeIsSkippedAndReported()
    {
        Write(".git/info/exclude", "[z-a]\nworktrees/\n");
        Write("worktrees/x.txt");
        Write("keep.txt");

        var walk = Walk();

        Names(walk).ShouldBe(["keep.txt"]);
        walk.Warnings.ShouldBe([".git/info/exclude line 1 ('[z-a]') cannot be compiled (reversed character range)"]);
    }

    [Fact]
    public void EveryUnusableLineIsReportedOnce()
    {
        Write(".gitignore", "[z-a]\n[y-b]\n");
        Write("keep.txt");

        Walk().Warnings.Count.ShouldBe(2);
    }

    [Fact]
    public void AGitignoreOfUsableLinesRaisesNoWarning()
    {
        // The control for the tests above: nothing is reported when nothing is wrong.
        Write(".gitignore", "*.log\n[a-c]*.tmp\n[]x]\n");
        Write("keep.txt");

        Walk().Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void AGitignoreIsNotReadWhenGitignoreIsOffSoItsLineIsNotReported()
    {
        Write(".gitignore", "[z-a]\n");
        Write("keep.txt");

        Walk(useGitignore: false).Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void AnUnusableDexiconignoreLineFailsTheWalkNamingTheFileTheLineNumberAndTheLine()
    {
        Write(WorkspaceWalker.IgnoreFileName, "*.log\n[z-a]\n");
        Write("keep.txt");

        var thrown = Should.Throw<IgnorePatternException>(() => Walk());

        thrown.Message.ShouldBe(".dexiconignore line 2 ('[z-a]') cannot be compiled (reversed character range)");
    }

    [Fact]
    public void AnUnusableLineInANestedDexiconignoreFailsTheWalkNamingItsPath()
    {
        Write("sub/.dexiconignore", "[z-a]\n");
        Write("sub/keep.txt");

        Should.Throw<IgnorePatternException>(() => Walk())
            .Message.ShouldBe("sub/.dexiconignore line 1 ('[z-a]') cannot be compiled (reversed character range)");
    }

    [Fact]
    public void ADexiconignoreIsReadWhenGitignoreIsOffSoItsLineStillFailsTheWalk()
    {
        Write(WorkspaceWalker.IgnoreFileName, "[z-a]\n");

        Should.Throw<IgnorePatternException>(() => Walk(useGitignore: false));
    }

    [Fact]
    public void AnUnusableExcludeListEntryFailsTheWalkNamingTheListAndTheEntry()
    {
        Write("keep.txt");

        Should.Throw<IgnorePatternException>(() => Walk(exclude: ["*.log", "[z-a]"]))
            .Message.ShouldBe("exclude_globs entry 2 ('[z-a]') cannot be compiled (reversed character range)");
    }

    [Fact]
    public void AnUnusableIncludeListEntryFailsTheWalkNamingTheListAndTheEntry()
    {
        Write("keep.txt");

        Should.Throw<IgnorePatternException>(() => Walk(include: ["[z-a]"]))
            .Message.ShouldBe("include_globs entry 1 ('[z-a]') cannot be compiled (reversed character range)");
    }

    [Fact]
    public void ANullListEntryFailsTheWalkNamingTheList()
    {
        Write("keep.txt");

        Should.Throw<IgnorePatternException>(() => Walk(exclude: ["*.log", null!]))
            .Message.ShouldBe("exclude_globs entry 2 is null");
    }

    [Fact]
    public void AGlobTooLongForTheMatcherIsSkippedInAGitignoreAndFailsADexiconignore()
    {
        var tooLong = string.Concat(Enumerable.Repeat("*a", 2000)) + "b";
        Write(".gitignore", tooLong + "\nsecret.txt\n");
        Write("secret.txt");
        Write("keep.txt");

        var walk = Walk();
        Names(walk).ShouldBe([".gitignore", "keep.txt"]);
        walk.Warnings.Count.ShouldBe(1);
        walk.Warnings[0].ShouldStartWith(".gitignore line 1 ('");
        walk.Warnings[0].ShouldEndWith("...') is too long to match");

        File.Delete(Path.Combine(_root, ".gitignore"));
        Write(WorkspaceWalker.IgnoreFileName, tooLong + "\n");
        Should.Throw<IgnorePatternException>(() => Walk()).Message.ShouldEndWith("...') is too long to match");
    }

    [Fact]
    public void ControlCharactersInAnUnusableLineAreNotCarriedIntoTheMessage()
    {
        Write(".gitignore", "[z-a]\u0007\u001b[0m\n");
        Write("keep.txt");

        var warning = Walk().Warnings.ShouldHaveSingleItem();

        warning.ShouldNotContain("\u0007");
        warning.ShouldNotContain("\u001b");
        warning.ShouldContain("('[z-a]??[0m')");
    }

    [Fact]
    public void TheRuleSetSkipsAnUnusableLineAndKeepsTheOthersWhenGivenAPlaceToReportIt()
    {
        var rules = new IgnoreRuleSet();
        var unusable = new List<string>();

        rules.AddPatterns(["a.txt", "[z-a]", "b.txt"], "list", unusable: unusable);

        rules.Count.ShouldBe(2);
        unusable.ShouldBe(["list line 2 ('[z-a]') cannot be compiled (reversed character range)"]);
        rules.IsIgnored("b.txt", isDirectory: false).ShouldBeTrue();
    }

    [Fact]
    public void TheRuleSetThrowsOnAnUnusableLineWhenNotGivenAPlaceToReportIt()
    {
        // Still an ArgumentException, as the failure was before it had a type of its own.
        Should.Throw<ArgumentException>(() => new IgnoreRuleSet().AddPatterns(["a.txt", "[z-a]"], "list"))
            .ShouldBeOfType<IgnorePatternException>();
    }
}
