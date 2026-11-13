using Dexicon.Core.Indexing;
using static Dexicon.Tests.StashFixture;

namespace Dexicon.Tests;

/// <summary>
/// The sections of a commit's or a stash's document: the message, the stat and the diff, as the reader names
/// them for the chunker. Against a real repository, because where the stat ends and the patch begins is a fact
/// about what git prints.
///
/// Each test reads the sections back as the text between their offsets, so an offset that is off by a line, or
/// that points into the wrong part, shows as the wrong text and not as a number.
/// </summary>
public sealed class GitHistorySectionsTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"gitsections-{Guid.NewGuid():N}");

    public GitHistorySectionsTests()
    {
        Directory.CreateDirectory(_repo);
        Init(_repo);
    }

    public void Dispose() => Delete(_repo);

    private GitRepository Repo() =>
        GitHistory.RepositoryIn(Path.GetDirectoryName(_repo)!, Path.GetFileName(_repo))
        ?? throw new InvalidOperationException($"{_repo} does not exist");

    /// <summary>The newest commit as a document.</summary>
    private async Task<GitDocument> ReadHeadAsync(GitHistoryOptions options)
    {
        var commits = await GitHistory.EnumerateAsync(Repo(), options, null, TestContext.Current.CancellationToken);
        await foreach (var document in GitHistory.ReadAsync(Repo(), options, [commits[0].Sha], null, TestContext.Current.CancellationToken))
            return document;

        throw new InvalidOperationException("the commit was not read");
    }

    private async Task<GitDocument> ReadStashAsync(GitHistoryOptions options)
    {
        var inventory = await GitHistory.StashesAsync(Repo(), options, null, 1024 * 1024, null, TestContext.Current.CancellationToken);
        await foreach (var document in GitHistory.ReadStashesAsync(Repo(), options, inventory.Stashes, null, TestContext.Current.CancellationToken))
            return document;

        throw new InvalidOperationException("the stash was not read");
    }

    /// <summary>Each section's name and the text from its offset to the next section's, or to the end.</summary>
    private static List<(string Name, string Text)> Slices(GitDocument document)
    {
        var slices = new List<(string, string)>();
        for (var i = 0; i < document.Sections.Count; i++)
        {
            var from = document.Sections[i].StartOffset;
            var to = i + 1 < document.Sections.Count ? document.Sections[i + 1].StartOffset : document.Text.Length;
            slices.Add((document.Sections[i].Name, document.Text[from..to]));
        }

        return slices;
    }

    private static string[] Names(GitDocument document) => [.. document.Sections.Select(s => s.Name)];

    /// <summary>
    /// What every document's sections have in common, whatever the settings: the first starts at 0 so the
    /// header is in it, each later one starts a line, they are in order, and together they are the whole text.
    /// </summary>
    private static void ShouldTileTheText(GitDocument document)
    {
        if (document.Sections.Count == 0) return;

        document.Sections[0].StartOffset.ShouldBe(0);
        document.Sections.Select(s => s.StartOffset).ShouldBeInOrder(SortDirection.Ascending);
        document.Sections.Select(s => s.StartOffset).Distinct().Count().ShouldBe(document.Sections.Count);
        foreach (var section in document.Sections.Skip(1))
            document.Text[section.StartOffset - 1].ShouldBe('\n', $"{section.Name} starts a line");

        string.Concat(Slices(document).Select(s => s.Text)).ShouldBe(document.Text);
    }

    [Fact]
    public async Task ACommitWithTheDefaultSettingsIsAMessageAndAStat()
    {
        Commit(_repo, "a.txt", "one\n", "the subject\n\nthe body of the message");

        var document = await ReadHeadAsync(new GitHistoryOptions());

        Names(document).ShouldBe(["message", "stat"]);
        var slices = Slices(document);
        slices[0].Text.ShouldStartWith("commit ");
        slices[0].Text.ShouldContain("Author: A Test");
        slices[0].Text.ShouldContain("    the subject");
        slices[0].Text.ShouldContain("    the body of the message");
        slices[0].Text.ShouldNotContain("a.txt");
        slices[1].Text.ShouldStartWith(" a.txt | 1 +");
        slices[1].Text.ShouldNotContain("the subject");
        ShouldTileTheText(document);
    }

    [Fact]
    public async Task APatchIsASectionOfItsOwnThatStartsAtItsFirstLine()
    {
        Commit(_repo, "a.txt", "one\n", "add a");

        var document = await ReadHeadAsync(new GitHistoryOptions { IncludeDiff = true });

        Names(document).ShouldBe(["message", "stat", "diff"]);
        var slices = Slices(document);
        slices[1].Text.ShouldContain("1 file changed");
        slices[1].Text.ShouldNotContain("diff --git");
        slices[2].Text.ShouldStartWith("diff --git a/a.txt b/a.txt\n");
        slices[2].Text.ShouldContain("+one");
        ShouldTileTheText(document);
    }

    [Fact]
    public async Task APatchOverTheCapIsTheDiffSectionAndStatesItsSize()
    {
        Commit(_repo, "big.txt", string.Concat(Enumerable.Repeat("a line that is part of a large file\n", 200)), "large");

        var document = await ReadHeadAsync(new GitHistoryOptions { IncludeDiff = true, MaxDiffBytes = 2_000 });

        Names(document).ShouldBe(["message", "stat", "diff"]);
        var diff = Slices(document)[2].Text;
        diff.ShouldStartWith("[diff of ");
        diff.ShouldContain("bytes not included");
        diff.ShouldNotContain("diff --git");
        ShouldTileTheText(document);
    }

    [Fact]
    public async Task APatchOverTheCapWithNoStatIsTheDiffSectionAlone()
    {
        Commit(_repo, "big.txt", string.Concat(Enumerable.Repeat("a line that is part of a large file\n", 200)), "large");

        var document = await ReadHeadAsync(new GitHistoryOptions { IncludeDiff = true, IncludeStat = false, MaxDiffBytes = 2_000 });

        Names(document).ShouldBe(["message", "diff"]);
        Slices(document)[1].Text.ShouldStartWith("[diff of ");
        ShouldTileTheText(document);
    }

    [Fact]
    public async Task WithoutAMessageTheHeaderIsPartOfTheStat()
    {
        Commit(_repo, "a.txt", "one\n", "never indexed");

        var document = await ReadHeadAsync(new GitHistoryOptions { IncludeMessage = false });

        Names(document).ShouldBe(["stat"]);
        document.Sections[0].StartOffset.ShouldBe(0);
        document.Text.ShouldStartWith("commit ");
        document.Text.ShouldNotContain("never indexed");
        ShouldTileTheText(document);
    }

    [Fact]
    public async Task WithOnlyAPatchTheHeaderIsPartOfTheDiff()
    {
        Commit(_repo, "a.txt", "one\n", "never indexed");

        var document = await ReadHeadAsync(new GitHistoryOptions { IncludeMessage = false, IncludeStat = false, IncludeDiff = true });

        Names(document).ShouldBe(["diff"]);
        Slices(document)[0].Text.ShouldStartWith("commit ");
        Slices(document)[0].Text.ShouldContain("diff --git a/a.txt b/a.txt");
        ShouldTileTheText(document);
    }

    [Fact]
    public async Task WithNothingButTheHeaderThereAreNoSections()
    {
        Commit(_repo, "a.txt", "one\n", "never indexed");

        var document = await ReadHeadAsync(new GitHistoryOptions { IncludeMessage = false, IncludeStat = false });

        document.Sections.ShouldBeEmpty();
        document.Text.ShouldStartWith("commit ");
        document.Text.ShouldNotContain("a.txt");
    }

    [Fact]
    public async Task OnlyAMessageHasNoStatOrDiffSection()
    {
        Commit(_repo, "a.txt", "one\n", "the subject");

        var document = await ReadHeadAsync(new GitHistoryOptions { IncludeStat = false });

        Names(document).ShouldBe(["message"]);
        ShouldTileTheText(document);
    }

    /// <summary>
    /// A message is indented, so a line of it is never the start of a stat or a patch, whatever it says.
    /// Reading the sections out of the text would have taken the first of these for the patch.
    /// </summary>
    [Fact]
    public async Task AMessageThatLooksLikeAStatOrAPatchStaysInTheMessage()
    {
        Commit(_repo, "a.txt", "one\n",
            "subject\n\ndiff --git a/fake b/fake\n fake.txt | 9 +++++++++\n[diff of 5 bytes not included: over the limit]\n 1 file changed");

        var document = await ReadHeadAsync(new GitHistoryOptions { IncludeDiff = true });

        Names(document).ShouldBe(["message", "stat", "diff"]);
        var slices = Slices(document);
        slices[0].Text.ShouldContain("    diff --git a/fake b/fake");
        slices[0].Text.ShouldContain("    [diff of 5 bytes not included: over the limit]");
        slices[1].Text.ShouldNotContain("fake");
        slices[2].Text.ShouldStartWith("diff --git a/a.txt b/a.txt");
        ShouldTileTheText(document);
    }

    [Fact]
    public async Task AnEmptyCommitIsAMessageAlone()
    {
        Commit(_repo, "a.txt", "one\n", "first");
        Git(_repo, "commit", "--allow-empty", "-m", "nothing changed");

        var document = await ReadHeadAsync(new GitHistoryOptions { IncludeDiff = true });

        Names(document).ShouldBe(["message"]);
        document.Text.ShouldContain("nothing changed");
        ShouldTileTheText(document);
    }

    [Fact]
    public async Task TheTextOfADocumentIsTheSameWithOrWithoutTheSectionsBeingRead()
    {
        // The sections are about where chunks are cut and change no byte of the document.
        Commit(_repo, "a.txt", "one\n", "subject\n\nbody");
        var options = new GitHistoryOptions { IncludeDiff = true };

        var commits = await GitHistory.EnumerateAsync(Repo(), options, null, TestContext.Current.CancellationToken);
        var viaTuple = new List<(string Sha, string Text)>();
        await foreach (var (sha, text) in GitHistory.ReadAsync(Repo(), options, [commits[0].Sha], null, TestContext.Current.CancellationToken))
            viaTuple.Add((sha, text));

        var document = await ReadHeadAsync(options);

        viaTuple.ShouldBe([(document.Sha, document.Text)]);
        document.Sha.ShouldBe(commits[0].Sha);
    }

    [Fact]
    public async Task AStashListsItsUntrackedFilesUnderTheStat()
    {
        Commit(_repo, "a.txt", "base\n", "base");
        Write(_repo, "a.txt", "base\nchanged\n");
        Write(_repo, "notes.md", "decide X\n");
        Stash(_repo, "park");

        var document = await ReadStashAsync(new GitHistoryOptions { IncludeStashes = true, IncludeDiff = true });

        // The list follows the patch and is the stat's answer to "which files", so it is not diff text.
        Names(document).ShouldBe(["message", "stat", "diff", "stat"]);
        var slices = Slices(document);
        slices[2].Text.ShouldStartWith("diff --git a/a.txt b/a.txt");
        slices[2].Text.ShouldNotContain("notes.md");
        slices[3].Text.ShouldStartWith("Untracked files in this stash (1):");
        slices[3].Text.ShouldContain("notes.md");
        document.Text.ShouldStartWith("stash ");
        ShouldTileTheText(document);
    }

    [Fact]
    public async Task AStashWithoutADiffHasOneStatThatTheListContinues()
    {
        Commit(_repo, "a.txt", "base\n", "base");
        Write(_repo, "a.txt", "base\nchanged\n");
        Write(_repo, "notes.md", "decide X\n");
        Stash(_repo, "park");

        var document = await ReadStashAsync(new GitHistoryOptions { IncludeStashes = true });

        Names(document).ShouldBe(["message", "stat"]);
        var stat = Slices(document)[1].Text;
        stat.ShouldContain("a.txt |");
        stat.ShouldContain("Untracked files in this stash (1):");
        ShouldTileTheText(document);
    }

    [Fact]
    public async Task AStashWithOnlyUntrackedFilesHasTheListAsItsStat()
    {
        Commit(_repo, "a.txt", "base\n", "base");
        Write(_repo, "notes.md", "decide X\n");
        Stash(_repo, "park");

        var document = await ReadStashAsync(new GitHistoryOptions { IncludeStashes = true });

        Names(document).ShouldBe(["message", "stat"]);
        Slices(document)[1].Text.ShouldStartWith("Untracked files in this stash (1):");
        ShouldTileTheText(document);
    }

    [Fact]
    public async Task AStashWithNoUntrackedFilesHasNoListAndNoSectionPastTheText()
    {
        Commit(_repo, "a.txt", "base\n", "base");
        Write(_repo, "a.txt", "base\nchanged\n");
        Stash(_repo, "park");

        var document = await ReadStashAsync(new GitHistoryOptions { IncludeStashes = true, IncludeDiff = true });

        Names(document).ShouldBe(["message", "stat", "diff"]);
        document.Text.ShouldNotContain("Untracked files");
        ShouldTileTheText(document);
    }

    [Fact]
    public async Task AStashWithoutTheStatHasNoList()
    {
        Commit(_repo, "a.txt", "base\n", "base");
        Write(_repo, "notes.md", "decide X\n");
        Stash(_repo, "park");

        var document = await ReadStashAsync(new GitHistoryOptions { IncludeStashes = true, IncludeStat = false });

        Names(document).ShouldBe(["message"]);
        document.Text.ShouldNotContain("Untracked files");
        ShouldTileTheText(document);
    }

    [Fact]
    public void ASectionNameIsWhatTheSettingsAreNamedFor()
    {
        // The labels are public contract: the search filter takes them, and the three match the three settings.
        GitHistory.MessageSection.ShouldBe("message");
        GitHistory.StatSection.ShouldBe("stat");
        GitHistory.DiffSection.ShouldBe("diff");
    }
}
