using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dexicon.Core.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Dexicon.Tests;

/// <summary>
/// What <c>POST /api/search</c> and <c>POST /api/context</c> answer to the phrase argument, signed in, on
/// a host whose vector store and embedding service are unreachable. Both refusals below come before either
/// is needed, which is also what the control shows: a phrase that is valid goes on to the corpus check.
/// </summary>
[Collection(ConsoleOutputCollection.Name)]
public sealed class PhraseEndpointTests : IDisposable
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

    private WebApplicationFactory<Program> Start()
    {
        Directory.CreateDirectory(_data);
        Set("DEXICON__STORAGE__DATAPATH", _data);
        Set("DEXICON__QDRANT__ENDPOINT", "http://127.0.0.1:1");
        Set("DEXICON__OLLAMA__ENDPOINT", "http://127.0.0.1:1");
        Set("DEXICON__OLLAMA__MAXRETRIES", "0");
        Set("DEXICON__INDEXING__REFRESHMINUTES", "0");
        Set("DEXICON__ADMIN__PASSWORD", Password);

        return new WebApplicationFactory<Program>();
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

    private static async Task<(HttpStatusCode Status, string Title, string Detail)> PostAsync(
        HttpClient client, string path, object body)
    {
        var login = await client.PostAsJsonAsync("/api/session", new { password = Password }, TestContext.Current.CancellationToken);
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("token").GetString()!;

        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        string Text(string name) => problem.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
        return (response.StatusCode, Text("title"), Text("detail"));
    }

    [Theory]
    [InlineData("/api/search")]
    [InlineData("/api/context")]
    public async Task ARequestWithNeitherAQueryNorAPhraseIsRefused(string path)
    {
        await using var factory = Start();
        using var client = factory.CreateClient();

        var (status, title, _) = await PostAsync(client, path, new { query = " ", phrase = "  " });

        status.ShouldBe(HttpStatusCode.BadRequest);
        title.ShouldBe("Query or phrase is required");
    }

    [Theory]
    [InlineData("/api/search")]
    [InlineData("/api/context")]
    public async Task APhraseAloneIsNotRefusedForWantingAQuery(string path)
    {
        // The control for the test above. The request gets past the blank-query check and stops at the
        // corpus check instead, since this catalogue holds no corpus.
        await using var factory = Start();
        using var client = factory.CreateClient();

        var (_, title, _) = await PostAsync(client, path, new { phrase = "refresh token" });

        title.ShouldNotBe("Query or phrase is required");
        title.ShouldNotBe("Invalid phrase");
    }

    [Theory]
    [InlineData("/api/search")]
    [InlineData("/api/context")]
    public async Task APhraseThatCannotMatchIsABadRequestThatSaysWhy(string path)
    {
        await using var factory = Start();
        using var client = factory.CreateClient();

        var (status, title, detail) = await PostAsync(client, path, new { query = "tokens", phrase = "=>" });

        status.ShouldBe(HttpStatusCode.BadRequest);
        title.ShouldBe("Invalid phrase");
        detail.ShouldContain("at least one letter or digit");
    }
}
