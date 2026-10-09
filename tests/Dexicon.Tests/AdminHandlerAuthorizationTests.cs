using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// The handlers that create and change keys, corpora and chunk sets refuse a key without the admin scope
/// where they act. They were lambdas inside the route table, with no test calling them; moved into methods
/// of their own so a test can, each is called here with every scope but admin, and has to answer 403
/// without touching the catalogue.
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
            Sets: await db.ChunkSets.CountAsync());

        // The dependencies that are null are never reached: the scope is checked first.
        var answers = new Dictionary<string, IResult>
        {
            ["SystemEndpoints.CreateTokenAsync"] = await SystemEndpoints.CreateTokenAsync(
                new CreateTokenRequest("agent"), rc, null!, db, default),
            ["SystemEndpoints.ReplaceCorporaAsync"] = await SystemEndpoints.ReplaceCorporaAsync(
                "any", new UpdateTokenCorporaRequest([]), rc, db, default),
            ["CorpusEndpoints.CreateAsync"] = await CorpusEndpoints.CreateAsync(
                new CreateCorpusRequest("papers"), rc, db, null!, null!, default),
            ["CorpusEndpoints.UpdateAsync"] = await CorpusEndpoints.UpdateAsync(
                IndexingHarness.CorpusId, new UpdateCorpusRequest(), rc, scopes, db, null!, null!, default),
            ["ChunkSetEndpoints.CreateAsync"] = await ChunkSetEndpoints.CreateAsync(
                IndexingHarness.CorpusId, new CreateChunkSetRequest("fine"), rc, scopes, db, null!, null!, null!, null!, default),
            ["ChunkSetEndpoints.UpdateAsync"] = await ChunkSetEndpoints.UpdateAsync(
                IndexingHarness.CorpusId, "default", new UpdateChunkSetRequest(), rc, scopes, db, null!, default),
        };

        foreach (var (handler, answer) in answers)
            ((IStatusCodeHttpResult)answer).StatusCode.ShouldBe(403, $"{handler} has to refuse a key without admin");

        (await db.Tokens.CountAsync()).ShouldBe(before.Tokens);
        (await db.Corpora.CountAsync()).ShouldBe(before.Corpora);
        (await db.ChunkSets.CountAsync()).ShouldBe(before.Sets);
    }
}
