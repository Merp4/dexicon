using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// In gitignore a <c>]</c> straight after <c>[</c>, <c>[!</c> or <c>[^</c> is a member of the class, so
/// <c>[]a]</c> matches <c>]</c> or <c>a</c>. The scan for the closing bracket took the first <c>]</c> it found.
/// That split <c>[!]a]</c> into the one-member class <c>[!]</c> followed by the literal <c>a]</c>, and
/// <c>[^]a]</c> into <c>[^]</c> followed by <c>a]</c>, which .NET happened to read as the right class but
/// without excluding <c>/</c>. <c>[]a]</c> was split the same way and also came out right, because .NET reads
/// a leading <c>]</c> as a member.
/// </summary>
public sealed class LeadingBracketClassTests
{
    private static bool Ignored(string pattern, string path)
    {
        var rules = new IgnoreRuleSet();
        rules.AddPatterns([pattern], "test");
        return rules.IsIgnored(path, isDirectory: false);
    }

    [Theory]
    [InlineData("[]a]x", "]x", true)]
    [InlineData("[]a]x", "ax", true)]
    [InlineData("[]a]x", "bx", false)]
    [InlineData("[]a]x", "x", false)]
    [InlineData("v[]]", "v]", true)]
    [InlineData("v[]]", "v", false)]
    [InlineData("[]-a]x", "^x", true)]
    [InlineData("[]-a]x", "bx", false)]
    public void ABracketRightAfterTheOpeningBracketIsAMember(string pattern, string path, bool expected)
    {
        Ignored(pattern, path).ShouldBe(expected);
    }

    [Theory]
    [InlineData("[!]a]x", "bx", true)]
    [InlineData("[!]a]x", "]x", false)]
    [InlineData("[!]a]x", "ax", false)]
    [InlineData("[^]a]x", "bx", true)]
    [InlineData("[^]a]x", "]x", false)]
    [InlineData("[^]a]x", "ax", false)]
    [InlineData("[!]]x", "ax", true)]
    [InlineData("[!]]x", "]x", false)]
    public void ABracketRightAfterTheNegationIsAMemberOfTheNegatedClass(string pattern, string path, bool expected)
    {
        Ignored(pattern, path).ShouldBe(expected);
    }

    [Theory]
    [InlineData("v[!]a]x", "v/x")]
    [InlineData("v[^]a]x", "v/x")]
    public void ANegatedClassWithALeadingBracketStillDoesNotMatchAPathSeparator(string pattern, string path)
    {
        Ignored(pattern, path).ShouldBeFalse();
    }

    [Fact]
    public void TheClassIsWrittenOutWithTheBracketEscapedAndTheSlashExcluded()
    {
        // Pins what .NET is given. A leading ] is only literal as the first character of a class, so behind the
        // ^/ that a negated class carries it has to be escaped, or .NET closes the class there.
        IgnoreRuleSet.ToRegex("[]a]").ShouldBe(@"^(?:.*/)?[\]a](?:/.*)?$");
        IgnoreRuleSet.ToRegex("[!]a]").ShouldBe(@"^(?:.*/)?[^/\]a](?:/.*)?$");
        IgnoreRuleSet.ToRegex("[^]a]").ShouldBe(@"^(?:.*/)?[^/\]a](?:/.*)?$");
    }

    [Fact]
    public void ALeadingBracketInANestedFileIsAnchoredBeneathItsDirectory()
    {
        var rules = new IgnoreRuleSet();
        rules.AddPatterns(["[]a]x"], "sub/.gitignore", "sub");

        rules.IsIgnored("sub/ax", isDirectory: false).ShouldBeTrue();
        rules.IsIgnored("other/ax", isDirectory: false).ShouldBeFalse();
    }

    [Theory]
    [InlineData("[!]x", "!x", true)]
    [InlineData("[!]x", "ax", false)]
    [InlineData("[!a]x", "bx", true)]
    [InlineData("[!a]x", "ax", false)]
    [InlineData("[ab]x", "ax", true)]
    [InlineData("[ab]x", "cx", false)]
    public void AClassWithNoLeadingBracketReadsAsItDid(string pattern, string path, bool expected)
    {
        // The controls: a lone bang is still a one-member class, and a plain or negated class is unchanged.
        Ignored(pattern, path).ShouldBe(expected);
    }

    [Theory]
    [InlineData("[]x")]
    [InlineData("docs/[]x")]
    [InlineData("[^]x")]
    public void ABracketWithNoClosingBracketAfterItStaysUnusable(string pattern)
    {
        // `[]x` has no second ] to close it. .NET refuses the class, which is what kept it from being stored,
        // and it is still refused rather than read as a literal.
        Should.Throw<IgnorePatternException>(() => new IgnoreRuleSet().AddPatterns([pattern], "test"));
        SourceFilters.FirstUnusable([pattern]).ShouldBe(0);
    }

    [Theory]
    [InlineData("[]a]x")]
    [InlineData("[!]a]x")]
    [InlineData("[^]a]x")]
    public void TheStoredListCheckAcceptsWhatTheWalkNowReads(string pattern)
    {
        // FirstUnusable compiles with the walk's own parser, so the two agree on a leading bracket.
        SourceFilters.FirstUnusable([pattern]).ShouldBeNull();
    }
}
