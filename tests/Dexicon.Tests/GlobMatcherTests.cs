using System.Text.RegularExpressions;
using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// What <see cref="GlobMatcher"/> matches, as a table of the gitignore documentation's examples, and how much work a
/// match may take. The matcher is held to the regular expression it replaced by <see cref="GlobDifferentialTests"/>.
/// </summary>
public sealed class GlobMatcherTests
{
    private static bool Ignored(string pattern, string path, bool isDirectory = false)
    {
        var rules = new IgnoreRuleSet();
        rules.AddPatterns([pattern], "test");
        return rules.IsIgnored(path, isDirectory);
    }

    [Theory]
    // A name with no slash matches at any depth, and a directory that matches takes everything beneath it.
    [InlineData("foo", "foo", false, true)]
    [InlineData("foo", "a/b/foo", false, true)]
    [InlineData("foo", "foo/bar", false, true)]
    [InlineData("foo", "a/foo/bar/baz", false, true)]
    [InlineData("foo", "food", false, false)]
    [InlineData("foo", "afoo", false, false)]
    // A leading slash anchors at the root.
    [InlineData("/foo", "foo", false, true)]
    [InlineData("/foo", "a/foo", false, false)]
    // A trailing slash is for directories, and what is beneath one.
    [InlineData("foo/", "foo", false, false)]
    [InlineData("foo/", "foo", true, true)]
    [InlineData("foo/", "a/foo/x", false, true)]
    [InlineData("foo/", "a/foo", true, true)]
    // A slash inside anchors, and * does not cross one.
    [InlineData("*.md", "a/b.md", false, true)]
    [InlineData("*.md", "a/b.mdx", false, false)]
    [InlineData("doc/*.md", "doc/a.md", false, true)]
    [InlineData("doc/*.md", "doc/sub/a.md", false, false)]
    [InlineData("doc/*.md", "x/doc/a.md", false, false)]
    [InlineData("a*/b", "a1/b", false, true)]
    [InlineData("a*/b", "a1/x/b", false, false)]
    // A ** segment is zero or more directories.
    [InlineData("a/**/b", "a/b", false, true)]
    [InlineData("a/**/b", "a/x/b", false, true)]
    [InlineData("a/**/b", "a/x/y/b", false, true)]
    [InlineData("a/**/b", "ab", false, false)]
    [InlineData("a/**/b", "x/a/b", false, false)]
    [InlineData("**/foo", "foo", false, true)]
    [InlineData("**/foo", "a/b/foo", false, true)]
    [InlineData("foo/**", "foo/a/b", false, true)]
    [InlineData("foo/**", "foo", false, false)]
    [InlineData("**", "a/b", false, true)]
    // ? is one character that is not a slash.
    [InlineData("a?c", "abc", false, true)]
    [InlineData("a?c", "a/c", false, false)]
    [InlineData("a?c", "ac", false, false)]
    // Classes.
    [InlineData("[abc]x", "bx", false, true)]
    [InlineData("[abc]x", "dx", false, false)]
    [InlineData("[a-c]x", "bx", false, true)]
    [InlineData("[!abc]x", "dx", false, true)]
    [InlineData("[!abc]x", "ax", false, false)]
    [InlineData("[^abc]x", "dx", false, true)]
    // Case is ignored.
    [InlineData("README.md", "readme.MD", false, true)]
    [InlineData("*.MD", "docs/guide.md", false, true)]
    [InlineData("[a-c]x", "BX", false, true)]
    public void TheDocumentedPatternsMatchAsDocumented(string pattern, string path, bool isDirectory, bool expected)
    {
        Ignored(pattern, path, isDirectory).ShouldBe(expected);
    }

    [Theory]
    // A line feed in a name is an ordinary character, as git reads it. The regular expression this replaced stopped
    // ** at one and let `$` match before a final one.
    [InlineData("*.md", "x.md\n", false)]
    [InlineData("a*b", "a\nb", true)]
    [InlineData("**/c", "a\nb/c", true)]
    [InlineData("a**b", "a\nb", true)]
    [InlineData("a?b", "a\nb", true)]
    [InlineData("[!x]y", "\ny", true)]
    public void ALineFeedInANameIsAnOrdinaryCharacter(string pattern, string path, bool expected)
    {
        Ignored(pattern, path).ShouldBe(expected);
    }

    public static TheoryData<string> PathologicalGlobs() =>
    [
        "*a*a*a*a*a*a*a*a*a*a*a*a*b",
        string.Concat(Enumerable.Repeat("**/", 10)) + "x",
        string.Concat(Enumerable.Repeat("**/", 10)),
        string.Concat(Enumerable.Repeat("*a", 250)),
        string.Concat(Enumerable.Repeat("*a", 249)) + "*b",
        new string('?', 500),
        string.Concat(Enumerable.Repeat("[ab]*", 100)),
        string.Concat(Enumerable.Repeat("**/a", 125)),
        "a/" + string.Concat(Enumerable.Repeat("**/", 100)) + "z",
    ];

    private static IEnumerable<string> LongPaths(int length)
    {
        yield return new string('a', length);
        yield return new string('a', length - 1) + "b";
        yield return string.Concat(Enumerable.Repeat("a/", length / 2));
        yield return string.Concat(Enumerable.Repeat("ab/", length / 3));
        yield return string.Join('/', Enumerable.Repeat(new string('a', 15), length / 16));
    }

    private static long Steps(GlobMatcher matcher, string path)
    {
        long steps = 0;
        matcher.IsMatch(path, ref steps);
        return steps;
    }

    [Theory]
    [MemberData(nameof(PathologicalGlobs))]
    public void AMatchTakesNoMoreStepsThanTheTokensTimesThePath(string glob)
    {
        // The bound the design promises: a token costs at most two looks at each position of the path.
        var matcher = GlobMatcher.Compile(glob, "");
        foreach (var path in LongPaths(4096))
        {
            var bound = 2L * (matcher.TokenCount + 1) * (path.Length + 2);
            Steps(matcher, path).ShouldBeLessThanOrEqualTo(bound, $"path of {path.Length} characters");
        }
    }

    [Theory]
    [MemberData(nameof(PathologicalGlobs))]
    public void DoublingThePathAtMostDoublesTheSteps(string glob)
    {
        // Steps are counted and not timed, so this holds on a loaded machine. A scan that restarted a star from the
        // start of the pattern would square the count, and the ratio would be about 4.
        var matcher = GlobMatcher.Compile(glob, "");
        var half = LongPaths(2048).ToList();
        var full = LongPaths(4096).ToList();

        for (var i = 0; i < half.Count; i++)
        {
            var small = Steps(matcher, half[i]);
            var large = Steps(matcher, full[i]);
            if (small < 1_000) continue;

            ((double)large / small).ShouldBeLessThan(2.6, $"shape {i}: {small} steps at 2,048 and {large} at 4,096");
        }
    }

    [Fact]
    public void TheTwoReportedGlobsFinishQuicklyOnAHundredCharacterNameAndAThirtyDeepPath()
    {
        var stars = GlobMatcher.Compile("*a*a*a*a*a*a*a*a*a*a*a*a*b", "");
        var slashes = GlobMatcher.Compile(string.Concat(Enumerable.Repeat("**/", 10)) + "x", "");
        var name = new string('a', 100);
        var deep = string.Join('/', Enumerable.Repeat("d", 30)) + "/y";

        Should.CompleteIn(() =>
        {
            for (var i = 0; i < 1_000; i++)
            {
                stars.IsMatch(name).ShouldBeFalse();
                slashes.IsMatch(deep).ShouldBeFalse();
            }
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void ARuleHoldsOnlyItsTokens()
    {
        // The memory the replaced engine could not keep small: a 500-character glob is 500 tokens at most.
        GlobMatcher.Compile(new string('a', 500), "").TokenCount.ShouldBe(501);
        GlobMatcher.Compile(string.Concat(Enumerable.Repeat("*a", 250)), "").TokenCount.ShouldBeLessThanOrEqualTo(501);
    }

    [Fact]
    public void ThePrefixOfANestedFileIsMatchedBeforeAnythingElse()
    {
        var rules = new IgnoreRuleSet();
        rules.AddPatterns(["secret.txt"], "sub/.gitignore", "sub");

        rules.IsIgnored("sub/secret.txt", false).ShouldBeTrue();
        rules.IsIgnored("sub/a/b/SECRET.TXT", false).ShouldBeTrue();
        rules.IsIgnored("other/secret.txt", false).ShouldBeFalse();
        rules.IsIgnored("subsecret.txt", false).ShouldBeFalse();
        rules.IsIgnored("su", false).ShouldBeFalse();
    }
}

/// <summary>
/// <see cref="Fold"/> against the case equivalence of <c>RegexOptions.IgnoreCase</c>, over every code point of the BMP
/// that has a case, so the matcher and the regular expression it replaced agree on what ignoring case means.
/// </summary>
public sealed class CaseFoldTests
{
    [Fact]
    public void FoldPairsEveryCharacterAsTheRegularExpressionDoes()
    {
        var cased = new List<char>();
        for (var i = 0; i <= char.MaxValue; i++)
        {
            var c = (char)i;
            if (char.IsSurrogate(c)) continue;
            if (char.ToUpperInvariant(c) != c || char.ToLowerInvariant(c) != c) cased.Add(c);
        }

        var disagreements = new List<string>();
        foreach (var a in cased)
        {
            var regex = new Regex("^" + Regex.Escape(a.ToString()) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            foreach (var b in cased)
                if (regex.IsMatch(b.ToString()) != Fold.Equal(a, b))
                    disagreements.Add($"U+{(int)a:X4} and U+{(int)b:X4}");
        }

        disagreements.Take(10).ShouldBeEmpty($"of {cased.Count} cased code points");
        cased.Count.ShouldBeGreaterThan(2_000);
    }

    [Fact]
    public void TheFoldsThatDifferFromUpperCaseComparisonAreTheOnesTheRegularExpressionKeeps()
    {
        Fold.Equal('k', (char)0x212A).ShouldBeTrue("the Kelvin sign is a k");
        Fold.Equal((char)0xDF, (char)0x1E9E).ShouldBeTrue("capital sharp s is sharp s");
        Fold.Equal('s', (char)0x17F).ShouldBeFalse("the long s is not an s here, as in the regular expression");
        Fold.Equal((char)0x3C3, (char)0x3C2).ShouldBeFalse("final sigma is not sigma");
        Fold.Equal((char)0x3C3, (char)0x3A3).ShouldBeTrue();
        Fold.Equal((char)0x1C5, (char)0x1C6).ShouldBeTrue("the titlecase digraph is the digraph");
        Fold.Equal('i', (char)0x131).ShouldBeFalse("dotless i is not an i");
        Fold.Equal('i', (char)0x130).ShouldBeFalse("dotted capital I is not an i");
    }

    [Theory]
    [InlineData("k", "K", true)]
    [InlineData("[k]", "K", true)]
    [InlineData("[a-z]", "K", true)]
    [InlineData("[!a-z]", "K", false)]
    public void AClassIsHeldToTheSameEquivalence(string glob, string path, bool expected)
    {
        // The Kelvin sign is the character after the glob's own letter here: written as a code point so the source
        // holds no look-alike. It is in [a-z] by way of k, and in [k].
        var kelvin = ((char)0x212A).ToString();
        var rules = new IgnoreRuleSet();
        rules.AddPatterns([glob], "test");

        rules.IsIgnored(path == "K" ? kelvin : path, false).ShouldBe(expected);
    }
}
