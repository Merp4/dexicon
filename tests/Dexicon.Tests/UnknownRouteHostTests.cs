using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dexicon.Core.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// What the application answers on a path it does not serve, or serves under another method, signed in, with the built
/// UI in the web root. The page is served as <c>/</c> and <c>/index.html</c> and no other path (the UI keeps its screen in the URL fragment), so
/// routing's own answers stand: 404 for a path nothing serves, and 405 with the <c>Allow</c> of the endpoints that do
/// exist for a path served under another method. A catch-all that answered every other GET with index.html and a 200
/// made <c>GET /mcp</c> read as a working endpoint, although the stateless transport takes POST only.
/// </summary>
[Collection(ConsoleOutputCollection.Name)]
public sealed class UnknownRouteHost : IDisposable
{
    private const string Password = "a-test-password-for-this-run";

    private readonly string _data = Path.Combine(Path.GetTempPath(), $"dexicon-host-{Guid.NewGuid():N}");
    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), $"dexicon-web-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> _saved = [];
    private readonly TextWriter _console = Console.Out;

    private void Set(string name, string? value)
    {
        if (!_saved.ContainsKey(name)) _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    private WebApplicationFactory<Program> Start(bool builtUi = true)
    {
        Directory.CreateDirectory(_data);
        Directory.CreateDirectory(_webRoot);
        if (builtUi) File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><title>Dexicon</title>");

        Set("DEXICON__STORAGE__DATAPATH", _data);
        Set("DEXICON__QDRANT__ENDPOINT", "http://127.0.0.1:1");
        Set("DEXICON__OLLAMA__ENDPOINT", "http://127.0.0.1:1");
        Set("DEXICON__OLLAMA__MAXRETRIES", "0");
        Set("DEXICON__INDEXING__REFRESHMINUTES", "0");
        Set("DEXICON__ADMIN__PASSWORD", Password);

        return new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseWebRoot(_webRoot));
    }

    public void Dispose()
    {
        Console.SetOut(_console);
        Serilog.Log.CloseAndFlush();
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={new StorageOptions { DataPath = _data }.CatalogPath}"));

        foreach (var dir in new[] { _data, _webRoot })
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
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
        var body = await login.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return body.GetProperty("token").GetString()!;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? token, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("GET", "/mcp")]
    [InlineData("HEAD", "/mcp")]
    [InlineData("DELETE", "/mcp")]
    [InlineData("PUT", "/mcp")]
    [InlineData("GET", "/MCP")]
    public async Task TheMcpEndpointRefusesEveryMethodButPostAndNamesPost(string method, string path)
    {
        await using var factory = Start();
        using var client = factory.CreateClient();
        var token = await SignInAsync(client);

        var response = await SendAsync(client, new HttpMethod(method), path, token);

        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed, $"{method} {path}");
        response.Content.Headers.ContentType?.MediaType.ShouldNotBe("text/html", $"{method} {path} is not answered with the UI page");
        response.Content.Headers.Allow.ShouldBe(["POST"], $"{method} {path} names the one method the transport takes");
    }

    [Fact]
    public async Task APostToTheMcpEndpointIsStillServed()
    {
        await using var factory = Start();
        using var client = factory.CreateClient();
        var token = await SignInAsync(client);

        using var content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("search_index");
    }

    [Theory]
    [InlineData("/api")]
    [InlineData("/api/nope")]
    [InlineData("/api/corpora/nope/nope")]
    [InlineData("/API/nope")]
    [InlineData("/healthz/nope")]
    [InlineData("/assets/nope")]
    [InlineData("/openapi/nope")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/mcp/nope")]
    // The UI keeps its screen in the fragment, so it asks for none of these: they are paths like any other.
    [InlineData("/corpora/e2e")]
    [InlineData("/search")]
    [InlineData("/apiary")]
    public async Task APathNothingServesIsA404NotThePage(string path)
    {
        await using var factory = Start();
        using var client = factory.CreateClient();
        var token = await SignInAsync(client);

        var response = await SendAsync(client, HttpMethod.Get, path, token);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, path);
        response.Content.Headers.ContentType?.MediaType.ShouldNotBe("text/html", path);

        var head = await SendAsync(client, HttpMethod.Head, path, token);
        head.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"HEAD {path}");
    }

    [Fact]
    public async Task AnApiErrorForAnUnknownPathIsProblemJson()
    {
        await using var factory = Start();
        using var client = factory.CreateClient();
        var token = await SignInAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/nope");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    public async Task ThePageIsServedAtTheRootAndByName(string path)
    {
        await using var factory = Start();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html", path);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("<title>Dexicon</title>");
    }

    [Fact]
    public async Task AWrongMethodOnAnApiPathNamesTheMethodItDoesTake()
    {
        await using var factory = Start();
        using var client = factory.CreateClient();
        var token = await SignInAsync(client);

        // /api/search takes POST only. A GET was answered with the page, and a PUT named GET and HEAD.
        var get = await SendAsync(client, HttpMethod.Get, "/api/search", token);
        get.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed, "GET /api/search");
        get.Content.Headers.Allow.ShouldBe(["POST"], "GET /api/search");

        var put = await SendAsync(client, HttpMethod.Put, "/api/search", token, new StringContent("{}", Encoding.UTF8, "application/json"));
        put.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed, "PUT /api/search");
        put.Content.Headers.Allow.ShouldBe(["POST"], "PUT /api/search");
    }

    [Fact]
    public async Task APostToAnUnknownApiPathIs404()
    {
        await using var factory = Start();
        using var client = factory.CreateClient();
        var token = await SignInAsync(client);

        // It was a 405 with Allow: GET, HEAD, which are the methods of the catch-all and not of any path under /api/nope.
        var response = await SendAsync(client, HttpMethod.Post, "/api/nope", token, new StringContent("{}", Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AnInstanceWithoutTheBuiltUiSaysSoAtTheRootAndKeepsTheSameBoundaryElsewhere()
    {
        await using var factory = Start(builtUi: false);
        using var client = factory.CreateClient();
        var token = await SignInAsync(client);

        var notice = await client.GetAsync("/", TestContext.Current.CancellationToken);
        notice.StatusCode.ShouldBe(HttpStatusCode.OK, "the notice that the UI is not built");
        (await notice.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("has not been built");

        using var head = new HttpRequestMessage(HttpMethod.Head, "/");
        (await client.SendAsync(head, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK, "HEAD / answers as GET / does");

        (await SendAsync(client, HttpMethod.Get, "/corpora/e2e", token)).StatusCode.ShouldBe(HttpStatusCode.NotFound, "GET /corpora/e2e");
        (await SendAsync(client, HttpMethod.Get, "/api/nope", token)).StatusCode.ShouldBe(HttpStatusCode.NotFound, "GET /api/nope");
        (await SendAsync(client, HttpMethod.Get, "/mcp", token)).StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed, "GET /mcp");
    }

    [Fact]
    public async Task APathThatIsServedIsStillServed()
    {
        await using var factory = Start();
        using var client = factory.CreateClient();
        var token = await SignInAsync(client);

        // Not /healthz/ready: it answers 503 here, where no vector store is reachable.
        foreach (var path in new[] { "/api/corpora", "/healthz", "/healthz/live" })
        {
            var response = await SendAsync(client, HttpMethod.Get, path, token);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
        }

        var document = await SendAsync(client, HttpMethod.Get, "/openapi/integration.json", token);
        document.StatusCode.ShouldBe(HttpStatusCode.OK, "/openapi/integration.json");
    }

    [Fact]
    public async Task AnUnsignedRequestIsStill401NotA404()
    {
        await using var factory = Start();
        using var client = factory.CreateClient();

        foreach (var path in new[] { "/api/nope", "/mcp", "/corpora/e2e" })
            (await SendAsync(client, HttpMethod.Get, path, token: null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, $"unsigned GET {path}");
    }
}
