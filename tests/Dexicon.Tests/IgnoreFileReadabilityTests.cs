using System.Text;
using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// An ignore file the walk cannot read as UTF-8 text, or cannot read at all. A <c>.dexiconignore</c> is written to
/// keep content out of the index, so one that is a link, is locked, holds a NUL byte or is UTF-16 fails the walk
/// with the reason, where it used to apply no rules and let the content in. A <c>.gitignore</c> or
/// <c>.git/info/exclude</c> in the same state is skipped whole with a warning. Also the scope of the warnings: a
/// directory another source owns is not read, and a bad line in a directory's own file leaves the rest of that file
/// in force for everything beneath it.
/// </summary>
public sealed class IgnoreFileReadabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"readability-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"readability-outside-{Guid.NewGuid():N}");

    public IgnoreFileReadabilityTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }

    private string FullPath(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    private void Write(string relative, string content = "hello") => WriteBytes(relative, Encoding.UTF8.GetBytes(content));

    private void WriteBytes(string relative, byte[] content)
    {
        var full = FullPath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
    }

    private WorkspaceWalker.WalkResult Walk(bool useGitignore = true, string[]? shadowed = null) =>
        WorkspaceWalker.Walk(_root, useGitignore, null, null, 1_000_000, shadowedPrefixes: shadowed);

    private static List<string> Names(WorkspaceWalker.WalkResult walk) =>
        [.. walk.Files.Select(f => f.RelativePath).OrderBy(p => p, StringComparer.Ordinal)];

    [Fact]
    public void ALinkedDexiconignoreFailsTheWalkInsteadOfApplyingNoRules()
    {
        File.WriteAllText(Path.Combine(_outside, "rules"), "secret.txt\n");
        File.CreateSymbolicLink(FullPath(".dexiconignore"), Path.Combine(_outside, "rules"));
        Write("secret.txt");

        Should.Throw<IgnorePatternException>(() => Walk())
            .Message.ShouldBe(".dexiconignore is a link, and links are not followed");
    }

    [Fact]
    public void ALinkedNestedDexiconignoreIsNamedByItsPath()
    {
        Directory.CreateDirectory(FullPath("sub"));
        File.WriteAllText(Path.Combine(_outside, "rules"), "secret.txt\n");
        File.CreateSymbolicLink(FullPath("sub/.dexiconignore"), Path.Combine(_outside, "rules"));

        Should.Throw<IgnorePatternException>(() => Walk())
            .Message.ShouldBe("sub/.dexiconignore is a link, and links are not followed");
    }

    [Fact]
    public void ADexiconignoreThatCannotBeOpenedFailsTheWalkWithoutNamingTheHostPath()
    {
        Write(".dexiconignore", "secret.txt\n");
        Write("secret.txt");
        using var held = new FileStream(FullPath(".dexiconignore"), FileMode.Open, FileAccess.Read, FileShare.None);

        var thrown = Should.Throw<IgnorePatternException>(() => Walk());

        thrown.Message.ShouldBe(".dexiconignore cannot be read");
        thrown.Message.ShouldNotContain(_root);
    }

    [Fact]
    public void ADexiconignoreWithANulByteFailsTheWalkAndSaysSo()
    {
        WriteBytes(".dexiconignore", Encoding.UTF8.GetBytes("secret.txt\0\n"));

        Should.Throw<IgnorePatternException>(() => Walk()).Message.ShouldBe(
            ".dexiconignore cannot be used as patterns because it contains a NUL character and is not UTF-8 text");
    }

    [Theory]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    [InlineData("utf32le")]
    public void ADexiconignoreWithAUtf16OrUtf32ByteOrderMarkFailsTheWalkAndSaysSo(string encoding)
    {
        var text = "secret.txt\n";
        var bytes = encoding switch
        {
            "utf16le" => Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)),
            "utf16be" => Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(text)),
            _ => Encoding.UTF32.GetPreamble().Concat(Encoding.UTF32.GetBytes(text)),
        };
        WriteBytes(".dexiconignore", [.. bytes]);

        Should.Throw<IgnorePatternException>(() => Walk()).Message.ShouldBe(
            ".dexiconignore cannot be used as patterns because it starts with a UTF-16 or UTF-32 byte order mark and is not UTF-8");
    }

    [Fact]
    public void ADexiconignoreWithAUtf8ByteOrderMarkAndWindowsLineEndingsIsRead()
    {
        // The control for the failures above: the first pattern is not spoiled by the mark, and CRLF splits lines.
        WriteBytes(".dexiconignore", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("secret.txt\r\n*.log\r\n")]);
        Write("secret.txt");
        Write("a.log");
        Write("keep.txt");

        Names(Walk()).ShouldBe([".dexiconignore", "keep.txt"]);
    }

    [Fact]
    public void AGitignoreWithANulByteIsSkippedWholeWithAWarning()
    {
        WriteBytes(".gitignore", Encoding.UTF8.GetBytes("secret.txt\0\n"));
        Write("secret.txt");

        var walk = Walk();

        // The file holds a NUL byte, so the binary sniff leaves it out of the files; the secret is not excluded.
        Names(walk).ShouldBe(["secret.txt"]);
        walk.Warnings.ShouldBe([
            ".gitignore cannot be used as patterns because it contains a NUL character and is not UTF-8 text; the file was skipped"]);
    }

    [Fact]
    public void ANestedGitignoreInUtf16IsSkippedWholeWithAWarningNamingIt()
    {
        WriteBytes("sub/.gitignore", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("secret.txt\n")]);
        Write("sub/secret.txt");

        Walk().Warnings.ShouldBe([
            "sub/.gitignore cannot be used as patterns because it starts with a UTF-16 or UTF-32 byte order mark and is not UTF-8; the file was skipped"]);
    }

    [Fact]
    public void ALockedGitignoreIsSkippedWholeWithAWarning()
    {
        Write(".gitignore", "secret.txt\n");
        using var held = new FileStream(FullPath(".gitignore"), FileMode.Open, FileAccess.Read, FileShare.None);

        Walk().Warnings.ShouldBe([".gitignore cannot be read; the file was skipped"]);
    }

    [Fact]
    public void ALocalGitExcludeInUtf16IsSkippedWholeWithAWarning()
    {
        WriteBytes(".git/info/exclude", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("worktrees/\n")]);

        Walk().Warnings.ShouldBe([
            ".git/info/exclude cannot be used as patterns because it starts with a UTF-16 or UTF-32 byte order mark and is not UTF-8; the file was skipped"]);
    }

    [Fact]
    public void ABadLineInADirectorysGitignoreLeavesTheRestOfThatFileInForceBeneathIt()
    {
        // A bad line costs that line and nothing else, wherever the file sits: the rules after it still reach
        // every directory below. Files above the source root are never read, so there is no ancestor to fail.
        Write(".gitignore", "[z-a]\nsecret.txt\n");
        Write("secret.txt");
        Write("sub/deep/secret.txt");
        Write("sub/deep/keep.txt");

        var walk = Walk();

        Names(walk).ShouldBe([".gitignore", "sub/deep/keep.txt"]);
        walk.Warnings.Count.ShouldBe(1);
    }

    [Fact]
    public void ADirectoryAnotherSourceOwnsIsNotReadSoItsBadGitignoreLineIsNotReported()
    {
        Write("owned/.gitignore", "[z-a]\n");
        Write("owned/x.txt");
        Write("mine.txt");

        Walk(shadowed: ["owned"]).Warnings.ShouldBeEmpty();
        Walk().Warnings.Count.ShouldBe(1, "the control: the same line is reported when nothing shadows the directory");
    }

    [Fact]
    public void ADirectoryAnotherSourceOwnsIsNotReadSoItsBadDexiconignoreCannotFailThisWalk()
    {
        Write("owned/.dexiconignore", "[z-a]\n");
        Write("owned/x.txt");
        Write("mine.txt");

        Names(Walk(shadowed: ["owned"])).ShouldContain("mine.txt");
        Should.Throw<IgnorePatternException>(() => Walk(), "the control: the same file fails the walk when nothing shadows it");
    }

    [Fact]
    public void ADirectoryDeeperThanAShadowedOneIsNotReadEither()
    {
        Write("owned/deep/.dexiconignore", "[z-a]\n");
        Write("mine.txt");

        Names(Walk(shadowed: ["owned"])).ShouldContain("mine.txt");
    }

    [Fact]
    public void ASiblingWhoseNameStartsWithAShadowedPrefixIsStillRead()
    {
        // `owned2` is not inside `owned`.
        Write("owned2/.dexiconignore", "[z-a]\n");

        Should.Throw<IgnorePatternException>(() => Walk(shadowed: ["owned"]));
    }

    [Fact]
    public void ADexiconignoreOverEightMebibytesFailsTheWalkAndAGitignoreIsSkipped()
    {
        var big = new byte[9 * 1024 * 1024];
        Array.Fill(big, (byte)'#');

        WriteBytes(".dexiconignore", big);
        Should.Throw<IgnorePatternException>(() => Walk())
            .Message.ShouldBe(".dexiconignore cannot be used as patterns because it is larger than 8 MiB");

        File.Delete(FullPath(".dexiconignore"));
        WriteBytes(".gitignore", big);
        Walk().Warnings.ShouldBe([
            ".gitignore cannot be used as patterns because it is larger than 8 MiB; the file was skipped"]);
    }
}
