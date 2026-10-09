using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// A line of an ignore file, or an entry of a glob list, is refused above <see cref="IgnoreRuleSet.MaxPatternLength"/>
/// characters before anything is compiled. A compiled glob retains memory in proportion to its length (about 186 KB
/// for 1,500 characters, measured by the review of the pull request that added the matcher), and a 10 MB line fed
/// through <c>.gitignore</c> took 938 MB. For <c>.dexiconignore</c> and for the lists the source fails; for
/// <c>.gitignore</c> and <c>.git/info/exclude</c> the line is skipped with a warning.
/// </summary>
public sealed class IgnorePatternLengthTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"length-{Guid.NewGuid():N}");

    public IgnorePatternLengthTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string content = "hello") =>
        File.WriteAllText(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)), content);

    private WorkspaceWalker.WalkResult Walk(string[]? exclude = null) =>
        WorkspaceWalker.Walk(_root, true, null, exclude, 1_000_000);

    [Fact]
    public void TheLimitIsFiveHundredCharacters()
    {
        IgnoreRuleSet.MaxPatternLength.ShouldBe(500);
    }

    [Fact]
    public void ALineOfExactlyTheLimitIsCompiled()
    {
        var rules = new IgnoreRuleSet();

        rules.AddPatterns([new string('a', IgnoreRuleSet.MaxPatternLength)], "test");

        rules.Count.ShouldBe(1);
    }

    [Fact]
    public void TrailingSpacesDoNotCountTowardsTheLimit()
    {
        var rules = new IgnoreRuleSet();

        rules.AddPatterns([new string('a', IgnoreRuleSet.MaxPatternLength) + "      "], "test");

        rules.Count.ShouldBe(1);
    }

    [Fact]
    public void ALineOneCharacterOverTheLimitIsRefusedNamingTheLimitAndShowingOnlyTheStart()
    {
        var line = new string('a', IgnoreRuleSet.MaxPatternLength + 1);

        var thrown = Should.Throw<IgnorePatternException>(() => new IgnoreRuleSet().AddPatterns(["x", line], "test"));

        thrown.Message.ShouldBe($"test line 2 ('{new string('a', 100)}...') is longer than 500 characters");
    }

    [Fact]
    public void ALongLineIsRefusedBeforeAnythingIsCompiled()
    {
        // A million characters of wildcards. Translated to a regular expression this allocates several
        // megabytes on this thread, so the allocation measures whether the translation ran. The matcher
        // cache is shared with tests running beside this one and cannot be read for the same purpose.
        var line = string.Concat(Enumerable.Repeat("*a", 500_000));
        var rules = new IgnoreRuleSet();
        var before = GC.GetAllocatedBytesForCurrentThread();

        Should.Throw<IgnorePatternException>(() => rules.AddPatterns([line], "test"));

        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBeLessThan(100_000);
    }

    [Fact]
    public void AnOverlongGitignoreLineIsSkippedWithAWarningAndTheRestApplies()
    {
        Write(".gitignore", new string('a', 501) + "\nsecret.txt\n");
        Write("secret.txt");
        Write("keep.txt");

        var walk = Walk();

        walk.Files.Select(f => f.RelativePath).OrderBy(p => p, StringComparer.Ordinal).ShouldBe([".gitignore", "keep.txt"]);
        walk.Warnings.ShouldBe([$".gitignore line 1 ('{new string('a', 100)}...') is longer than 500 characters; the line was skipped"]);
    }

    [Fact]
    public void AnOverlongDexiconignoreLineFailsTheWalk()
    {
        Write(WorkspaceWalker.IgnoreFileName, "*.log\n" + new string('a', 501) + "\n");

        Should.Throw<IgnorePatternException>(() => Walk())
            .Message.ShouldBe($".dexiconignore line 2 ('{new string('a', 100)}...') is longer than 500 characters");
    }

    [Fact]
    public void AnOverlongListEntryFailsTheWalk()
    {
        Write("keep.txt");

        Should.Throw<IgnorePatternException>(() => Walk(exclude: ["*.log", new string('a', 501)]))
            .Message.ShouldBe($"excludeGlobs[1] ('{new string('a', 100)}...') is longer than 500 characters");
    }

    [Fact]
    public void AStoredListIsCheckedAgainstTheSameLimit()
    {
        SourceFilters.FirstUnusable(["*.md", new string('a', 500)]).ShouldBeNull();
        SourceFilters.FirstUnusable(["*.md", new string('a', 501)]).ShouldBe(1);
    }

    [Fact]
    public void AStoredListIsCheckedAgainstTheRuleLimitToo()
    {
        var atLimit = Enumerable.Range(0, IgnoreRuleSet.MaxRulesPerSource).Select(i => $"x{i}").ToList();
        var over = atLimit.Append("one-too-many").ToList();

        SourceFilters.FirstUnusable(atLimit).ShouldBeNull();
        SourceFilters.FirstUnusable(over).ShouldBe(IgnoreRuleSet.MaxRulesPerSource);
        SourceFilters.FirstUnusable(over, SourceFilters.GlobReader.Git).ShouldBeNull("git reads a history source's list, not the walk");
    }
}
