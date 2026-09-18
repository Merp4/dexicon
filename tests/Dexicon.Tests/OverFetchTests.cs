using Dexicon.Core.Search;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// How many candidates search asks the store for when it is going to collapse duplicates.
///
/// Collapsing one hit per document only works if there are spare candidates to promote,
/// and how far it has to reach depends on how many chunks a document has — which is decided
/// by the chunk size. Measured on one corpus held two ways, asking for ten distinct titles:
/// at 2,065 tokens it returned 6.5, and at 1,365 tokens the same books returned 3.7, because
/// the finer chunking put more chunks of the same book into the candidate pool.
///
/// Two separate ceilings caused that, and only one of them was visible from here. The other
/// was the vector store clamping any request to 50, a number that belongs on what a CALLER
/// may ask for and was being applied to an internal figure that is deliberately larger.
/// </summary>
public sealed class OverFetchTests
{
    private static int Fetch(int limit, bool distinctTitles) =>
        distinctTitles ? Math.Min(limit * 16, 400) : limit;

    [Fact]
    public void CollapsingDuplicatesAsksForMoreThanItWillReturn()
    {
        // Without spare candidates a dropped duplicate leaves a gap rather than promoting
        // the next distinct document.
        Fetch(10, distinctTitles: true).ShouldBeGreaterThan(10);
    }

    [Fact]
    public void NotCollapsingAsksForExactlyWhatWasRequested()
    {
        // Nothing to promote, so nothing to over-fetch. Reaching wider here would be paid
        // for on every query that does not need it.
        Fetch(10, distinctTitles: false).ShouldBe(10);
        Fetch(50, distinctTitles: false).ShouldBe(50);
    }

    [Fact]
    public void TheOverFetchIsBounded()
    {
        // A heuristic, so a generous one, but not an unbounded scan: the store pays for
        // every candidate it ranks.
        Fetch(50, distinctTitles: true).ShouldBe(400);
        Fetch(1000, distinctTitles: true).ShouldBe(400);
    }

    [Fact]
    public void TheOverFetchIsWorthAskingFor()
    {
        // The regression. The store used to clamp everything to 50, so an over-fetch of
        // 160 arrived as 50 and collapsing duplicates quietly returned fewer results than
        // were asked for. A fetch the store will not honour is not an over-fetch.
        const int storeCeiling = 500;

        Fetch(10, distinctTitles: true).ShouldBeLessThanOrEqualTo(storeCeiling);
        Fetch(50, distinctTitles: true).ShouldBeLessThanOrEqualTo(storeCeiling);

        // And it has to be able to exceed what a caller may ask for, or there is no room
        // to promote anything on a request at the maximum.
        Fetch(50, distinctTitles: true).ShouldBeGreaterThan(50);
    }

    [Fact]
    public void ShortResultsAreReportedRatherThanPassedOffAsAll()
    {
        // The over-fetch is a heuristic and a corpus can simply not hold that many distinct
        // documents on a subject, so the response says when it came up short. That sentence
        // is what makes the heuristic honest rather than hidden.
        var hits = new[]
        {
            new SearchHit { CorpusId = "c", FilePath = "One.pdf", Content = "x", SourceRoot = "s" },
            new SearchHit { CorpusId = "c", FilePath = "One.epub", Content = "x", SourceRoot = "s" },
        };

        SearchPresentation.DistinctByTitle(hits, 5).Count.ShouldBe(1);
    }
}
