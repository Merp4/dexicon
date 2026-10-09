using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// A glob that compiles can still take exponential time to match. <c>*a*a*a*a*a*a*a*a*a*a*a*a*b</c> against
/// a hundred-character name, and ten <c>**/</c> in a row against a path thirty directories deep, ran past the
/// 250 ms match timeout, and <see cref="IgnoreRuleSet.IsIgnored"/> threw <see cref="System.Text.RegularExpressions.RegexMatchTimeoutException"/>
/// from the middle of a walk, which nothing caught. The same timeout also fired for ordinary patterns when the
/// host was starved of CPU.
/// </summary>
public sealed class PathologicalGlobTests : IDisposable
{
    private const string ManyStars = "*a*a*a*a*a*a*a*a*a*a*a*a*b";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pathological-{Guid.NewGuid():N}");

    public PathologicalGlobTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static bool Ignored(string pattern, string path, bool isDirectory = false)
    {
        var rules = new IgnoreRuleSet();
        rules.AddPatterns([pattern], "test");
        return rules.IsIgnored(path, isDirectory);
    }

    private static string Deep(int levels, string last) =>
        string.Join('/', Enumerable.Repeat("d", levels)) + "/" + last;

    [Fact]
    public void AGlobOfManyStarsDoesNotMatchANameThatLacksItsLastLiteral()
    {
        Ignored(ManyStars, new string('a', 100)).ShouldBeFalse();
    }

    [Fact]
    public void AGlobOfManyStarsStillMatchesANameThatHasIt()
    {
        // The control: the same pattern against a name it does match, so the test above is not passing because
        // the pattern matches nothing.
        Ignored(ManyStars, new string('a', 100) + "b").ShouldBeTrue();
    }

    [Fact]
    public void RepeatedDoubleStarSlashesDoNotMatchAPathWithoutTheFinalLiteral()
    {
        var pattern = string.Concat(Enumerable.Repeat("**/", 10)) + "x";

        Ignored(pattern, Deep(30, "y")).ShouldBeFalse();
    }

    [Fact]
    public void RepeatedDoubleStarSlashesStillMatchAPathWithTheFinalLiteral()
    {
        var pattern = string.Concat(Enumerable.Repeat("**/", 10)) + "x";

        Ignored(pattern, Deep(30, "x")).ShouldBeTrue();
    }

    [Fact]
    public void ARunOfDoubleStarSlashesAnswersForAThirtyDeepDirectory()
    {
        // Ten of them with nothing after: the form the report gave. A trailing slash makes it directory-only.
        var pattern = string.Concat(Enumerable.Repeat("**/", 10));

        Ignored(pattern, Deep(30, "y"), isDirectory: true).ShouldBeTrue();
        Ignored(pattern, Deep(30, "y"), isDirectory: false).ShouldBeTrue("a file beneath a directory the pattern matches");
    }

    [Fact]
    public void AWalkOverANameThatTheGlobCannotMatchReturnsTheFile()
    {
        // Through the walk, where the exception surfaced: a .gitignore holding the pattern, and a file whose
        // name is the hundred characters it backtracks over.
        var name = new string('a', 100);
        File.WriteAllText(Path.Combine(_root, ".gitignore"), ManyStars + "\n");
        File.WriteAllText(Path.Combine(_root, name), "hello");

        var files = WorkspaceWalker.Walk(_root, useGitignore: true, null, null, 1_000_000)
            .Files.Select(f => f.RelativePath).ToList();

        files.ShouldContain(name);
    }

    [Fact]
    public void AWalkUnderADexiconignoreOfTheSameGlobReturnsTheFile()
    {
        var name = new string('a', 100);
        File.WriteAllText(Path.Combine(_root, WorkspaceWalker.IgnoreFileName), ManyStars + "\n");
        File.WriteAllText(Path.Combine(_root, name), "hello");

        var files = WorkspaceWalker.Walk(_root, useGitignore: false, null, null, 1_000_000)
            .Files.Select(f => f.RelativePath).ToList();

        files.ShouldContain(name);
    }

    [Fact]
    public void AWalkUnderADeepTreeAndRepeatedDoubleStarsReturnsTheFile()
    {
        var file = Path.Combine(_root, Deep(30, "leaf").Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "hello");

        var files = WorkspaceWalker.Walk(_root, useGitignore: true, null,
            [string.Concat(Enumerable.Repeat("**/", 10)) + "x"], 1_000_000)
            .Files.Select(f => f.RelativePath).ToList();

        files.ShouldContain(Deep(30, "leaf"));
    }
}
