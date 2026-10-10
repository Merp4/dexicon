using Dexicon.Core.Catalog;
using Dexicon.Core.Indexing;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// What a walk will read. The ignore files of a walk (<c>.git/info/exclude</c> and every <c>.gitignore</c> and
/// <c>.dexiconignore</c>) share one budget of <see cref="IgnoreRuleSet.MaxRulesPerSource"/> rules and
/// <see cref="IgnoreRuleSet.MaxWeightPerSource"/> pattern parts; each stored glob list has a budget of its own. Passing
/// a budget fails the walk, for every kind of file, because the rules after the limit are the ones the operator wrote
/// last. Beside the budget: 1,000 skipped lines per git-owned file, 1 MiB per file and 16 MiB per walk.
/// </summary>
public sealed class IgnoreRuleLimitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"limits-{Guid.NewGuid():N}");

    public IgnoreRuleLimitTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string content)
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static string Rules(int count, int from = 0) =>
        string.Join("\n", Enumerable.Range(from, count).Select(i => $"g{i}")) + "\n";

    /// <summary>A line of weight 500: a star, and the any-depth prefix and each character as tokens, in a pattern with a wildcard.</summary>
    private static string HeavyLine(int n) => $"*h{n:D4}" + new string('x', 493);

    private WorkspaceWalker.WalkResult Walk(
        bool useGitignore = true, string[]? include = null, string[]? exclude = null, CancellationToken ct = default) =>
        WorkspaceWalker.Walk(_root, useGitignore, include, exclude, 1_000_000, ct: ct);

    [Fact]
    public void TheLimitsAre()
    {
        IgnoreRuleSet.MaxRulesPerSource.ShouldBe(5_000);
        IgnoreRuleSet.MaxWeightPerSource.ShouldBe(60_000);
        IgnoreRuleSet.MaxRulesPerList.ShouldBe(1_000);
        IgnoreRuleSet.MaxWeightPerList.ShouldBe(20_000);
        WarningSink.MaxKept.ShouldBe(20);
    }

    [Fact]
    public void APatternWeighsOnePerTokenAndAClassSixteenMoreAndOnePerRange()
    {
        // The any-depth prefix is a token. A star is one, a class is sixteen and one for each range.
        GlobMatcher.Compile("*.md", "").Weight.ShouldBe(5, "prefix, star, and three characters");
        GlobMatcher.Compile("docs/**/*.md", "").Weight.ShouldBe(10);
        GlobMatcher.Compile("*[a-cx-z]", "").Weight.ShouldBe(1 + 1 + 16 + 2);
        GlobMatcher.Compile("a/**", "").Weight.ShouldBe(3);
        GlobMatcher.Compile("a", "").Weight.ShouldBe(2, "the prefix and the character");
        GlobMatcher.Compile(new string('a', 39), "").Weight.ShouldBe(40);
        GlobMatcher.Compile("[abcdef]x?", "").Weight.ShouldBe(1 + 16 + 6 + 1 + 1);
    }

    // ---- the rule count of the ignore files -------------------------------------------------------------------------

    [Fact]
    public void ADexiconignoreOfExactlyTheRuleLimitIsRead()
    {
        Write(WorkspaceWalker.IgnoreFileName, Rules(IgnoreRuleSet.MaxRulesPerSource));
        Write("g4999", "x");
        Write("keep.txt", "x");

        Walk().Files.Select(f => f.RelativePath).OrderBy(p => p, StringComparer.Ordinal)
            .ShouldBe([".dexiconignore", "keep.txt"]);
    }

    [Fact]
    public void ADexiconignoreOneRuleOverTheLimitFailsTheWalkNamingTheLineAndTheLimit()
    {
        Write(WorkspaceWalker.IgnoreFileName, Rules(IgnoreRuleSet.MaxRulesPerSource + 1));

        Should.Throw<IgnorePatternException>(() => Walk()).Message.ShouldBe(
            ".dexiconignore line 5001 ('g5000') is past the limit of 5,000 rules or 60,000 pattern parts for one source; "
            + "the files using most so far are .dexiconignore (5,000 rules, 28,890 parts); reduce them, or turn off use_gitignore for the source");
    }

    [Fact]
    public void AGitignorePastTheRuleLimitFailsTheWalkAndSaysHowToTurnItOff()
    {
        Write(".gitignore", Rules(IgnoreRuleSet.MaxRulesPerSource + 5));

        Should.Throw<IgnorePatternException>(() => Walk()).Message.ShouldBe(
            ".gitignore line 5001 ('g5000') is past the limit of 5,000 rules or 60,000 pattern parts for one source; "
            + "the files using most so far are .gitignore (5,000 rules, 28,890 parts); reduce them, or turn off use_gitignore for the source");
    }

    [Fact]
    public void WithGitignoreTurnedOffTheSameFileIsNotReadAndTheWalkSucceeds()
    {
        Write(".gitignore", Rules(IgnoreRuleSet.MaxRulesPerSource + 5));
        Write("g0", "x");

        Walk(useGitignore: false).Files.Select(f => f.RelativePath).ShouldContain("g0");
    }

    [Fact]
    public void ALocalGitExcludePastTheLimitFailsTheWalkNamingIt()
    {
        Write(".git/info/exclude", Rules(IgnoreRuleSet.MaxRulesPerSource + 1));

        Should.Throw<IgnorePatternException>(() => Walk()).Message.ShouldStartWith(
            ".git/info/exclude line 5001 ('g5000') is past the limit");
    }

    [Fact]
    public void ABlankOrCommentLineDoesNotUseARule()
    {
        Write(WorkspaceWalker.IgnoreFileName, string.Concat(Enumerable.Repeat("# comment\n\n", 4_000)) + Rules(IgnoreRuleSet.MaxRulesPerSource));

        Should.NotThrow(() => Walk());
    }

    // ---- the weight --------------------------------------------------------------------------------------------------

    [Fact]
    public void RulesOfExactlyTheWeightLimitAreRead()
    {
        Write(WorkspaceWalker.IgnoreFileName, string.Join("\n", Enumerable.Range(0, 120).Select(HeavyLine)) + "\n");

        Should.NotThrow(() => Walk());
    }

    [Fact]
    public void TheRuleThatPassesTheWeightLimitFailsTheWalkNamingIt()
    {
        Write(WorkspaceWalker.IgnoreFileName, string.Join("\n", Enumerable.Range(0, 121).Select(HeavyLine)) + "\n");

        Should.Throw<IgnorePatternException>(() => Walk()).Message.ShouldStartWith(
            ".dexiconignore line 121 ('*h0120");
    }

    [Fact]
    public void ARuleOfWeightOnePastTheLimitFailsTheWalk()
    {
        // 119 lines of 500 and one of 501 are 60,001.
        Write(WorkspaceWalker.IgnoreFileName, string.Join("\n", Enumerable.Range(0, 119).Select(HeavyLine)) + "\n" + HeavyLine(119) + "x\n");

        Should.Throw<IgnorePatternException>(() => Walk()).Message.ShouldStartWith(".dexiconignore line 120 ('*h0119");
    }

    [Fact]
    public void AClassHeavyFileIsHeldToTheWeightToo()
    {
        // A star and 45 classes of 3 ranges: 1 + 1 + 45 x 19 = 857 a rule, so 70 rules are 59,990 and the 71st passes 60,000.
        var line = "*" + string.Concat(Enumerable.Repeat("[a-cx-zA-C]", 45));
        GlobMatcher.Compile(line, "").Weight.ShouldBe(1 + 1 + 45 * (16 + 3));

        Write(WorkspaceWalker.IgnoreFileName, string.Join("\n", Enumerable.Repeat(line, 71)) + "\n");

        Should.Throw<IgnorePatternException>(() => Walk()).Message.ShouldStartWith(".dexiconignore line 71 ");
    }

    // ---- one budget for the files, in a stable order ------------------------------------------------------------------

    [Fact]
    public void TheFilesOfAWalkShareOneBudgetAndTheFailureNamesTheFirstFilePastIt()
    {
        Write(".gitignore", Rules(IgnoreRuleSet.MaxRulesPerSource - 1));
        Write("sub/.gitignore", "one.txt\ntwo.txt\nthree.txt\n");

        Should.Throw<IgnorePatternException>(() => Walk()).Message.ShouldStartWith(
            "sub/.gitignore line 2 ('two.txt') is past the limit");
    }

    [Fact]
    public void WhichDirectoryFailsDoesNotDependOnTheOrderTheyWereMade()
    {
        // 3,000 rules in each of B/ and a/ is more than the walk reads. The directories are visited in ordinal order,
        // where `B` comes before `a`, so it is a/ that is past the limit, whichever was created first. A filesystem
        // that sorts without regard to case lists a first, so a walk in listing order would name B/.
        Write("a/.gitignore", Rules(3_000));
        Write("B/.gitignore", Rules(3_000));

        for (var pass = 0; pass < 3; pass++)
            Should.Throw<IgnorePatternException>(() => Walk()).Message.ShouldStartWith(
                "a/.gitignore line 2001 ('g2000') is past the limit");
    }

    private static int WeightOf(int count, int from = 0) =>
        Enumerable.Range(from, count).Sum(i => GlobMatcher.Compile($"g{i}", string.Empty).Weight);

    [Fact]
    public void TheMessageNamesTheFilesThatUsedTheBudgetAndNotOnlyTheOneThatPassedIt()
    {
        // Three files, the last one small: it is where the budget runs out, and the first two are where it went.
        Write("a/.gitignore", Rules(3_000));
        Write("b/.gitignore", Rules(1_500));
        Write("c/.gitignore", Rules(1_000));

        var thrown = Should.Throw<IgnorePatternException>(() => Walk());

        thrown.Message.ShouldBe(
            "c/.gitignore line 501 ('g500') is past the limit of 5,000 rules or 60,000 pattern parts for one source; "
            + $"the files using most so far are a/.gitignore (3,000 rules, {WeightOf(3_000):N0} parts), "
            + $"b/.gitignore (1,500 rules, {WeightOf(1_500):N0} parts), c/.gitignore (500 rules, {WeightOf(500):N0} parts); "
            + "reduce them, or turn off use_gitignore for the source");
    }

    [Fact]
    public void ARootGitignoreAtTheLimitFailsALaterDexiconignoreAndNeverDropsItsRulesQuietly()
    {
        Write(".gitignore", Rules(IgnoreRuleSet.MaxRulesPerSource));
        Write(WorkspaceWalker.IgnoreFileName, ".env\n");
        Write(".env", "secret");

        Should.Throw<IgnorePatternException>(() => Walk()).Message.ShouldStartWith(
            ".dexiconignore line 1 ('.env') is past the limit");
    }

    [Fact]
    public void IgnoreFilesInsideAnIgnoredDirectoryAreNotReadOrCharged()
    {
        // `!.gitkeep` applies at every depth, so the walk goes into node_modules to look for a re-included file. Git
        // reads nothing in an ignored directory, and 700 packages of 10 rules would be 7,000 rules.
        Write(".gitignore", "node_modules/\n!.gitkeep\n");
        Write("app.txt", "x");
        Write("node_modules/.gitkeep", "x");
        for (var i = 0; i < 700; i++) Write($"node_modules/pkg{i:D4}/.gitignore", Rules(10, i * 10));
        Write("node_modules/pkg0000/.dexiconignore", "[z-a]\n");

        var files = Walk().Files.Select(f => f.RelativePath).OrderBy(p => p, StringComparer.Ordinal).ToList();

        files.ShouldBe([".gitignore", "app.txt", "node_modules/.gitkeep"]);
    }

    [Fact]
    public void AnIgnoreFileInsideADirectoryTheRulesIgnoreDoesNotApplyToAFileTheyReinclude()
    {
        // `lib/` is ignored and `!lib/keep/` re-includes one directory in it; `lib/.gitignore` is not read, so its
        // `keep.txt` rule does not take effect.
        Write(".gitignore", "lib/\n!lib/keep/\n");
        Write("lib/.gitignore", "keep.txt\n");
        Write("lib/keep/keep.txt", "x");

        Walk().Files.Select(f => f.RelativePath).ShouldContain("lib/keep/keep.txt");
    }

    [Fact]
    public void ANestedGitignorePastTheLimitFailsInsteadOfIndexingWhatItExcludes()
    {
        Write(".gitignore", Rules(IgnoreRuleSet.MaxRulesPerSource));
        Write("zz/.gitignore", ".env\n");
        Write("zz/.env", "secret");

        Should.Throw<IgnorePatternException>(() => Walk()).Message.ShouldStartWith("zz/.gitignore line 1 ('.env')");
    }

    // ---- the lists have budgets of their own --------------------------------------------------------------------------

    [Fact]
    public void ALocalGitExcludeAtTheLimitDoesNotFailTheOperatorsList()
    {
        Write(".git/info/exclude", Rules(IgnoreRuleSet.MaxRulesPerSource));
        Write("keep.txt", "x");
        Write("a.log", "x");

        var names = Walk(exclude: ["*.log"]).Files.Select(f => f.RelativePath).ToList();

        names.ShouldContain("keep.txt");
        names.ShouldNotContain("a.log");
    }

    [Fact]
    public void TwoListsEachWithinTheirLimitDoNotFailTogether()
    {
        var include = Enumerable.Range(0, 600).Select(i => $"inc{i}").ToArray();
        var exclude = Enumerable.Range(0, 600).Select(i => $"exc{i}").ToArray();
        Write("keep.txt", "x");

        Should.NotThrow(() => Walk(include: include, exclude: exclude));
    }

    [Fact]
    public void AListPastItsRuleLimitFailsTheWalkNamingTheEntryAndTheList()
    {
        var exclude = Enumerable.Range(0, IgnoreRuleSet.MaxRulesPerList + 1).Select(i => $"x{i}").ToArray();

        Should.Throw<IgnorePatternException>(() => Walk(exclude: exclude)).Message.ShouldBe(
            "excludeGlobs[1000] ('x1000') is past the limit of 1,000 rules or 20,000 pattern parts for one list; shorten the list");
    }

    [Fact]
    public void AListPastItsWeightLimitFailsTheWalkNamingTheEntry()
    {
        var include = Enumerable.Range(0, 41).Select(HeavyLine).ToArray();

        Should.Throw<IgnorePatternException>(() => Walk(include: include)).Message.ShouldStartWith(
            "includeGlobs[40] ('*h0040");
    }

    [Fact]
    public void AStoredListIsCheckedAgainstTheLimitsTheWalkAppliesToIt()
    {
        var atLimit = Enumerable.Range(0, IgnoreRuleSet.MaxRulesPerList).Select(i => $"x{i}").ToList();
        var over = atLimit.Append("one-too-many").ToList();
        var heavy = Enumerable.Range(0, 41).Select(HeavyLine).ToList();

        SourceFilters.FirstUnusable(atLimit).ShouldBeNull();
        SourceFilters.FirstUnusable(over).ShouldBe(IgnoreRuleSet.MaxRulesPerList);
        SourceFilters.FirstUnusable(heavy).ShouldBe(40);
        SourceFilters.FirstUnusable(over, SourceFilters.GlobReader.WalkAndGit).ShouldBe(IgnoreRuleSet.MaxRulesPerList);
        SourceFilters.FirstUnusable(over, SourceFilters.GlobReader.Git).ShouldBeNull("git reads a history source's list, not the walk");
    }

    // ---- skipped lines, warnings and bytes ----------------------------------------------------------------------------

    [Fact]
    public void AGitignoreStopsBeingReadAfterAThousandSkippedLinesWithOneWarningSayingSo()
    {
        Write(".gitignore", string.Concat(Enumerable.Repeat("[z-a]\n", 1_500)) + "secret.txt\n");
        Write("secret.txt", "x");

        var walk = WorkspaceWalker.Walk(_root, true, null, null, 1_000_000);

        // The rule after the thousandth skipped line is not read, which is the one way a git-owned file can leave a
        // rule out without failing the walk. The warning says where reading stopped.
        walk.Files.Select(f => f.RelativePath).ShouldContain("secret.txt");
        walk.Warnings.Count.ShouldBe(WarningSink.MaxKept + 1, "twenty descriptions and the notice");
        walk.WarningsOmitted.ShouldBe(1_000 - WarningSink.MaxKept);
        walk.Warnings[0].ShouldBe(".gitignore line 1 ('[z-a]') cannot be compiled (reversed character range); the line was skipped");
        walk.Warnings[^1].ShouldBe(
            ".gitignore has 1,000 lines that cannot be used; the rest of the file was not read, so no rule after them applies");
    }

    [Fact]
    public void ADexiconignoreFailsAtTheFirstBadLineWhateverFollows()
    {
        Write(WorkspaceWalker.IgnoreFileName, string.Concat(Enumerable.Repeat("[z-a]\n", 1_500)));

        Should.Throw<IgnorePatternException>(() => Walk()).Message.ShouldStartWith(".dexiconignore line 1 ");
    }

    [Fact]
    public void ManyBadLinesAreRejectedWithoutThrowingAnExceptionForEach()
    {
        // A line is rejected as a value, and the file is read to its first thousand bad lines. The exceptions raised
        // while the walk runs are counted: a throw per line would be 170,000, the 1,000 allowed leaves room for
        // another test raising its own in the same process.
        Write(".gitignore", string.Concat(Enumerable.Repeat("[z-a]\n", 170_000)));

        var thrown = 0;
        void Count(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is FormatException or IgnorePatternException) Interlocked.Increment(ref thrown);
        }

        WorkspaceWalker.WalkResult walk;
        AppDomain.CurrentDomain.FirstChanceException += Count;
        try { walk = WorkspaceWalker.Walk(_root, true, null, null, 1_000_000); }
        finally { AppDomain.CurrentDomain.FirstChanceException -= Count; }

        walk.Warnings.Count.ShouldBe(WarningSink.MaxKept + 1, "twenty descriptions and the notice");
        thrown.ShouldBeLessThan(1_000);
    }

    [Fact]
    public void WarningsBeforeAFailureAreKeptOnTheException()
    {
        Write(".gitignore", "[z-a]\n");
        Write(WorkspaceWalker.IgnoreFileName, "[z-a]\n");

        var thrown = Should.Throw<IgnorePatternException>(() => Walk());

        thrown.Warnings.ShouldBe([".gitignore line 1 ('[z-a]') cannot be compiled (reversed character range); the line was skipped"]);
        thrown.WarningsOmitted.ShouldBe(0);
    }

    [Fact]
    public void WarningsPastTheCapAreCountedOnTheExceptionToo()
    {
        Write(".gitignore", string.Concat(Enumerable.Repeat("[z-a]\n", 25)));
        Write(WorkspaceWalker.IgnoreFileName, "[z-a]\n");

        var thrown = Should.Throw<IgnorePatternException>(() => Walk());

        thrown.Warnings.Count.ShouldBe(WarningSink.MaxKept);
        thrown.WarningsOmitted.ShouldBe(5);
    }

    [Fact]
    public void AnIgnoreFileOfExactlyOneMebibyteIsReadAndOneByteOverFailsTheWalk()
    {
        var comment = new byte[IgnoreFileText.MaxBytes];
        Array.Fill(comment, (byte)'#');
        File.WriteAllBytes(Path.Combine(_root, WorkspaceWalker.IgnoreFileName), comment);
        Should.NotThrow(() => Walk());

        File.WriteAllBytes(Path.Combine(_root, WorkspaceWalker.IgnoreFileName), [.. comment, (byte)'#']);
        Should.Throw<IgnorePatternException>(() => Walk())
            .Message.ShouldBe(".dexiconignore cannot be used as patterns because it is larger than 1 MiB; reduce the file");
    }

    [Theory]
    [InlineData(".gitignore")]
    [InlineData(".git/info/exclude")]
    public void AGitOwnedIgnoreFileOverOneMebibyteFailsTheWalkInsteadOfBeingSkipped(string file)
    {
        // `.env` first and 200,000 comments after it: skipping the file whole would index `.env`.
        Write(file, ".env\n" + string.Concat(Enumerable.Repeat("# a comment line of some length\n", 40_000)));
        Write(".env", "secret");

        var thrown = Should.Throw<IgnorePatternException>(() => Walk());

        thrown.Message.ShouldBe($"{file} cannot be used as patterns because it is larger than 1 MiB; reduce the file, or turn off use_gitignore for the source");
        thrown.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void FilesThatAreRefusedStillCountTowardTheBytesAWalkMayRead()
    {
        // Seventeen of 1 MiB with a NUL at the end: each is skipped whole with a warning, and the seventeenth is past 16 MiB.
        var content = new byte[IgnoreFileText.MaxBytes];
        Array.Fill(content, (byte)'#');
        content[^1] = 0;
        for (var i = 0; i < 17; i++)
        {
            var dir = Path.Combine(_root, $"d{i:D2}");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, ".gitignore"), content);
        }

        var thrown = Should.Throw<IgnorePatternException>(() => Walk());

        thrown.Message.ShouldBe("d16/.gitignore takes the walk past the 16 MiB of ignore files it may read; reduce the files");
        thrown.Warnings.Count.ShouldBe(16);
    }

    [Fact]
    public void SixteenMebibytesOfIgnoreFilesAreReadAndOneByteMoreFailsTheWalk()
    {
        var content = new byte[IgnoreFileText.MaxBytes];
        Array.Fill(content, (byte)'#');
        for (var i = 0; i < 16; i++)
        {
            var dir = Path.Combine(_root, $"d{i:D2}");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, ".gitignore"), content);
        }

        Should.NotThrow(() => Walk());

        Directory.CreateDirectory(Path.Combine(_root, "zz"));
        File.WriteAllText(Path.Combine(_root, "zz", ".gitignore"), "#");
        Should.Throw<IgnorePatternException>(() => Walk()).Message.ShouldBe(
            "zz/.gitignore takes the walk past the 16 MiB of ignore files it may read; reduce the files");
    }

    [Fact]
    public void ManyLargeFilesCannotAddUpPastWhatAWalkMayRead()
    {
        // Seventeen directories of 960 KiB each: every file is under its own limit, and the sum is past 16 MiB.
        var content = new byte[960 * 1024];
        Array.Fill(content, (byte)'#');
        for (var i = 0; i < 18; i++)
        {
            var dir = Path.Combine(_root, $"d{i:D2}");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, ".gitignore"), content);
        }

        Should.Throw<IgnorePatternException>(() => Walk()).Message.ShouldBe(
            "d17/.gitignore takes the walk past the 16 MiB of ignore files it may read; reduce the files");
    }

    // ---- stopping ----------------------------------------------------------------------------------------------------

    [Fact]
    public void ACancelledTokenStopsTheWalkAtTheFirstDirectory()
    {
        Write("a.txt", "x");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Should.Throw<OperationCanceledException>(() => Walk(ct: cts.Token));
    }

    [Fact]
    public void TheWalkLooksAtItsTokenEveryTwoHundredAndFiftySixFiles()
    {
        WorkspaceWalker.FilesBetweenPolls.ShouldBe(256);
    }

    [Fact]
    public void ATokenCancelledPartWayStopsTheWalkWithinAnotherDirectory()
    {
        // One directory with many files, so only the poll among the files can see the cancel.
        for (var i = 0; i < 6_000; i++) File.WriteAllText(Path.Combine(_root, $"f{i:D5}.txt"), "x");
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(5));

        Should.Throw<OperationCanceledException>(() => Walk(ct: cts.Token));
    }

    // ---- reporting ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APassLogsTheFirstTwentyOnceAndTheCountOnceHoweverManyChunkSetsWalkTheSource()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.WriteFileAsync(".gitignore", string.Concat(Enumerable.Repeat("[z-a]\n", 50)));
        await harness.WriteFileAsync("kept.md", IndexingHarness.Prose("kept"));
        await harness.SeedCorpusAsync(SourceKind.Workspace, sets: 3);
        var log = new RecordingLoggerFactory();

        await harness.RunIndexAsync(log: new Logger<CorpusIndexer>(log));

        log.Lines.Count(l => l.Contains(".gitignore line", StringComparison.Ordinal)).ShouldBe(WarningSink.MaxKept);
        log.Lines.Count(l => l.Contains("30 more ignore-file warnings", StringComparison.Ordinal)).ShouldBe(1);
    }
}
