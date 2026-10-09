using Dexicon.Api;
using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// A line of an ignore file, or an entry of a glob list, is refused above <see cref="IgnoreRuleSet.MaxPatternLength"/>
/// characters before it is read into tokens. For <c>.dexiconignore</c> and for the lists the source fails; for
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
    public void ALineOfExactlyTheLimitIsRead()
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
    public void ALongLineIsRefusedBeforeItIsReadIntoTokens()
    {
        // A million characters of wildcards. Read into tokens this allocates tens of megabytes on this thread, so the
        // allocation measures whether the line was read.
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
    public void TheApiRefusalOfAnOverlongEntryNamesTheLengthAndTheListLimits()
    {
        var refusal = CorpusEndpoints.UnusableGlobs(["*.md", new string('a', 501)], null).ShouldNotBeNull();

        refusal.Status.ShouldBe(400);
        refusal.Detail.ShouldBe(
            "includeGlobs[1] (include[1] for the configure tools) is null, or a pattern that does not compile (such as [z-a]) "
            + "or is longer than 500 characters, or the list goes past 1,000 rules or 12,000 pattern parts. Nothing was saved.");
    }

    [Fact]
    public void TheApiRefusalOfAListPastItsLimitsIsTheSameText()
    {
        var list = Enumerable.Range(0, 1_001).Select(i => $"x{i}").ToArray();

        CorpusEndpoints.UnusableGlobs(null, list).ShouldNotBeNull().Detail.ShouldStartWith("excludeGlobs[1000] (exclude[1000] for the configure tools)");
    }
}
