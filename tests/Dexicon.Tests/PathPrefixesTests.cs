using Dexicon.Core.Vectors;

namespace Dexicon.Tests;

/// <summary>
/// The folders above a path, stored on each chunk, and the argument they are compared with. A path filter is a
/// prefix at whole path segments and ignores case, so each side is folded and a value that stops inside a name
/// has no match.
/// </summary>
public sealed class PathPrefixesTests
{
    [Fact]
    public void AFileHasEachFolderAboveItAndItself()
    {
        PathPrefixes.Of("src/Auth/a.cs").ShouldBe(["src", "src/auth", "src/auth/a.cs"]);
    }

    [Fact]
    public void AFileAtTheRootHasOnlyItself()
    {
        PathPrefixes.Of("Readme.MD").ShouldBe(["readme.md"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    public void ABlankPathHasNone(string path)
    {
        PathPrefixes.Of(path).ShouldBeEmpty();
    }

    [Fact]
    public void ARepeatedOrLeadingSlashAddsNoEmptySegment()
    {
        PathPrefixes.Of("/src//Auth/a.cs").ShouldBe(["src", "src/auth", "src/auth/a.cs"]);
    }

    [Theory]
    [InlineData("docs\\guide.pdf", "docs", "docs/guide.pdf")]
    [InlineData("./x.pdf", "x.pdf")]
    [InlineData("a/./b.txt", "a", "a/b.txt")]
    [InlineData("report.pdf ", "report.pdf")]
    [InlineData("dir / file.txt", "dir", "dir/file.txt")]
    public void AStoredPathIsReadWithTheSameRulesAsAnArgument(string stored, params string[] expected)
    {
        PathPrefixes.Of(stored).ShouldBe(expected);
    }

    [Theory]
    [InlineData("docs\\guide.pdf")]
    [InlineData("./x.pdf")]
    [InlineData("a/./b.txt")]
    [InlineData("report.pdf ")]
    [InlineData("dir / file.txt")]
    [InlineData("/src//Auth/a.cs")]
    [InlineData("Doku/ÄÖÜ.txt")]
    public void TheArgumentThatNamesAStoredFileIsOneOfItsPrefixes(string stored)
    {
        PathPrefixes.Of(stored).ShouldContain(PathPrefixes.Normalise(stored)!);
    }

    [Fact]
    public void ANonAsciiNameIsFoldedWithTheInvariantCulture()
    {
        PathPrefixes.Of("Doku/ÄÖÜ.txt").ShouldBe(["doku", "doku/äöü.txt"]);
    }

    [Theory]
    [InlineData("src/Auth", "src/auth")]
    [InlineData("src/Auth/", "src/auth")]
    [InlineData("/src/Auth", "src/auth")]
    [InlineData("./src/Auth", "src/auth")]
    [InlineData("src\\Auth", "src/auth")]
    [InlineData("src//Auth", "src/auth")]
    [InlineData(" src / Auth ", "src/auth")]
    public void AnArgumentIsComparedWithoutCaseSlashesOrADotSegment(string value, string expected)
    {
        PathPrefixes.Normalise(value).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData(".")]
    [InlineData("./")]
    [InlineData("\\")]
    public void AnArgumentThatNamesNoPathIsNull(string? value)
    {
        PathPrefixes.Normalise(value).ShouldBeNull();
    }

    [Theory]
    [InlineData("src/auth", "src/Auth/a.cs", true)]
    [InlineData("src/auth", "lib/src/Auth/b.cs", false)]
    [InlineData("src/auth", "src/Authorization/c.cs", false)]
    [InlineData("src/auth/a.cs", "src/Auth/a.cs", true)]
    [InlineData("src", "src/Auth/a.cs", true)]
    [InlineData("src/au", "src/Auth/a.cs", false)]
    public void AFolderOrAFileIsFoundWhenItIsOneOfTheStoredPrefixes(string normalised, string storedPath, bool found)
    {
        PathPrefixes.Of(storedPath).Contains(normalised).ShouldBe(found);
    }
}
