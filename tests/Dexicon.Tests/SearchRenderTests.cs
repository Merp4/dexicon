using Dexicon.Core.Catalog;
using Dexicon.Core.Search;
using Dexicon.Mcp;

namespace Dexicon.Tests;

/// <summary>
/// What search_index actually says to an agent.
///
/// Most MCP clients show this text verbatim, so it IS the product's interface for the
/// only caller that matters. It had no tests at all, which is how a hit in a book came to
/// be cited as `book.epub#chapter=7` and nothing else — a correct citation that an agent
/// cannot do anything further with, because the tool for reading on takes a line.
/// </summary>
public sealed class SearchRenderTests
{
    private static SearchHit Hit(
        string path,
        int startLine = 10,
        int endLine = 20,
        int? page = null,
        string? section = null) => new()
        {
            CorpusId = "c1",
            CorpusName = "library",
            FilePath = path,
            StartLine = startLine,
            EndLine = endLine,
            Page = page,
            Section = section,
            Content = "the matched text",
            Score = 0.8f,
        };

    private static SearchResult Result(params SearchHit[] hits) => new()
    {
        Query = "a question",
        Mode = SearchMode.Hybrid,
        Scope = [new SearchResult.ScopeEntry("c1", "library", CorpusState.Ready)],
        Hits = hits,
    };

    [Fact]
    public void ACodeHitIsCitedByLine()
    {
        var text = DexiconTools.Render(Result(Hit("src/Api/Search.cs", 228, 265)));

        text.ShouldContain("src/Api/Search.cs:228-265");
    }

    [Fact]
    public void ABookHitCarriesBothItsCitationAndALineToReadOnFrom()
    {
        // `#chapter=` is the right citation and the wrong argument: get_context centres on
        // a line. Without the line span an agent can find a passage in a book and then has
        // no way to read the next page of it.
        var text = DexiconTools.Render(Result(Hit("moby-dick.epub", 4210, 4260, page: 7)));

        text.ShouldContain("moby-dick.epub#chapter=7");
        text.ShouldContain("lines 4210-4260");
    }

    [Fact]
    public void APdfIsCitedAsAPageAndASlideDeckAsASlide()
    {
        // A confidently wrong citation propagates: it is the part a model repeats verbatim.
        DexiconTools.Render(Result(Hit("report.pdf", page: 14))).ShouldContain("report.pdf#page=14");
        DexiconTools.Render(Result(Hit("deck.pptx", page: 14))).ShouldContain("deck.pptx#slide=14");
    }

    [Fact]
    public void ALineHitIsNotGivenARedundantLineSpan()
    {
        // The citation already is the line span; repeating it would be noise on every hit
        // in every code search.
        var text = DexiconTools.Render(Result(Hit("src/Api/Search.cs", 228, 265)));

        text.ShouldNotContain("lines 228-265");
    }

    [Fact]
    public void NothingFoundSaysWhatToCheck()
    {
        // An empty result is the moment an agent decides the content is not there. It may
        // simply not be indexed yet, and saying so is the difference between retrying and
        // giving up.
        var text = DexiconTools.Render(Result());

        text.ShouldContain("No results");
        text.ShouldContain("index_status");
    }

    [Fact]
    public void DegradationIsAnnouncedRatherThanImplied()
    {
        // Keyword-only results that look like full results are a lie by omission: the
        // absence of a semantic match is not the absence of the content.
        var result = Result(Hit("a.md")) with
        {
            Degraded = true,
            DegradedReason = "Embeddings unavailable; keyword only.",
        };

        DexiconTools.Render(result).ShouldContain("DEGRADED: Embeddings unavailable");
    }

    [Fact]
    public void AMidIndexCorpusSaysSoBeforeTheResults()
    {
        var result = Result(Hit("a.md")) with { Note = "library is still indexing." };

        DexiconTools.Render(result).ShouldContain("library is still indexing.");
    }

    [Fact]
    public void TheCorpusIsNamedOnEachHitOnlyWhenSeveralWereSearched()
    {
        var one = DexiconTools.Render(Result(Hit("a.md")));
        one.ShouldNotContain("[library]");

        var many = Result(Hit("a.md")) with
        {
            Scope =
            [
                new SearchResult.ScopeEntry("c1", "library", CorpusState.Ready),
                new SearchResult.ScopeEntry("c2", "api-repo", CorpusState.Ready),
            ],
        };
        DexiconTools.Render(many).ShouldContain("[library]");
    }
}
