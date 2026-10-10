using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Indexing;
using Dexicon.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dexicon.Tests;

/// <summary>
/// A change to a set that was stored before a rule existed. The set holds a size of 29,491 (what the corpus
/// dialog used to send for a model with a 32k context), which the rules now refuse. Each setting a change
/// touches is judged on its own, and one the change does not touch is not judged at all, so the old size
/// neither blocks an edit of something else nor hides a bad value in what is edited.
/// </summary>
public sealed class ChunkSetChangeTests : IAsyncLifetime
{
    private const string TooBig = "chunkSize must be between 64 and 8192 tokens";

    private IndexingHarness _harness = null!;

    public async Task InitializeAsync()
    {
        _harness = await IndexingHarness.StartAsync("notes");
        await _harness.SeedCorpusAsync(SourceKind.Workspace);
        await using var db = _harness.NewContext();
        await db.ChunkSets.ExecuteUpdateAsync(u => u
            .SetProperty(s => s.ChunkSize, 29_491).SetProperty(s => s.ChunkOverlap, 3_686)
            .SetProperty(s => s.BoundaryMode, "blank-line").SetProperty(s => s.Description, "old"));
    }

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    private static RequestContext AsAdmin() => new()
    {
        Principal = new Principal("k", "admin", new HashSet<string>(StringComparer.Ordinal) { Scopes.Admin }),
    };

    private async Task<IResult> PatchAsync(UpdateChunkSetRequest body)
    {
        await using var db = _harness.NewContext();
        var queue = new IndexJobQueue(db, new WorkScheduler(_harness.Settings), NullLogger<IndexJobQueue>.Instance);
        return await ChunkSetEndpoints.UpdateAsync(
            IndexingHarness.CorpusId, "default", body, AsAdmin(), new ScopeResolver(db), db, queue, default);
    }

    public static TheoryData<string, UpdateChunkSetRequest, string> Refused => new()
    {
        { "a size over the range", new UpdateChunkSetRequest(ChunkSize: 9_000), TooBig },
        { "a size under the range", new UpdateChunkSetRequest(ChunkSize: 10), TooBig },
        { "a negative overlap", new UpdateChunkSetRequest(ChunkOverlap: -5), "chunkOverlap cannot be negative" },
        { "an overlap past the stored size", new UpdateChunkSetRequest(ChunkOverlap: 40_000), "chunkOverlap must be smaller than chunkSize" },
        { "an unknown mode", new UpdateChunkSetRequest(BoundaryMode: "bogus"), "Unknown boundary mode" },
        { "custom with no pattern", new UpdateChunkSetRequest(BoundaryMode: "custom"),
            "customBoundaryPattern is required for boundary mode 'custom'" },
        { "custom with a pattern that does not compile",
            new UpdateChunkSetRequest(BoundaryMode: "custom", CustomBoundaryPattern: "[z-a]"), "Invalid custom boundary pattern" },
        { "a size and an overlap that fail together",
            new UpdateChunkSetRequest(ChunkSize: 300, ChunkOverlap: 400), "chunkOverlap must be smaller than chunkSize" },
        { "a size the stored overlap is past", new UpdateChunkSetRequest(ChunkSize: 1_000), "chunkOverlap must be smaller than chunkSize" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task ABadValueInWhatIsTouchedIsRefusedAndNothingIsStoredOrQueued(
        string what, UpdateChunkSetRequest body, string title)
    {
        _ = what;

        var result = await PatchAsync(body);

        result.ShouldBeOfType<ProblemHttpResult>().ProblemDetails.Title.ShouldBe(title);
        await using var fresh = _harness.NewContext();
        var set = await fresh.ChunkSets.AsNoTracking().SingleAsync();
        (set.ChunkSize, set.ChunkOverlap, set.BoundaryMode).ShouldBe((29_491, 3_686, "blank-line"));
        (await fresh.Jobs.CountAsync()).ShouldBe(0, "a refused change queues no re-chunk");
    }

    public static TheoryData<string, UpdateChunkSetRequest> Accepted => new()
    {
        { "nothing", new UpdateChunkSetRequest() },
        { "a description only", new UpdateChunkSetRequest(Description: "edited") },
        { "the stored size sent again", new UpdateChunkSetRequest(ChunkSize: 29_491) },
        { "every stored value sent again",
            new UpdateChunkSetRequest("old", 29_491, 3_686, "blank-line", null, false, false, false) },
        { "a valid overlap beside the stored size", new UpdateChunkSetRequest(ChunkOverlap: 10) },
        { "a valid mode", new UpdateChunkSetRequest(BoundaryMode: "none") },
        { "a pattern while the mode is not custom", new UpdateChunkSetRequest(CustomBoundaryPattern: "[z-a]") },
        { "custom with a pattern", new UpdateChunkSetRequest(BoundaryMode: "custom", CustomBoundaryPattern: "^#") },
        { "a size that mends the set", new UpdateChunkSetRequest(ChunkSize: 8_192) },
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public async Task ASettingThatIsNotChangedIsNotJudgedHoweverOldTheStoredSizeIs(string what, UpdateChunkSetRequest body)
    {
        _ = what;

        var result = await PatchAsync(body);

        result.ShouldBeOfType<Ok<ChunkSetUpdated>>();
    }

    [Fact]
    public async Task ANothingChangedSaveQueuesNoRechunk()
    {
        var result = await PatchAsync(new UpdateChunkSetRequest("old", 29_491, 3_686, "blank-line", null, false, false, false));

        result.ShouldBeOfType<Ok<ChunkSetUpdated>>().Value!.RechunkJob.ShouldBeNull();
    }

    [Fact]
    public async Task AnOverlapPastTheSizeStoredBeforeTheRuleDoesNotBlockAModeChangeButAnUnknownModeIsStillRefused()
    {
        await using (var db = _harness.NewContext())
            await db.ChunkSets.ExecuteUpdateAsync(u => u.SetProperty(s => s.ChunkSize, 100).SetProperty(s => s.ChunkOverlap, 500));

        var bogus = await PatchAsync(new UpdateChunkSetRequest(BoundaryMode: "bogus"));
        var none = await PatchAsync(new UpdateChunkSetRequest(BoundaryMode: "none"));

        bogus.ShouldBeOfType<ProblemHttpResult>().ProblemDetails.Title.ShouldBe("Unknown boundary mode");
        none.ShouldBeOfType<Ok<ChunkSetUpdated>>();
    }

    [Fact]
    public async Task ANegativeOverlapStoredBeforeTheRuleDoesNotBlockADescriptionOrAModeChange()
    {
        await using (var db = _harness.NewContext())
            await db.ChunkSets.ExecuteUpdateAsync(u => u.SetProperty(s => s.ChunkSize, 256).SetProperty(s => s.ChunkOverlap, -5));

        var description = await PatchAsync(new UpdateChunkSetRequest(Description: "edited"));
        var mode = await PatchAsync(new UpdateChunkSetRequest(BoundaryMode: "none"));
        var overlap = await PatchAsync(new UpdateChunkSetRequest(ChunkOverlap: -6));

        description.ShouldBeOfType<Ok<ChunkSetUpdated>>();
        mode.ShouldBeOfType<Ok<ChunkSetUpdated>>();
        overlap.ShouldBeOfType<ProblemHttpResult>().ProblemDetails.Title.ShouldBe("chunkOverlap cannot be negative");
    }

    [Fact]
    public async Task APatternSentWhileTheModeIsNotCustomIsNotJudgedAgainstAnUnknownModeStoredBeforeTheRule()
    {
        await using (var db = _harness.NewContext())
            await db.ChunkSets.ExecuteUpdateAsync(u => u.SetProperty(s => s.BoundaryMode, "bogus"));

        var result = await PatchAsync(new UpdateChunkSetRequest(CustomBoundaryPattern: "^#"));

        result.ShouldBeOfType<Ok<ChunkSetUpdated>>();
    }

    [Fact]
    public async Task ASetThatInheritsAStoredSizeIsNotRefusedForItWhenTheRequestSentOnlyAModel()
    {
        await using var db = _harness.NewContext();
        var options = _harness.Settings;

        var inherited = await ChunkSetEndpoints.CreateAsync(
            IndexingHarness.CorpusId, new CreateChunkSetRequest("second", EmbeddingModel: "m"), AsAdmin(),
            new ScopeResolver(db), db, _harness.Vectors, _harness.Embedder,
            new IndexJobQueue(db, new WorkScheduler(options), NullLogger<IndexJobQueue>.Instance), options, default);
        var sent = await ChunkSetEndpoints.CreateAsync(
            IndexingHarness.CorpusId, new CreateChunkSetRequest("third", EmbeddingModel: "m", ChunkSize: 9_000), AsAdmin(),
            new ScopeResolver(db), db, _harness.Vectors, _harness.Embedder,
            new IndexJobQueue(db, new WorkScheduler(options), NullLogger<IndexJobQueue>.Instance), options, default);

        inherited.ShouldBeOfType<Accepted<ChunkSetCreated>>();
        sent.ShouldBeOfType<ProblemHttpResult>().ProblemDetails.Title.ShouldBe(TooBig);
    }
}
