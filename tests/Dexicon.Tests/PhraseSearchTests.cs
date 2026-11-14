using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Documents;
using Dexicon.Core.Embedding;
using Dexicon.Core.Search;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dexicon.Tests;

/// <summary>
/// A search with a phrase, through <see cref="SearchService"/> against a vector store that records the
/// query it is asked.
///
/// The phrase has to reach every query the store makes as a filter, because a phrase applied to the hits
/// that came back would only see the candidates a window of the ranking held, and measured on 320 phrases
/// taken from real chunks that window held the source chunk for 60 to 75 percent of them. And nothing
/// about a search without one may change: the store is asked for the same candidates it always was.
/// </summary>
public sealed class PhraseSearchTests : IAsyncLifetime
{
    private static readonly Principal Reader = new("k", "agent", new HashSet<string>(StringComparer.Ordinal) { Scopes.Search });

    private IndexingHarness _harness = null!;
    private readonly List<SearchQuery> _asked = [];

    public async ValueTask InitializeAsync()
    {
        _harness = await IndexingHarness.StartAsync("notes");
        await _harness.SeedCorpusAsync(SourceKind.Workspace);
        _harness.Vectors.OnSearch = q =>
        {
            _asked.Add(q);
            return Answer(q, Hit(string.Join('\n', Enumerable.Range(1, 40).Select(i => i == 38 ? "the needle in a haystack" : $"alpha beta gamma {i}"))));
        };
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    /// <summary>The store's answer: these hits, in the mode that was asked for.</summary>
    private static SearchResponse Answer(SearchQuery q, params SearchHit[] hits) => new() { Hits = hits, Mode = q.Mode };

    private static SearchHit Hit(string content) => new()
    {
        CorpusId = IndexingHarness.CorpusId,
        SourceId = IndexingHarness.SourceIdFor(0),
        FilePath = "a.md",
        StartLine = 1,
        EndLine = 40,
        Content = content,
        Score = 1f,
    };

    private async Task<SearchResult> SearchAsync(SearchRequest request)
    {
        await using var db = _harness.NewContext();
        var search = new SearchService(new ScopeResolver(db), _harness.Vectors, _harness.Embedder,
            new MemoryCache(new MemoryCacheOptions()), NullLogger<SearchService>.Instance);
        return await search.SearchAsync(Reader, request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task APhraseReachesTheVectorStoreWithItsWhitespaceCollapsed()
    {
        await SearchAsync(new SearchRequest { Query = "tokens", Phrase = "  Refresh \n token " });

        _asked.ShouldHaveSingleItem().Phrase.ShouldBe("Refresh token");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public async Task ASearchWithoutAPhraseAsksTheStoreForNone(string? phrase)
    {
        await SearchAsync(new SearchRequest { Query = "tokens", Phrase = phrase });

        _asked.ShouldHaveSingleItem().Phrase.ShouldBeNull();
    }

    [Fact]
    public async Task APhraseDoesNotWidenWhatTheStoreIsAskedFor()
    {
        // Every chunk in scope that holds the phrase is already a candidate, since the store filters on it,
        // so there is no window to enlarge. The same numbers as OverFetchTests holds for a plain search.
        await SearchAsync(new SearchRequest { Query = "tokens", Limit = 10 });
        await SearchAsync(new SearchRequest { Query = "tokens", Limit = 10, Phrase = "refresh token" });
        await SearchAsync(new SearchRequest { Query = "tokens", Limit = 10, DistinctTitles = false, Phrase = "refresh token" });

        _asked.Select(q => q.Limit).ShouldBe([160, 160, 10]);
    }

    [Fact]
    public async Task ABlankQueryIsSearchedAsThePhraseAndTheResultSaysWhatRan()
    {
        var result = await SearchAsync(new SearchRequest { Query = "", Phrase = "refresh   token" });

        _asked.ShouldHaveSingleItem().Text.ShouldBe("refresh token");
        result.Query.ShouldBe("refresh token");
        result.Phrase.ShouldBe("refresh token");
    }

    [Fact]
    public async Task AQueryAndAPhraseAreBothKeptWhenBothAreGiven()
    {
        var result = await SearchAsync(new SearchRequest { Query = "how tokens are renewed", Phrase = "refresh token" });

        var asked = _asked.ShouldHaveSingleItem();
        asked.Text.ShouldBe("how tokens are renewed");
        asked.Phrase.ShouldBe("refresh token");
        result.Query.ShouldBe("how tokens are renewed");
        result.Phrase.ShouldBe("refresh token");
    }

    [Fact]
    public async Task ASearchWithoutAPhraseReportsNone()
    {
        (await SearchAsync(new SearchRequest { Query = "tokens" })).Phrase.ShouldBeNull();
    }

    [Theory]
    [InlineData("!!!")]
    [InlineData("=>")]
    public async Task APhraseThatCannotMatchIsRefusedBeforeTheStoreIsAsked(string phrase)
    {
        await Should.ThrowAsync<InvalidSearchPhraseException>(
            () => SearchAsync(new SearchRequest { Query = "tokens", Phrase = phrase }));

        _asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnOverlongPhraseIsRefusedBeforeTheStoreIsAsked()
    {
        await Should.ThrowAsync<InvalidSearchPhraseException>(
            () => SearchAsync(new SearchRequest { Query = "tokens", Phrase = new string('a', SearchPhrase.MaxChars + 1) }));

        _asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task EachHitIsWindowedAroundThePhraseRatherThanTheQueryTerms()
    {
        var result = await SearchAsync(new SearchRequest
        {
            Query = "alpha beta",
            Phrase = "needle in a haystack",
            MaxCharsPerHit = 200,
        });

        result.Hits.ShouldHaveSingleItem().Content.ShouldContain("needle in a haystack");
    }

    [Fact]
    public async Task WithoutAPhraseTheSameHitIsWindowedOnTheQueryTermsAsBefore()
    {
        var result = await SearchAsync(new SearchRequest { Query = "alpha beta", MaxCharsPerHit = 200 });

        result.Hits.ShouldHaveSingleItem().Content.ShouldNotContain("needle in a haystack");
    }

    private void FindNothing() => _harness.Vectors.OnSearch = q =>
    {
        _asked.Add(q);
        return Answer(q);
    };

    /// <summary>An embedding service that is away, so a hybrid search degrades to keyword.</summary>
    private sealed class UnavailableEmbedder : IEmbeddingService
    {
        public Task<IReadOnlyList<float[]>> EmbedAsync(EmbeddingTarget target, EmbedPurpose purpose,
            IReadOnlyList<string> inputs, string? source = null, CancellationToken ct = default) =>
            throw new EmbeddingUnavailableException("the provider is away");

        public Task<int?> CountTokensAsync(EmbeddingTarget t, string text, CancellationToken ct = default) =>
            Task.FromResult<int?>(null);

        public Task<int> ProbeDimensionsAsync(EmbeddingTarget t, CancellationToken ct = default) =>
            Task.FromResult(768);

        public int KnownDimensions(EmbeddingTarget t) => 768;
    }

    [Fact]
    public async Task AKeywordSearchForAPhraseOfStopWordsSaysItCanMatchNothing()
    {
        // The keyword vector drops stop words and single characters, so keyword mode has nothing to retrieve
        // with and returns nothing whatever the text holds. For a phrase that reads as "not in the index".
        FindNothing();

        var result = await SearchAsync(new SearchRequest { Query = "", Phrase = "it is to be", Mode = SearchMode.Keyword });

        result.Note.ShouldNotBeNull().ShouldContain("Keyword mode retrieves by the words of the query");
    }

    [Fact]
    public async Task AHybridSearchThatFellBackToKeywordSaysTheEmbeddingServiceWasAway()
    {
        // The advice to use hybrid or semantic mode is wrong for a search that asked for hybrid.
        FindNothing();
        _harness.Embedder = new UnavailableEmbedder();

        var result = await SearchAsync(new SearchRequest { Query = "", Phrase = "it is to be", Mode = SearchMode.Hybrid });

        result.Degraded.ShouldBeTrue();
        var note = result.Note.ShouldNotBeNull();
        note.ShouldContain("embedding service was unavailable");
        note.ShouldNotContain("Use hybrid or semantic");
    }

    [Fact]
    public async Task ADegradedSearchWithAWordTheKeywordIndexHoldsCarriesNoKeywordNote()
    {
        FindNothing();
        _harness.Embedder = new UnavailableEmbedder();

        var result = await SearchAsync(new SearchRequest { Query = "", Phrase = "refresh token", Mode = SearchMode.Hybrid });

        result.Degraded.ShouldBeTrue();
        result.Note.ShouldBeNull();
    }

    [Theory]
    [InlineData(true, true, 0, SearchMode.Keyword, 1, 0, true)]
    [InlineData(true, true, 0, SearchMode.Hybrid, 1, 1, true)]
    [InlineData(true, true, 0, SearchMode.Semantic, 2, 2, true)]
    [InlineData(true, true, 0, SearchMode.Hybrid, 2, 1, false)]
    [InlineData(true, true, 0, SearchMode.Hybrid, 1, 0, false)]
    [InlineData(true, true, 0, SearchMode.Hybrid, 0, 0, false)]
    [InlineData(true, true, 1, SearchMode.Keyword, 1, 0, false)]
    [InlineData(true, false, 0, SearchMode.Keyword, 1, 0, false)]
    [InlineData(false, true, 0, SearchMode.Keyword, 1, 0, false)]
    public void TheKeywordNoteIsGivenOnlyWhenKeywordRetrievalWasAllThatAnsweredAndFoundNothing(
        bool hasPhrase, bool sparseIsEmpty, int hits, SearchMode requested, int groups, int degradedGroups, bool given)
    {
        // Two collections can differ: one loses its embeddings and falls back to keyword while the other is
        // searched properly, and the empty answer of that one is a real answer, so "can match nothing" is false.
        var phrase = hasPhrase ? SearchPhrase.Create("it is to be") : null;

        var note = SearchService.TermlessKeywordNote(phrase, sparseIsEmpty, hits, requested, groups, degradedGroups);

        (note is not null).ShouldBe(given);
    }

    [Fact]
    public async Task TheKeywordNoteIsLeftOutWhenSomethingCameBack()
    {
        // With two collections one can answer while the other has nothing to retrieve with, and "can match
        // nothing" would then be false of the result.
        var result = await SearchAsync(new SearchRequest { Query = "", Phrase = "it is to be", Mode = SearchMode.Keyword });

        result.Hits.ShouldHaveSingleItem();
        result.Note.ShouldBeNull();
    }

    [Theory]
    [InlineData(SearchMode.Hybrid, "it is to be", "")]
    [InlineData(SearchMode.Semantic, "it is to be", "")]
    [InlineData(SearchMode.Keyword, "refresh token", "")]
    [InlineData(SearchMode.Keyword, "it is to be", "refresh token")]
    public async Task OtherSearchesDoNotCarryTheKeywordNote(SearchMode mode, string phrase, string query)
    {
        // Hybrid and semantic embed the query, so a phrase of stop words still retrieves. A query or phrase
        // with a real term has something for keyword mode to search on.
        var result = await SearchAsync(new SearchRequest { Query = query, Phrase = phrase, Mode = mode });

        result.Note.ShouldBeNull();
    }

    [Fact]
    public async Task AKeywordSearchOfStopWordsWithoutAPhraseIsLeftAsItWas()
    {
        // The note is for the phrase search. A search with no phrase is the one it always was.
        var result = await SearchAsync(new SearchRequest { Query = "it is to be", Mode = SearchMode.Keyword });

        result.Note.ShouldBeNull();
    }

    [Fact]
    public async Task AContextRequestPassesItsPhraseToTheSearch()
    {
        await using var db = _harness.NewContext();
        var search = new SearchService(new ScopeResolver(db), _harness.Vectors, _harness.Embedder,
            new MemoryCache(new MemoryCacheOptions()), NullLogger<SearchService>.Instance);
        _harness.Vectors.OnSearch = q =>
        {
            _asked.Add(q);
            return Answer(q);
        };
        var context = new ContextService(search, new ScopeResolver(db), _harness.Vectors, new DocumentReader(db));

        var result = await context.BuildAsync(Reader, new ContextRequest { Query = "", Phrase = "refresh   token" },
            TestContext.Current.CancellationToken);

        var asked = _asked.ShouldHaveSingleItem();
        asked.Phrase.ShouldBe("refresh token");
        asked.Text.ShouldBe("refresh token");

        // The result says what ran, as a search result does: not the blank query the request carried.
        result.Query.ShouldBe("refresh token");
        result.Phrase.ShouldBe("refresh token");
    }
}
