using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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

    private static async Task<string> SignInAsync(HttpClient client)
    {
        var login = await client.PostAsJsonAsync("/api/session", new { password = Password }, TestContext.Current.CancellationToken);
        login.EnsureSuccessStatusCode();
        return (await login.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("token").GetString()!;
    }

    /// <summary>One JSON-RPC call to the MCP endpoint, without a handshake, and the <c>result</c> of its reply.</summary>
    private static async Task<JsonElement> McpAsync(HttpClient client, string token, string method, string parameters)
    {
        using var content = new StringContent(
            $$"""{"jsonrpc":"2.0","id":1,"method":"{{method}}","params":{{parameters}}}""", Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The reply is one JSON object, or one event-stream frame whose data line holds it.
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json = text.TrimStart().StartsWith('{')
            ? text
            : string.Join('\n', text.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].Trim()));
        return JsonDocument.Parse(json).RootElement.GetProperty("result").Clone();
    }

    private static string ToolText(JsonElement result) =>
        string.Concat(result.GetProperty("content").EnumerateArray().Select(c => c.GetProperty("text").GetString()));

    private static async Task<(HttpStatusCode Status, string Title, string Detail)> PostAsync(
        HttpClient client, string path, object body)
    {
        var token = await SignInAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        string Text(string name) => problem.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
        return (response.StatusCode, Text("title"), Text("detail"));
    }

    [Fact]
    public async Task TheSearchToolNeedsItsQueryKeyAndTakesAnEmptyStringForASearchByPhraseAlone()
    {
        // The descriptions say that query is always required and that an empty string searches by phrase alone.
        // Leaving the key out is refused by the tool binding, before the tool runs, with a message that gives
        // no reason, so a description that said query may be empty would send a client into it.
        await using var factory = Start();
        using var client = factory.CreateClient();
        var token = await SignInAsync(client);

        var tools = await McpAsync(client, token, "tools/list", "{}");
        var search = tools.GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "search_index");
        var schema = search.GetProperty("inputSchema");
        schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ShouldBe(["query"]);

        var queryText = schema.GetProperty("properties").GetProperty("query").GetProperty("description").GetString()!;
        queryText.ShouldContain("Always required");
        queryText.ShouldContain("empty string");
        schema.GetProperty("properties").GetProperty("phrase").GetProperty("description").GetString()!
            .ShouldContain("Send query as an empty string");

        // An empty string is accepted: the call reaches the phrase check, which refuses this phrase by name.
        var empty = await McpAsync(client, token, "tools/call",
            """{"name":"search_index","arguments":{"query":"","phrase":"=>"}}""");
        empty.GetProperty("isError").GetBoolean().ShouldBeTrue();
        ToolText(empty).ShouldContain("at least one letter or digit");

        // Leaving the key out is not: it never reaches the phrase check.
        var absent = await McpAsync(client, token, "tools/call",
            """{"name":"search_index","arguments":{"phrase":"=>"}}""");
        absent.GetProperty("isError").GetBoolean().ShouldBeTrue();
        ToolText(absent).ShouldNotContain("at least one letter or digit");
        ToolText(absent).ShouldStartWith("An error occurred invoking 'search_index'");
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
