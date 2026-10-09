using System.Diagnostics;
using System.Text;
using Dexicon.Core.Indexing;
using Shouldly;
using Xunit;

namespace Dexicon.Tests;

/// <summary>
/// An ignore file the walk cannot read as text, or cannot read at all. A <c>.dexiconignore</c> is written to keep
/// content out of the index, so one that is a link, is locked, is a pipe, holds a NUL or is not valid UTF-8 fails the
/// walk with the reason. The same in a <c>.gitignore</c> or <c>.git/info/exclude</c> is skipped, the file whole or the
/// lines that cannot be decoded, with a warning. A byte order mark for UTF-8, UTF-16 or UTF-32 is honoured as
/// <c>File.ReadAllLines</c> honours it. Also the scope of the warnings: a directory another source owns is not read.
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

    // ---- links and files that cannot be opened -----------------------------------------------------------------------

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
    public void ALockedGitignoreIsSkippedWholeWithAWarning()
    {
        Write(".gitignore", "secret.txt\n");
        using var held = new FileStream(FullPath(".gitignore"), FileMode.Open, FileAccess.Read, FileShare.None);

        Walk().Warnings.ShouldBe([".gitignore cannot be read; the file was skipped"]);
    }

    // ---- named pipes, on Linux ---------------------------------------------------------------------------------------

    [LinuxFact]
    public void APipeNamedDexiconignoreFailsTheWalkInsteadOfBlockingForever()
    {
        MakePipe(".dexiconignore");

        var thrown = Should.Throw<IgnorePatternException>(
            () => RunWithinTimeout(() => Walk()), "a walk that blocked opening the pipe did not finish");

        thrown.Message.ShouldBe(".dexiconignore cannot be used as patterns because it is not a regular file");
    }

    [LinuxFact]
    public void APipeNamedGitignoreIsSkippedWithAWarningInsteadOfBlockingForever()
    {
        MakePipe(".gitignore");
        MakePipe("sub/.gitignore");

        var walk = RunWithinTimeout(() => Walk());

        walk.Warnings.ShouldBe([
            ".gitignore cannot be used as patterns because it is not a regular file; the file was skipped",
            "sub/.gitignore cannot be used as patterns because it is not a regular file; the file was skipped"]);
    }

    [LinuxFact]
    public void APipeAsTheLocalGitExcludeIsSkippedWithAWarning()
    {
        Directory.CreateDirectory(FullPath(".git/info"));
        MakePipe(".git/info/exclude");

        RunWithinTimeout(() => Walk()).Warnings.ShouldBe([
            ".git/info/exclude cannot be used as patterns because it is not a regular file; the file was skipped"]);
    }

    [LinuxFact]
    public void ACoverageCheckOverADirectoryWithAPipeDoesNotBlock()
    {
        Directory.CreateDirectory(FullPath("a"));
        Directory.CreateDirectory(FullPath("b"));
        MakePipe(".dexiconignore");

        RunWithinTimeout(() => SourceCoverage.Find(_root,
            [new SourceCoverage.SourceRoot("a", 262_144), new SourceCoverage.SourceRoot("b", 262_144)])).ShouldBeEmpty();
    }

    private void MakePipe(string relative)
    {
        var full = FullPath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var made = Process.Start(new ProcessStartInfo("mkfifo", full) { RedirectStandardError = true })!;
        made.WaitForExit();
        made.ExitCode.ShouldBe(0, made.StandardError.ReadToEnd());
    }

    /// <summary>Runs <paramref name="action"/> on another thread and fails if it has not finished, since a walk that blocks cannot fail itself.</summary>
    private static T RunWithinTimeout<T>(Func<T> action)
    {
        var task = Task.Factory.StartNew(action, TaskCreationOptions.LongRunning);
        if (!task.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("the walk blocked");

        return task.Result;
    }

    // ---- encodings ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ADexiconignoreWithANulByteFailsTheWalkAndSaysSo()
    {
        WriteBytes(".dexiconignore", Encoding.UTF8.GetBytes("secret.txt\0\n"));

        Should.Throw<IgnorePatternException>(() => Walk()).Message.ShouldBe(
            ".dexiconignore cannot be used as patterns because it contains a NUL character");
    }

    [Fact]
    public void ANulByteLateInALargeFileIsStillFound()
    {
        // The scan is of the whole text, not of its first 8 KiB as the binary sniff is.
        var lines = string.Concat(Enumerable.Repeat("generated.txt\n", 2_000));
        WriteBytes(".dexiconignore", Encoding.UTF8.GetBytes(lines + "secret.txt\0\n"));

        Should.Throw<IgnorePatternException>(() => Walk()).Message.ShouldContain("contains a NUL character");
    }

    [Fact]
    public void ADexiconignoreInUtf16WithoutAByteOrderMarkHasNulsAndFailsTheWalk()
    {
        WriteBytes(".dexiconignore", Encoding.Unicode.GetBytes("secret.txt\n"));

        Should.Throw<IgnorePatternException>(() => Walk()).Message.ShouldContain("contains a NUL character");
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    [InlineData("utf32le")]
    [InlineData("utf32be")]
    public void ADexiconignoreWithAByteOrderMarkIsDecodedAndApplied(string encoding)
    {
        var text = "secret.txt\r\n*.log\r\n";
        byte[] Encoded(Encoding e) => [.. e.GetPreamble(), .. e.GetBytes(text)];
        WriteBytes(".dexiconignore", encoding switch
        {
            "utf8" => Encoded(new UTF8Encoding(true)),
            "utf16le" => Encoded(new UnicodeEncoding(false, true)),
            "utf16be" => Encoded(new UnicodeEncoding(true, true)),
            "utf32le" => Encoded(new UTF32Encoding(false, true)),
            _ => Encoded(new UTF32Encoding(true, true)),
        });
        Write("secret.txt");
        Write("a.log");
        Write("keep.txt");

        Names(Walk()).ShouldContain("keep.txt");
        Names(Walk()).ShouldNotContain("secret.txt");
        Names(Walk()).ShouldNotContain("a.log");
    }

    [Fact]
    public void AGitignoreInUtf16WithAByteOrderMarkIsDecodedAndApplied()
    {
        WriteBytes("sub/.gitignore", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("secret.txt\n")]);
        Write("sub/secret.txt");
        Write("sub/keep.txt");

        var walk = Walk();

        Names(walk).ShouldContain("sub/keep.txt");
        Names(walk).ShouldNotContain("sub/secret.txt");
        walk.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void ADexiconignoreThatIsNotValidUtf8FailsTheWalk()
    {
        // `caf` and a Latin-1 e-acute: decoded leniently it is a rule that matches nothing.
        WriteBytes(".dexiconignore", [.. "secret.txt\n"u8.ToArray(), .. "caf"u8.ToArray(), 0xE9, .. ".txt\n"u8.ToArray()]);

        Should.Throw<IgnorePatternException>(() => Walk()).Message.ShouldBe(
            ".dexiconignore cannot be used as patterns because it is not valid UTF-8");
    }

    [Fact]
    public void AGitignoreThatIsNotValidUtf8SkipsOnlyTheLinesItCannotDecodeAndCountsThem()
    {
        WriteBytes(".gitignore", [.. "secret.txt\n"u8.ToArray(), .. "caf"u8.ToArray(), 0xE9, .. ".txt\n"u8.ToArray(), .. "other.txt\n"u8.ToArray()]);
        Write("secret.txt");
        Write("other.txt");
        Write("keep.txt");

        var walk = Walk();

        Names(walk).ShouldContain("keep.txt");
        Names(walk).ShouldNotContain("secret.txt");
        Names(walk).ShouldNotContain("other.txt");
        walk.Warnings.ShouldBe([".gitignore has 1 lines with bytes that are not valid UTF-8; those lines were skipped"]);
    }

    [Fact]
    public void AGitignoreWithANulByteIsSkippedWholeWithAWarning()
    {
        WriteBytes(".gitignore", Encoding.UTF8.GetBytes("secret.txt\0\n"));
        Write("secret.txt");

        var walk = Walk();

        // The file holds a NUL byte, so the binary sniff leaves it out of the files; the secret is not excluded.
        Names(walk).ShouldBe(["secret.txt"]);
        walk.Warnings.ShouldBe([".gitignore cannot be used as patterns because it contains a NUL character; the file was skipped"]);
    }

    [Fact]
    public void ALocalGitExcludeWithANulByteIsSkippedWholeWithAWarning()
    {
        WriteBytes(".git/info/exclude", Encoding.UTF8.GetBytes("worktrees/\0\n"));

        Walk().Warnings.ShouldBe([
            ".git/info/exclude cannot be used as patterns because it contains a NUL character; the file was skipped"]);
    }

    // ---- names and lines ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(".DexiconIgnore")]
    [InlineData(".DEXICONIGNORE")]
    public void ADexiconignoreIsFoundWhateverTheCaseOfItsName(string name)
    {
        Write(name, "secret.txt\n");
        Write("secret.txt");
        Write("keep.txt");

        Names(Walk()).ShouldNotContain("secret.txt");
    }

    [Fact]
    public void AGitignoreIsFoundWhateverTheCaseOfItsName()
    {
        Write(".GitIgnore", "secret.txt\n");
        Write("secret.txt");
        Write("keep.txt");

        Names(Walk()).ShouldNotContain("secret.txt");
    }

    [Fact]
    public void ALineEndsAtALineFeedAndNotAtTheOtherCharactersThatBreakALine()
    {
        // Git splits on LF. A CR, U+0085 or U+2028 inside a line is part of the pattern, which then matches no
        // file of the names below, so both are indexed.
        var separators = new[] { "\r", ((char)0x85).ToString(), ((char)0x2028).ToString() };
        foreach (var separator in separators)
        {
            WriteBytes(".dexiconignore", Encoding.UTF8.GetBytes($"one.txt{separator}two.txt\n"));
            Write("one.txt");
            Write("two.txt");

            Names(Walk()).ShouldContain("one.txt");
            Names(Walk()).ShouldContain("two.txt");
        }
    }

    [Fact]
    public void ACarriageReturnBeforeALineFeedIsNotPartOfTheLine()
    {
        WriteBytes(".dexiconignore", Encoding.UTF8.GetBytes("one.txt\r\ntwo.txt\r\n"));
        Write("one.txt");
        Write("two.txt");
        Write("keep.txt");

        Names(Walk()).ShouldBe([".dexiconignore", "keep.txt"]);
    }

    [Fact]
    public void LeadingAndTrailingWhitespaceOfALineIsDropped()
    {
        // Git keeps leading whitespace and honours an escaped trailing space. Here both ends are trimmed, so a rule
        // for a name that begins with a space cannot be written.
        WriteBytes(".dexiconignore", Encoding.UTF8.GetBytes("  one.txt\t \ntwo.txt  \n"));
        Write("one.txt");
        Write("two.txt");

        Names(Walk()).ShouldNotContain("one.txt");
        Names(Walk()).ShouldNotContain("two.txt");
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

    // ---- directories another source owns -----------------------------------------------------------------------------

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
}

/// <summary>
/// A fact that runs on Linux where <c>mkfifo</c> exists, and is reported as skipped, with the reason, everywhere else.
/// </summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "named pipes are made with mkfifo, which is only used on Linux";
        else if (!File.Exists("/usr/bin/mkfifo") && !File.Exists("/bin/mkfifo")) Skip = "mkfifo was not found in /usr/bin or /bin";
    }
}
