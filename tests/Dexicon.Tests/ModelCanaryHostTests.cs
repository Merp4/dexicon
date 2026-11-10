using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Embedding;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// The application started the way it is deployed, with a catalogue that holds two corpora that each have a chunk set
/// whose model has changed. The unit tests call the canary query and the service directly; this is the wiring between
/// them and the process: the service is one of the host's hosted services, and GET /healthz lists the changed sets of
/// the corpora the caller reaches and no others.
/// </summary>
[Collection(ConsoleOutputCollection.Name)]
public sealed class ModelCanaryHostTests : IDisposable
{
    private const string Password = "a-test-password-for-this-run";

    private readonly string _data = Path.Combine(Path.GetTempPath(), $"dexicon-host-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> _saved = [];
    private readonly TextWriter _console = Console.Out;

    private void Set(string name, string? value)
    {
        if (!_saved.ContainsKey(name)) _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    /// <summary>
    /// Environment for a start that reaches nothing outside the machine and keeps its data in a temp folder, then the
    /// catalogue: two corpora, each with a set below the canary threshold and one in alpha that is not.
    /// </summary>
    private async Task SeedAsync()
    {
        Directory.CreateDirectory(_data);
        Set("DEXICON__STORAGE__DATAPATH", _data);
        Set("DEXICON__QDRANT__ENDPOINT", "http://127.0.0.1:1");
        Set("DEXICON__OLLAMA__ENDPOINT", "http://127.0.0.1:1");
        Set("DEXICON__OLLAMA__MAXRETRIES", "0");
        Set("DEXICON__INDEXING__REFRESHMINUTES", "0");
        Set("DEXICON__ADMIN__PASSWORD", Password);

        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite($"Data Source={new StorageOptions { DataPath = _data }.CatalogPath};Pooling=False").Options;
        await using var db = new CatalogDbContext(options);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        foreach (var (id, name) in new[] { ("c-alpha", "alpha"), ("c-beta", "beta") })
            db.Corpora.Add(new Corpus { Id = id, Name = name, CreatedUtc = DateTime.UtcNow });

        // embeddinggemma's built-in document template is `title: none | text: {text}`: a set recorded as `{text}` was
        // embedded raw, and one recorded with the template was not.
        AddSet(db, "s-alpha-changed", "c-alpha", "changed", similarity: 0.5, framing: "{text}");
        AddSet(db, "s-alpha-same", "c-alpha", "same", similarity: 1.0, framing: "title: none | text: {text}");
        AddSet(db, "s-beta-changed", "c-beta", "changed", similarity: 0.25, framing: "{text}");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static void AddSet(
        CatalogDbContext db, string id, string corpusId, string name, double similarity, string framing) =>
        db.ChunkSets.Add(new ChunkSet
        {
            Id = id,
            CorpusId = corpusId,
            Name = name,
            EmbeddingModel = "embeddinggemma",
            EmbeddingProvider = "ollama",
            CollectionName = $"dexicon_{id}",
            BoundaryMode = "blank-line",
            CreatedUtc = DateTime.UtcNow,
            CanaryVector = ModelCanary.Pack([1f, 0f, 0f, 0f]),
            CanarySimilarity = similarity,
            CanaryCheckedUtc = DateTime.UtcNow,
            FramingRecorded = framing,
        });

    public void Dispose()
    {
        Console.SetOut(_console);
        Serilog.Log.CloseAndFlush();
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        // The host's catalogue connection is pooled and keeps catalog.db open after the host is disposed.
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={new StorageOptions { DataPath = _data }.CatalogPath}"));

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(_data)) Directory.Delete(_data, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }

    private static async Task<string[]> ChangedSetsAsync(
        HttpClient client, string bearer, string field = "changedModels")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return [.. body.GetProperty(field).EnumerateArray().Select(e => e.GetProperty("set").GetString()!)];
    }

    [Fact]
    public async Task TheHostRunsTheModelCanaryService()
    {
        await SeedAsync();
        await using var factory = new WebApplicationFactory<Program>();

        factory.Services.GetServices<IHostedService>().OfType<ModelCanaryService>().ShouldHaveSingleItem();
    }

    /// <summary>An administrator's session and a search key restricted to the alpha corpus.</summary>
    private static async Task<(string Admin, string Key)> AdminAndAlphaKeyAsync(HttpClient client)
    {
        var login = await client.PostAsJsonAsync("/api/session", new { password = Password }, TestContext.Current.CancellationToken);
        login.EnsureSuccessStatusCode();
        var admin = (await login.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("token").GetString()!;

        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/tokens")
        {
            Content = JsonContent.Create(new { name = "reader", scopes = new[] { "search" }, corpusIds = new[] { "c-alpha" }, allCorpora = false }),
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        using var created = await client.SendAsync(create, TestContext.Current.CancellationToken);
        created.EnsureSuccessStatusCode();
        var key = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("secret").GetString()!;

        return (admin, key);
    }

    [Fact]
    public async Task HealthListsTheChangedSetsOfTheCorporaTheCallerReachesAndNoOthers()
    {
        await SeedAsync();
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var (admin, key) = await AdminAndAlphaKeyAsync(client);

        // An administrator reaches every corpus; the set that is not below the threshold is not listed.
        (await ChangedSetsAsync(client, admin)).ShouldBe(["alpha:changed", "beta:changed"]);

        // A key restricted to alpha learns nothing of beta.
        (await ChangedSetsAsync(client, key)).ShouldBe(["alpha:changed"]);
    }

    [Fact]
    public async Task HealthListsTheSetsEmbeddedUnderAnotherFramingOfTheCorporaTheCallerReachesAndNoOthers()
    {
        await SeedAsync();
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var (admin, key) = await AdminAndAlphaKeyAsync(client);

        // The profile is read from the running host, which gives embeddinggemma its built-in template: the set that
        // was recorded with that template is not listed.
        (await ChangedSetsAsync(client, admin, "changedFramings")).ShouldBe(["alpha:changed", "beta:changed"]);
        (await ChangedSetsAsync(client, key, "changedFramings")).ShouldBe(["alpha:changed"]);
    }
}
