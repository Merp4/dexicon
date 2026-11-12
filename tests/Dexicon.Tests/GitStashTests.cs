using System.Text;
using Dexicon.Core.Indexing;
using Shouldly;
using static Dexicon.Tests.StashFixture;

namespace Dexicon.Tests;

/// <summary>
/// A repository's stashes read as documents, against a real repository built in the test.
///
/// What this has to get right is the shape of what git prints for a stash: a merge whose diff
/// git omits unless asked for the first parent's, an untracked-files commit that
/// <c>git show</c> does not display, and a reflog that holds the older stashes the commit graph
/// does not reach.
/// </summary>
public sealed class GitStashTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"gitstash-{Guid.NewGuid():N}");

    public GitStashTests()
    {
        Directory.CreateDirectory(_repo);
        Init(_repo);
    }

    public void Dispose() => Delete(_repo);

    private static readonly GitHistoryOptions On = new() { IncludeStashes = true };

    // By code, so no editor or tool turns them into the characters, which end a line in C# source.
    private const char Fffd = (char)0xFFFD;
    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;

    private GitRepository Repo() =>
        GitHistory.RepositoryIn(Path.GetDirectoryName(_repo)!, Path.GetFileName(_repo))
        ?? throw new InvalidOperationException($"{_repo} does not exist");

    private Task<GitStashInventory> InventoryAsync(
        GitHistoryOptions? options = null, IReadOnlyList<string>? paths = null, long maxFileBytes = 1024 * 1024,
        IReadOnlyList<string>? excludes = null) =>
        GitHistory.StashesAsync(Repo(), options ?? On, paths, maxFileBytes, excludes, default);

    private async Task<Dictionary<string, string>> ReadStashesAsync(
        GitHistoryOptions options, GitStashInventory inventory, IReadOnlyList<string>? paths = null)
    {
        var read = new Dictionary<string, string>(StringComparer.Ordinal);
        await foreach (var (sha, text) in GitHistory.ReadStashesAsync(Repo(), options, inventory.Stashes, paths))
            read[sha] = text;
        return read;
    }

    private async Task<List<(GitStashFile File, string Text)>> ReadFilesAsync(
        GitHistoryOptions options, GitStashInventory inventory)
    {
        var read = new List<(GitStashFile, string)>();
        await foreach (var item in GitHistory.ReadStashFilesAsync(Repo(), options, inventory.Files))
            read.Add(item);
        return read;
    }

    [Fact]
    public async Task AStashAndItsUntrackedFileAreInventoried()
    {
        Commit(_repo, "a.txt", "base\n", "base");
        Write(_repo, "a.txt", "base\nchanged\n");
        Write(_repo, "notes.md", "decide X\n");
        var sha = Stash(_repo, "park notes");

        var inventory = await InventoryAsync();

        var stash = inventory.Stashes.ShouldHaveSingleItem();
        stash.Sha.ShouldBe(sha);
        stash.Subject.ShouldBe("On main: park notes");
        stash.BaseSha.ShouldBe(Head(_repo));
        stash.Untracked.Select(f => f.Path).ShouldBe(["notes.md"]);
        stash.RelativePath.ShouldStartWith("stashes/");

        var file = inventory.Files.ShouldHaveSingleItem();
        file.Path.ShouldBe("notes.md");
        file.HolderSha.ShouldBe(sha);
        file.RelativePath.ShouldBe($"stashes/untracked/{file.BlobSha[..12]}/notes.md");
        inventory.Skipped.ShouldBeEmpty();
    }

    [Fact]
    public async Task StashesAreOffUnlessAskedFor()
    {
        Write(_repo, "notes.md", "decide X\n");
        Commit(_repo, "a.txt", "base\n", "base");
        Stash(_repo, "park");

        (await InventoryAsync(new GitHistoryOptions())).Stashes.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARepositoryWithoutStashesIsNotAnError()
    {
        Commit(_repo, "a.txt", "base\n", "base");

        var inventory = await InventoryAsync();

        inventory.Stashes.ShouldBeEmpty();
        inventory.Files.ShouldBeEmpty();
    }

    /// <summary>
    /// Why the stashes are listed from the reflog. Walking <c>refs/stash</c> reaches the newest
    /// stash and what it was made from; an older stash is a commit nothing points to.
    /// </summary>
    [Fact]
    public async Task EveryStashIsListedAndWalkingTheRefReachesOnlyTheNewest()
    {
        Commit(_repo, "a.txt", "base\n", "base");
        Write(_repo, "one.md", "one\n");
        var oldest = Stash(_repo, "one");
        Write(_repo, "two.md", "two\n");
        Stash(_repo, "two");
        Write(_repo, "three.md", "three\n");
        var newest = Stash(_repo, "three");

        var walked = Git(_repo, "log", "--format=%H", "refs/stash");
        walked.ShouldContain(newest);
        // An older stash is not an ancestor of the newest one.
        walked.ShouldNotContain(oldest);

        var inventory = await InventoryAsync();

        inventory.Stashes.Count.ShouldBe(3);
        inventory.Stashes.Select(s => s.Sha).ShouldContain(oldest);
    }

    [Fact]
    public async Task TheSameFileUnchangedInSeveralStashesIsOneDocumentHeldByTheOldest()
    {
        Commit(_repo, "a.txt", "base\n", "base");
        Write(_repo, "notes.md", "same\n");
        var oldest = Stash(_repo, "one");
        Write(_repo, "notes.md", "same\n");
        Stash(_repo, "two");
        Write(_repo, "notes.md", "changed\n");
        Stash(_repo, "three");

        var inventory = await InventoryAsync();

        inventory.Stashes.Count.ShouldBe(3);
        inventory.Files.Count.ShouldBe(2, "two versions of notes.md, one of them in two stashes");

        var same = inventory.Files.Single(f => f.SizeBytes == 5);
        same.HolderSha.ShouldBe(oldest);
        same.HolderSubject.ShouldBe("On main: one");
        inventory.Files.Select(f => f.RelativePath).Distinct().Count().ShouldBe(2);
    }

    /// <summary>
    /// <c>git stash --all</c> records the ignored files too, among them a <c>.env</c>. Git's own
    /// ignore rules decide, as they do for a file walk, and the file is not a skipped row: it is
    /// not part of what the source indexes.
    /// </summary>
    [Fact]
    public async Task FilesGitIgnoresAreNotIndexed()
    {
        Commit(_repo, ".gitignore", "*.log\ngenerated/\n.env\n", "ignore");
        Write(_repo, "keep.md", "keep\n");
        Write(_repo, "debug.log", "noise\n");
        Write(_repo, "generated/out.txt", "output\n");
        // Ignored files that would otherwise be skipped for their own reasons leave no row either.
        File.WriteAllBytes(Path.Combine(_repo, "generated", "blob.dat"), [1, 0, 2]);
        Write(_repo, "generated/empty.txt", "");
        Write(_repo, ".env", "SECRET=1\n");
        Stash(_repo, "everything", all: true);

        var inventory = await InventoryAsync();

        inventory.Files.Select(f => f.Path).ShouldBe(["keep.md"]);
        inventory.Skipped.ShouldBeEmpty();

        // The stash's own document names what it held, and its contents are not in it.
        inventory.Stashes.Single().Untracked.Select(f => f.Path).ShouldContain(".env");
        var read = await ReadStashesAsync(new GitHistoryOptions { IncludeStashes = true, IncludeDiff = true }, inventory);
        read.Values.Single().ShouldNotContain("SECRET=1");
    }

    [Fact]
    public async Task BinaryEmptyAndOversizedFilesAreSkippedWithTheirReason()
    {
        Commit(_repo, "a.txt", "base\n", "base");
        File.WriteAllBytes(Path.Combine(_repo, "pic.dat"), [1, 2, 0, 3, 4]);
        Write(_repo, "empty.txt", "");
        Write(_repo, "big.txt", new string('x', 2000));
        Write(_repo, "ok.txt", "fine\n");
        Stash(_repo, "mixed");

        var inventory = await InventoryAsync(maxFileBytes: 1000);

        inventory.Files.Select(f => f.Path).ShouldBe(["ok.txt"]);

        var reasons = inventory.Skipped.ToDictionary(s => s.RelativePath.Split('/')[^1], s => s.Reason);
        reasons.Keys.OrderBy(k => k, StringComparer.Ordinal).ShouldBe(["big.txt", "empty.txt", "pic.dat"]);
        reasons["pic.dat"].ShouldStartWith("binary content");
        reasons["empty.txt"].ShouldBe("empty file");
        reasons["big.txt"].ShouldBe("over the 1,000 byte size cap (2,000 bytes)");
        inventory.Skipped.Single(s => s.RelativePath.EndsWith("/big.txt", StringComparison.Ordinal)).SizeBytes.ShouldBe(2000);
    }

    [Fact]
    public async Task TheStashDocumentHoldsTheTrackedChangeAndNamesTheUntrackedFiles()
    {
        Commit(_repo, "a.txt", "base\n", "base");
        Write(_repo, "a.txt", "base\nchanged line\n");
        Write(_repo, "notes.md", "decide X\n");
        var sha = Stash(_repo, "park");
        var inventory = await InventoryAsync();

        var withDiff = (await ReadStashesAsync(new GitHistoryOptions { IncludeStashes = true, IncludeDiff = true }, inventory))
            .Single().Value;

        withDiff.ShouldStartWith($"stash {sha}\n");
        withDiff.ShouldContain($"Base:   {Head(_repo)}\n");
        withDiff.ShouldContain("    On main: park");
        withDiff.ShouldContain(" a.txt | 1 +");
        withDiff.ShouldContain("+changed line");
        withDiff.ShouldContain("Untracked files in this stash (1):");
        withDiff.ShouldContain("  notes.md  (9 bytes, blob ");

        // The patch follows the source's diff setting, as for a commit; the stat and the
        // files stay.
        var statOnly = (await ReadStashesAsync(On, inventory)).Single().Value;
        statOnly.ShouldNotContain("+changed line");
        statOnly.ShouldContain(" a.txt | 1 +");
        statOnly.ShouldContain("notes.md");

        var messageOnly = (await ReadStashesAsync(
            new GitHistoryOptions { IncludeStashes = true, IncludeStat = false }, inventory)).Single().Value;
        messageOnly.ShouldContain("On main: park");
        messageOnly.ShouldNotContain("a.txt");
        messageOnly.ShouldNotContain("Untracked files");
    }

    [Fact]
    public async Task AnUntrackedFileDocumentHasTheStashAndTheContent()
    {
        Commit(_repo, "a.txt", "base\n", "base");
        Write(_repo, "docs/plan.md", "# Plan\n\ndecide X\n");
        var sha = Stash(_repo, "park");
        var inventory = await InventoryAsync();

        var (file, text) = (await ReadFilesAsync(On, inventory)).Single();

        text.ShouldStartWith($"untracked file docs/plan.md\nStash:  {sha[..12]} \"On main: park\"\nDate:   ");
        text.ShouldContain($"\nBlob:   {file.BlobSha} (17 bytes)\n");
        text.ShouldEndWith("\n\n# Plan\n\ndecide X\n");

        var silent = (await ReadFilesAsync(new GitHistoryOptions { IncludeStashes = true, IncludeMessage = false }, inventory))
            .Single().Text;
        silent.ShouldContain($"Stash:  {sha[..12]}\n");
        silent.ShouldNotContain("park");
    }

    /// <summary>
    /// Git looks at the first 8,000 bytes. A NUL past them is text to git and a hazard to the
    /// catalogue, whose text column ends a value at the first one.
    /// </summary>
    [Fact]
    public async Task ANulPastWhatGitLooksAtIsNotIndexedAsText()
    {
        Commit(_repo, "a.txt", "base\n", "base");
        File.WriteAllBytes(Path.Combine(_repo, "late.txt"), [.. Encoding.ASCII.GetBytes(new string('a', 9000)), 0, .. "tail"u8.ToArray()]);
        Stash(_repo, "late nul");
        var inventory = await InventoryAsync();

        inventory.Files.Select(f => f.Path).ShouldBe(["late.txt"], "git treats the file as text");

        var text = (await ReadFilesAsync(On, inventory)).Single().Text;
        text.ShouldContain("[binary content, not indexed]");
        text.Contains('\0').ShouldBeFalse();
    }

    [Fact]
    public async Task SinceCutsStashesByTheirDate()
    {
        Commit(_repo, "a.txt", "base\n", "base");
        Write(_repo, "notes.md", "decide X\n");
        Stash(_repo, "park");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        (await InventoryAsync(On with { Since = today.AddDays(-1) })).Stashes.Count.ShouldBe(1);

        var tomorrow = await InventoryAsync(On with { Since = today.AddDays(1) });
        tomorrow.Stashes.ShouldBeEmpty();
        tomorrow.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheIncludePathsSelectUntrackedFilesAndTheStat()
    {
        Commit(_repo, "docs/a.md", "a\n", "docs");
        Commit(_repo, "src/b.cs", "b\n", "src");
        Write(_repo, "docs/a.md", "a\nmore\n");
        Write(_repo, "src/b.cs", "b\nmore\n");
        Write(_repo, "docs/new.md", "new doc\n");
        Write(_repo, "src/new.cs", "new code\n");
        Stash(_repo, "both");

        var inventory = await InventoryAsync(paths: ["docs"]);

        var stash = inventory.Stashes.Single();
        stash.UntrackedTotal.ShouldBe(1);
        inventory.Files.Select(f => f.Path).ShouldBe(["docs/new.md"]);

        var text = (await ReadStashesAsync(On, inventory, ["docs"])).Values.Single();
        text.ShouldContain("docs/a.md");
        text.ShouldNotContain("src/b.cs");
    }

    /// <summary>
    /// Also the case that found a deadlock: 505 blob names are about 20 KB of input to
    /// <c>git cat-file --batch-check</c>, which answers as it reads, and a write that waited for
    /// the read to start hung for ever. The timeout makes a return of it a failure.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AStashListsAndIndexesOnlyItsFirstUntrackedFiles()
    {
        Commit(_repo, "a.txt", "base\n", "base");
        var count = GitHistory.MaxUntrackedPerStash + 5;
        for (var i = 0; i < count; i++) Write(_repo, $"f{i:0000}.txt", $"file {i}\n");
        Stash(_repo, "many");

        var inventory = await GitHistory.StashesAsync(Repo(), On, null, 1024 * 1024, null, TestContext.Current.CancellationToken);

        var stash = inventory.Stashes.Single();
        stash.UntrackedTotal.ShouldBe(count);
        stash.Untracked.Count.ShouldBe(GitHistory.MaxUntrackedPerStash);
        inventory.Files.Count.ShouldBe(GitHistory.MaxUntrackedPerStash);

        var text = (await ReadStashesAsync(On, inventory)).Values.Single();
        text.ShouldContain($"Untracked files in this stash ({count:N0}):");
        text.ShouldContain("[5 more not listed or indexed: the limit is 500 per stash]");
    }

    /// <summary>
    /// The cap per stash is applied after git has listed the files, so a stash over a large tree
    /// is bounded by the ceiling of the call that lists it. A stash over it is kept, says its files
    /// are too many to list, and is not an outage: the other stash in the same call is still read.
    /// </summary>
    [Fact]
    public async Task AStashWhoseListingIsTooLargeToReadIsKeptAndSaysSo()
    {
        Commit(_repo, "a.txt", "base\n", "base");
        Write(_repo, "small.md", "small\n");
        Stash(_repo, "small");
        for (var i = 0; i < 80; i++) Write(_repo, $"f{i:000}.txt", $"file {i}\n");
        Stash(_repo, "large");

        var inventory = await GitHistory.StashesAsync(
            Repo(), On, null, 1024 * 1024, null, new GitHistory.StashLimits(ListCeiling: 3_000),
            TestContext.Current.CancellationToken);

        inventory.Stashes.Count.ShouldBe(2);
        var large = inventory.Stashes.Single(s => s.Subject == "On main: large");
        large.UntrackedTotal.ShouldBeNull();
        large.Untracked.ShouldBeEmpty();
        inventory.Stashes.Single(s => s.Subject == "On main: small").UntrackedTotal.ShouldBe(1);
        inventory.Files.Select(f => f.Path).ShouldBe(["small.md"]);

        var read = await ReadStashesAsync(On, inventory);
        read[large.Sha].ShouldContain("Untracked files in this stash: too many to list or index.");
    }

    /// <summary>
    /// The files are files, so what a file walk always leaves out (build output, dependency folders) and the
    /// source's exclude list apply to them. They leave no row, and git's ignore rules are not what decides it:
    /// nothing here is ignored.
    /// </summary>
    [Fact]
    public async Task TheExclusionsOfAFileWalkApplyToUntrackedFiles()
    {
        Commit(_repo, "a.txt", "base\n", "base");
        Write(_repo, "build/out.txt", "output\n");
        Write(_repo, "node_modules/lib/x.js", "dependency\n");
        Write(_repo, "drafts/private.md", "not for the index\n");
        Write(_repo, "notes.md", "decide X\n");
        Stash(_repo, "park");

        var withoutList = await InventoryAsync();
        withoutList.Files.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal).ShouldBe(["drafts/private.md", "notes.md"]);

        var inventory = await InventoryAsync(excludes: ["drafts/"]);

        inventory.Files.Select(f => f.Path).ShouldBe(["notes.md"]);
        inventory.Skipped.ShouldBeEmpty();
        inventory.Stashes.Single().UntrackedTotal.ShouldBe(4, "the stash's own document lists what it holds, whatever is excluded");
    }

    [Fact]
    public async Task AnExcludeListThatCannotBeUsedIsAnErrorTheIndexerHandles()
    {
        Commit(_repo, "a.txt", "base\n", "base");
        Write(_repo, "notes.md", "decide X\n");
        Stash(_repo, "park");

        await Should.ThrowAsync<GitHistoryException>(() => InventoryAsync(excludes: ["[z-a]"]));
    }

    /// <summary>
    /// What a source indexes from stashes is bounded. The versions held by the oldest stashes are the
    /// ones left out, each as a skipped row, and nothing is silently dropped.
    /// </summary>
    [Fact]
    public async Task TheDocumentCapLeavesTheOldestOutWithAReason()
    {
        Commit(_repo, "a.txt", "base\n", "base");
        Write(_repo, "one.md", "one\n");
        Stash(_repo, "one");
        Write(_repo, "two.md", "two\n");
        Stash(_repo, "two");
        Write(_repo, "three.md", "three\n");
        Stash(_repo, "three");

        var inventory = await GitHistory.StashesAsync(
            Repo(), On, null, 1024 * 1024, null, new GitHistory.StashLimits(MaxFileDocuments: 2),
            TestContext.Current.CancellationToken);

        inventory.Files.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal).ShouldBe(["three.md", "two.md"]);
        var skipped = inventory.Skipped.ShouldHaveSingleItem();
        skipped.RelativePath.ShouldEndWith("/one.md");
        skipped.Reason.ShouldStartWith("over the 2 untracked file versions");
    }

    /// <summary>
    /// <c>git check-ignore</c> reads each name as a pathspec and refuses magic, so a name that starts with a
    /// colon is not asked about, and is skipped with a reason. Linux only: a colon cannot be in a file name
    /// on Windows.
    /// </summary>
    [Fact]
    public async Task AFileNamedLikePathspecMagicIsSkippedAndDoesNotFailTheInventory()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A colon cannot be in a file name on Windows.");

        Commit(_repo, "a.txt", "base\n", "base");
        Write(_repo, ":(literal)note.md", "text\n");
        Write(_repo, "ok.md", "fine\n");
        Stash(_repo, "park");

        var inventory = await InventoryAsync();

        inventory.Files.Select(f => f.Path).ShouldBe(["ok.md"]);
        var skipped = inventory.Skipped.ShouldHaveSingleItem();
        skipped.RelativePath.ShouldEndWith("/:(literal)note.md");
        skipped.Reason.ShouldStartWith("ignore rules could not be checked");
    }
    /// <summary>
    /// Git fails the whole ignore check, with status 128, when one path lies beyond a symbolic link in
    /// the working tree. That path is skipped with a reason and the other files are still judged.
    /// Linux only: a link needs a privilege on Windows.
    /// </summary>
    [Fact]
    public async Task APathBeyondASymbolicLinkIsSkippedAndDoesNotFailTheInventory()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege on Windows.");

        Commit(_repo, "a.txt", "base\n", "base");
        Write(_repo, "dir/x.txt", "held in the stash\n");
        Write(_repo, "ok.md", "fine\n");
        Stash(_repo, "park");
        Directory.CreateDirectory(Path.Combine(_repo, "elsewhere"));
        Directory.CreateSymbolicLink(Path.Combine(_repo, "dir"), Path.Combine(_repo, "elsewhere"));

        var inventory = await InventoryAsync();

        inventory.Files.Select(f => f.Path).ShouldBe(["ok.md"]);
        var skipped = inventory.Skipped.ShouldHaveSingleItem();
        skipped.RelativePath.ShouldEndWith("/dir/x.txt");
        skipped.Reason.ShouldStartWith("ignore rules could not be checked");
    }

    /// <summary>
    /// A control character in a path would end the line a file list shows the key on. Linux
    /// only: a file name cannot hold a newline on Windows.
    /// </summary>
    [Fact]
    public async Task APathWithAControlCharacterIsSkippedAndKeyedWithoutIt()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A file name cannot hold a newline on Windows.");

        Commit(_repo, "a.txt", "base\n", "base");
        Write(_repo, "line\nbreak.md", "text\n");
        Stash(_repo, "park");

        var inventory = await InventoryAsync();

        inventory.Files.ShouldBeEmpty();
        var skipped = inventory.Skipped.ShouldHaveSingleItem();
        skipped.Reason.ShouldBe("path holds a control character");
        skipped.RelativePath.ShouldNotContain("\n");
        skipped.RelativePath.ShouldEndWith("/line" + Fffd + "break.md");
    }

    /// <summary>
    /// Raw entries are paired with their paths by position, so a path is a path whatever it
    /// looks like. Canned, because a file name with a tab cannot be made on every platform.
    /// </summary>
    [Fact]
    public void APathIsReadByPositionAndNotByWhatItLooksLike()
    {
        const string marker = "<<marker>>";
        var sha = new string('a', 40);
        string Blob(char c) => new(c, 40);
        var zeros = new string('0', 40);

        string Raw(string mode, char blob, string path) => $":000000 {mode} {zeros} {Blob(blob)} A\0{path}\0";

        var stdout = marker + sha + "\0\n"
            + Raw("100644", 'b', ":colon.md")
            + Raw("100755", 'c', "tab\tname.txt")
            + Raw("100644", 'd', "bin.dat")
            + Raw("120000", 'e', "link")
            + Raw("160000", 'f', "sub")
            + "1\t0\t:colon.md\0"
            + "2\t0\ttab\tname.txt\0"
            + "-\t-\tbin.dat\0"
            + "1\t0\tlink\0";

        var parsed = GitHistory.ParseUntrackedListing(stdout, marker, [sha]);

        parsed[sha].ShouldBe(
        [
            (":colon.md", Blob('b'), false),
            ("tab\tname.txt", Blob('c'), false),
            ("bin.dat", Blob('d'), true),
        ]);
    }

    [Fact]
    public void ARawEntryWithNoPathIsAnErrorAndNotAnEmptyListing()
    {
        const string marker = "<<marker>>";
        var sha = new string('a', 40);
        var stdout = marker + sha + "\0\n" + $":000000 100644 {new string('0', 40)} {new string('b', 40)} A";

        Should.Throw<GitHistoryException>(() => GitHistory.ParseUntrackedListing(stdout, marker, [sha]));
    }

    /// <summary>
    /// A name or a message with a control character would start a line the document did not
    /// have, and a line is what a reader takes for a field.
    /// </summary>
    [Fact]
    public void ANewlineInAPathOrAMessageCannotStartALine()
    {
        var file = new GitStashFile(
            "a\nStash:  forged.md", new string('b', 40), 3, new string('c', 40), DateTimeOffset.UnixEpoch, "subject\nBlob:   forged");

        var text = GitHistory.StashFileDocument(On, file, "x\n");

        text.Split('\n').Count(l => l.StartsWith("Stash:", StringComparison.Ordinal)).ShouldBe(1);
        text.Split('\n').Count(l => l.StartsWith("Blob:", StringComparison.Ordinal)).ShouldBe(1);

        // The line and paragraph separators are not control characters, and end a line for a reader that breaks on them.
        var separated = new GitStashFile(
            "x" + LineSeparator + "y" + ParagraphSeparator + "z.md", new string('b', 40), 3, new string('c', 40), DateTimeOffset.UnixEpoch, "s");
        var other = GitHistory.StashFileDocument(On, separated, "x\n");
        other.ShouldStartWith("untracked file x" + Fffd + "y" + Fffd + "z.md\n");
        other.Contains(LineSeparator).ShouldBeFalse();
        other.Contains(ParagraphSeparator).ShouldBeFalse();
    }

    [Fact]
    public void AStashAndItsFileAreKeyedBySettingsOnlyWhereTheTextDependsOnThem()
    {
        var stash = new GitStash(new string('a', 40), DateTimeOffset.UnixEpoch, string.Empty, "s", [], 0);
        var file = new GitStashFile("n.md", new string('b', 40), 3, stash.Sha, DateTimeOffset.UnixEpoch, "s");
        var commit = new GitCommit(new string('c', 40), DateTimeOffset.UnixEpoch);

        // A commit's key is what it always was, so enabling stashes reads no commit again.
        commit.ContentKey(On, "m|s|-|-").ShouldBe(commit.Sha + "|m|s|-|-");

        stash.ContentKey(On, "m|s|-|-").ShouldContain("m|s|-|-");
        stash.ContentKey(On, "m|s|d65536|-").ShouldNotBe(stash.ContentKey(On, "m|s|-|-"));

        // A file's text carries the stash message and nothing of the diff settings.
        file.ContentKey(On, "m|s|-|-").ShouldBe(file.ContentKey(On, "m|-|d65536|-"));
        file.ContentKey(On with { IncludeMessage = false }, "-|s|-|-").ShouldNotBe(file.ContentKey(On, "m|s|-|-"));
    }
}
