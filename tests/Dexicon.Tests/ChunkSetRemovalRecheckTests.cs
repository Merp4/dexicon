using Dexicon.Core.Catalog;
using Dexicon.Core.Indexing;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// A chunk set removal checks the set again under the lock, after its vectors are gone. What it refuses then
/// leaves the set in place without its vectors, and a refresh of the set is queued to index it again.
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

        var outcome = await harness.NewConfiguration(removing)
            .RemoveChunkSetAsync(await removing.Corpora.SingleAsync(), "alt-1", default);

        outcome.Refusal.ShouldNotBeNull().Status.ShouldBe(409);
        outcome.Refusal.Title.ShouldBe("Cannot delete the default chunk set");
        await using var check = harness.NewContext();
        (await check.ChunkSets.CountAsync()).ShouldBe(2);
        (await check.ChunkSets.CountAsync(s => s.IsDefault)).ShouldBe(1, "a corpus keeps a default set");
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

        var outcome = await harness.NewConfiguration(removing)
            .RemoveChunkSetAsync(await removing.Corpora.SingleAsync(), "alt-1", default);

        outcome.Refusal.ShouldNotBeNull().Title.ShouldBe("Cannot delete the only chunk set");
        await using var check = harness.NewContext();
        (await check.ChunkSets.Select(s => s.Id).ToListAsync()).ShouldBe(["set-2"]);
        (await check.Jobs.CountAsync(j => j.ChunkSetId == "set-2")).ShouldBe(1);
    }
}
