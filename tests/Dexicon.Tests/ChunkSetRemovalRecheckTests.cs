using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Indexing;
using Dexicon.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// A chunk set removal checks the set again under the lock, after its vectors are gone. What it refuses then
/// leaves the set in place without its vectors, and the refusal names the set so that its caller queues a
/// refresh to index it again. The removal itself queues nothing: queuing saves the context, which a proposal
/// being approved shares with the removal.
/// </summary>
[Collection(nameof(AttachmentLockCollection))]
public sealed class ChunkSetRemovalRecheckTests
{
    private static async Task<IndexingHarness> StartAsync(int sets)
    {
        var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload, sets);
        return harness;
    }

    [Fact]
    public async Task ASetPromotedToTheDefaultWhileItsVectorsAreDeletedIsKept()
    {
        await using var harness = await StartAsync(sets: 2);
        await using var removing = harness.NewContext();
        harness.Vectors.OnDeleteAsync = async () =>
        {
            await using var other = harness.NewContext();
            await other.ChunkSets.Where(s => s.Id == "set-2").ExecuteUpdateAsync(u => u.SetProperty(s => s.IsDefault, true));
            await other.ChunkSets.Where(s => s.Id == "set-1").ExecuteUpdateAsync(u => u.SetProperty(s => s.IsDefault, false));
        };

        var config = harness.NewConfiguration(removing);
        var corpus = await removing.Corpora.SingleAsync();
        var outcome = await config.RemoveChunkSetAsync(corpus, "alt-1", default);

        outcome.Refusal.ShouldNotBeNull().Status.ShouldBe(409);
        outcome.Refusal.Title.ShouldBe("Cannot delete the default chunk set");
        outcome.Refusal.KeptSetId.ShouldBe("set-2");
        await using var check = harness.NewContext();
        (await check.ChunkSets.CountAsync()).ShouldBe(2);
        (await check.ChunkSets.CountAsync(s => s.IsDefault)).ShouldBe(1, "a corpus keeps a default set");
        (await check.Jobs.CountAsync()).ShouldBe(0, "the removal queued nothing");

        await config.FollowUpAsync(corpus, outcome.Refusal);

        var queued = await check.Jobs.SingleAsync();
        (queued.State, queued.Kind, queued.ChunkSetId).ShouldBe((JobState.Queued, JobKind.Refresh, "set-2"));
    }

    [Fact]
    public async Task ASetThatBecomesTheOnlyOneWhileItsVectorsAreDeletedIsKept()
    {
        await using var harness = await StartAsync(sets: 2);
        await using var removing = harness.NewContext();
        harness.Vectors.OnDeleteAsync = async () =>
        {
            await using var other = harness.NewContext();
            await other.ChunkSets.Where(s => s.Id == "set-1").ExecuteDeleteAsync();
        };

        var config = harness.NewConfiguration(removing);
        var corpus = await removing.Corpora.SingleAsync();
        var outcome = await config.RemoveChunkSetAsync(corpus, "alt-1", default);

        outcome.Refusal.ShouldNotBeNull().Title.ShouldBe("Cannot delete the only chunk set");
        await config.FollowUpAsync(corpus, outcome.Refusal);
        await using var check = harness.NewContext();
        (await check.ChunkSets.Select(s => s.Id).ToListAsync()).ShouldBe(["set-2"]);
        (await check.Jobs.CountAsync(j => j.ChunkSetId == "set-2")).ShouldBe(1);
    }

    [Fact]
    public async Task ARemovalThatSucceedsQueuesNothingAndFollowUpDoesNothingForIt()
    {
        await using var harness = await StartAsync(sets: 2);
        await using var removing = harness.NewContext();
        var config = harness.NewConfiguration(removing);
        var corpus = await removing.Corpora.SingleAsync();

        var outcome = await config.RemoveChunkSetAsync(corpus, "alt-1", default);

        outcome.Refusal.ShouldBeNull();
        await using var check = harness.NewContext();
        (await check.Jobs.CountAsync()).ShouldBe(0, "a set that is gone has nothing to refresh");
        await config.FollowUpAsync(corpus, new ConfigRefusal("any", "any", 409));
        (await check.Jobs.CountAsync()).ShouldBe(0, "a refusal that names no set asks for nothing");
    }

    [Fact]
    public async Task APromotionWaitsForARemovalThatHasCheckedTheSetAndThenFindsTheSetGone()
    {
        // The removal has read the set as not the default and is about to delete its row. A promotion of the
        // same set made in between would be deleted with the row, and the corpus would have no default.
        var hold = new HoldOneLookup(sql => sql.Contains("DELETE FROM \"chunk_sets\"", StringComparison.Ordinal));
        await using var harness = await IndexingHarness.StartAsync(hold, "notes");
        await harness.SeedCorpusAsync(SourceKind.Upload, sets: 2);
        await using var removing = harness.NewContext();
        await using var promoting = harness.NewContext();
        var admin = new RequestContext
        {
            Principal = new Principal("k", "admin", new HashSet<string>(StringComparer.Ordinal) { Scopes.Admin }),
        };
        hold.Armed = true;

        var removal = Task.Run(async () => await harness.NewConfiguration(removing)
            .RemoveChunkSetAsync(await removing.Corpora.SingleAsync(), "alt-1", default));
        bool promotedWhileHeld;
        Task<IResult> promotion;
        try
        {
            await Gates.ReachedAsync(hold.Reached, "the delete of the set");
            promotion = ChunkSetEndpoints.PromoteAsync(
                IndexingHarness.CorpusId, "alt-1", admin, new ScopeResolver(promoting), promoting, default);
            promotedWhileHeld = await Task.WhenAny(promotion, Task.Delay(TimeSpan.FromSeconds(3))) == promotion;
        }
        finally { hold.Release(); }

        (await removal.FinishesAsync("the removal")).Refusal.ShouldBeNull();
        var result = await promotion.FinishesAsync("the promotion");

        promotedWhileHeld.ShouldBeFalse("the promotion ran between the removal's check and its delete");
        result.ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(404);
        await using var check = harness.NewContext();
        var left = await check.ChunkSets.ToListAsync();
        (left.Single().Id, left.Single().IsDefault).ShouldBe(("set-1", true));
    }

    [Fact]
    public async Task TheDeleteRouteQueuesTheRefreshOfASetItKept()
    {
        await using var harness = await StartAsync(sets: 2);
        await using var removing = harness.NewContext();
        harness.Vectors.OnDeleteAsync = async () =>
        {
            await using var other = harness.NewContext();
            await other.ChunkSets.Where(s => s.Id == "set-2").ExecuteUpdateAsync(u => u.SetProperty(s => s.IsDefault, true));
            await other.ChunkSets.Where(s => s.Id == "set-1").ExecuteUpdateAsync(u => u.SetProperty(s => s.IsDefault, false));
        };
        var admin = new RequestContext
        {
            Principal = new Principal("k", "admin", new HashSet<string>(StringComparer.Ordinal) { Scopes.Admin }),
        };

        var result = await ChunkSetEndpoints.RemoveAsync(
            IndexingHarness.CorpusId, "alt-1", admin, new ScopeResolver(removing), harness.NewConfiguration(removing), default);

        result.ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(409);
        await using var check = harness.NewContext();
        (await check.Jobs.SingleAsync()).ChunkSetId.ShouldBe("set-2");
    }
}
