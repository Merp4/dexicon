using Dexicon.Api;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Indexing;

namespace Dexicon.Tests;

/// <summary>
/// What a source reports about itself.
///
/// The globs were accepted, stored and never returned. Nothing failed; a caller set a
/// filter, the indexer honoured it, and every screen and every agent afterwards had no way
/// to discover that a corpus was only reading part of a folder. "Indexed 12 files" with no
/// way to see why it was not 400.
///
/// They live in one column as a JSON array, so mapping them back is a place to be wrong in
/// two directions: dropping them, and throwing on a column that does not parse.
/// </summary>
public sealed class SourceSummaryTests
{
    /// <summary>A corpus that sets no defaults, so a source's own values are what resolve.</summary>
    private static readonly Corpus Corpus =
        new() { Id = "c1", Name = "docs", CreatedUtc = DateTime.UtcNow };

    private static readonly IndexingOptions Configured = new() { MaxFileBytes = 262_144 };

    private static Source Source(string? include = null, string? exclude = null) => new()
    {
        Id = "s1",
        CorpusId = "c1",
        Kind = SourceKind.Workspace,
        RootPath = "api-repo",
        UseGitignore = true,
        MaxFileBytes = 2 * 1024 * 1024,
        IncludeGlobs = include,
        ExcludeGlobs = exclude,
    };

    /// <summary>
    /// A setting that can be written and never read back is one nobody can check,
    /// correct or reproduce. The ref and the diff decide what every document in a
    /// history source holds, and they were accepted at creation and then invisible.
    /// </summary>
    [Fact]
    public void AHistorySourceReportsItsOwnSettings()
    {
        var options = new GitHistoryOptions { Ref = "release/1.0", IncludeDiff = true, MaxDiffBytes = 4096 };

        var source = Source();
        source.Kind = SourceKind.GitHistory;
        source.GitOptions = options.ToJson();

        source.ToSummary(Corpus, Configured).Git.ShouldBe(options);
    }

    [Fact]
    public void ASourceWithNoHistoryReportsNone()
    {
        // Null rather than the defaults: a workspace source does not have these settings,
        // and returning a plausible set would say it did.
        Source().ToSummary(Corpus, Configured).Git.ShouldBeNull();
    }

    /// <summary>
    /// Where the ref had got to. Without it a source whose ref stopped moving reads the
    /// same as one that is current.
    /// </summary>
    [Fact]
    public void AHistorySourceReportsTheNewestCommitItFound()
    {
        var authored = new DateTime(2026, 9, 20, 21, 51, 20, DateTimeKind.Utc);

        var source = Source();
        source.Kind = SourceKind.GitHistory;
        source.NewestCommitSha = "9d2ef0633a530462b4091ad8226d1b02faadea84";
        source.NewestCommitUtc = authored;

        source.ToSummary(Corpus, Configured).NewestCommit
            .ShouldBe(new CommitSummary("9d2ef0633a530462b4091ad8226d1b02faadea84", authored));
    }

    [Fact]
    public void NoNewestCommitIsReportedUntilOneWasFound()
    {
        var source = Source();
        source.Kind = SourceKind.GitHistory;

        source.ToSummary(Corpus, Configured).NewestCommit.ShouldBeNull("no pass has read the history yet");

        // Half a record is not a commit: a sha with no date cannot say how old the
        // source is, which is the one thing this is shown for.
        source.NewestCommitSha = "9d2ef0633a530462b4091ad8226d1b02faadea84";
        source.ToSummary(Corpus, Configured).NewestCommit.ShouldBeNull();
    }

    [Fact]
    public void AFileSourceHasNoNewestCommit()
    {
        var source = Source();
        source.NewestCommitSha = "9d2ef0633a530462b4091ad8226d1b02faadea84";
        source.NewestCommitUtc = DateTime.UtcNow;

        source.ToSummary(Corpus, Configured).NewestCommit.ShouldBeNull();
    }

    [Fact]
    public void GlobsComeBackAsAList()
    {
        var summary = Source(include: """["src/**","docs/**"]""", exclude: """["**/vendor/**"]""").ToSummary(Corpus, Configured);

        summary.IncludeGlobs.ShouldBe(["src/**", "docs/**"]);
        summary.ExcludeGlobs.ShouldBe(["**/vendor/**"]);
    }

    [Fact]
    public void NoFilterIsAnEmptyList_NotNull()
    {
        // A caller rendering `globs.length` should not have to guard against null for the
        // ordinary case of "no filter", which is most sources.
        var summary = Source().ToSummary(Corpus, Configured);

        summary.IncludeGlobs.ShouldBeEmpty();
        summary.ExcludeGlobs.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"not\":\"an array\"}")]
    public void AnUnreadableColumnIsEmpty_NotAnException(string stored)
    {
        // "No filter" and "a filter we cannot read" look the same to a caller. The honest
        // one of those two is the one that does not take a screen down: a corpus listing
        // should not 500 because one row holds something unexpected.
        Should.NotThrow(() => Source(include: stored).ToSummary(Corpus, Configured))
            .IncludeGlobs.ShouldBeEmpty();
    }

    [Fact]
    public void TheRestOfTheSourceStillCarriesThrough()
    {
        var summary = Source().ToSummary(Corpus, Configured);

        summary.RootPath.ShouldBe("api-repo");
        summary.UseGitignore.ShouldBeTrue();
        summary.MaxFileBytes.ShouldBe(2 * 1024 * 1024);
        summary.Kind.ShouldBe("workspace");
    }
}
