using Dexicon.Core.Indexing;

namespace Dexicon.Tests;

/// <summary>
/// A bracket class that starts with <c>!</c> negates it in a gitignore pattern: <c>[!a]*.md</c> is every
/// markdown file whose name does not start with <c>a</c>. The pattern was copied into the regular
/// expression as written, where .NET reads <c>!</c> as a member, so the class matched <c>!</c> or
/// <c>a</c> and an include list or an exclude list selected the opposite of what it said.
/// </summary>
public sealed class NegatedBracketClassTests
{
    private static bool Ignored(string pattern, string path)
    {
        var rules = new IgnoreRuleSet();
        rules.AddPatterns([pattern], "test");
        return rules.IsIgnored(path, isDirectory: false);
    }

    [Theory]
    [InlineData("[!a]*.md", "b.md", true)]
    [InlineData("[!a]*.md", "a.md", false)]
    [InlineData("[!a]*.md", "!.md", true)]
    [InlineData("[!a-c]x", "dx", true)]
    [InlineData("[!a-c]x", "bx", false)]
    [InlineData("docs/[!_]*", "docs/index.md", true)]
    [InlineData("docs/[!_]*", "docs/_draft.md", false)]
    [InlineData("v[!0-9]*", "vx", true)]
    [InlineData("v[!0-9]*", "v1", false)]
    public void AClassStartingWithABangMatchesWhatIsNotInIt(string pattern, string path, bool expected)
    {
        Ignored(pattern, path).ShouldBe(expected);
    }

    [Theory]
    [InlineData("[^a]*.md", "b.md", true)]
    [InlineData("[^a]*.md", "a.md", false)]
    [InlineData("[a-c]x", "bx", true)]
    [InlineData("[a-c]x", "dx", false)]
    public void ACaretClassAndAPlainClassMatchAsTheyDid(string pattern, string path, bool expected)
    {
        // The control for the theory above: a caret class and a plain class pick the same files as before.
        Ignored(pattern, path).ShouldBe(expected);
    }

    [Theory]
    [InlineData("v[!0-9]*", "v/foo")]
    [InlineData("v[^0-9]*", "v/foo")]
    [InlineData("docs[!_]x", "docs/x")]
    public void ANegatedClassDoesNotMatchAPathSeparator(string pattern, string path)
    {
        // In gitignore a class never matches /, so a pattern cannot reach across a directory boundary.
        // Without it the negated class took the / and the pattern selected everything under a directory called v.
        Ignored(pattern, path).ShouldBeFalse();
    }

    [Fact]
    public void ALoneBangInAClassIsStillAMember()
    {
        // Git reads this as an unterminated class that matches nothing. Dexicon keeps the one-member class it has
        // always had, which still compiles: nothing follows the bang, so there is nothing to negate.
        Ignored("[!]x", "!x").ShouldBeTrue();
        Ignored("[!]x", "ax").ShouldBeFalse();
    }
}
