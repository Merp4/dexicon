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
    private static readonly DateTime At = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);

    private IndexingHarness _harness = null!;

    public async ValueTask InitializeAsync()
    {
        _harness = await IndexingHarness.StartAsync("notes");
        await using var db = _harness.NewContext();
        // Ids 1 to 6, in arrival order and a minute apart: three found nothing.
        Row(db, At.AddMinutes(0), "agent", SearchSurface.Mcp, "books", hits: 3);
        Row(db, At.AddMinutes(1), "agent", SearchSurface.Mcp, "books", hits: 0);
        Row(db, At.AddMinutes(2), "hook", SearchSurface.Context, "docs", hits: 1);
        Row(db, At.AddMinutes(3), "hook", SearchSurface.Context, "docs", hits: 0);
        Row(db, At.AddMinutes(4), "script", SearchSurface.Api, "books,docs", hits: 2);
        Row(db, At.AddMinutes(5), "agent", SearchSurface.Mcp, "docs", hits: 0);
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
        string? corpus = null, string? surface = null, bool? noResults = null, SearchLogOptions? settings = null,
        SearchLog? logged = null)
    {
        await using var db = _harness.NewContext();
        var log = logged ?? new SearchLog(Options.Create(new DexiconOptions()));
        return await SearchLogEndpoints.ListAsync(limit, before, key, corpus, surface, noResults, rc, db, log,
            Options.Create(new DexiconOptions { SearchLog = settings ?? new SearchLogOptions() }), TestContext.Current.CancellationToken);
    }

    private async Task<SearchLogPage> PageAsync(int? limit = null, long? before = null, string? key = null,
        string? corpus = null, string? surface = null, bool? noResults = null, SearchLogOptions? settings = null,
        SearchLog? logged = null) =>
        ((Ok<SearchLogPage>)await ListAsync(Admin(), limit, before, key, corpus, surface, noResults, settings, logged)).Value.ShouldNotBeNull();

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
        page.OldestUtc.ShouldBe(At);
    }

    [Fact]
    public async Task TheOldestRowIsOfTheWholeLogWhateverTheFiltersAre()
    {
        // The rows of key "hook" are the third and fourth, so the oldest they hold is two minutes in; the log goes back further.
        (await PageAsync(key: "hook")).OldestUtc.ShouldBe(At);
        (await PageAsync(noResults: true)).OldestUtc.ShouldBe(At);
    }

    [Fact]
    public async Task NoResultsFalseDoesNotNarrowTheRows()
    {
        var page = await PageAsync(noResults: false);

        page.Matching.ShouldBe(6);
        page.Items.Count.ShouldBe(6);
    }

    [Fact]
    public async Task ThePageCarriesTheNumberOfRowsTheLogDropped()
    {
        var log = new SearchLog(Options.Create(new DexiconOptions()));
        log.CountDropped(3);

        (await PageAsync(logged: log)).Settings.Dropped.ShouldBe(3);
    }

    [Fact]
    public async Task ALimitAboveTheMaximumReturnsTheMaximum()
    {
        await using (var db = _harness.NewContext())
        {
            for (var i = 0; i < 205; i++) Row(db, At.AddHours(1), "bulk", SearchSurface.Api, "docs", hits: 1);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var page = await PageAsync(limit: 100_000);

        page.Items.Count.ShouldBe(200, "a page holds at most 200 rows");
        page.NextBefore.ShouldNotBeNull();
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

    [Theory]
    [InlineData("AGE", null, new[] { 6L, 2, 1 })]
    [InlineData("gent", null, new[] { 6L, 2, 1 })]
    [InlineData(" agent ", null, new[] { 6L, 2, 1 })]
    [InlineData(null, "DOCS", new[] { 6L, 5, 4, 3 })]
    [InlineData(null, "oc", new[] { 6L, 5, 4, 3 })]
    [InlineData("%", null, new long[0])]
    [InlineData("_gent", null, new long[0])]
    [InlineData(null, "%", new long[0])]
    public async Task AKeyOrCorpusIsMatchedAsPartOfTheNameIgnoringCaseAndLiterally(string? key, string? corpus, long[] ids)
    {
        var page = await PageAsync(key: key, corpus: corpus);

        page.Items.Select(i => i.Id).ShouldBe(ids);
        page.Matching.ShouldBe(ids.Length);
    }

    [Fact]
    public async Task ABackslashInAKeyOrCorpusNameIsMatchedLiterally()
    {
        await using (var db = _harness.NewContext())
        {
            Row(db, At.AddMinutes(6), @"back\slash", SearchSurface.Api, @"x\y", hits: 1);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await PageAsync(key: @"\")).Items.Select(i => i.Id).ShouldBe([7L]);
        (await PageAsync(key: @"back\")).Items.Select(i => i.Id).ShouldBe([7L]);
        (await PageAsync(corpus: @"x\y")).Items.Select(i => i.Id).ShouldBe([7L]);
        (await PageAsync(key: @"a\b")).Items.ShouldBeEmpty("the backslash is a character of the name, not an escape for the next one");
    }

    [Fact]
    public async Task SpaceAroundACorpusFilterIsIgnored()
    {
        (await PageAsync(corpus: "  docs ")).Items.Select(i => i.Id).ShouldBe([6L, 5, 4, 3]);
    }

    [Theory]
    [InlineData("carrier-pigeon")]
    [InlineData("1,2")]
    [InlineData("3")]
    [InlineData("api,mcp")]
    [InlineData("99")]
    [InlineData(" ")]
    public async Task ASurfaceThatIsNotOneOfTheNamesIsRefusedAndTheNamesAreListed(string surface)
    {
        // Enum.TryParse takes a number or a comma list, and "1,2" is 3, which is a surface nobody named.
        var result = await ListAsync(Admin(), surface: surface);

        ((IStatusCodeHttpResult)result).StatusCode.ShouldBe(400);
        ((ProblemHttpResult)result).ProblemDetails.Detail.ShouldBe("Expected other, api, mcp, context.");
    }

    [Theory]
    [InlineData("MCP")]
    [InlineData(" context ")]
    public async Task ASurfaceIsMatchedByNameWhateverItsCaseAndPadding(string surface)
    {
        var page = await PageAsync(surface: surface);

        page.Items.ShouldNotBeEmpty();
        page.Items.ShouldAllBe(i => string.Equals(i.Surface.ToString(), surface.Trim(), StringComparison.OrdinalIgnoreCase));
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
/// All three interfaces, through the running application: a search, a context request, and a read of the log, each as the
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
    public async Task ASearchAContextRequestAndAnMcpCallEachLeaveARowNamingTheirInterface()
    {
        _store.OnSearch = q => new SearchResponse { Mode = q.Mode, Hits = [] };

        await using var factory = Start();
        await SeedAsync(factory);
        using var client = await SignedInAsync(factory);

        (await client.PostAsJsonAsync("/api/search",
            new { query = "alpha", corpus = new[] { "history" }, mode = "keyword" }, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/context",
            new { query = "beta", corpus = new[] { "history" }, mode = "keyword" }, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        // The MCP tool, over the transport an agent uses. The server is stateless, so no handshake comes first.
        using var mcp = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = new { name = "search_index", arguments = new { query = "gamma", corpus = new[] { "history" }, mode = "keyword" } },
            }),
        };
        mcp.Headers.Accept.ParseAdd("application/json");
        mcp.Headers.Accept.ParseAdd("text/event-stream");
        (await client.SendAsync(mcp, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var page = await ReadLogAsync(client, expected: 3);

        page.GetProperty("matching").GetInt32().ShouldBe(3);
        var items = page.GetProperty("items").EnumerateArray()
            .Select(i => (i.GetProperty("surface").GetString(), i.GetProperty("query").GetString(), i.GetProperty("hits").GetInt32()))
            .ToList();
        items.ShouldBe([("Mcp", "gamma", 0), ("Context", "beta", 0), ("Api", "alpha", 0)]);
        page.GetProperty("settings").GetProperty("enabled").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task WithTheLogSwitchedOffTheSettingReachesTheScreen()
    {
        _store.OnSearch = q => new SearchResponse { Mode = q.Mode, Hits = [] };

        await using var factory = Start(logOn: false);
        await SeedAsync(factory);
        using var client = await SignedInAsync(factory);

        (await client.PostAsJsonAsync("/api/search",
            new { query = "alpha", corpus = new[] { "history" }, mode = "keyword" }, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        // That nothing is queued while the log is off is tested without a host (SearchLogTests); a wait for a row that
        // must not appear would be a timer, so this checks only that the setting was read from the environment.
        var page = await ReadLogAsync(client, expected: 0);

        page.GetProperty("settings").GetProperty("enabled").GetBoolean().ShouldBeFalse();
    }
}

/// <summary>
/// The log names the interface each search came in by, and only the code that builds the request knows it. A caller added
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
    public void EveryCallThatSearchesBuildsARequestThatNamesItsSurface()
    {
        var calls = 0;
        var problems = new List<string>();
        var sep = Path.DirectorySeparatorChar;

        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal)))
        {
            // Comments and strings are blanked first, at the same length and with their line breaks kept, so a brace or a
            // `new SearchRequest` inside either cannot move a bracket match or satisfy a check.
            var text = Regex.Replace(File.ReadAllText(file), @"//[^\n]*|/\*.*?\*/|@""(?:[^""]|"""")*""|""(?:[^""\\\n]|\\.)*""",
                m => new string([.. m.Value.Select(c => c == '\n' ? '\n' : ' ')]), RegexOptions.Singleline);
            string Where(int index) => $"{Path.GetFileName(file)}:{text[..index].Count(c => c == '\n') + 1}";

            // Every call of SearchAsync on any receiver: the vector store's takes a SearchQuery and is left alone, and any
            // other takes a request written out in the call. A request built elsewhere, or target-typed (`new()`), cannot
            // be read by this scan, so it is refused here and the call names its request. It reads calls and not method
            // groups, so `Func<...> f = search.SearchAsync;` reaches a search it does not see.
            foreach (Match call in Regex.Matches(text, @"\.SearchAsync\("))
            {
                var args = Between(text, call.Index + call.Length - 1, '(', ')');
                if (args.Contains("new SearchQuery", StringComparison.Ordinal)) continue;

                calls++;
                if (!args.Contains("new SearchRequest", StringComparison.Ordinal))
                    problems.Add($"{Where(call.Index)}: SearchAsync is not given `new SearchRequest {{ ... }}` written in the call");
            }

            // Every construction has an initializer that names a surface other than Other, outside comments and strings.
            foreach (Match m in Regex.Matches(text, @"new SearchRequest\b"))
            {
                var open = text.IndexOf('{', m.Index);
                if (open < 0 || text[(m.Index + "new SearchRequest".Length)..open].Trim().Length != 0)
                {
                    problems.Add($"{Where(m.Index)}: `new SearchRequest` has no object initializer");
                    continue;
                }

                var initializer = Between(text, open, '{', '}');
                if (!Regex.IsMatch(initializer, @"\bSurface\s*=\s*SearchSurface\.(Api|Mcp|Context)\b"))
                    problems.Add($"{Where(m.Index)}: the request does not set Surface to Api, Mcp or Context");
            }
        }

        // A scan that reads nothing reports a clean result, so it states what it has to find: the MCP tool,
        // /api/search and the context service.
        calls.ShouldBeGreaterThanOrEqualTo(3, "the scan found fewer SearchAsync calls than the three callers that exist");
        problems.ShouldBeEmpty("a search that does not name its surface is recorded as Other: " + string.Join("; ", problems));
    }

    /// <summary>The text from the bracket at <paramref name="open"/> to its match, brackets of the same kind counted.</summary>
    private static string Between(string text, int open, char opening, char closing)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == opening) depth++;
            else if (text[i] == closing && --depth == 0) return text[open..(i + 1)];
        }

        return text[open..];
    }
}
