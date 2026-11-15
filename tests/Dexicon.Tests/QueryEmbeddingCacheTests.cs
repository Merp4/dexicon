using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Embedding;
using Dexicon.Core.Search;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dexicon.Tests;

/// <summary>
/// The cache of query vectors in <see cref="SearchService"/>. A query is wrapped in its model's query template
/// before it is embedded, and saving a profile changes that template at once, so a vector cached under the old
/// template must not answer the same query after the save. The document template does not touch a query
/// vector, so changing only that one keeps the cached vector.
/// </summary>
public sealed class QueryEmbeddingCacheTests : IAsyncLifetime
{
    private static readonly Principal Reader = new("k", "agent", new HashSet<string>(StringComparer.Ordinal) { Scopes.Search });

    private IndexingHarness _harness = null!;
    private CountingEmbedder _embedder = null!;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public async ValueTask InitializeAsync()
    {
        _harness = await IndexingHarness.StartAsync("notes");
        await _harness.SeedCorpusAsync(SourceKind.Workspace);
        _embedder = new CountingEmbedder(_harness.Embedder);
        _harness.Vectors.OnSearch = q => new SearchResponse
        {
            Mode = q.Mode,
            Hits =
            [
                new SearchHit
                {
                    CorpusId = IndexingHarness.CorpusId,
                    SourceId = IndexingHarness.SourceIdFor(0),
                    FilePath = "a.md",
                    StartLine = 1,
                    EndLine = 1,
                    Content = "alpha",
                    Score = 1f,
                },
            ],
        };
    }

    public async ValueTask DisposeAsync()
    {
        _cache.Dispose();
        await _harness.DisposeAsync();
    }

    private async Task SearchAsync(string query)
    {
        await using var db = _harness.NewContext();
        var search = new SearchService(new ScopeResolver(db), _harness.Vectors, _embedder, _harness.Profiles,
            _cache, NullLogger<SearchService>.Instance);
        await search.SearchAsync(Reader, new SearchRequest { Query = query }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ARepeatedQueryIsEmbeddedOnce()
    {
        await SearchAsync("token refresh");
        await SearchAsync("token refresh");

        _embedder.QueryEmbeds.ShouldBe(1);
    }

    [Fact]
    public async Task ASavedQueryTemplateMakesTheRepeatedQueryEmbedAgain()
    {
        await SearchAsync("token refresh");

        _harness.Profiles.Templates = new ModelTemplates("{text}", "task: search result | query: {text}", TemplateOrigin.Configured);
        await SearchAsync("token refresh");
        _embedder.QueryEmbeds.ShouldBe(2);

        // The new template's vector is cached in its turn.
        await SearchAsync("token refresh");
        _embedder.QueryEmbeds.ShouldBe(2);
    }

    [Fact]
    public async Task ASavedDocumentTemplateKeepsTheCachedQueryVector()
    {
        await SearchAsync("token refresh");

        _harness.Profiles.Templates = new ModelTemplates("title: none | text: {text}", "{text}", TemplateOrigin.Configured);
        await SearchAsync("token refresh");

        _embedder.QueryEmbeds.ShouldBe(1);
    }

    [Fact]
    public async Task ASwitchBackToAnEarlierQueryTemplateFindsItsOwnCachedVector()
    {
        await SearchAsync("token refresh");
        var raw = _harness.Profiles.Templates;

        _harness.Profiles.Templates = new ModelTemplates("{text}", "q: {text}", TemplateOrigin.Configured);
        await SearchAsync("token refresh");
        _embedder.QueryEmbeds.ShouldBe(2);

        // Back to the first template within the cache's life: its own vector is still there, which is correct.
        _harness.Profiles.Templates = raw;
        await SearchAsync("token refresh");
        _embedder.QueryEmbeds.ShouldBe(2);
    }

    /// <summary>Counts the embed calls made for a query and passes every call on.</summary>
    private sealed class CountingEmbedder(IEmbeddingService inner) : IEmbeddingService
    {
        public int QueryEmbeds { get; private set; }

        public Task<IReadOnlyList<float[]>> EmbedAsync(EmbeddingTarget target, EmbedPurpose purpose,
            IReadOnlyList<string> inputs, string? source = null, CancellationToken ct = default)
        {
            if (purpose == EmbedPurpose.Query) QueryEmbeds++;
            return inner.EmbedAsync(target, purpose, inputs, source, ct);
        }

        public Task<int?> CountTokensAsync(EmbeddingTarget target, string text, CancellationToken ct = default) =>
            inner.CountTokensAsync(target, text, ct);

        public Task<int> ProbeDimensionsAsync(EmbeddingTarget target, CancellationToken ct = default) =>
            inner.ProbeDimensionsAsync(target, ct);

        public int KnownDimensions(EmbeddingTarget target) => inner.KnownDimensions(target);
    }
}
