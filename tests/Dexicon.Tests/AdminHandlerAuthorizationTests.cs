using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// Seven handlers that create or change keys, corpora, chunk sets and model measurements are methods a test can call:
/// <c>SystemEndpoints.CreateTokenAsync</c>, <c>ReplaceCorporaAsync</c> and <c>ProbeModelAsync</c>, <c>CorpusEndpoints.CreateAsync</c>
/// and <c>UpdateAsync</c>, and <c>ChunkSetEndpoints.CreateAsync</c> and <c>UpdateAsync</c>. Each is called here
/// with every scope but admin and has to answer 403 and leave the catalogue as it was. The admin routes still
/// written as lambdas in the route table (deleting a key or a corpus, a key's scopes, the source routes and
/// chunk-set promotion and removal) are not covered.
/// </summary>
public sealed class AdminHandlerAuthorizationTests
{
    private static RequestContext WithEveryScopeButAdmin() => new()
    {
        Principal = new Principal("k", "agent", new HashSet<string>(StringComparer.Ordinal)
        {
            Scopes.Search, Scopes.Ingest, Scopes.Configure, Scopes.Propose, Scopes.Destroy,
        }),
    };

    [Fact]
    public async Task EveryHandlerThatChangesKeysCorporaOrChunkSetsRefusesAKeyWithoutAdmin()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await using var db = harness.NewContext();
        var rc = WithEveryScopeButAdmin();
        var scopes = new ScopeResolver(db);
        var before = (Tokens: await db.Tokens.CountAsync(), Corpora: await db.Corpora.CountAsync(),
            Sets: await db.ChunkSets.CountAsync(), Measurements: await db.ModelMeasurements.CountAsync());

        // The dependencies that are null are never reached: the scope is checked first. A change is a
        // description, so an update that got through would show.
        var handlers = new Dictionary<string, Func<Task<IResult>>>
        {
            ["SystemEndpoints.CreateTokenAsync"] = () => SystemEndpoints.CreateTokenAsync(
                new CreateTokenRequest("agent"), rc, null!, db, default),
            ["SystemEndpoints.ReplaceCorporaAsync"] = () => SystemEndpoints.ReplaceCorporaAsync(
                "any", new UpdateTokenCorporaRequest([]), rc, db, default),
            ["SystemEndpoints.ProbeModelAsync"] = () => SystemEndpoints.ProbeModelAsync(
                new ProbeModelRequest("nomic-embed-text"), rc, null!, db, null!, default),
            ["CorpusEndpoints.CreateAsync"] = () => CorpusEndpoints.CreateAsync(
                new CreateCorpusRequest("papers"), rc, db, null!, null!, default),
            ["CorpusEndpoints.UpdateAsync"] = () => CorpusEndpoints.UpdateAsync(
                IndexingHarness.CorpusId, new UpdateCorpusRequest("changed"), rc, scopes, db, null!, null!, default),
            ["ChunkSetEndpoints.CreateAsync"] = () => ChunkSetEndpoints.CreateAsync(
                IndexingHarness.CorpusId, new CreateChunkSetRequest("fine"), rc, scopes, db, null!, null!, null!, null!, default),
            ["ChunkSetEndpoints.UpdateAsync"] = () => ChunkSetEndpoints.UpdateAsync(
                IndexingHarness.CorpusId, "default", new UpdateChunkSetRequest("changed"), rc, scopes, db, null!, default),
        };

        foreach (var (name, call) in handlers)
            ((IStatusCodeHttpResult)await call()).StatusCode.ShouldBe(403, $"{name} has to refuse a key without admin");

        (await db.Tokens.CountAsync()).ShouldBe(before.Tokens);
        (await db.Corpora.CountAsync()).ShouldBe(before.Corpora);
        (await db.ChunkSets.CountAsync()).ShouldBe(before.Sets);
        (await db.ModelMeasurements.CountAsync()).ShouldBe(before.Measurements);
        (await db.Corpora.AsNoTracking().Select(c => c.Description).ToListAsync()).ShouldNotContain("changed");
        (await db.ChunkSets.AsNoTracking().Select(s => s.Description).ToListAsync()).ShouldNotContain("changed");
    }

    [Fact]
    public async Task AKeyWithAdminReachesTheBodyOfTheModelProbe()
    {
        // The control for the test above: with admin the scope check passes, so the blank model is what is refused.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await using var db = harness.NewContext();
        var admin = new RequestContext
        {
            Principal = new Principal("k", "admin", new HashSet<string>(StringComparer.Ordinal) { Scopes.Admin }),
        };

        var result = await SystemEndpoints.ProbeModelAsync(new ProbeModelRequest(" "), admin, null!, db, null!, default);

        ((IStatusCodeHttpResult)result).StatusCode.ShouldBe(400);
    }
}
