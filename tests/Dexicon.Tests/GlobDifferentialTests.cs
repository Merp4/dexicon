using System.Diagnostics;
using System.Text.RegularExpressions;
using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// <see cref="GlobMatcher"/> against the regular expression it replaced (<see cref="GlobOracle"/>), over random
/// (glob, directory prefix, path) triples drawn from an alphabet of the characters that make a glob or a path
/// hard: every wildcard and class form, slashes, escapes, case-folding oddities (the Kelvin sign, the long s, sharp
/// s in both cases, dotted and dotless i, the Greek sigmas, a titlecase digraph) and an emoji. Paths with a line
/// feed are left out, which is the one documented difference (<see cref="GlobOracle"/>).
/// </summary>
public sealed class GlobDifferentialTests
{
    private static string U(int codePoint) => char.ConvertFromUtf32(codePoint);

    private static readonly string[] GlobPieces =
    [
        "a", "b", "A", "k", "K", U(0x212A), U(0x17F), "s", "S", U(0xDF), U(0x1E9E), U(0x130), U(0x131), "i", "I",
        U(0x3C3), U(0x3C2), U(0x3A3), U(0x1C5), U(0x1F600), "x.md", "dir", ".", "-", "!", "#", "$", "^", " ", "\r", "\n", "\\",
        "\\*", "\\]", "/", "/", "/", "*", "*", "**", "**/", "/**", "?", "?", "[", "]", "[!", "[^", "[]", "[]a]", "[!]a]",
        "[a-z]", "[a-c]", "[!a-c]", "[^k]", "[" + U(0x212A) + "]", "[s-z]", "[z-a]", "[a-]", "[-a]", "[[]", "[a[]", "[/]",
        "[+-9]", "[\\]]", "[a\\-c]", ".md", ".git", "node_modules", "build", "0", "9",
    ];

    private static readonly string[] PathPieces =
    [
        "a", "b", "A", "k", "K", U(0x212A), U(0x17F), "s", "S", U(0xDF), U(0x1E9E), U(0x130), U(0x131), "i", "I",
        U(0x3C3), U(0x3C2), U(0x3A3), U(0x1C5), U(0x1C4), U(0x1C6), U(0x1F600), "x.md", "X.MD", "dir", "Dir", ".", "..", "-",
        "!", "#", "$", "^", " ", "\r", "\\", "]", "[", "a]", "[]", "ab", "abc", "ac", "a.b", ".md", ".git", "node_modules",
        "build", "0", "9", "5", "z", "m", "+", ",", ":",
    ];

    private static readonly string[] Prefixes = ["", "", "", "sub", "a/b", "Dir", U(0x212A) + "ing"];

    private static string RandomGlob(Random random)
    {
        var count = random.Next(1, 9);
        var parts = new string[count];
        for (var i = 0; i < count; i++) parts[i] = GlobPieces[random.Next(GlobPieces.Length)];
        return string.Concat(parts);
    }

    private static string RandomPath(Random random)
    {
        var depth = random.Next(1, 8);
        var segments = new string[depth];
        for (var d = 0; d < depth; d++)
        {
            var count = random.Next(1, 4);
            var parts = new string[count];
            for (var i = 0; i < count; i++) parts[i] = PathPieces[random.Next(PathPieces.Length)];
            segments[d] = string.Concat(parts);
        }

        var path = string.Join('/', segments);
        return random.Next(4) == 0 ? Prefixes[random.Next(1, Prefixes.Length)] + "/" + path : path;
    }

    private static string Show(string text) => text.Replace("\r", "\\r", StringComparison.Ordinal);

    /// <summary>
    /// Runs the comparison and returns the disagreements, with the number of pairs compared and of globs both sides
    /// refused. A glob one side refuses and the other accepts is a disagreement.
    /// </summary>
    private static (List<string> Differences, long Compared, int Refused, int Globs, long Matched) Compare(
        int globCount, int pathsPerGlob, int seed, Func<string, string>? mutate = null)
    {
        var random = new Random(seed);
        var differences = new List<string>();
        long compared = 0;
        long matched = 0;
        var refused = 0;

        for (var g = 0; g < globCount; g++)
        {
            var glob = RandomGlob(random);
            var prefix = Prefixes[random.Next(Prefixes.Length)];

            Regex? oracle = null;
            GlobMatcher? matcher = null;
            var oracleRefused = false;
            var matcherRefused = false;
            try { oracle = GlobOracle.Compile(glob, prefix); } catch (FormatException) { oracleRefused = true; }
            try { matcher = GlobMatcher.Compile(mutate is null ? glob : mutate(glob), prefix); } catch (FormatException) { matcherRefused = true; }

            if (oracleRefused != matcherRefused)
            {
                differences.Add($"glob '{Show(glob)}' is refused by only one of them");
                continue;
            }

            if (matcher is null || oracle is null)
            {
                refused++;
                continue;
            }

            for (var p = 0; p < pathsPerGlob; p++)
            {
                var path = RandomPath(random);

                // Half the paths are made to resemble the glob: a prefix of it with the wildcards filled in.
                if (p % 2 == 0) path = Resemble(random, glob, prefix);
                if (path.Contains('\n', StringComparison.Ordinal)) continue;

                compared++;
                bool expected;
                try { expected = oracle.IsMatch(path); }
                catch (RegexMatchTimeoutException) { continue; }

                if (expected) matched++;
                if (matcher.IsMatch(path) != expected)
                    differences.Add($"glob '{Show(glob)}', prefix '{prefix}', path '{Show(path)}': oracle {expected}");
            }
        }

        return (differences, compared, refused, globCount, matched);
    }

    /// <summary>A path that a glob is likely to match: its literal characters kept and its wildcards replaced.</summary>
    private static string Resemble(Random random, string glob, string prefix)
    {
        var sb = new System.Text.StringBuilder();
        if (prefix.Length > 0) sb.Append(prefix).Append('/');

        foreach (var c in glob.TrimStart('/'))
        {
            switch (c)
            {
                case '*': sb.Append(PathPieces[random.Next(PathPieces.Length)]); break;
                case '?': sb.Append(random.Next(2) == 0 ? "a" : "K"); break;
                case '[' or ']' or '\\': if (random.Next(3) == 0) sb.Append(c); break;
                default: sb.Append(random.Next(8) == 0 ? char.ToUpperInvariant(c) : c); break;
            }
        }

        if (random.Next(3) == 0) sb.Append('/').Append(PathPieces[random.Next(PathPieces.Length)]);
        return sb.ToString();
    }

    [Fact]
    public void TheMatcherAnswersAsTheRegularExpressionDidOverAMillionPairs()
    {
        var clock = Stopwatch.StartNew();

        var (differences, compared, refused, globs, matched) = Compare(globCount: 4_000, pathsPerGlob: 320, seed: 20261009);

        var summary = $"{compared:N0} pairs from {globs:N0} globs, {matched:N0} of them matches, {refused} globs refused by both, in {clock.Elapsed.TotalSeconds:F1} s";
        differences.Take(10).ShouldBeEmpty(summary);
        compared.ShouldBeGreaterThanOrEqualTo(1_000_000, summary);
        matched.ShouldBeGreaterThan(50_000, "pairs the oracle matches; too few and the test mostly compares falsehoods: " + summary);
        refused.ShouldBeGreaterThan(0, "globs refused by both, such as [z-a], were drawn");
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void ADifferentSeedAgreesToo()
    {
        var (differences, compared, _, _, _) = Compare(globCount: 1_000, pathsPerGlob: 120, seed: 7);

        differences.Take(10).ShouldBeEmpty($"of {compared:N0} pairs");
    }

    [Fact]
    public void TheComparisonFindsAMatcherThatDiffers()
    {
        // The control: a matcher built from every glob with each `*` doubled, which crosses a slash, must be caught,
        // or the comparison above could pass against anything.
        var (differences, compared, _, _, _) = Compare(
            globCount: 1_000, pathsPerGlob: 120, seed: 7, mutate: glob => glob.Replace("*", "**", StringComparison.Ordinal));

        compared.ShouldBeGreaterThan(0);
        differences.Count.ShouldBeGreaterThan(100);
    }
}
