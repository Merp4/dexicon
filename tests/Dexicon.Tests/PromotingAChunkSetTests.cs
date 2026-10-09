using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// The promote route: a ready set becomes the only default, a set that is still building is refused, and an
/// unknown set is not found. Its interleaving with a removal is in <see cref="ChunkSetRemovalRecheckTests"/>.
/// </summary>
[Collection(nameof(AttachmentLockCollection))]
public sealed class PromotingAChunkSetTests
{
    private static readonly RequestContext Admin = new()
    {
        Principal = new Principal("k", "admin", new HashSet<string>(StringComparer.Ordinal) { Scopes.Admin }),
    };

    private static Task<IResult> PromoteAsync(IndexingHarness harness, CatalogDbContext db, string setName) =>
        ChunkSetEndpoints.PromoteAsync(IndexingHarness.CorpusId, setName, Admin, new ScopeResolver(db), db, default);

    [Fact]
    public async Task AReadySetBecomesTheOnlyDefaultAndThePreviousDefaultIsCleared()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload, sets: 3);
        await using var db = harness.NewContext();

        var result = await PromoteAsync(harness, db, "alt-1");

        var promoted = result.ShouldBeOfType<Ok<ChunkSetPromoted>>().Value.ShouldNotBeNull();
        promoted.Promoted.ShouldBe("alt-1");
        await using var check = harness.NewContext();
        (await check.ChunkSets.Where(s => s.IsDefault).Select(s => s.Id).ToListAsync()).ShouldBe(["set-2"]);
    }

    [Fact]
    public async Task ASetThatStillHasFilesToIndexIsRefusedAndTheDefaultStays()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload, sets: 2);
        await using var db = harness.NewContext();
        var documents = harness.NewDocumentService(db);
        var stored = await documents.StoreAsync(new MemoryStream("one document"u8.ToArray()), "doc.txt");
        await documents.AttachAsync(await db.Corpora.SingleAsync(), stored.Sha256, "doc.txt");

        var result = await PromoteAsync(harness, db, "alt-1");

        var refused = result.ShouldBeOfType<ProblemHttpResult>();
        refused.StatusCode.ShouldBe(409);
        refused.ProblemDetails.Title.ShouldBe("Chunk set is not ready");
        await using var check = harness.NewContext();
        (await check.ChunkSets.Where(s => s.IsDefault).Select(s => s.Id).ToListAsync()).ShouldBe(["set-1"]);
    }

    [Fact]
    public async Task ASetThatIsNotThereIsNotFound()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload, sets: 2);
        await using var db = harness.NewContext();

        var result = await PromoteAsync(harness, db, "no-such-set");

        result.ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(404);
    }
}
