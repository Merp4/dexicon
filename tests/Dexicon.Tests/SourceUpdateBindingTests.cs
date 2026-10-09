using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using Dexicon.Api;
using Dexicon.Core.Catalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dexicon.Tests;

/// <summary>
/// A JSON body reaches <see cref="CorpusConfiguration.UpdateSourceAsync"/> through minimal-API binding, which the
/// tests that call the method with a built request do not exercise. The route here is the PATCH route's handler
/// body on a test server with the JSON options Program.cs sets, so it covers binding and the refusal's status,
/// and not the route's authentication, scope check or corpus lookup.
/// </summary>
public sealed class SourceUpdateBindingTests
{
    private static async Task<(HttpStatusCode Status, string Body)> PatchAsync(IndexingHarness harness, string json)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
        builder.WebHost.UseTestServer();

        // The options Program.cs sets for the JSON the API reads and writes.
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            o.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
        });

        await using var app = builder.Build();
        app.MapPatch("/sources/{sourceId}", async (string sourceId, UpdateSourceRequest body) =>
        {
            await using var db = harness.NewContext();
            var corpus = await db.Corpora.SingleAsync();
            var updated = await harness.NewConfiguration(db).UpdateSourceAsync(corpus, sourceId, body, default);
            return updated.Refusal is { } refused ? refused.ToResult() : Results.Ok();
        });

        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.PatchAsync(
            $"/sources/{IndexingHarness.SourceIdFor(0)}", new StringContent(json, Encoding.UTF8, "application/json"));

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("""{"clear":[null]}""")]
    [InlineData("""{"clear":["maxFileBytes",null]}""")]
    [InlineData("""{"maxFileBytes":1,"clear":[null]}""")]
    public async Task ANullEntryInClearIsAnsweredWith400AndChangesNothing(string json)
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);

        var (status, body) = await PatchAsync(harness, json);

        status.ShouldBe(HttpStatusCode.BadRequest);
        body.ShouldContain("Unknown filter");
        body.ShouldContain("A null entry is not a filter that can be cleared");
        await using var db = harness.NewContext();
        (await db.Sources.AsNoTracking().SingleAsync()).MaxFileBytes.ShouldBeNull();
    }

    [Fact]
    public async Task AnUnknownClearNameIsAnsweredWith400AndTheNamedFilterWording()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);

        var (status, body) = await PatchAsync(harness, """{"clear":["maxFileKb"]}""");

        status.ShouldBe(HttpStatusCode.BadRequest);
        body.ShouldContain("'maxFileKb' is not a filter that can be cleared. Name one of:");
    }

    [Fact]
    public async Task ANamedFilterInClearIsAccepted()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);

        var (status, _) = await PatchAsync(harness, """{"clear":["MaxFileBytes","includeGlobs"]}""");

        status.ShouldBe(HttpStatusCode.OK);
    }
}
