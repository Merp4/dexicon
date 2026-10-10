using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// How a bracket class reads its members, against git. The expected values were measured with
/// <c>git check-ignore --no-index</c> on a <c>.gitignore</c> holding the one pattern, on git 2.31.1 (Windows) and
/// git 2.54.0 (Linux, in the dexicon image), which agreed on every case here. .NET reads some of these differently
/// when a class is handed to it as written: <c>[a-z-[b]]</c> is a subtraction, <c>\d</c> is a digit class, and a
/// positive class can hold <c>/</c>.
/// </summary>
public sealed class BracketClassMembersTests
{
    private static bool Ignored(string pattern, string path)
    {
        var rules = new IgnoreRuleSet();
        rules.AddPatterns([pattern], "test");
        return rules.IsIgnored(path, isDirectory: false);
    }

    [Theory]
    // `[` is a member. .NET would read the first as a subtraction of [b] from a-z-; git reads the class
    // as a-z, -, [ and b, closed by the first ], then a literal ].
    [InlineData("[a-z-[b]]", "a]", true)]
    [InlineData("[a-z-[b]]", "z]", true)]
    [InlineData("[a-z-[b]]", "b]", true)]
    [InlineData("[a-z-[b]]", "[]", true)]
    [InlineData("[a-z-[b]]", "-]", true)]
    [InlineData("[a-z-[b]]", "1]", false)]
    [InlineData("[a-z-[b]]", "a", false)]
    [InlineData("[a-z-[b]]", "-", false)]
    [InlineData("[[]", "[", true)]
    [InlineData("[[]", "a", false)]
    [InlineData("[[]", "[]", false)]
    [InlineData("[a[]", "a", true)]
    [InlineData("[a[]", "[", true)]
    [InlineData("[a[]", "b", false)]
    [InlineData("[[a]", "[", true)]
    [InlineData("[[a]", "a", true)]
    [InlineData("[[a]", "b", false)]
    public void ABracketInsideAClassIsAMember(string pattern, string path, bool expected)
    {
        Ignored(pattern, path).ShouldBe(expected);
    }

    [Theory]
    // A - after a finished range, or as the last member, is a member and not the start of another range.
    [InlineData("[a-z-x]", "a", true)]
    [InlineData("[a-z-x]", "-", true)]
    [InlineData("[a-z-x]", "x", true)]
    [InlineData("[a-z-x]", "5", false)]
    // A backslash takes the next character literally.
    [InlineData("[a\\-c]x", "-x", true)]
    [InlineData("[a\\-c]x", "bx", false)]
    [InlineData("[a\\-c]x", "cx", true)]
    [InlineData("[\\]]x", "]x", true)]
    [InlineData("[\\]]x", "ax", false)]
    [InlineData("[\\d]x", "dx", true)]
    [InlineData("[\\d]x", "1x", false)]
    [InlineData("a[+--]b", "a,b", true)]
    [InlineData("a[+--]b", "a-b", true)]
    [InlineData("a[+--]b", "a.b", false)]
    public void DashesAndEscapesAreReadAsGitReadsThem(string pattern, string path, bool expected)
    {
        Ignored(pattern, path).ShouldBe(expected);
    }

    [Theory]
    // No class matches /, positive or negated.
    [InlineData("a[/]b", "a/b")]
    [InlineData("a[/]b", "ab")]
    [InlineData("a[!x]b", "a/b")]
    [InlineData("a[^x]b", "a/b")]
    public void NoClassMatchesAPathSeparator(string pattern, string path)
    {
        Ignored(pattern, path).ShouldBeFalse();
    }

    [Fact]
    public void ARangeAcrossThePathSeparatorMatchesTheCharactersOnEachSideOfIt()
    {
        // `[+-9]` runs from + to 9 and passes over /. Git leaves / out of it.
        Ignored("a[+-9]b", "a.b").ShouldBeTrue();
        Ignored("a[+-9]b", "a0b").ShouldBeTrue();
        Ignored("a[+-9]b", "a/b").ShouldBeFalse();
        Ignored("a[+-9]b", "a:b").ShouldBeFalse();
    }

    [Fact]
    public void AClassWithOnlyASlashMatchesNothing()
    {
        Ignored("a[/]b", "a/b").ShouldBeFalse();
        Ignored("a[/]b", "a[/]b").ShouldBeFalse();
    }

    [Fact]
    public void AnUnterminatedClassIsALiteralBracket()
    {
        // Git matches nothing for `[abc`, not even a file of that name. Here the bracket is a character, which is
        // how an opening bracket with no close has been read here since classes were; the line is not refused.
        Ignored("[abc", "[abc").ShouldBeTrue();
        Ignored("[abc", "a").ShouldBeFalse();
    }

    [Theory]
    [InlineData("[z-a]")]
    [InlineData("a[b-/]c")]
    [InlineData("[]")]
    public void ARangeBackwardsAndAnEmptyClassAreRefused(string pattern)
    {
        // Git reads `[z-a]` as the single character z and `a[b-/]c` as b, which is nothing the line's writer meant.
        // Refused, so the line is seen: a .dexiconignore fails and a .gitignore line is skipped with a warning.
        Should.Throw<IgnorePatternException>(() => new IgnoreRuleSet().AddPatterns([pattern], "test"));
    }

    [Theory]
    // More rows from git (the same two versions).
    [InlineData("[a-]x", "ax", true)]
    [InlineData("[a-]x", "-x", true)]
    [InlineData("[a-]x", "bx", false)]
    [InlineData("[a-a]x", "ax", true)]
    [InlineData("[a-a]x", "bx", false)]
    [InlineData("[--/]x", ".x", true)]
    [InlineData("[--/]x", "-x", true)]
    [InlineData("[--/]x", "+x", false)]
    [InlineData("[--/]x", "0x", false)]
    [InlineData("[!-a]x", "-x", false)]
    [InlineData("[!-a]x", "ax", false)]
    [InlineData("[!-a]x", "bx", true)]
    [InlineData("[!-a]x", "!x", true)]
    [InlineData("[a-c-e]x", "ax", true)]
    [InlineData("[a-c-e]x", "cx", true)]
    [InlineData("[a-c-e]x", "-x", true)]
    [InlineData("[a-c-e]x", "ex", true)]
    [InlineData("[a-c-e]x", "dx", false)]
    [InlineData("[:alpha:]x", "ax", true)]
    [InlineData("[:alpha:]x", "lx", true)]
    [InlineData("[:alpha:]x", "bx", false)]
    public void MoreClassesAreReadAsGitReadsThem(string pattern, string path, bool expected)
    {
        Ignored(pattern, path).ShouldBe(expected);
    }

    [Theory]
    // A POSIX class is read by git as the characters it names (`[[:alpha:]]x` ignores `ax` and `Ax`), and as the members
    // `[:alph` and a literal `]` it would match nothing git ignores, which fails open. Refused, so the line is seen.
    [InlineData("[[:alpha:]]x")]
    [InlineData("[[:digit:]]x")]
    [InlineData("[[:upper:]]x")]
    [InlineData("[[:alnum:]_]x")]
    [InlineData("[a[:digit:]]x")]
    [InlineData("[[:foo:]]x")]
    [InlineData("[![:space:]]x")]
    [InlineData("[[:ALPHA:]]x")]
    [InlineData("[[:a:]]x")]
    [InlineData("[[:a1:]]x")]
    [InlineData("[[::]]x")]
    [InlineData("[[:foo bar:]]x")]
    public void APosixClassIsRefused(string pattern)
    {
        var thrown = Should.Throw<IgnorePatternException>(() => new IgnoreRuleSet().AddPatterns([pattern], "test"));

        thrown.Message.ShouldBe($"test line 1 ('{pattern}') cannot be compiled (a POSIX character class such as [:alpha:] is not supported)");
        SourceFilters.FirstUnusable([pattern]).ShouldBe(0);
    }

    [Theory]
    // Not POSIX syntax, so members, as in git: the first `]` after `[:` must follow a colon that is not the one in `[:`.
    [InlineData("[[:ab:cd]x", "cx", true)]
    [InlineData("[[:ab:cd]x", "ax", true)]
    [InlineData("[[:ab:cd]x", "zx", false)]
    public void ABracketColonThatIsNotAPosixClassIsOrdinaryMembers(string pattern, string path, bool expected)
    {
        Ignored(pattern, path).ShouldBe(expected);
    }

    [Fact]
    public void ABracketColonWithNoClosingColonIsOrdinaryMembers()
    {
        // The colon check is for `[:name:` ending the class. `[[:]` holds `[` and `:`.
        Ignored("a[[:]b", "a[b").ShouldBeTrue();
        Ignored("a[[:]b", "a:b").ShouldBeTrue();
        Ignored("a[[:]b", "axb").ShouldBeFalse();
    }

    [Theory]
    // A class holds a character when it holds any case variant of it, so a range of capitals holds the small letters.
    [InlineData("[A-C]x", "bx", true)]
    [InlineData("[A-C]x", "Bx", true)]
    [InlineData("[A-C]x", "dx", false)]
    [InlineData("[a-c]x", "Bx", true)]
    [InlineData("[!A-C]x", "bx", false)]
    [InlineData("[!A-C]x", "dx", true)]
    [InlineData("[\u00E9]x", "\u00C9x", true)]
    [InlineData("[\u00C0-\u00C5]x", "\u00E4x", true)]
    public void AClassFoldsCaseOverItsRanges(string pattern, string path, bool expected)
    {
        Ignored(pattern, path).ShouldBe(expected);
    }

    [Theory]
    // The ASCII table of a class has a bit for each of the 128 characters: the last of the low word and the first of the high.
    [InlineData("[?@]x", "?x", true)]
    [InlineData("[?@]x", "@x", true)]
    [InlineData("[>A]x", "Ax", true)]
    [InlineData("[~]x", "~x", true)]
    [InlineData("[ -/]x", " x", true)]
    [InlineData("[ -/]x", "/x", false)]
    public void EveryAsciiCharacterIsAMemberWhenItIsNamed(string pattern, string path, bool expected)
    {
        Ignored(pattern, path).ShouldBe(expected);
    }
}
