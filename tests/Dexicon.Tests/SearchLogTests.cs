using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Embedding;
using Dexicon.Core.Search;
using Dexicon.Infrastructure;
using Dexicon.Mcp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dexicon.Tests;

/// <summary>
/// The record of searches (D-52): one row for each search that ran, written off the request path, with the
/// query text cut and switched off as the settings say, and rows past retention removed. The queue between
/// a search and the writer is drained by hand here, so a test reads exactly what a search left behind.
/// </summary>
public sealed class SearchLogTests : IAsyncLifetime
{
    private static readonly Principal Agent = new("k", "agent", new HashSet<string>(StringComparer.Ordinal) { Scopes.Search });

    private const int SearchPause = 50;

    private readonly List<ServiceProvider> _providers = [];
    private IndexingHarness _harness = null!;

    public async ValueTask InitializeAsync()
    {
        _harness = await IndexingHarness.StartAsync("notes");
        await _harness.SeedCorpusAsync(SourceKind.Workspace);
        // Two hits, the better one first as the store returns them, and a pause that gives the time taken a floor
        // the machine cannot lower.
        _harness.Vectors.OnSearch = q =>
        {
            Thread.Sleep(SearchPause);
            return new SearchResponse { Mode = q.Mode, Hits = [Hit(0.9f), Hit(0.4f, "b.md")] };
        };
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers) await provider.DisposeAsync();
        await _harness.DisposeAsync();
    }

    private static SearchHit Hit(float score, string file = "a.md") => new()
    {
        CorpusId = IndexingHarness.CorpusId,
        SourceId = IndexingHarness.SourceIdFor(0),
        FilePath = file,
        StartLine = 1,
        EndLine = 1,
        Content = "alpha",
        Score = score,
    };

    private static IOptions<DexiconOptions> Settings(bool enabled = true, bool text = true, int days = 30) =>
        Options.Create(new DexiconOptions
        {
            SearchLog = new SearchLogOptions { Enabled = enabled, StoreQueryText = text, RetentionDays = days },
        });

    /// <summary>A scope factory that hands out contexts on the harness's catalogue, as the host's writer gets them.</summary>
    private IServiceScopeFactory ScopesOverTheCatalogue()
    {
        var services = new ServiceCollection();
        services.AddDbContext<CatalogDbContext>(o =>
            o.UseSqlite($"Data Source={Path.Combine(_harness.DataPath, "catalog.db")};Pooling=False"));
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    private SearchLogWriter WriterFor(SearchLog log, IOptions<DexiconOptions> settings, TimeProvider? clock = null,
        IServiceScopeFactory? scopes = null) =>
        new(log, scopes ?? ScopesOverTheCatalogue(), settings, clock ?? TimeProvider.System, NullLogger<SearchLogWriter>.Instance)
        {
            RetryDelay = TimeSpan.Zero,
        };

    private SearchService ServiceWith(ISearchLog log, IEmbeddingService? embedder = null)
    {
        var db = _harness.NewContext();
        return new SearchService(new ScopeResolver(db), _harness.Vectors, embedder ?? _harness.Embedder, _harness.Profiles,
            new MemoryCache(new MemoryCacheOptions()), NullLogger<SearchService>.Instance, log);
    }

    /// <summary>Moves what the searches queued through the writer, and returns what the catalogue then holds.</summary>
    private async Task<List<SearchLogRow>> FlushAsync(SearchLog log, SearchLogWriter writer)
    {
        var batch = new List<SearchLogRow>();
        while (log.Reader.TryRead(out var row)) batch.Add(row);
        if (batch.Count > 0) await writer.WriteAsync(batch, TestContext.Current.CancellationToken);

        await using var db = _harness.NewContext();
        return await db.SearchLog.OrderBy(r => r.Id).ToListAsync(TestContext.Current.CancellationToken);
    }

    private static SearchRequest Ask(string query = "alpha", SearchSurface surface = SearchSurface.Mcp, SearchMode mode = SearchMode.Keyword) =>
        new() { Query = query, Mode = mode, Surface = surface };

    [Fact]
    public async Task ASearchLeavesOneRowWithWhatWasAskedAndWhatCameBack()
    {
        var settings = Settings();
        var log = new SearchLog(settings);
        var before = DateTime.UtcNow;

        await ServiceWith(log).SearchAsync(Agent, Ask("alpha beta"), TestContext.Current.CancellationToken);

        var row = (await FlushAsync(log, WriterFor(log, settings))).ShouldHaveSingleItem();
        row.TokenId.ShouldBe("k");
        row.TokenName.ShouldBe("agent");
        row.Surface.ShouldBe(SearchSurface.Mcp);
        row.Corpora.ShouldBe("notes");
        row.Mode.ShouldBe("keyword");
        row.Query.ShouldBe("alpha beta");
        row.QueryChars.ShouldBe(10);
        row.Hits.ShouldBe(2);
        row.TopScore.ShouldBe(0.9f, "the first hit's, not the last");
        row.TookMs.ShouldBeGreaterThanOrEqualTo(SearchPause - 5, "the time the store took");
        row.Degraded.ShouldBeFalse();
        row.Noted.ShouldBeFalse();
        row.AtUtc.ShouldBeInRange(before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
    }

    [Theory]
    [InlineData(SearchMode.Hybrid, "hybrid")]
    [InlineData(SearchMode.Semantic, "semantic")]
    [InlineData(SearchMode.Keyword, "keyword")]
    public async Task ASearchRecordsTheModeThatRan(SearchMode asked, string recorded)
    {
        var settings = Settings();
        var log = new SearchLog(settings);

        await ServiceWith(log).SearchAsync(Agent, Ask(mode: asked), TestContext.Current.CancellationToken);

        (await FlushAsync(log, WriterFor(log, settings))).ShouldHaveSingleItem().Mode.ShouldBe(recorded);
    }

    [Fact]
    public async Task AResultThatCarriesANoteIsRecordedAsNoted()
    {
        var settings = Settings();
        var log = new SearchLog(settings);
        await using (var db = _harness.NewContext())
            await db.ChunkSets.ExecuteUpdateAsync(u => u.SetProperty(s => s.State, CorpusState.Unavailable), TestContext.Current.CancellationToken);

        await ServiceWith(log).SearchAsync(Agent, Ask(), TestContext.Current.CancellationToken);

        (await FlushAsync(log, WriterFor(log, settings))).ShouldHaveSingleItem().Noted.ShouldBeTrue();
    }

    [Fact]
    public async Task ASearchThatFoundNothingIsARowWithNoTopScore()
    {
        var settings = Settings();
        var log = new SearchLog(settings);
        _harness.Vectors.OnSearch = q => new SearchResponse { Mode = q.Mode, Hits = [] };

        await ServiceWith(log).SearchAsync(Agent, Ask(), TestContext.Current.CancellationToken);

        var row = (await FlushAsync(log, WriterFor(log, settings))).ShouldHaveSingleItem();
        row.Hits.ShouldBe(0);
        row.TopScore.ShouldBeNull();
    }

    [Fact]
    public async Task ASearchThatFellBackToKeywordRecordsThatModeAndTheFlag()
    {
        var settings = Settings();
        var log = new SearchLog(settings);

        await ServiceWith(log, new EmbedderThatIsAway()).SearchAsync(
            Agent, Ask(mode: SearchMode.Hybrid), TestContext.Current.CancellationToken);

        var row = (await FlushAsync(log, WriterFor(log, settings))).ShouldHaveSingleItem();
        row.Degraded.ShouldBeTrue();
        row.Mode.ShouldBe("keyword", "the mode that ran, not the one asked for");
    }

    [Fact]
    public async Task ASearchRefusedBeforeItRanLeavesNoRow()
    {
        var settings = Settings();
        var log = new SearchLog(settings);
        var request = Ask() with { Corpus = ["not-a-corpus"] };

        await Should.ThrowAsync<ScopeResolutionException>(
            ServiceWith(log).SearchAsync(Agent, request, TestContext.Current.CancellationToken));

        (await FlushAsync(log, WriterFor(log, settings))).ShouldBeEmpty();
    }

    [Fact]
    public async Task ASearchRecordsTheDoorItCameInBy()
    {
        var settings = Settings();
        var log = new SearchLog(settings);
        var search = ServiceWith(log);

        foreach (var surface in new[] { SearchSurface.Api, SearchSurface.Mcp, SearchSurface.Context })
            await search.SearchAsync(Agent, Ask(surface: surface), TestContext.Current.CancellationToken);

        (await FlushAsync(log, WriterFor(log, settings))).Select(r => r.Surface)
            .ShouldBe([SearchSurface.Api, SearchSurface.Mcp, SearchSurface.Context]);
    }

    [Fact]
    public async Task TheMcpToolRecordsItselfAsTheMcpSurface()
    {
        var settings = Settings();
        var log = new SearchLog(settings);
        var rc = new RequestContext { Principal = Agent };

        await DexiconTools.SearchIndexAsync(rc, ServiceWith(log), "alpha", mode: "keyword", ct: TestContext.Current.CancellationToken);

        (await FlushAsync(log, WriterFor(log, settings))).ShouldHaveSingleItem().Surface.ShouldBe(SearchSurface.Mcp);
    }

    [Fact]
    public async Task AQueryIsCutAtTheLimitAndItsWholeLengthIsKept()
    {
        var settings = Settings();
        var log = new SearchLog(settings);

        await ServiceWith(log).SearchAsync(Agent, Ask(new string('a', 600)), TestContext.Current.CancellationToken);

        var row = (await FlushAsync(log, WriterFor(log, settings))).ShouldHaveSingleItem();
        row.Query.ShouldNotBeNull().Length.ShouldBe(SearchLog.MaxQueryChars);
        row.QueryChars.ShouldBe(600);
    }

    [Fact]
    public void ACutNeverSplitsASurrogatePair()
    {
        // 499 letters, then a character outside the basic plane: its two halves sit at positions 499 and 500.
        var text = new string('a', SearchLog.MaxQueryChars - 1) + "\U0001F600" + "tail";

        var cut = SearchLog.Cut(text, SearchLog.MaxQueryChars).ShouldNotBeNull();

        cut.Length.ShouldBe(SearchLog.MaxQueryChars - 1);
        char.IsHighSurrogate(cut[^1]).ShouldBeFalse();
    }

    [Fact]
    public async Task WithTheTextSwitchedOffNoQueryTextIsKeptButItsLengthIs()
    {
        var settings = Settings(text: false);
        var log = new SearchLog(settings);

        await ServiceWith(log).SearchAsync(Agent, Ask("a private prompt"), TestContext.Current.CancellationToken);

        var row = (await FlushAsync(log, WriterFor(log, settings))).ShouldHaveSingleItem();
        row.Query.ShouldBeNull();
        row.QueryChars.ShouldBe(16);
        row.Hits.ShouldBe(2);
    }

    [Fact]
    public async Task WithTheLogSwitchedOffNothingIsQueued()
    {
        var settings = Settings(enabled: false);
        var log = new SearchLog(settings);

        await ServiceWith(log).SearchAsync(Agent, Ask(), TestContext.Current.CancellationToken);

        (await FlushAsync(log, WriterFor(log, settings))).ShouldBeEmpty();
        log.Dropped.ShouldBe(0);
    }

    [Fact]
    public void AFullQueueDropsTheNewRowAndCountsIt()
    {
        var log = new SearchLog(Settings());

        for (var i = 0; i < SearchLog.Capacity + 3; i++)
            log.Record(new SearchLogRow { TokenId = "k", TokenName = "agent", Corpora = "notes", Mode = "keyword" });

        log.Dropped.ShouldBe(3);
    }

    [Fact]
    public async Task ABatchTheCatalogueRefusesIsDroppedAndCountedAndDoesNotThrow()
    {
        var settings = Settings();
        var log = new SearchLog(settings);
        var writer = WriterFor(log, settings, scopes: new RefusingScopes());
        var batch = Enumerable.Range(0, 4)
            .Select(_ => new SearchLogRow { TokenId = "k", TokenName = "agent", Corpora = "notes", Mode = "keyword" })
            .ToList();

        await writer.WriteAsync(batch, TestContext.Current.CancellationToken);

        log.Dropped.ShouldBe(4);
    }

    [Fact]
    public async Task ABatchRefusedOnceIsWrittenOnTheRetry()
    {
        var settings = Settings();
        var log = new SearchLog(settings);
        var scopes = new FlakyScopes(ScopesOverTheCatalogue(), refusals: 1);
        var writer = WriterFor(log, settings, scopes: scopes);
        await ServiceWith(log).SearchAsync(Agent, Ask(), TestContext.Current.CancellationToken);

        (await FlushAsync(log, writer)).ShouldHaveSingleItem();

        scopes.Attempts.ShouldBe(2, "the first attempt was refused and the second wrote it");
        log.Dropped.ShouldBe(0);
    }

    [Fact]
    public async Task ABatchRefusedTwiceIsDroppedAfterExactlyTwoAttempts()
    {
        var settings = Settings();
        var log = new SearchLog(settings);
        var scopes = new FlakyScopes(ScopesOverTheCatalogue(), refusals: 5);
        var writer = WriterFor(log, settings, scopes: scopes);

        await writer.WriteAsync([new SearchLogRow { TokenId = "k", TokenName = "agent", Corpora = "notes", Mode = "keyword" }],
            TestContext.Current.CancellationToken);

        scopes.Attempts.ShouldBe(2);
        log.Dropped.ShouldBe(1);
    }

    [Fact]
    public async Task AQueryWithALoneSurrogateAndANulIsWrittenAndReadBack()
    {
        var settings = Settings();
        var log = new SearchLog(settings);

        await ServiceWith(log).SearchAsync(Agent, Ask("\ud800 a\0b"), TestContext.Current.CancellationToken);

        var row = (await FlushAsync(log, WriterFor(log, settings))).ShouldHaveSingleItem();
        row.Hits.ShouldBe(2, "the row was kept whole whatever the text held");
        row.QueryChars.ShouldBe(5);
    }

    [Fact]
    public void ACorpusListThatIsTooLongIsCutAtAWholeNameAndSaysSo()
    {
        var names = string.Join(",", Enumerable.Range(0, 80).Select(i => $"corpus-number-{i:00}"));

        var cut = SearchLog.CutList(names, SearchLog.MaxCorporaChars);

        cut.Length.ShouldBeLessThanOrEqualTo(SearchLog.MaxCorporaChars);
        cut.ShouldEndWith(",…");
        cut[..^2].Split(',').ShouldAllBe(n => n.StartsWith("corpus-number-", StringComparison.Ordinal) && n.Length == 16,
            "every name that is kept is whole");
        SearchLog.CutList("books,docs", SearchLog.MaxCorporaChars).ShouldBe("books,docs");
    }

    [Fact]
    public void ASingleNameLongerThanTheLimitIsCutAndMarked()
    {
        var cut = SearchLog.CutList(new string('a', 600), SearchLog.MaxCorporaChars);

        cut.Length.ShouldBe(SearchLog.MaxCorporaChars);
        cut.ShouldEndWith("…");
    }

    [Fact]
    public async Task ARetentionLongerThanADateCanHoldIsClampedAndStillPrunes()
    {
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var settings = Settings(days: int.MaxValue);
        var log = new SearchLog(settings);
        await SeedAsync(new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc), now.UtcDateTime.AddDays(-1));

        var removed = await WriterFor(log, settings, new FixedClock(now)).PruneAsync(TestContext.Current.CancellationToken);

        removed.ShouldBe(1, "the cut-off is 36,500 days back, and a row from 1900 is older than that");
    }

    [Fact]
    public async Task RowsPastRetentionAreRemovedAndTheRestStay()
    {
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var settings = Settings(days: 30);
        var log = new SearchLog(settings);
        await SeedAsync(now.UtcDateTime.AddDays(-31), now.UtcDateTime.AddDays(-29), now.UtcDateTime.AddHours(-1));

        var removed = await WriterFor(log, settings, new FixedClock(now)).PruneAsync(TestContext.Current.CancellationToken);

        removed.ShouldBe(1);
        await using var db = _harness.NewContext();
        (await db.SearchLog.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(2);
        (await db.SearchLog.AnyAsync(r => r.AtUtc < now.UtcDateTime.AddDays(-30), TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task ARowExactlyAtTheCutOffStays()
    {
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var settings = Settings(days: 30);
        var log = new SearchLog(settings);
        await SeedAsync(now.UtcDateTime.AddDays(-30), now.UtcDateTime.AddDays(-30).AddSeconds(-1));

        (await WriterFor(log, settings, new FixedClock(now)).PruneAsync(TestContext.Current.CancellationToken)).ShouldBe(1);

        await using var db = _harness.NewContext();
        (await db.SearchLog.AsNoTracking().Select(r => r.AtUtc).SingleAsync(TestContext.Current.CancellationToken))
            .ShouldBe(now.UtcDateTime.AddDays(-30), "the row a second older than the window went, and the one at its edge stayed");
    }

    [Fact]
    public async Task ALongCorporaListIsCutWhenItIsRecorded()
    {
        var settings = Settings();
        var log = new SearchLog(settings);
        var names = string.Join(",", Enumerable.Range(0, 80).Select(i => $"corpus-number-{i:00}"));

        log.Record(new SearchLogRow { TokenId = "k", TokenName = "agent", Corpora = names, Mode = "keyword" });

        var row = (await FlushAsync(log, WriterFor(log, settings))).ShouldHaveSingleItem();
        row.Corpora.Length.ShouldBeLessThanOrEqualTo(SearchLog.MaxCorporaChars);
        row.Corpora.ShouldEndWith(",…");
    }

    [Fact]
    public async Task TheHostedWriterPrunesAtStartAndWritesWhatIsQueued()
    {
        var now = DateTimeOffset.UtcNow;
        var settings = Settings(days: 30);
        var log = new SearchLog(settings);
        await SeedAsync(now.UtcDateTime.AddDays(-45), now.UtcDateTime.AddDays(-2));
        var writer = WriterFor(log, settings, new FixedClock(now));

        await writer.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            for (var i = 0; i < 250; i++)
                log.Record(new SearchLogRow { TokenId = "k", TokenName = "queued", Corpora = "notes", Mode = "keyword", AtUtc = now.UtcDateTime });

            // The end state: all 250 queued rows written and the 45 day old row gone, leaving the 2 day old one. The
            // total passes through 251 on the way (the old row and 249 of the new), so the wait is for both facts.
            var cutoff = now.UtcDateTime.AddDays(-30);
            (int Queued, bool Stale) seen = (0, true);
            for (var attempt = 0; attempt < 100 && (seen.Queued != 250 || seen.Stale); attempt++)
            {
                await using var db = _harness.NewContext();
                seen = (await db.SearchLog.CountAsync(r => r.TokenName == "queued", TestContext.Current.CancellationToken),
                        await db.SearchLog.AnyAsync(r => r.AtUtc < cutoff, TestContext.Current.CancellationToken));
                if (seen.Queued != 250 || seen.Stale) await Task.Delay(100, TestContext.Current.CancellationToken);
            }

            seen.Queued.ShouldBe(250);
            seen.Stale.ShouldBeFalse("the 45 day old row was removed by the prune at start");
            await using var final = _harness.NewContext();
            (await final.SearchLog.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(251);
        }
        finally
        {
            await writer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ZeroRetentionDaysKeepsEveryRow()
    {
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var settings = Settings(days: 0);
        var log = new SearchLog(settings);
        await SeedAsync(now.UtcDateTime.AddDays(-400), now.UtcDateTime.AddDays(-1));

        (await WriterFor(log, settings, new FixedClock(now)).PruneAsync(TestContext.Current.CancellationToken)).ShouldBe(0);

        await using var db = _harness.NewContext();
        (await db.SearchLog.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(2);
    }

    private async Task SeedAsync(params DateTime[] at)
    {
        await using var db = _harness.NewContext();
        foreach (var when in at)
            db.SearchLog.Add(new SearchLogRow { AtUtc = when, TokenId = "k", TokenName = "agent", Corpora = "notes", Mode = "keyword" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Refuses the first <paramref name="refusals"/> attempts to open the catalogue, then hands out real scopes.</summary>
    private sealed class FlakyScopes(IServiceScopeFactory inner, int refusals) : IServiceScopeFactory
    {
        public int Attempts { get; private set; }

        public IServiceScope CreateScope()
        {
            Attempts++;
            return Attempts <= refusals ? throw new InvalidOperationException("the catalogue is locked") : inner.CreateScope();
        }
    }

    /// <summary>A catalogue that cannot be opened: the case the writer has to survive.</summary>
    private sealed class RefusingScopes : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("the catalogue is locked");
    }

    private sealed class EmbedderThatIsAway : IEmbeddingService
    {
        public Task<IReadOnlyList<float[]>> EmbedAsync(EmbeddingTarget target, EmbedPurpose purpose,
            IReadOnlyList<string> inputs, string? source = null, CancellationToken ct = default) =>
            throw new EmbeddingUnavailableException("the provider is away");

        public Task<int?> CountTokensAsync(EmbeddingTarget target, string text, CancellationToken ct = default) =>
            Task.FromResult<int?>(null);

        public Task<int> ProbeDimensionsAsync(EmbeddingTarget target, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public int KnownDimensions(EmbeddingTarget target) => 768;
    }
}
