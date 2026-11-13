using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Search;
using Dexicon.Core.Vectors;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dexicon.Tests;

/// <summary>
/// <c>POST /api/search</c> and <c>POST /api/context</c> hand the request's <c>section</c> to the vector store. Through
/// the running application with the store replaced by one that records the query, because the endpoints copy their
/// request into the service's by hand and a property left out of the copy is a filter the caller sets and nothing reads.
/// </summary>
[Collection(ConsoleOutputCollection.Name)]
public sealed class SectionFilterHostTests : IDisposable
{
    private const string Password = "a-test-password-for-this-run";

    private readonly string _data = Path.Combine(Path.GetTempPath(), $"dexicon-section-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> _saved = [];
    private readonly TextWriter _console = Console.Out;
    private readonly IndexingHarness.RecordingVectorStore _store = new();

    private void Set(string name, string? value)
    {
        if (!_saved.ContainsKey(name)) _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    private WebApplicationFactory<Program> Start()
    {
        Directory.CreateDirectory(_data);
        Set("DEXICON__STORAGE__DATAPATH", _data);
        Set("DEXICON__QDRANT__ENDPOINT", "http://127.0.0.1:1");
        Set("DEXICON__OLLAMA__ENDPOINT", "http://127.0.0.1:1");
        Set("DEXICON__OLLAMA__MAXRETRIES", "0");
        Set("DEXICON__INDEXING__REFRESHMINUTES", "0");
        Set("DEXICON__ADMIN__PASSWORD", Password);

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

    /// <summary>A corpus the signed-in session can search, with one chunk set and no points.</summary>
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

    [Theory]
    [InlineData("/api/search", "diff")]
    [InlineData("/api/context", "message")]
    public async Task TheEndpointAsksTheStoreForTheSectionItWasGiven(string path, string section)
    {
        SearchQuery? seen = null;
        _store.OnSearch = query =>
        {
            seen = query;
            return new SearchResponse { Hits = [] };
        };

        await using var factory = Start();
        await SeedAsync(factory);
        using var client = await SignedInAsync(factory);

        var response = await client.PostAsJsonAsync(path,
            new { query = "alpha", corpus = new[] { "history" }, mode = "keyword", section }, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        seen.ShouldNotBeNull().Section.ShouldBe(section);
    }

    [Theory]
    [InlineData("/api/search")]
    [InlineData("/api/context")]
    public async Task TheEndpointAsksForNoSectionWhenNoneIsGiven(string path)
    {
        SearchQuery? seen = null;
        _store.OnSearch = query =>
        {
            seen = query;
            return new SearchResponse { Hits = [] };
        };

        await using var factory = Start();
        await SeedAsync(factory);
        using var client = await SignedInAsync(factory);

        var response = await client.PostAsJsonAsync(path,
            new { query = "alpha", corpus = new[] { "history" }, mode = "keyword" }, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        seen.ShouldNotBeNull().Section.ShouldBeNull();
    }
}
