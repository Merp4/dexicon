using System.Diagnostics;
using Dexicon.Core.Indexing;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Dexicon.Tests;

/// <summary>
/// What a walk costs at the limits (<see cref="IgnoreRuleSet.MaxRulesPerSource"/>,
/// <see cref="IgnoreRuleSet.MaxWeightPerSource"/>): the work to test one file against every rule the limits let in, for
/// the patterns that cost most, and the memory those rules are built from. These are the figures
/// <c>docs/04-ingestion.md</c> gives. Show them with
/// <c>dotnet test tests/Dexicon.Tests --filter "Category=Benchmark" --logger "console;verbosity=detailed"</c>.
///
/// The work is counted in steps (<see cref="GlobMatcher.IsMatch(ReadOnlySpan{char}, ref long)"/>), which are the same on
/// every machine and every load, and asserted against the figure measured. The time is printed and not asserted: it
/// is what the steps cost on the machine that runs the test: about 1.5 ns each in a release build on an idle one.
/// </summary>
[Trait("Category", "Benchmark")]
public sealed class IgnoreRuleCostTests(ITestOutputHelper output)
{
    private static string Stars => string.Concat(Enumerable.Repeat("*a", 248)) + "*b";

    /// <summary>The matchers a budget lets in for a glob, as a walk would hold them.</summary>
    private static List<GlobMatcher> AtTheLimit(string glob)
    {
        var budget = RuleBudget.ForIgnoreFiles();
        var rules = new IgnoreRuleSet();
        var matchers = new List<GlobMatcher>();
        for (var n = 0; ; n++)
        {
            var line = string.Format(System.Globalization.CultureInfo.InvariantCulture, glob, n);
            try { rules.AddPatterns([line], "bench", budget: budget); }
            catch (IgnorePatternException) { break; }

            matchers.Add(GlobMatcher.Compile(line, string.Empty));
        }

        return matchers;
    }

    /// <summary>The steps to test one path against every matcher, and the time it took.</summary>
    private static (long Steps, double Milliseconds) Test(List<GlobMatcher> matchers, string path, bool beneath = false)
    {
        long steps = 0;
        var clock = Stopwatch.StartNew();
        foreach (var matcher in matchers)
        {
            if (beneath) matcher.IsMatchBeneath(path, ref steps);
            else matcher.IsMatch(path, ref steps);
        }

        return (steps, clock.Elapsed.TotalMilliseconds);
    }

    [Theory]
    // `*a` repeated and then `*b`, against a path of a: the one with the most work a pattern can make. 24 rules.
    [InlineData(100, 450_000L)]
    [InlineData(255, 3_000_000L)]
    [InlineData(1000, 20_000_000L)]
    public void TheWorstWildcardRulesAtTheLimitTakeMillionsOfStepsToTestOneFile(int pathLength, long allowedSteps)
    {
        var matchers = AtTheLimit(Stars);
        var (steps, milliseconds) = Test(matchers, new string('a', pathLength));

        output.WriteLine($"{matchers.Count} rules, path of {pathLength} characters: {steps:N0} steps, {milliseconds:F2} ms");
        matchers.Count.ShouldBe(24);
        steps.ShouldBeLessThanOrEqualTo(allowedSteps);
    }

    [Fact]
    public void ARuleWithOneStarAndFiveHundredLiteralsAtTheLimitFindsItsLiteralMissing()
    {
        // Rejected by a search for the literal run when the path is shorter than it, and a full match when it is long.
        var matchers = AtTheLimit("*" + new string('a', 498));

        var (shortSteps, _) = Test(matchers, new string('a', 255));
        var (longSteps, milliseconds) = Test(matchers, new string('a', 1000));

        output.WriteLine($"{matchers.Count} rules: {shortSteps:N0} steps for 255 characters, {longSteps:N0} for 1,000 ({milliseconds:F2} ms)");
        shortSteps.ShouldBe(0);
        longSteps.ShouldBeLessThanOrEqualTo(2L * matchers.Count * 500 * 1_002);
    }

    [Fact]
    public void ThePatternsWithoutAWildcardAtTheLimitTakeTensOfThousandsOfStepsToTestOneFile()
    {
        var matchers = AtTheLimit("a" + new string('?', 498));

        var (steps, milliseconds) = Test(matchers, new string('a', 255));

        output.WriteLine($"{matchers.Count} rules, path of 255 characters: {steps:N0} steps, {milliseconds:F2} ms");
        matchers.Count.ShouldBe(480);
        steps.ShouldBeLessThanOrEqualTo(330_000L);
    }

    [Fact]
    public void ClassRulesAtTheLimitTakeThousandsOfStepsToTestOneFile()
    {
        var matchers = AtTheLimit(string.Concat(Enumerable.Repeat("[a-cx-z]", 62)));

        var (steps, milliseconds) = Test(matchers, new string('a', 255));

        output.WriteLine($"{matchers.Count} rules, path of 255 characters: {steps:N0} steps, {milliseconds:F2} ms");
        matchers.Count.ShouldBe(1_200);
        steps.ShouldBeLessThanOrEqualTo(500_000L);
    }

    [Fact]
    public void RulesAtTheCountLimitThatALiteralRejectsTakeNoSteps()
    {
        var matchers = AtTheLimit("foo{0}*.bar");

        var (steps, milliseconds) = Test(matchers, "src/module1/sub2/file3.cs");

        output.WriteLine($"{matchers.Count} rules `fooN*.bar`: {steps:N0} steps, {milliseconds:F3} ms (the search for the literal)");
        steps.ShouldBe(0);
    }

    [Fact]
    public void ADirectoryOnlyRuleTakesOnePassOverAFileAThousandDirectoriesDeep()
    {
        var matcher = GlobMatcher.Compile(Stars, string.Empty);
        var path = string.Concat(Enumerable.Repeat("a/", 2_040)) + "file";

        var (steps, milliseconds) = Test([matcher], path, beneath: true);

        output.WriteLine($"one rule `*a` x248 `*b/`, path 2,040 directories deep: {steps:N0} steps, {milliseconds:F3} ms");
        steps.ShouldBeLessThanOrEqualTo(2L * (matcher.TokenCount + 1) * (path.Length + 2));
    }

    [Fact]
    public void TheMemoryTheRulesAtTheLimitsAreBuiltFromIsAFewMegabytes()
    {
        // Allocated while the rules are built, which is more than they keep, and counted for this thread so that the
        // tests running beside this one do not move it. What is kept, measured in a process of its own, is at most
        // 15 MiB (class-heavy rules), 8 MiB (question marks), 3 MiB (plain rules) and under 1 MiB (wildcards).
        var shapes = new (string Name, string Glob, long Allowed)[]
        {
            ("plain rules", "generated{0}.txt", 32L * 1024 * 1024),
            ("wildcard rules of 498 characters", Stars, 4L * 1024 * 1024),
            ("class-heavy rules", string.Concat(Enumerable.Repeat("[a-cx-z]", 62)), 150L * 1024 * 1024),
            ("rules of 498 question marks", "a" + new string('?', 498), 50L * 1024 * 1024),
        };

        foreach (var (name, glob, allowed) in shapes)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var rules = AtTheLimit(glob);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(rules);

            output.WriteLine($"{name}: {rules.Count} rules allocate {allocated / 1024.0 / 1024.0:F2} MiB building");
            allocated.ShouldBeLessThan(allowed, name);
        }
    }

    /// <summary>Whether any matches, having tested every one as a walk must, since the last to match decides.</summary>
    private static bool EveryRuleTested(List<System.Text.RegularExpressions.Regex> expressions, string path)
    {
        var any = false;
        foreach (var expression in expressions)
            if (expression.IsMatch(path)) any = true;

        return any;
    }

    [Fact]
    public void BuildingPlainLinesTakesMillisecondsAndMegabytes()
    {
        // Lines of the form `fooN*.bar`, as an ignore file has them, built without a budget to show the cost per rule.
        foreach (var count in new[] { 469, 10_000, 50_000 })
        {
            var lines = Enumerable.Range(0, count).Select(i => $"foo{i}*.bar").ToList();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var clock = Stopwatch.StartNew();
            var rules = new IgnoreRuleSet();
            rules.AddPatterns(lines, "bench");
            var milliseconds = clock.Elapsed.TotalMilliseconds;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(rules);

            output.WriteLine($"{count:N0} lines: {milliseconds:F1} ms, {allocated / 1024.0 / 1024.0:F1} MiB allocated");
            rules.Count.ShouldBe(count);
        }
    }

    [ReleaseBuildFact]
    public void TestingFilesAgainstManyRulesTakesLessThanTheRegularExpressionsDid()
    {
        // The rules and paths of the figures in docs/04-ingestion.md, against the regular expression
        // translation the matcher replaced (GlobOracle). Printed, and compared only for the answer.
        foreach (var (rules, paths) in new[] { (469, 20_000), (3_000, 30_000) })
        {
            var lines = Enumerable.Range(0, rules).Select(i => $"foo{i}*.bar").ToList();
            var random = new Random(1);
            var files = Enumerable.Range(0, paths)
                .Select(i => $"src/module{random.Next(200)}/sub{random.Next(20)}/file{i}{(random.Next(50) == 0 ? ".bar" : ".cs")}")
                .ToList();

            var set = new IgnoreRuleSet();
            for (var i = 0; i < lines.Count; i += 1_000) set.AddPatterns(lines.Skip(i).Take(1_000), "bench");

            var clock = Stopwatch.StartNew();
            var ignored = files.Count(f => set.IsIgnored(f, isDirectory: false));
            var matcherSeconds = clock.Elapsed.TotalSeconds;

            var expressions = lines.Select(l => GlobOracle.Compile(l, string.Empty)).ToList();
            clock.Restart();
            var hits = files.Count(f => EveryRuleTested(expressions, f));
            var expressionSeconds = clock.Elapsed.TotalSeconds;

            output.WriteLine($"{rules:N0} rules x {paths:N0} paths: matcher {matcherSeconds:F2} s, regular expressions {expressionSeconds:F2} s ({ignored} ignored, {hits} matched)");
            ignored.ShouldBe(hits);
        }
    }

    [ReleaseBuildFact]
    public void RulesNoLiteralCanRejectTakeLessThanTheRegularExpressionsDidToo()
    {
        // `*a*b*c*.cs` with three letters at random, against paths that all end in `.cs`: every pair runs the whole match.
        var random = new Random(3);
        var lines = Enumerable.Range(0, 469)
            .Select(_ => $"*{(char)('a' + random.Next(26))}*{(char)('a' + random.Next(26))}*{(char)('a' + random.Next(26))}*.cs")
            .ToList();
        var files = Enumerable.Range(0, 20_000).Select(i => $"src/module{random.Next(200)}/sub{random.Next(20)}/component_{i}.cs").ToList();

        var set = new IgnoreRuleSet();
        set.AddPatterns(lines, "bench");
        var clock = Stopwatch.StartNew();
        var ignored = files.Count(f => set.IsIgnored(f, isDirectory: false));
        var matcherSeconds = clock.Elapsed.TotalSeconds;

        var expressions = lines.Select(l => GlobOracle.Compile(l, string.Empty)).ToList();
        clock.Restart();
        var hits = files.Count(f => EveryRuleTested(expressions, f));
        var expressionSeconds = clock.Elapsed.TotalSeconds;

        output.WriteLine($"469 rules x 20,000 paths, none rejected by a literal: matcher {matcherSeconds:F2} s, regular expressions {expressionSeconds:F2} s ({ignored} ignored, {hits} matched)");
        ignored.ShouldBe(hits);
    }
}

/// <summary>
/// A fact that runs in a release build and is reported as skipped, with the reason, in a debug one, where the times it
/// prints are not the ones the documentation gives and the regular expressions take a minute.
/// </summary>
public sealed class ReleaseBuildFactAttribute : FactAttribute
{
    public ReleaseBuildFactAttribute()
    {
#if DEBUG
        Skip = "a timing comparison, which means something in a release build: dotnet test tests/Dexicon.Tests -c Release --filter Category=Benchmark";
#endif
    }
}
