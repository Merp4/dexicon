using System.Net;
using System.Net.Http.Headers;
using Dexicon.Core.Configuration;
using Dexicon.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// The policy for a path, alone: which files are told they never change and which are told to ask.
/// </summary>
public sealed class StaticAssetCachingPolicy
{
    [Theory]
    [InlineData("/assets/index-CnRHqk6r.js")]
    [InlineData("/assets/index-DSJppzH7.css")]
    [InlineData("/assets/logo-AbCd_-12.svg")]
    [InlineData("/assets/inter-latin-Zz09Zz09.woff2")]
    public void AFileNamedWithAnEightCharacterHashUnderAssetsIsImmutable(string path) =>
        StaticAssetCaching.For(path).ShouldBe("public, max-age=31536000, immutable");

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/favicon.svg")]
    [InlineData("/corpora/e2e")]
    [InlineData("/assets/index.js")]
    [InlineData("/assets/index-short.js")]
    [InlineData("/assets/index-CnRHqk6r")]
    [InlineData("/assets/nested/index-CnRHqk6r.js")]
    [InlineData("/other/index-CnRHqk6r.js")]
    [InlineData("/prefix/assets/index-CnRHqk6r.js")]
    [InlineData("/assets/index-CnRHqk6rX.js")]
    [InlineData("/assets/index-CnRHqk6r.js\n")]
    public void EverythingElseIsToldToAskBeforeUsingItsCopy(string path) =>
        StaticAssetCaching.For(path).ShouldBe("no-cache");

    [Fact]
    public void ARequestWithNoPathIsToldToAsk() =>
        StaticAssetCaching.For(default(PathString)).ShouldBe("no-cache");
}

/// <summary>
/// The application started the way it is deployed, with a web root of three kinds of file. The policy is a header that
/// the static file middleware has to apply; a unit test of the function does not show that the middleware calls it, and
/// the answer to a conditional request (304) has to carry the header too.
/// </summary>
[Collection(ConsoleOutputCollection.Name)]
public sealed class StaticAssetCachingHost : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), $"dexicon-host-{Guid.NewGuid():N}");
    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), $"dexicon-web-{Guid.NewGuid():N}");
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
        Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));
        File.WriteAllText(Path.Combine(_webRoot, "index.html"),
            "<!doctype html><title>Dexicon</title><script type=\"module\" src=\"/assets/app-AbCdEf12.js\"></script>");
        File.WriteAllText(Path.Combine(_webRoot, "assets", "app-AbCdEf12.js"), "console.log('app');");
        File.WriteAllText(Path.Combine(_webRoot, "assets", "plain.js"), "console.log('plain');");
        File.WriteAllText(Path.Combine(_webRoot, "favicon.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");

        Set("DEXICON__STORAGE__DATAPATH", _data);
        Set("DEXICON__QDRANT__ENDPOINT", "http://127.0.0.1:1");
        Set("DEXICON__OLLAMA__ENDPOINT", "http://127.0.0.1:1");
        Set("DEXICON__OLLAMA__MAXRETRIES", "0");
        Set("DEXICON__INDEXING__REFRESHMINUTES", "0");
        Set("DEXICON__ADMIN__PASSWORD", "a-test-password-for-this-run");

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

    private static string CacheControl(HttpResponseMessage response) =>
        string.Join(", ", response.Headers.GetValues("Cache-Control"));

    [Fact]
    public async Task EachKindOfFileIsServedWithItsOwnCacheControl()
    {
        await using var factory = Start();
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var root = await client.GetAsync("/", ct);
        root.StatusCode.ShouldBe(HttpStatusCode.OK);
        root.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        CacheControl(root).ShouldBe("no-cache", "the page that names the bundle is revalidated on every load");

        var index = await client.GetAsync("/index.html", ct);
        CacheControl(index).ShouldBe("no-cache", "the same file by its own name");

        var bundle = await client.GetAsync("/assets/app-AbCdEf12.js", ct);
        bundle.StatusCode.ShouldBe(HttpStatusCode.OK);
        CacheControl(bundle).ShouldBe("public, max-age=31536000, immutable", "its name carries a hash of its content");

        var unhashed = await client.GetAsync("/assets/plain.js", ct);
        unhashed.StatusCode.ShouldBe(HttpStatusCode.OK);
        CacheControl(unhashed).ShouldBe("no-cache", "a name that does not carry a hash can be replaced");

        var favicon = await client.GetAsync("/favicon.svg", ct);
        CacheControl(favicon).ShouldBe("no-cache");

        var missing = await client.GetAsync("/assets/app-ZZZZZZZZ.js", ct);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        missing.Headers.CacheControl.ShouldBeNull("an old bundle's name after a deploy is a 404, and a 404 is not cached for a year");
    }

    [Fact]
    public async Task AHeadRequestCarriesTheSameCacheControlAsTheGet()
    {
        await using var factory = Start();
        using var client = factory.CreateClient();

        foreach (var (path, expected) in new[]
        {
            ("/", "no-cache"),
            ("/assets/app-AbCdEf12.js", "public, max-age=31536000, immutable"),
        })
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, path);
            var response = await client.SendAsync(head, TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
            CacheControl(response).ShouldBe(expected, $"HEAD {path}");
        }
    }

    [Fact]
    public async Task AnErrorAnsweredForAHashedFileIsNotGivenAYearOfCaching()
    {
        await using var factory = Start();
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var range = new HttpRequestMessage(HttpMethod.Get, "/assets/app-AbCdEf12.js");
        range.Headers.Range = new RangeHeaderValue(99_999, null);
        var unsatisfiable = await client.SendAsync(range, ct);
        unsatisfiable.StatusCode.ShouldBe(HttpStatusCode.RequestedRangeNotSatisfiable);
        (unsatisfiable.Headers.CacheControl?.ToString() ?? "").Contains("immutable").ShouldBeFalse("a 416 says nothing about the file");

        using var precondition = new HttpRequestMessage(HttpMethod.Get, "/assets/app-AbCdEf12.js");
        precondition.Headers.IfMatch.Add(new EntityTagHeaderValue("\"not-the-etag\""));
        var failed = await client.SendAsync(precondition, ct);
        failed.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (failed.Headers.CacheControl?.ToString() ?? "").Contains("immutable").ShouldBeFalse("a 412 says nothing about the file");
    }

    [Fact]
    public async Task AnUnchangedFileAnsweredWith304StillCarriesTheCacheControl()
    {
        await using var factory = Start();
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        foreach (var (path, expected) in new[]
        {
            ("/", "no-cache"),
            ("/assets/app-AbCdEf12.js", "public, max-age=31536000, immutable"),
        })
        {
            var first = await client.GetAsync(path, ct);
            var etag = first.Headers.ETag.ShouldNotBeNull($"{path} has an ETag");

            using var again = new HttpRequestMessage(HttpMethod.Get, path);
            again.Headers.IfNoneMatch.Add(etag);
            var revalidated = await client.SendAsync(again, ct);

            revalidated.StatusCode.ShouldBe(HttpStatusCode.NotModified, path);
            CacheControl(revalidated).ShouldBe(expected, $"{path} answered 304");
        }
    }
}
