using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Search;
using Dexicon.Core.Vectors;
using Dexicon.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Dexicon.Tests;

/// <summary>
/// <c>GET /api/searches</c>: admin only, newest first, a page at a time, with every filter applied in the query
/// so a page is full of rows that match and the count says how many do.
/// </summary>
public sealed class SearchLogEndpointTests : IAsyncLifetime
{
    private IndexingHarness _harness = null!;

    public async ValueTask InitializeAsync()
    {
        _harness = await IndexingHarness.StartAsync("notes");
        await using var db = _harness.NewContext();
        var at = new DateTime(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);

        // Ids 1 to 6, in arrival order: three found nothing.
        Row(db, at, "agent", SearchSurface.Mcp, "books", hits: 3);
        Row(db, at, "agent", SearchSurface.Mcp, "books", hits: 0);
        Row(db, at, "hook", SearchSurface.Context, "docs", hits: 1);
        Row(db, at, "hook", SearchSurface.Context, "docs", hits: 0);
        Row(db, at, "script", SearchSurface.Api, "books,docs", hits: 2);
        Row(db, at, "agent", SearchSurface.Mcp, "docs", hits: 0);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    private static void Row(CatalogDbContext db, DateTime at, string key, SearchSurface surface, string corpora, int hits) =>
        db.SearchLog.Add(new SearchLogRow
        {
            AtUtc = at,
            TokenId = key,
            TokenName = key,
            Surface = surface,
            Corpora = corpora,
            Mode = "hybrid",
            Query = $"query {surface}",
            QueryChars = 9,
            Hits = hits,
        });

    private static RequestContext Admin() => new()
    {
        Principal = new Principal("admin", "admin", new HashSet<string>(StringComparer.Ordinal) { Scopes.Admin }),
    };

    private async Task<IResult> ListAsync(RequestContext rc, int? limit = null, long? before = null, string? key = null,
        string? corpus = null, string? surface = null, bool? noResults = null, SearchLogOptions? settings = null)
    {
        await using var db = _harness.NewContext();
        var log = new SearchLog(Options.Create(new DexiconOptions()));
        return await SearchLogEndpoints.ListAsync(limit, before, key, corpus, surface, noResults, rc, db, log,
            Options.Create(new DexiconOptions { SearchLog = settings ?? new SearchLogOptions() }), TestContext.Current.CancellationToken);
    }

    private async Task<SearchLogPage> PageAsync(int? limit = null, long? before = null, string? key = null,
        string? corpus = null, string? surface = null, bool? noResults = null, SearchLogOptions? settings = null) =>
        ((Ok<SearchLogPage>)await ListAsync(Admin(), limit, before, key, corpus, surface, noResults, settings)).Value.ShouldNotBeNull();

    [Fact]
    public async Task AKeyWithEveryScopeButAdminIsRefused()
    {
        var rc = new RequestContext
        {
            Principal = new Principal("k", "agent", new HashSet<string>(StringComparer.Ordinal)
            {
                Scopes.Search, Scopes.Ingest, Scopes.Configure, Scopes.Propose, Scopes.Destroy,
            }),
        };

        ((IStatusCodeHttpResult)await ListAsync(rc)).StatusCode.ShouldBe(403);
    }

    [Fact]
    public async Task TheNewestRowComesFirstAndTheCountIsEveryRow()
    {
        var page = await PageAsync();

        page.Items.Select(i => i.Id).ShouldBe([6L, 5, 4, 3, 2, 1]);
        page.Matching.ShouldBe(6);
        page.NextBefore.ShouldBeNull();
        page.OldestUtc.ShouldBe(new DateTime(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task APageContinuesFromTheIdItEndedOnWithoutRepeatingOrSkipping()
    {
        var first = await PageAsync(limit: 4);
        first.Items.Select(i => i.Id).ShouldBe([6L, 5, 4, 3]);
        first.NextBefore.ShouldBe(3);

        var second = await PageAsync(limit: 4, before: first.NextBefore);

        second.Items.Select(i => i.Id).ShouldBe([2L, 1]);
        second.NextBefore.ShouldBeNull();
        second.Matching.ShouldBe(6, "the count is of every row the filters match, not of the page");
    }

    [Fact]
    public async Task AFilterIsAppliedBeforeThePageIsCut()
    {
        // Three rows found nothing, and they are ids 6, 4 and 2: a page of two must hold two of them, not the
        // two newest rows with the empty ones picked out afterwards.
        var page = await PageAsync(limit: 2, noResults: true);

        page.Items.Select(i => i.Id).ShouldBe([6L, 4]);
        page.Matching.ShouldBe(3);
        page.NextBefore.ShouldBe(4);
    }

    [Theory]
    [InlineData("agent", null, null, new[] { 6L, 2, 1 })]
    [InlineData(null, "docs", null, new[] { 6L, 5, 4, 3 })]
    [InlineData(null, null, "context", new[] { 4L, 3 })]
    [InlineData("agent", "docs", "mcp", new[] { 6L })]
    public async Task EveryFilterNarrowsTheRows(string? key, string? corpus, string? surface, long[] ids)
    {
        var page = await PageAsync(key: key, corpus: corpus, surface: surface);

        page.Items.Select(i => i.Id).ShouldBe(ids);
        page.Matching.ShouldBe(ids.Length);
    }

    [Fact]
    public async Task AnUnknownSurfaceIsRefusedWithTheValidOnes()
    {
        var result = await ListAsync(Admin(), surface: "carrier-pigeon");

        ((IStatusCodeHttpResult)result).StatusCode.ShouldBe(400);
    }

    [Fact]
    public async Task ThePageSaysWhatTheLogIsSetToDo()
    {
        var page = await PageAsync(settings: new SearchLogOptions { Enabled = true, StoreQueryText = false, RetentionDays = 7 });

        page.Settings.ShouldBe(new SearchLogSettings(Enabled: true, StoresQueryText: false, RetentionDays: 7, Dropped: 0));
    }

    [Fact]
    public async Task ALimitOutsideTheRangeIsClamped()
    {
        (await PageAsync(limit: 0)).Items.Count.ShouldBe(1);
        (await PageAsync(limit: 100_000)).Items.Count.ShouldBe(6);
    }
}

/// <summary>
/// All three doors, through the running application: a search, a context request, and a read of the log, each as the
/// UI or the hook makes it. The writer is the real hosted service, so a row is read once it has been written.
/// </summary>
[Collection(ConsoleOutputCollection.Name)]
public sealed class SearchLogHostTests : IDisposable
{
    private const string Password = "a-test-password-for-this-run";

    private readonly string _data = Path.Combine(Path.GetTempPath(), $"dexicon-searchlog-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> _saved = [];
    private readonly TextWriter _console = Console.Out;
    private readonly IndexingHarness.RecordingVectorStore _store = new();

    private void Set(string name, string? value)
    {
        if (!_saved.ContainsKey(name)) _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    private WebApplicationFactory<Program> Start(bool logOn = true)
    {
        Directory.CreateDirectory(_data);
        Set("DEXICON__STORAGE__DATAPATH", _data);
        Set("DEXICON__QDRANT__ENDPOINT", "http://127.0.0.1:1");
        Set("DEXICON__OLLAMA__ENDPOINT", "http://127.0.0.1:1");
        Set("DEXICON__OLLAMA__MAXRETRIES", "0");
        Set("DEXICON__INDEXING__REFRESHMINUTES", "0");
        Set("DEXICON__ADMIN__PASSWORD", Password);
        Set("DEXICON__SEARCHLOG__ENABLED", logOn ? "true" : "false");

        return new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<IVectorStore>();
            services.AddSingleton<IVectorStore>(_store);
        }));
    }

    public void Dispose()
    {
        Console.SetOut(_console);
        Serilog.Log.CloseAndFlush();
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={new StorageOptions { DataPath = _data }.CatalogPath}"));

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(_data)) Directory.Delete(_data, recursive: true);
                break;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }

    private static async Task SeedAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var corpus = new Corpus { Id = "corpus-1", Name = "history", State = CorpusState.Ready, CreatedUtc = DateTime.UtcNow };
        corpus.ChunkSets.Add(new ChunkSet
        {
            Id = "set-1",
            CorpusId = corpus.Id,
            Name = "default",
            EmbeddingModel = IndexingHarness.Model,
            EmbeddingDimensions = 768,
            CollectionName = IndexingHarness.Collection,
            ChunkSize = 256,
            ChunkOverlap = 0,
            BoundaryMode = "blank-line",
            IsDefault = true,
            State = CorpusState.Ready,
            CreatedUtc = DateTime.UtcNow,
        });
        db.Corpora.Add(corpus);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<HttpClient> SignedInAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/session", new { password = Password }, TestContext.Current.CancellationToken);
        login.EnsureSuccessStatusCode();
        var body = await login.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        return client;
    }

    /// <summary>The log as the UI reads it, retried until it holds <paramref name="expected"/> rows or the wait is over.</summary>
    private static async Task<JsonElement> ReadLogAsync(HttpClient client, int expected)
    {
        JsonElement page = default;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var response = await client.GetAsync("/api/searches", TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            page = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            if (page.GetProperty("matching").GetInt32() >= expected) break;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        return page;
    }

    [Fact]
    public async Task ASearchAndAContextRequestEachLeaveARowNamingTheirDoor()
    {
        _store.OnSearch = q => new SearchResponse { Mode = q.Mode, Hits = [] };

        await using var factory = Start();
        await SeedAsync(factory);
        using var client = await SignedInAsync(factory);

        (await client.PostAsJsonAsync("/api/search",
            new { query = "alpha", corpus = new[] { "history" }, mode = "keyword" }, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/context",
            new { query = "beta", corpus = new[] { "history" }, mode = "keyword" }, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var page = await ReadLogAsync(client, expected: 2);

        page.GetProperty("matching").GetInt32().ShouldBe(2);
        var items = page.GetProperty("items").EnumerateArray()
            .Select(i => (i.GetProperty("surface").GetString(), i.GetProperty("query").GetString(), i.GetProperty("hits").GetInt32()))
            .ToList();
        items.ShouldBe([("Context", "beta", 0), ("Api", "alpha", 0)]);
        page.GetProperty("settings").GetProperty("enabled").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task WithTheLogSwitchedOffTheScreenSaysSoAndHoldsNothing()
    {
        _store.OnSearch = q => new SearchResponse { Mode = q.Mode, Hits = [] };

        await using var factory = Start(logOn: false);
        await SeedAsync(factory);
        using var client = await SignedInAsync(factory);

        (await client.PostAsJsonAsync("/api/search",
            new { query = "alpha", corpus = new[] { "history" }, mode = "keyword" }, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        await Task.Delay(500, TestContext.Current.CancellationToken);

        var page = await ReadLogAsync(client, expected: 0);

        page.GetProperty("matching").GetInt32().ShouldBe(0);
        page.GetProperty("settings").GetProperty("enabled").GetBoolean().ShouldBeFalse();
    }
}

/// <summary>
/// The log names the door each search came in by, and only the code that builds the request knows it. A caller added
/// later that leaves it out is recorded as <see cref="SearchSurface.Other"/>, and this scan fails first.
/// </summary>
public sealed class SearchSurfaceCoverageTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LICENSE"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    [Fact]
    public void EveryConstructionOfASearchRequestNamesItsSurface()
    {
        var found = 0;
        var missing = new List<string>();
        var sep = Path.DirectorySeparatorChar;

        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"new SearchRequest\b"))
            {
                found++;
                var open = text.IndexOf('{', m.Index);
                var depth = 0;
                var end = open;
                for (; end < text.Length; end++)
                {
                    if (text[end] == '{') depth++;
                    else if (text[end] == '}' && --depth == 0) break;
                }

                if (!text[open..end].Contains("Surface =", StringComparison.Ordinal))
                    missing.Add($"{Path.GetFileName(file)}:{text[..m.Index].Count(c => c == '\n') + 1}");
            }
        }

        // A scan that reads nothing reports a clean result, so it states what it has to find: the MCP tool,
        // /api/search and the context service.
        found.ShouldBeGreaterThanOrEqualTo(3, "the scan found fewer SearchRequest constructions than the three callers that exist");
        missing.ShouldBeEmpty("these build a SearchRequest without a Surface, so the search log records them as Other: " + string.Join(", ", missing));
    }
}
