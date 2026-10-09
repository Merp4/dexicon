using System.Text.RegularExpressions;
using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// The matcher for an ignore rule is built with <see cref="RegexOptions.NonBacktracking"/>, so that no glob can
/// take exponential time. That is only safe if it answers every match the backtracking engine did, so this
/// compares the two over the regular expressions <see cref="IgnoreRuleSet.ToRegex(string, string)"/> emits: the
/// repository's own ignore files, a fixed list covering each construct a glob can use, and a seeded set of
/// generated globs, each under three directory prefixes, against a fixed and a generated set of paths.
/// </summary>
public sealed class IgnorePatternEquivalenceTests
{
    private static readonly string[] FixedGlobs =
    [
        ".git", ".hg", "node_modules", "bin", "*.exe", "*.min.js", "package-lock.json",
        "docs/*.md", "docs/**/*.md", "**/build", "**/build/**", "/build", "src/**", "a/**/b", "a/**/**/b",
        "*", "**", "?", "??", "a?c", "[ab]", "[a-c]x", "[!a-c]x", "[^a-c]x", "[!a]*.md", "[]a]x", "[!]a]x", "[^]a]x",
        "[!]x", "[a", "a[", "x[]y]", "[z-a]", "[]x", "docs/[]x", "*.[ch]", "*.[!ch]", "**/[Bb]in/**", "a*b*c*d", "*a*a*b", "foo*/bar",
        "a.b", "a+b", "a(b)", "a{b}", "a^b", "a$b", "a|b", "a\\b", "\\*", "\\#x", "ä*", "straße", "İ*", "k*",
    ];

    private static readonly string[] FixedPaths =
    [
        "a", "b", "A", "ab", "abc", "a.b", "a+b", "a(b)", "a{b}", "a^b", "a$b", "a|b", "a\\b", "x", "x.md", "X.MD",
        "]x", "ax", "bx", "cx", "dx", "!x", "a]x", "v/x", "v]", "a/b", "a/x/b", "a/x/y/b", "a/b/b", "sub/a/b",
        ".git", ".git/config", "src/.git/HEAD", "node_modules/x/y.js", "bin/Debug/App.dll", "obj/bin/x", "Bin/x",
        "docs/a.md", "docs/sub/a.md", "docs/sub/deep/a.md", "docs/.md", "build", "build/out", "x/build/out/y",
        "app.exe", "dir/app.exe", "app.min.js", "package-lock.json", "a/package-lock.json",
        "foo123/bar", "foo/bar", "foo/x/bar", "abcd", "aXbXcXd", "aab", "aaab", "aaaa", "#x", "*", "a/*",
        "äx", "Äx", "strasse", "straße", "STRAẞE", "İx", "ix", "Ix", "ıx", "kx", "Kx", "Kx",
        "line\nbreak", "x.md\n", "a\nb/c",
    ];

    private static readonly string[] GeneratedSegments =
    [
        "a", "b", "A", "ab", "x.md", "*", "**", "?", "[ab]", "[!a]", "[^a]", "[a-c]", "[!a-c]", "[!]x", "[]a]",
        "[!]a]", "[^]a]", "*.md", "a*", "*b", "?b", "dir", "build", "sub", "x", ".git", "c", "*a*", "a?c", "[a-c]*",
        "ä", "İ", "i", "ss", "ß", "a.b", "a**", "**b",
    ];

    private static readonly string[] GeneratedPathSegments =
    [
        "a", "b", "A", "B", "ab", "AB", "x.md", "X.MD", "dir", "build", "Build", "sub", "x", ".git", "c", "a.b", "abc",
        "ac", "axc", "]", "!", "a]", "ä", "Ä", "İ", "i", "I", "ı", "ss", "SS", "ß", "k", "K",
        "K", "bar\nbaz", "x.md\n",
    ];

    private static readonly string[] Prefixes = ["", "sub", "a/b"];

    private static List<string> Globs()
    {
        var globs = new List<string>(FixedGlobs);

        // The repository's own files, found from the test binary, and required to hold something: a lookup that
        // finds nothing would otherwise compare an empty set and pass.
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "Dexicon.slnx"))) root = Path.GetDirectoryName(root);
        root.ShouldNotBeNull("the repository root, found by looking upwards for Dexicon.slnx");

        var fromFiles = new[] { ".gitignore", WorkspaceWalker.IgnoreFileName }
            .Select(name => Path.Combine(root, name))
            .Where(File.Exists)
            .SelectMany(File.ReadAllLines)
            .Select(Normalise)
            .Where(line => line.Length > 0)
            .ToList();
        fromFiles.Count.ShouldBeGreaterThan(10, "lines read from the repository's .gitignore");
        globs.AddRange(fromFiles);

        var random = new Random(20261009);
        for (var i = 0; i < 400; i++)
        {
            var glob = string.Join('/', Enumerable.Range(0, random.Next(1, 5))
                .Select(_ => GeneratedSegments[random.Next(GeneratedSegments.Length)]));
            if (random.Next(4) == 0) glob = "/" + glob;
            globs.Add(glob);
        }

        return globs;
    }

    /// <summary>A line as <see cref="IgnoreRuleSet.AddPatterns"/> hands it to the regular expression, or empty.</summary>
    private static string Normalise(string raw)
    {
        var line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#')) return string.Empty;
        if (line.StartsWith('!')) line = line[1..];
        if (line.EndsWith('/')) line = line[..^1];
        return line;
    }

    private static List<string> Paths()
    {
        var paths = new HashSet<string>(FixedPaths, StringComparer.Ordinal);
        var random = new Random(20261010);
        for (var i = 0; i < 250; i++)
            paths.Add(string.Join('/', Enumerable.Range(0, random.Next(1, 6))
                .Select(_ => GeneratedPathSegments[random.Next(GeneratedPathSegments.Length)])));

        return [.. paths];
    }

    /// <summary>
    /// Every (glob, prefix, path) on which the two engines disagree, with how many were compared. A glob that
    /// the backtracking engine refuses must be refused by the other, and one it accepts must be accepted.
    /// </summary>
    private static (List<string> Differences, int Compared, int Refused) Compare(RegexOptions linear)
    {
        var differences = new List<string>();
        var compared = 0;
        var refused = 0;
        var paths = Paths();

        foreach (var glob in Globs())
        {
            foreach (var prefix in Prefixes)
            {
                var pattern = IgnoreRuleSet.ToRegex(glob, prefix);

                Regex? backtracking = null;
                try { backtracking = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(10)); }
                catch (ArgumentException) { refused++; }

                Regex? other = null;
                try { other = new Regex(pattern, linear); }
                catch (ArgumentException) { }

                if ((backtracking is null) != (other is null))
                {
                    differences.Add($"compiles in one engine only: glob '{glob}', prefix '{prefix}'");
                    continue;
                }

                if (backtracking is null || other is null) continue;

                foreach (var path in paths)
                {
                    compared++;
                    if (backtracking.IsMatch(path) != other.IsMatch(path))
                        differences.Add($"glob '{glob}', prefix '{prefix}', path '{path.Replace("\n", "\\n")}'");
                }
            }
        }

        return (differences, compared, refused);
    }

    [Fact]
    public void TheLinearEngineAnswersEveryMatchTheBacktrackingEngineDoes()
    {
        var (differences, compared, refused) = Compare(RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

        differences.Take(10).ShouldBeEmpty($"of {compared} comparisons");
        compared.ShouldBeGreaterThan(300_000, "pairs compared; too few means the globs or the paths were not read");
        refused.ShouldBeGreaterThan(0, "globs neither engine compiles, such as [z-a], were seen");
    }

    [Fact]
    public void TheComparisonFindsAnEngineThatDiffers()
    {
        // The control: a linear engine that ignores case is a different matcher, and the comparison has to say so.
        var (differences, compared, _) = Compare(RegexOptions.NonBacktracking);

        compared.ShouldBeGreaterThan(0);
        differences.ShouldNotBeEmpty();
    }
}
