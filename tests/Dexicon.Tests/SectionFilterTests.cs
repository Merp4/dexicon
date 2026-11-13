using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Documents;
using Dexicon.Core.Search;
using Dexicon.Infrastructure;
using Dexicon.Mcp;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dexicon.Tests;

/// <summary>
/// The section filter, hop by hop from the caller to the vector store: the MCP tool, the search service and the
/// context service each hand the section on, and a hop that dropped it would return every section while the
/// caller believed the search was restricted. The REST endpoints are in <see cref="SectionFilterHostTests"/> and the
/// store's own half is <see cref="QdrantSectionFilterTests"/>.
/// </summary>
public sealed class SectionFilterTests : IAsyncLifetime
{
    private IndexingHarness _harness = null!;
    private SearchQuery? _seen;

    public async ValueTask InitializeAsync()
    {
        _harness = await IndexingHarness.StartAsync("notes");
        await _harness.SeedCorpusAsync(Dexicon.Core.Catalog.SourceKind.Workspace);
        _harness.Vectors.OnSearch = query =>
        {
            _seen = query;
            return new SearchResponse { Hits = [] };
        };
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    private static RequestContext Holding(params string[] scopes) => new()
    {
        Principal = new Principal("k", "agent", scopes.ToHashSet(StringComparer.Ordinal)),
    };

    private static Principal Reader() => new("k", "agent", new HashSet<string>(StringComparer.Ordinal) { Scopes.Search });

    private SearchService SearchOver(Dexicon.Core.Catalog.CatalogDbContext db) =>
        new(new ScopeResolver(db), _harness.Vectors, _harness.Embedder,
            new MemoryCache(new MemoryCacheOptions()), NullLogger<SearchService>.Instance);

    [Fact]
    public async Task TheSearchServiceAsksTheStoreForTheSection()
    {
        await using var db = _harness.NewContext();

        await SearchOver(db).SearchAsync(Reader(),
            new SearchRequest { Query = "alpha", Corpus = ["notes"], Section = "diff", Mode = SearchMode.Keyword },
            TestContext.Current.CancellationToken);

        _seen.ShouldNotBeNull().Section.ShouldBe("diff");
    }

    [Fact]
    public async Task ASearchWithNoSectionAsksTheStoreForNone()
    {
        await using var db = _harness.NewContext();

        await SearchOver(db).SearchAsync(Reader(),
            new SearchRequest { Query = "alpha", Corpus = ["notes"], Mode = SearchMode.Keyword },
            TestContext.Current.CancellationToken);

        _seen.ShouldNotBeNull().Section.ShouldBeNull();
    }

    [Fact]
    public async Task TheSearchToolPassesTheSectionOn()
    {
        await using var db = _harness.NewContext();

        await DexiconTools.SearchIndexAsync(Holding(Scopes.Search), SearchOver(db), "alpha",
            corpus: ["notes"], mode: "keyword", section: "message", ct: TestContext.Current.CancellationToken);

        _seen.ShouldNotBeNull().Section.ShouldBe("message");
    }

    [Fact]
    public async Task TheContextServicePassesTheSectionOn()
    {
        await using var db = _harness.NewContext();
        var context = new ContextService(SearchOver(db), new ScopeResolver(db), _harness.Vectors, new DocumentReader(db));

        await context.BuildAsync(Reader(),
            new ContextRequest { Query = "alpha", Corpus = ["notes"], Section = "stat", Mode = SearchMode.Keyword },
            TestContext.Current.CancellationToken);

        _seen.ShouldNotBeNull().Section.ShouldBe("stat");
    }
}
