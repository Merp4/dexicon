using System.Reflection;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Search;
using Dexicon.Infrastructure;
using Dexicon.Mcp;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace Dexicon.Tests;

/// <summary>
/// The phrase as an agent meets it: the argument of <c>search_index</c>, and the text that comes back.
/// </summary>
public sealed class PhraseToolTests : IAsyncLifetime
{
    private IndexingHarness _harness = null!;
    private readonly List<SearchQuery> _asked = [];

    public async ValueTask InitializeAsync()
    {
        _harness = await IndexingHarness.StartAsync("notes");
        await _harness.SeedCorpusAsync(SourceKind.Workspace);
        _harness.Vectors.OnSearch = q =>
        {
            _asked.Add(q);
            return Answer(q, new SearchHit
            {
                CorpusId = IndexingHarness.CorpusId,
                SourceId = IndexingHarness.SourceIdFor(0),
                FilePath = "a.md",
                StartLine = 3,
                EndLine = 9,
                Content = "the refresh token is renewed",
            });
        };
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    /// <summary>The store's answer: these hits, in the mode that was asked for.</summary>
    private static SearchResponse Answer(SearchQuery q, params SearchHit[] hits) => new() { Hits = hits, Mode = q.Mode };

    private static RequestContext Reader() => new()
    {
        Principal = new Principal("k", "agent", new HashSet<string>(StringComparer.Ordinal) { Scopes.Search }),
    };

    private async Task<string> CallAsync(string query, string? phrase, string mode = "hybrid")
    {
        await using var db = _harness.NewContext();
        var search = new SearchService(new ScopeResolver(db), _harness.Vectors, _harness.Embedder,
            new MemoryCache(new MemoryCacheOptions()), NullLogger<SearchService>.Instance);
        return await DexiconTools.SearchIndexAsync(Reader(), search, query, phrase: phrase, mode: mode);
    }

    [Fact]
    public void OnlyTheQueryIsRequiredAndThePhraseIsOptional()
    {
        // The advertised schema marks a parameter required when it has no default. Making phrase required
        // would break every client that has never heard of it.
        var parameters = typeof(DexiconTools).GetMethod(nameof(DexiconTools.SearchIndexAsync))!.GetParameters()
            .Where(p => p.ParameterType != typeof(CancellationToken) && p.ParameterType != typeof(RequestContext)
                        && p.ParameterType != typeof(SearchService));

        parameters.Where(p => !p.HasDefaultValue).Select(p => p.Name).ShouldBe(["query"]);
        parameters.Single(p => p.Name == "phrase").DefaultValue.ShouldBeNull();
    }

    [Fact]
    public async Task APhraseSearchNamesTheQueryAndThePhraseInItsHeader()
    {
        var text = await CallAsync("how tokens are renewed", "refresh token");

        text.ShouldStartWith("1 result for \"how tokens are renewed\" containing \"refresh token\" (hybrid, corpus: notes)");
        _asked.ShouldHaveSingleItem().Phrase.ShouldBe("refresh token");
    }

    [Fact]
    public async Task ASearchForThePhraseAloneNamesItOnce()
    {
        var text = await CallAsync("", "refresh token");

        text.ShouldStartWith("1 result containing \"refresh token\" (hybrid, corpus: notes)");
    }

    [Fact]
    public async Task ASearchWithoutAPhraseHasTheHeaderItAlwaysHad()
    {
        var text = await CallAsync("how tokens are renewed", null);

        text.ShouldStartWith("1 result for \"how tokens are renewed\" (hybrid, corpus: notes)");
        text.ShouldNotContain("containing");
    }

    [Fact]
    public async Task AnEmptyAnswerToAPhraseSaysHowThePhraseIsMatched()
    {
        // A caller used to substring search expects "refresh" to find RefreshAsync. Matching is by word, and
        // an empty answer that did not say so would read as "the text is not indexed".
        _harness.Vectors.OnSearch = q =>
        {
            _asked.Add(q);
            return Answer(q);
        };

        var text = await CallAsync("", "refresh");

        text.ShouldStartWith("No results containing \"refresh\" (hybrid, corpus: notes)");
        text.ShouldContain("word for word and in order");
        text.ShouldContain("RefreshAsync");
    }

    [Fact]
    public void AnEmptyAnswerFromADegradedPhraseSearchDoesNotSayThePhraseMustAppearWordForWord()
    {
        // That search fell back to keyword mode and could not have found the phrase. A rule about how the
        // phrase must appear, under "Nothing matched", would read as saying it is absent.
        var result = new SearchResult
        {
            Query = "it is to be",
            Phrase = "it is to be",
            Mode = SearchMode.Keyword,
            Degraded = true,
            DegradedReason = "embedding service unavailable; keyword-only results",
            Scope = [new SearchResult.ScopeEntry("c1", "notes", CorpusState.Ready)],
            Hits = [],
        };

        var text = DexiconTools.Render(result);

        text.ShouldContain("! DEGRADED");
        text.ShouldContain("Nothing matched.");
        text.ShouldNotContain("word for word");
    }

    [Fact]
    public async Task AnEmptyAnswerWithoutAPhraseIsTheOneItAlwaysWas()
    {
        _harness.Vectors.OnSearch = q => Answer(q);

        var text = await CallAsync("anything", null);

        text.ShouldNotContain("word for word");
        text.ShouldContain("Nothing matched. If this is unexpected, check index_status");
    }

    [Fact]
    public async Task AnUnmatchablePhraseReachesAnMcpCallerAsAnErrorThatSaysWhy()
    {
        // The SDK shows a caller the message of an McpException and nothing of any other exception.
        var thrown = await Should.ThrowAsync<McpException>(() => CallAsync("", "!!!"));

        thrown.Message.ShouldContain("at least one letter or digit");
        _asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task AHostilePhraseIsShownOnOneLineAndCutInTheHeader()
    {
        var hostile = "bad\n[10:00:00Z INF] forged\u001B[2J " + new string('z', 400);

        var text = await CallAsync("", hostile);

        var first = text.Split('\n')[0];
        first.ShouldNotContain("\u001B");
        first.Length.ShouldBeLessThan(300);
        text.Split('\n').ShouldNotContain(l => l.StartsWith("[10:00:00Z", StringComparison.Ordinal));
    }
}
