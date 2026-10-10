using System.Diagnostics;
using System.Globalization;
using System.Text;
using Dexicon.Core.Indexing;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Dexicon.Tests;

/// <summary>
/// What testing one file costs at the limits (<see cref="IgnoreRuleSet.MaxRulesPerSource"/> rules and
/// <see cref="IgnoreRuleSet.MaxWeightPerSource"/> weight), for the rule shapes that cost most, against paths of 255 and
/// 4,093 characters. These are the figures <c>docs/04-ingestion.md</c> gives. Print the table, in a release build for
/// times that mean something, with
/// <c>dotnet test tests/Dexicon.Tests -c Release --filter "Category=Benchmark" --logger "console;verbosity=detailed"</c>.
///
/// The work is counted in steps (<see cref="GlobMatcher.IsMatch(ReadOnlySpan{char}, ref long)"/>), which are the same on
/// every machine and under every load, and asserted against the bound of the cost model in <see cref="GlobMatcher"/>.
/// The time is printed and not asserted.
/// </summary>
[Trait("Category", "Benchmark")]
public sealed class IgnoreRuleCostTests(ITestOutputHelper output)
{
    private static string Repeat(string unit, int times) => string.Concat(Enumerable.Repeat(unit, times));

    /// <summary>The rule shapes: each is the line of an ignore file, repeated until a limit is reached.</summary>
    public static TheoryData<string, string> Shapes() => new()
    {
        { "stars: *a x248 then *b", Repeat("*a", 248) + "*b" },
        { "a near miss: a x30 then c", new string('a', 30) + "c" },
        { "question marks: ? x20", new string('?', 20) },
        { "classes: [a-c] x62", Repeat("[a-c]", 62) },
        { "a star before each class: *[a-c] x62", Repeat("*[a-c]", 62) },
        { "slash globs: **/a x100", Repeat("**/a", 100) },
        { "one star", "*" },
        { "typical: *.log", "*.log" },
        { "plain: a x498", new string('a', 498) },
        { "a literal that nearly matches: aac", "aac" },
        { "a star before each class: *[a-c] x10", Repeat("*[a-c]", 10) },
        { "a class of 495 members that are not adjacent", "*[" + NonAdjacentMembers + "]" },
    };

    /// <summary>495 members that cannot be merged into fewer ranges (every second character from U+0100), so a lookup cannot shorten.</summary>
    private static readonly string NonAdjacentMembers = string.Concat(Enumerable.Range(0, 495).Select(i => (char)(0x0100 + 2 * i)));

    private const string Iota = "ι";

    private static string DistinctCharacters(int count) => string.Concat(Enumerable.Range(0x4E00, count).Select(c => (char)c));

    private static readonly (string Name, string Path)[] Paths =
    [
        ("255 characters in one name", new string('a', 255)),
        ("127 directories of one character", Repeat("a/", 127) + "a"),
        ("255 different characters", DistinctCharacters(255)),
        ("4,093 characters in one name", new string('a', 4093)),
        ("2,046 directories of one character", Repeat("a/", 2046) + "a"),
        ("4,093 different characters", DistinctCharacters(4093)),
        ("255 Greek characters in one name", Repeat(Iota, 255)),
        ("127 and 127 Greek characters in two directories", Repeat(Iota, 127) + "/" + Repeat(Iota, 127)),
        ("2,046 Greek characters in one name", Repeat(Iota, 2046)),
        ("a typical path", "src/module123/sub7/component_1234.cs"),
    ];

    /// <summary>The rules a budget lets in for a line, as a walk would hold them, and the glob each was written as.</summary>
    private static List<GlobMatcher> AtTheLimit(string glob)
    {
        var budget = RuleBudget.ForIgnoreFiles();
        var rules = new IgnoreRuleSet();
        var matchers = new List<GlobMatcher>();
        for (var n = 0; ; n++)
        {
            var line = string.Format(CultureInfo.InvariantCulture, glob, n);
            try { rules.AddPatterns([line], "bench", budget: budget); }
            catch (IgnorePatternException) { break; }

            matchers.Add(GlobMatcher.Compile(line, string.Empty));
        }

        return matchers;
    }

    /// <summary>The most steps the cost model allows for one path against one rule of <paramref name="glob"/>.</summary>
    private static long Bound(GlobMatcher matcher, string glob, string path) =>
        (matcher.TokenCount + 1L) * ((path.Length >> 6) + 1) + glob.Count(c => c == '[') * (path.Length + 1L) + path.Length;

    [Theory]
    [MemberData(nameof(Shapes))]
    public void TheWorkOfOneFileAgainstTheRulesAtTheLimitsStaysWithinTheCostModel(string name, string glob)
    {
        var matchers = AtTheLimit(glob);

        foreach (var (pathName, path) in Paths)
        {
            long steps = 0;
            var bound = 0L;
            var clock = Stopwatch.StartNew();
            foreach (var matcher in matchers)
            {
                matcher.IsMatch(path, ref steps);
                bound += Bound(matcher, glob, path);
            }

            var milliseconds = clock.Elapsed.TotalMilliseconds;
            output.WriteLine($"{name} ({matchers.Count} rules) against {pathName}: {steps:N0} steps of {bound:N0} allowed, {milliseconds:F3} ms");
            steps.ShouldBeLessThanOrEqualTo(bound, $"{name} against {pathName}");
        }
    }

    /// <summary>The rows of the table in docs/04-ingestion.md: its label, the glob repeated up to the limit, and the rules that fit.</summary>
    private static readonly (string Label, string Glob, int Rules, string Unit)[] TableRows =
    [
        ("aac", "aac", 5_000, "a"),
        ("a x30 then c", new string('a', 30) + "c", 1_875, "a"),
        ("*[a-c] x10", Repeat("*[a-c]", 10), 331, "a"),
        ("*[a-c] x62", Repeat("*[a-c]", 62), 53, "a"),
        ("**/a x100", Repeat("**/a", 100), 300, "a"),
        ("*a x248 then *b", Repeat("*a", 248) + "*b", 120, "a"),
        ("*", "*", 5_000, "a"),
        ("? x20", new string('?', 20), 2_857, "a"),
        ("*.log", "*.log", 5_000, "a"),
        ("*[495 members], Greek path", "*[" + NonAdjacentMembers + "]", 116, Iota),
    ];

    /// <summary><paramref name="unit"/> repeated and cut to <paramref name="length"/> characters.</summary>
    private static string Fill(string unit, int length) => Repeat(unit, length / unit.Length + 1)[..length];

    [Fact]
    public void TheShapesFillTheLimitsTheDocumentationNames()
    {
        foreach (var (label, glob, rules, _) in TableRows)
            AtTheLimit(glob).Count.ShouldBe(rules, $"{label}: the Rules column of the table in docs/04-ingestion.md");
    }

    /// <summary>The rule set a walk holds for <paramref name="glob"/> repeated up to the limits, and its size in rules.</summary>
    private static (IgnoreRuleSet Rules, int Count) RuleSetAtTheLimit(string glob)
    {
        var budget = RuleBudget.ForIgnoreFiles();
        var rules = new IgnoreRuleSet();
        var count = 0;
        for (var n = 0; ; n++)
        {
            try { rules.AddPatterns([string.Format(CultureInfo.InvariantCulture, glob, n)], "bench", budget: budget); }
            catch (IgnorePatternException) { break; }

            count++;
        }

        return (rules, count);
    }

    private static double BestMilliseconds(IgnoreRuleSet rules, string path)
    {
        for (var warm = 0; warm < 3; warm++) rules.IsIgnored(path, isDirectory: false);

        var best = double.MaxValue;
        for (var run = 0; run < 7; run++)
        {
            var clock = Stopwatch.StartNew();
            rules.IsIgnored(path, isDirectory: false);
            best = Math.Min(best, clock.Elapsed.TotalMilliseconds);
        }

        return best;
    }

    /// <summary>
    /// Prints the table of docs/04-ingestion.md: one file tested through the rule set as a walk tests it (the character
    /// masks of the path built once and shared by every rule), best of seven runs, the larger of the one-name path and the
    /// one-character-directories path at each length (the longer one is as long as 4,093 bytes of the row's character
    /// allow: 4,093 characters of `a`, 2,046 of a Greek letter), and one typical path. Nothing is asserted about time,
    /// which is a machine figure; the memory is bounded by <see cref="TheMemoryTheRulesAtTheLimitsAreBuiltFromIsAFewMegabytes"/>, the rule counts are asserted by
    /// <see cref="TheShapesFillTheLimitsTheDocumentationNames"/> and the steps by the theory above.
    /// </summary>
    [Fact]
    public void ThePrintedTableIsTheOneInTheDocumentation()
    {
        output.WriteLine("| Rules at the limit | Rules | 255-character path | longest path (4,093 bytes) | typical path (36 characters) |");
        foreach (var (label, glob, expected, unit) in TableRows)
        {
            var (rules, count) = RuleSetAtTheLimit(glob);

            var longest = 4093 / Encoding.UTF8.GetByteCount(unit);
            var short255 = Math.Max(
                BestMilliseconds(rules, Fill(unit, 255)), BestMilliseconds(rules, Fill(unit + "/", 255).TrimEnd('/')));
            var long4093 = Math.Max(
                BestMilliseconds(rules, Fill(unit, longest)), BestMilliseconds(rules, Fill(unit + "/", longest).TrimEnd('/')));
            GC.KeepAlive(rules);

            var typical = BestMilliseconds(rules, "src/module123/sub7/component_1234.cs");

            output.WriteLine($"| {label} | {count:N0} | {short255:F1} ms | {long4093:F1} ms ({longest:N0} characters) | {typical:F2} ms |");
            count.ShouldBe(expected, label);
        }
    }

    [Fact]
    public void RulesAtTheCountLimitThatALiteralRejectsReadAFewCharactersEach()
    {
        var matchers = AtTheLimit("foo{0}*.bar");
        long steps = 0;

        foreach (var matcher in matchers) matcher.IsMatch("src/module1/sub2/file3.cs", ref steps);

        output.WriteLine($"{matchers.Count} rules `fooN*.bar`: {steps:N0} steps");
        steps.ShouldBeLessThanOrEqualTo(matchers.Count * 3L, "the search skips to an f and reads at most `fi`");
    }

    [Fact]
    public void ADirectoryOnlyRuleTakesOnePassOverAFileAThousandDirectoriesDeep()
    {
        var glob = Repeat("*a", 248) + "*b";
        var matcher = GlobMatcher.Compile(glob, string.Empty);
        var path = Repeat("a/", 2_040) + "file";

        long steps = 0;
        var clock = Stopwatch.StartNew();
        matcher.IsMatchBeneath(path, ref steps);

        output.WriteLine($"one rule `*a` x248 `*b/`, path 2,040 directories deep: {steps:N0} steps, {clock.Elapsed.TotalMilliseconds:F3} ms");
        steps.ShouldBeLessThanOrEqualTo(Bound(matcher, glob, path));
    }

    [Fact]
    public void TheMemoryTheRulesAtTheLimitsAreBuiltFromIsAFewMegabytes()
    {
        // Allocated while the rules are built, which is more than they keep, and counted for this thread so that the
        // tests running beside this one do not move it.
        var shapes = new (string Name, string Glob, long Allowed)[]
        {
            ("plain rules", "generated{0}.txt", 16L * 1024 * 1024),
            // 3.9 MiB measured in a release build; the bound leaves room for a runtime that allocates a little more.
            ("wildcard rules of 498 characters", Repeat("*a", 248) + "*b", 8L * 1024 * 1024),
            ("class-heavy rules", Repeat("[a-cx-z]", 62), 16L * 1024 * 1024),
            ("rules of 498 question marks", "a" + new string('?', 498), 16L * 1024 * 1024),
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
}
