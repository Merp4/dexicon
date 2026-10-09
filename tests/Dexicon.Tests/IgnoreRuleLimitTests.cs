using Dexicon.Core.Indexing;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// What one walk will read and report. It reads at most <see cref="IgnoreRuleSet.MaxRulesPerSource"/> rules from
/// ignore files and glob lists together; past that a <c>.dexiconignore</c> or a list fails the walk, and a
/// <c>.gitignore</c> or <c>.git/info/exclude</c> is skipped from that line with one warning. A walk holds the first
/// <see cref="WarningSink.MaxKept"/> warnings and counts the rest, and the index pass logs those once.
/// </summary>
public sealed class IgnoreRuleLimitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"limits-{Guid.NewGuid():N}");

    public IgnoreRuleLimitTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string content) =>
        File.WriteAllText(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)), content);

    private static string Rules(int count) =>
        string.Join("\n", Enumerable.Range(0, count).Select(i => $"generated{i}.txt")) + "\n";

    private WorkspaceWalker.WalkResult Walk(string[]? exclude = null) =>
        WorkspaceWalker.Walk(_root, true, null, exclude, 1_000_000);

    [Fact]
    public void TheLimitIsTenThousandRules()
    {
        IgnoreRuleSet.MaxRulesPerSource.ShouldBe(10_000);
    }

    [Fact]
    public void ADexiconignoreOfExactlyTheLimitIsRead()
    {
        Write(WorkspaceWalker.IgnoreFileName, Rules(IgnoreRuleSet.MaxRulesPerSource));
        Write("generated9999.txt", "x");
        Write("keep.txt", "x");

        var names = Walk().Files.Select(f => f.RelativePath).OrderBy(p => p, StringComparer.Ordinal).ToList();

        names.ShouldBe([".dexiconignore", "keep.txt"]);
    }

    [Fact]
    public void ADexiconignoreOneRuleOverTheLimitFailsTheWalkNamingTheLine()
    {
        Write(WorkspaceWalker.IgnoreFileName, Rules(IgnoreRuleSet.MaxRulesPerSource + 1));

        Should.Throw<IgnorePatternException>(() => Walk()).Message.ShouldBe(
            ".dexiconignore line 10001 ('generated10000.txt') is past the limit of 10,000 rules for one source");
    }

    [Fact]
    public void AGitignorePastTheLimitIsSkippedFromThatLineWithOneWarning()
    {
        Write(".gitignore", Rules(IgnoreRuleSet.MaxRulesPerSource + 500));
        Write("generated0.txt", "x");
        Write("generated10400.txt", "x");

        var walk = Walk();

        walk.Files.Select(f => f.RelativePath).ShouldContain("generated10400.txt", "a rule past the limit is not in force");
        walk.Files.Select(f => f.RelativePath).ShouldNotContain("generated0.txt", "a rule within the limit is");
        walk.Warnings.ShouldBe([
            ".gitignore line 10001 ('generated10000.txt') is past the limit of 10,000 rules for one source; this line and the lines after it were skipped"]);
        walk.WarningsOmitted.ShouldBe(0);
    }

    [Fact]
    public void TheLimitIsOneBudgetAcrossTheFilesAndListsOfAWalk()
    {
        // The root's file takes most of it, and a nested file and the exclude list find the rest gone.
        Write(".gitignore", Rules(IgnoreRuleSet.MaxRulesPerSource - 1));
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        Write("sub/.gitignore", "one.txt\ntwo.txt\nthree.txt\n");

        var walk = Walk();

        walk.Warnings.Count.ShouldBe(1);
        walk.Warnings[0].ShouldStartWith("sub/.gitignore line 2 ('two.txt') is past the limit");
    }

    [Fact]
    public void AListPastTheLimitFailsTheWalkNamingTheEntry()
    {
        var exclude = Enumerable.Range(0, IgnoreRuleSet.MaxRulesPerSource + 1).Select(i => $"x{i}").ToArray();

        Should.Throw<IgnorePatternException>(() => Walk(exclude)).Message.ShouldBe(
            "excludeGlobs[10000] ('x10000') is past the limit of 10,000 rules for one source");
    }

    [Fact]
    public void ABlankOrCommentLineDoesNotUseARule()
    {
        Write(WorkspaceWalker.IgnoreFileName, string.Concat(Enumerable.Repeat("# comment\n\n", 6_000)) + Rules(IgnoreRuleSet.MaxRulesPerSource));

        Should.NotThrow(() => Walk());
    }

    [Fact]
    public void ABudgetOfItsOwnCountsWhatItIsGiven()
    {
        var budget = new RuleBudget(2);
        var rules = new IgnoreRuleSet();
        var sink = new WarningSink();

        rules.AddPatterns(["a", "b", "c", "d"], "list", unusable: sink, budget: budget);

        rules.Count.ShouldBe(2);
        budget.Used.ShouldBe(2);
        budget.Exhausted.ShouldBeTrue();
        sink.Count.ShouldBe(1);
    }

    [Fact]
    public void FiftyThousandBadLinesKeepTwentyWarningsAndCountTheRest()
    {
        Write(".gitignore", string.Concat(Enumerable.Repeat("[z-a]\n", 9_000)));

        var walk = Walk();

        walk.Warnings.Count.ShouldBe(WarningSink.MaxKept);
        walk.WarningsOmitted.ShouldBe(9_000 - WarningSink.MaxKept);
        walk.Warnings[0].ShouldStartWith(".gitignore line 1 ");
    }

    [Fact]
    public void TheWarningSinkKeepsTheFirstTwentyInOrder()
    {
        var sink = new WarningSink();
        for (var i = 0; i < 25; i++) sink.Add($"warning {i}");

        sink.Kept.ShouldBe(Enumerable.Range(0, 20).Select(i => $"warning {i}"));
        sink.Count.ShouldBe(25);
        sink.Omitted.ShouldBe(5);
    }

    [Fact]
    public async Task APassLogsTheFirstTwentyOnceAndTheCountOnceHoweverManyChunkSetsWalkTheSource()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.WriteFileAsync(".gitignore", string.Concat(Enumerable.Repeat("[z-a]\n", 50)));
        await harness.WriteFileAsync("kept.md", IndexingHarness.Prose("kept"));
        await harness.SeedCorpusAsync(Dexicon.Core.Catalog.SourceKind.Workspace, sets: 3);
        var log = new RecordingLoggerFactory();

        await harness.RunIndexAsync(log: new Logger<CorpusIndexer>(log));

        log.Lines.Count(l => l.Contains(".gitignore line", StringComparison.Ordinal)).ShouldBe(WarningSink.MaxKept);
        log.Lines.Count(l => l.Contains("30 more ignore-file", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public void ARuleCostsFewBytesToBuild()
    {
        // Memory per rule, measured here by what is allocated building them: the order of a few hundred bytes for a
        // line like `generated123.txt`, where the engine this replaced needed about 390 KiB.
        var rules = new IgnoreRuleSet();
        var lines = Enumerable.Range(0, IgnoreRuleSet.MaxRulesPerSource).Select(i => $"generated{i}.txt").ToList();
        var before = GC.GetAllocatedBytesForCurrentThread();

        rules.AddPatterns(lines, "test");

        var perRule = (GC.GetAllocatedBytesForCurrentThread() - before) / IgnoreRuleSet.MaxRulesPerSource;
        perRule.ShouldBeLessThan(2_000, $"bytes allocated per rule: {perRule}");
    }
}
