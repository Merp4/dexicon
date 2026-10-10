using System.Text.RegularExpressions;
using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// What <see cref="GlobMatcher"/> matches, as a table of the gitignore documentation's examples, and how much work a
/// match may take. <see cref="GlobDifferentialTests"/> compares the matcher with a regular expression translation of each pattern.
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
    // Three or more stars that make a whole segment are one `**`, as in git.
    [InlineData("***/foo", "foo", false, true)]
    [InlineData("***/foo", "a/b/foo", false, true)]
    [InlineData("a/***/b", "a/b", false, true)]
    [InlineData("a/****/b", "a/x/y/b", false, true)]
    [InlineData("a/***", "a/x/y", false, true)]
    [InlineData("a/***", "a", false, false)]
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
    // The search for the longest literal run restarts inside a partial match as Knuth-Morris-Pratt does, so a run that
    // begins inside an earlier near miss is found.
    [InlineData("*aab*", "aaab", true)]
    [InlineData("*aab*", "xaaab", true)]
    [InlineData("*abcabd*", "abcabcabd", true)]
    [InlineData("*abcabd*", "abcabcabc", false)]
    [InlineData("*AAB*", "aAab", true)]
    [InlineData("*aabaab*", "aabaaab", false)]
    [InlineData("*aabaaab*", "aabaaab", true)]
    [InlineData("*abab*", "ababab", true)]
    public void ALiteralRunIsFoundWhereverItBeginsInsideAnEarlierNearMiss(string pattern, string path, bool expected)
    {
        Ignored(pattern, path).ShouldBe(expected);
    }

    [Theory]
    // A line feed in a name is an ordinary character, as git reads it: `**` crosses it, and `*.md` does not match a
    // name that ends in one after `.md`. A regular expression translation would stop `**` at a line feed and let `$`
    // match before a final one.
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
        "*" + new string('a', 498),
    ];

    private static readonly string[] Endings = ["", "b", "x", "z"];

    /// <summary>
    /// Paths of about <paramref name="length"/> characters: one segment, deep, and segments of three and fifteen characters,
    /// each ending in nothing and in each letter the globs above end in, so that the match reaches the end of the pattern
    /// and is not turned away by a search for its last literal.
    /// </summary>
    private static IEnumerable<string> LongPaths(int length)
    {
        foreach (var ending in Endings)
        {
            yield return new string('a', length) + ending;
            yield return new string('a', length - 1) + "b" + ending;
            yield return string.Concat(Enumerable.Repeat("a/", length / 2)) + ending;
            yield return string.Concat(Enumerable.Repeat("ab/", length / 3)) + ending;
            yield return string.Join('/', Enumerable.Repeat(new string('a', 15), length / 16)) + ending;
        }
    }

    private const int ShapeCount = 20;

    private static long Steps(GlobMatcher matcher, string path, bool beneath = false)
    {
        long steps = 0;
        if (beneath) matcher.IsMatchBeneath(path, ref steps);
        else matcher.IsMatch(path, ref steps);
        return steps;
    }

    /// <summary>A walk that does not finish cannot fail itself, so the loop is given a limit the test can fail on.</summary>
    private static void Bounded(Action action) => Should.CompleteIn(action, TimeSpan.FromSeconds(60));

    /// <summary>The most steps a match can take: see the cost paragraph of <see cref="GlobMatcher"/>.</summary>
    private static long Bound(GlobMatcher matcher, string glob, string path)
    {
        var words = (path.Length >> 6) + 1;
        var classes = glob.Count(c => c == '[');
        return (matcher.TokenCount + 1L) * words + classes * (path.Length + 1L) + path.Length;
    }

    [Theory]
    [MemberData(nameof(PathologicalGlobs))]
    public void AMatchTakesNoMoreStepsThanTokensTimesWordsPlusAClassTestForEachPositionPlusOnePassOverThePath(string glob)
    {
        var matcher = GlobMatcher.Compile(glob, "");
        var checkedShapes = 0;

        Bounded(() =>
        {
            foreach (var path in LongPaths(4096))
            {
                var bound = Bound(matcher, glob, path);
                Steps(matcher, path).ShouldBeLessThanOrEqualTo(bound, $"path of {path.Length} characters");
                Steps(matcher, path, beneath: true).ShouldBeLessThanOrEqualTo(bound, $"path of {path.Length} characters, beneath");
                checkedShapes++;
            }
        });

        checkedShapes.ShouldBe(ShapeCount);
    }

    [Theory]
    [MemberData(nameof(PathologicalGlobs))]
    public void DoublingThePathAtMostDoublesTheSteps(string glob)
    {
        // Steps are counted and not timed, so this holds on a loaded machine. A scan that squared the count would show
        // as a ratio near 4. Shapes that cost under 200 steps are too small to give a ratio and are not counted;
        // the test fails if fewer than four shapes of a glob were.
        var matcher = GlobMatcher.Compile(glob, "");
        var half = LongPaths(2048).ToList();
        var full = LongPaths(4096).ToList();
        var ratios = new List<double>();

        Bounded(() =>
        {
            for (var i = 0; i < half.Count; i++)
            {
                var small = Steps(matcher, half[i]);
                var large = Steps(matcher, full[i]);
                if (small < 200) continue;

                ratios.Add((double)large / small);
            }
        });

        ratios.Count.ShouldBeGreaterThanOrEqualTo(4, "shapes that were large enough to measure");
        ratios.ShouldAllBe(r => r < 2.6, $"ratios: {string.Join(", ", ratios.Select(r => r.ToString("F2")))}");
    }

    [Fact]
    public void AFileAgainstADirectoryOnlyRuleCostsOnePassAndNotOneForEachDirectoryAbove()
    {
        // A rule for directories is asked of a file by whether some match ends at a slash before the end of the
        // path, in one pass. Asked once for each directory above the file, the steps would grow with the square of
        // the depth, and doubling the depth here must at most double them.
        var rule = GlobMatcher.Compile(string.Concat(Enumerable.Repeat("*a", 248)) + "*b", "");
        var deep = string.Concat(Enumerable.Repeat("a/", 2_040));
        var deeper = string.Concat(Enumerable.Repeat("a/", 4_080));

        var steps = 0L;
        var more = 0L;
        Bounded(() =>
        {
            steps = Steps(rule, deep, beneath: true);
            more = Steps(rule, deeper, beneath: true);
        });

        steps.ShouldBeLessThanOrEqualTo(Bound(rule, string.Concat(Enumerable.Repeat("*a", 248)) + "*b", deep));
        ((double)more / steps).ShouldBeLessThan(2.6);
    }

    [Theory]
    // glob, path, beneath, matched, steps. A path of under 64 characters is one word, so a step is a token, a
    // position a class is asked about, or a character the literal search reads. `bin` is the any-depth prefix and three
    // literals (4) and the search reads `bin` (3).
    [InlineData("*a*b", "aab", false, true, 6L)]
    [InlineData("**/x", "a/b/x", false, true, 3L)]
    [InlineData("a?c", "abc", false, true, 5L)]
    [InlineData("bin", "src/bin/x.dll", true, true, 7L)]
    [InlineData("bin", "src/bin", true, false, 7L)]
    [InlineData("bin", "src/bin/x.dll", false, true, 7L)]
    [InlineData("docs/*.md", "docs/a.md", false, true, 14L)]
    [InlineData("[a-c]*x", "bcx", false, true, 6L)]
    [InlineData("a*a*a*b", "aaaa", false, false, 9L)]
    public void TheStepsOfASmallMatchAreWhatTheCostModelSays(string glob, string path, bool beneath, bool matched, long expected)
    {
        var matcher = GlobMatcher.Compile(glob, "");
        long steps = 0;

        (beneath ? matcher.IsMatchBeneath(path, ref steps) : matcher.IsMatch(path, ref steps)).ShouldBe(matched);

        steps.ShouldBe(expected);
    }

    [Theory]
    // A path of 200 characters is 4 words. The any-depth prefix reaches into the 3 words after the first, which a `*`
    // and `**` then fill the same way, and `?` moves the one position it holds and extends nothing.
    [InlineData("*", true, 8L)]
    [InlineData("**", true, 8L)]
    [InlineData("?", false, 5L)]
    public void TheStepsOnAPathOfSeveralWordsCountTheWordsAWildcardFills(string glob, bool matched, long expected)
    {
        var matcher = GlobMatcher.Compile(glob, "");
        long steps = 0;

        matcher.IsMatch(new string('a', 200), ref steps).ShouldBe(matched);

        steps.ShouldBe(expected);
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
        // A rule holds its tokens: a 500-character glob is 501 tokens at most.
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
/// that has a case, so the matcher and the regular expression's case-insensitive comparison agree on what ignoring case
/// means.
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

        // The number of code points with a case is the runtime's: 2,357 with ICU, 2,366 under the invariant globalization
        // mode this repository's projects set. Either way there is no pair the two disagree on.
        var invariant = AppContext.TryGetSwitch("System.Globalization.Invariant", out var on) && on;
        disagreements.Take(10).ShouldBeEmpty($"of {cased.Count} cased code points");
        cased.Count.ShouldBe(invariant ? 2_366 : 2_357);
    }

    [Fact]
    public void TheFoldsThatDifferFromUpperCaseComparisonAreTheOnesTheRegularExpressionKeeps()
    {
        Fold.Equal('k', (char)0x212A).ShouldBeTrue("the Kelvin sign is a k");
        Fold.Equal((char)0xDF, (char)0x1E9E).ShouldBeTrue("capital sharp s is sharp s");
        Fold.Equal('s', (char)0x17F).ShouldBeFalse("the long s is not an s, as in the regular expression's comparison");
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

    [Fact]
    public void ADirectoryIsNotPrunedWhenARuleReincludesBeneathItUnderACaseVariantOfItsName()
    {
        // The matcher pairs the Kelvin sign with k, so `k/` is ignored, `k/f.txt` is re-included by the second rule,
        // and the walk must go into `k` to find it.
        var kelvin = ((char)0x212A).ToString();
        var rules = new IgnoreRuleSet();
        rules.AddPatterns(["/k/", $"!/{kelvin}/f.txt"], "test");

        rules.IsIgnored("k", isDirectory: true).ShouldBeTrue();
        rules.IsIgnored("k/f.txt", isDirectory: false).ShouldBeFalse();
        rules.MayReincludeBeneath("k").ShouldBeTrue();
        rules.MayReincludeBeneath("k/sub").ShouldBeFalse("nothing is re-included inside k/sub that k/f.txt is not");
    }
}
