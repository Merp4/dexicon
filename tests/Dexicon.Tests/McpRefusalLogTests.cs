using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dexicon.Core.Configuration;
using Dexicon.Mcp;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Dexicon.Tests;

/// <summary>
/// A tool's refusal of what the caller sent reaches the caller as before and is not logged as an error. Before
/// <see cref="McpRefusals"/>, an unknown corpus name wrote '"search_index" threw an unhandled exception.' at Error
/// with a stack trace, on a live instance where the only error entries after a deploy were two such calls.
/// </summary>
[Collection(ConsoleOutputCollection.Name)]
public sealed class McpRefusalLogTests : IDisposable
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

    /// <summary>One <c>tools/call</c> to the MCP endpoint, without a handshake, and the <c>result</c> of its reply.</summary>
    private static async Task<JsonElement> CallAsync(HttpClient client, string token, string parameters)
    {
        using var content = new StringContent(
            $$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{{parameters}}}""", Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json = text.TrimStart().StartsWith('{')
            ? text
            : string.Join('\n', text.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].Trim()));
        return JsonDocument.Parse(json).RootElement.GetProperty("result").Clone();
    }

    private static string ToolText(JsonElement result) =>
        string.Concat(result.GetProperty("content").EnumerateArray().Select(c => c.GetProperty("text").GetString()));

    /// <summary>
    /// The host registers the filter, and a refusal reaches the caller with the text it had before. Whether the SDK
    /// then logs an error is not read from the console here: on the Linux CI runner the console captured during a
    /// host test held Dexicon's request lines and none of the SDK's, so an assertion on its absence passed without
    /// proving anything. The filter's own behaviour is tested by <see cref="ARefusalIsAnsweredAndLoggedAtDebug"/>.
    /// </summary>
    [Fact]
    public async Task TheHostRegistersTheFilterAndARefusalReachesTheCallerAsBefore()
    {
        await using var factory = Start();
        using var client = factory.CreateClient();
        var token = await SignInAsync(client);

        factory.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.Filters.Request.CallToolFilters
            .ShouldContain((McpRequestFilter<CallToolRequestParams, CallToolResult>)McpRefusals.Filter);

        // Refused in the tool body: a corpus nobody has.
        var unknown = await CallAsync(client, token, """{"name":"search_index","arguments":{"query":"x","corpus":"nosuch"}}""");
        // Refused while the arguments are bound, before the tool body runs.
        var shape = await CallAsync(client, token, """{"name":"search_index","arguments":{"query":"x","corpus":7}}""");

        unknown.GetProperty("isError").GetBoolean().ShouldBeTrue();
        ToolText(unknown).ShouldStartWith("An error occurred invoking 'search_index': ");
        ToolText(unknown).ShouldContain("'nosuch'");
        shape.GetProperty("isError").GetBoolean().ShouldBeTrue();
        ToolText(shape).ShouldBe("An error occurred invoking 'search_index': corpus takes a name or a list of names, not a number.");
    }

    /// <summary>
    /// The SDK logs an exception at Error only when it escapes the call-tool filters (<c>ToolCallError</c> in its
    /// <c>McpServerImpl</c>). A refusal the filter answers does not escape, and is logged at Debug instead.
    /// </summary>
    [Fact]
    public async Task ARefusalIsAnsweredAndLoggedAtDebug()
    {
        var log = new Entries();
        await using var services = new ServiceCollection()
            .AddLogging(b => b.AddProvider(log).SetMinimumLevel(LogLevel.Trace)).BuildServiceProvider();

        var result = await McpRefusals.RunAsync("search_index", services,
            () => throw new McpException("Unknown corpus 'nosuch'."));

        result.IsError.ShouldBe(true);
        result.Content.OfType<TextContentBlock>().ShouldHaveSingleItem().Text
            .ShouldBe("An error occurred invoking 'search_index': Unknown corpus 'nosuch'.");
        log.Written.ShouldHaveSingleItem().ShouldBe((LogLevel.Debug, "Dexicon.Mcp", "Refused search_index: Unknown corpus 'nosuch'."));
    }

    [Fact]
    public async Task AFaultEscapesTheFilterUnloggedSoTheSdkLogsIt()
    {
        var log = new Entries();
        await using var services = new ServiceCollection()
            .AddLogging(b => b.AddProvider(log).SetMinimumLevel(LogLevel.Trace)).BuildServiceProvider();

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await McpRefusals.RunAsync("search_index", services, () => throw new InvalidOperationException("the store went away")));
        await Should.ThrowAsync<McpProtocolException>(async () =>
            await McpRefusals.RunAsync("search_index", services, () => throw new McpProtocolException("Unknown tool", McpErrorCode.InvalidParams)));

        log.Written.ShouldBeEmpty();
    }

    [Fact]
    public async Task AToolThatSucceedsIsReturnedAsItIs()
    {
        var answer = new CallToolResult { Content = [new TextContentBlock { Text = "3 results" }] };

        (await McpRefusals.RunAsync("search_index", null, () => ValueTask.FromResult(answer))).ShouldBeSameAs(answer);
    }

    private sealed class Entries : ILoggerProvider
    {
        public List<(LogLevel Level, string Category, string Text)> Written { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Category(this, categoryName);

        public void Dispose() { }

        private sealed class Category(Entries owner, string name) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner.Written) owner.Written.Add((logLevel, name, formatter(state, exception)));
            }
        }
    }

    [Fact]
    public void ARefusalGetsTheTextTheSdkWritesForOne()
    {
        var result = McpRefusals.AsResult("get_context", new McpException("No indexed file 'a.md' in corpus 'docs'."));

        result.ShouldNotBeNull().IsError.ShouldBe(true);
        result.Content.OfType<TextContentBlock>().ShouldHaveSingleItem().Text
            .ShouldBe("An error occurred invoking 'get_context': No indexed file 'a.md' in corpus 'docs'.");
    }

    [Fact]
    public void AFaultAndAProtocolErrorGoOnToTheSdk()
    {
        // A fault is logged at Error by the SDK, and a protocol error is answered as a JSON-RPC error.
        McpRefusals.AsResult("search_index", new InvalidOperationException("the store went away")).ShouldBeNull();
        McpRefusals.AsResult("search_index", new McpProtocolException("Unknown tool", McpErrorCode.InvalidParams)).ShouldBeNull();
        McpRefusals.AsResult("search_index", new OperationCanceledException()).ShouldBeNull();
    }
}
