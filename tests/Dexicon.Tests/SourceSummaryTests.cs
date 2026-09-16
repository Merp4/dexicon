using Dexicon.Api;
using Dexicon.Core.Catalog;

namespace Dexicon.Tests;

/// <summary>
/// What a source reports about itself.
///
/// The globs were accepted, stored and never returned. Nothing failed — a caller set a
/// filter, the indexer honoured it, and every screen and every agent afterwards had no way
/// to discover that a corpus was only reading part of a folder. "Indexed 12 files" with no
/// way to see why it was not 400.
///
/// They live in one column as a JSON array, so mapping them back is a place to be wrong in
/// two directions: dropping them, and throwing on a column that does not parse.
/// </summary>
public sealed class SourceSummaryTests
{
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

    [Fact]
    public void GlobsComeBackAsAList()
    {
        var summary = Source(include: """["src/**","docs/**"]""", exclude: """["**/vendor/**"]""").ToSummary();

        summary.IncludeGlobs.ShouldBe(["src/**", "docs/**"]);
        summary.ExcludeGlobs.ShouldBe(["**/vendor/**"]);
    }

    [Fact]
    public void NoFilterIsAnEmptyList_NotNull()
    {
        // A caller rendering `globs.length` should not have to guard against null for the
        // ordinary case of "no filter", which is most sources.
        var summary = Source().ToSummary();

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
        Should.NotThrow(() => Source(include: stored).ToSummary())
            .IncludeGlobs.ShouldBeEmpty();
    }

    [Fact]
    public void TheRestOfTheSourceStillCarriesThrough()
    {
        var summary = Source().ToSummary();

        summary.RootPath.ShouldBe("api-repo");
        summary.UseGitignore.ShouldBeTrue();
        summary.MaxFileBytes.ShouldBe(2 * 1024 * 1024);
        summary.Kind.ShouldBe("workspace");
    }
}
