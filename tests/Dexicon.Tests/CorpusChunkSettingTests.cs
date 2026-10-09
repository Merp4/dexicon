using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Embedding;
using Dexicon.Core.Indexing;
using Dexicon.Infrastructure;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dexicon.Tests;

/// <summary>
/// The chunk settings a corpus is created with are judged by the rules a chunk set is, wherever they come
/// from. <c>POST /api/corpora</c> stored whatever it was sent, so a size of 3 or an overlap past the size
/// was saved as the corpus's default set and failed (or chunked uselessly) at the first indexing pass.
/// </summary>
public sealed class CorpusChunkSettingTests : IAsyncLifetime
{
    private IndexingHarness _harness = null!;
    private CountingEmbedder _embedder = null!;

    public async Task InitializeAsync()
    {
        _harness = await IndexingHarness.StartAsync("notes");
        await _harness.SeedCorpusAsync(SourceKind.Workspace);
        _embedder = new CountingEmbedder();
        _harness.Embedder = _embedder;
    }

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    /// <summary>Counts probes, so a test can show a refusal came before the embedding service was asked.</summary>
    private sealed class CountingEmbedder : IEmbeddingService
    {
        public int Probes { get; private set; }

        public Task<IReadOnlyList<float[]>> EmbedAsync(EmbeddingTarget target, EmbedPurpose purpose,
            IReadOnlyList<string> inputs, string? source = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int?> CountTokensAsync(EmbeddingTarget t, string text, CancellationToken ct = default) =>
            Task.FromResult<int?>(null);

        public Task<int> ProbeDimensionsAsync(EmbeddingTarget t, CancellationToken ct = default)
        {
            Probes++;
            return Task.FromResult(768);
        }

        public int KnownDimensions(EmbeddingTarget t) => 768;
    }

    private static RequestContext AsAdmin() => new()
    {
        Principal = new Principal("k", "admin", new HashSet<string>(StringComparer.Ordinal) { Scopes.Admin }),
    };

    /// <summary>Size, overlap, boundary mode and the title both endpoints answer with.</summary>
    public static TheoryData<int, int, string, string> Refused => new()
    {
        { 63, 32, "language-aware", "chunkSize must be between 64 and 8192 tokens" },
        { 8193, 32, "language-aware", "chunkSize must be between 64 and 8192 tokens" },
        { 3, 0, "language-aware", "chunkSize must be between 64 and 8192 tokens" },
        { 256, -1, "language-aware", "chunkOverlap cannot be negative" },
        { 256, 256, "language-aware", "chunkOverlap must be smaller than chunkSize" },
        { 100, 150, "none", "chunkOverlap must be smaller than chunkSize" },
        { 256, 32, "paragraph", "Unknown boundary mode" },
        { 256, 32, "Blank-Line", "Unknown boundary mode" },
    };

    private async Task NothingWasCreatedAsync(CatalogDbContext db)
    {
        (await db.Corpora.CountAsync()).ShouldBe(1, "only the seeded corpus");
        (await db.ChunkSets.CountAsync()).ShouldBe(1, "only the seeded set");
        (await db.Sources.CountAsync()).ShouldBe(1);
        (await db.Jobs.CountAsync()).ShouldBe(0);
        _embedder.Probes.ShouldBe(0, "the settings are judged before the embedding service is asked");
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task ACorpusCreatedWithBadChunkSettingsIsRefusedBeforeAnythingIsProbedOrSaved(
        int size, int overlap, string mode, string title)
    {
        await using var db = _harness.NewContext();

        var created = await _harness.NewConfiguration(db).CreateCorpusAsync(
            new CreateCorpusRequest("papers", ChunkSize: size, ChunkOverlap: overlap, BoundaryMode: mode), default);

        var refusal = created.Refusal.ShouldNotBeNull();
        refusal.Status.ShouldBe(400);
        refusal.Title.ShouldBe(title);
        await NothingWasCreatedAsync(db);
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task ACorpusPostedWithBadChunkSettingsAnswers400AndSavesNothing(
        int size, int overlap, string mode, string title)
    {
        await using var db = _harness.NewContext();

        var result = await CorpusEndpoints.CreateAsync(
            new CreateCorpusRequest("papers", ChunkSize: size, ChunkOverlap: overlap, BoundaryMode: mode), AsAdmin(),
            db, _harness.NewConfiguration(db), _harness.Settings, default);

        var problem = result.ShouldBeOfType<ProblemHttpResult>().ProblemDetails;
        problem.Status.ShouldBe(400);
        problem.Title.ShouldBe(title);
        await NothingWasCreatedAsync(db);
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task ACorpusAndAChunkSetAreRefusedInTheSameWords(int size, int overlap, string mode, string title)
    {
        await using var db = _harness.NewContext();

        var corpus = (await CorpusEndpoints.CreateAsync(
            new CreateCorpusRequest("papers", ChunkSize: size, ChunkOverlap: overlap, BoundaryMode: mode), AsAdmin(),
            db, _harness.NewConfiguration(db), _harness.Settings, default)).ShouldBeOfType<ProblemHttpResult>();
        var set = (await ChunkSetEndpoints.CreateAsync(
            IndexingHarness.CorpusId,
            new CreateChunkSetRequest("second", ChunkSize: size, ChunkOverlap: overlap, BoundaryMode: mode), AsAdmin(),
            new ScopeResolver(db), db, _harness.Vectors, _harness.Embedder, QueueOn(db), _harness.Settings, default))
            .ShouldBeOfType<ProblemHttpResult>();

        set.ProblemDetails.Title.ShouldBe(title);
        corpus.ProblemDetails.Title.ShouldBe(set.ProblemDetails.Title);
        corpus.ProblemDetails.Status.ShouldBe(set.ProblemDetails.Status);
        // Where the title says it all neither answer has a detail, so the UI does not show it twice.
        corpus.ProblemDetails.Detail.ShouldBe(set.ProblemDetails.Detail);
    }

    [Fact]
    public async Task AnOverlapPastTheConfiguredSizeIsRefusedWhenOnlyTheOverlapIsSent()
    {
        // The size is the configured 256, so the settings that are judged are the resolved ones.
        await using var db = _harness.NewContext();

        var created = await _harness.NewConfiguration(db).CreateCorpusAsync(
            new CreateCorpusRequest("papers", ChunkOverlap: 300), default);

        created.Refusal.ShouldNotBeNull().Title.ShouldBe("chunkOverlap must be smaller than chunkSize");
        await NothingWasCreatedAsync(db);
    }

    [Theory]
    [InlineData(64, 0, "none")]
    [InlineData(512, 64, "blank-line")]
    [InlineData(8192, 8191, "language-aware")]
    public async Task ACorpusCreatedWithValidChunkSettingsStoresThemOnItsDefaultSet(int size, int overlap, string mode)
    {
        await using var db = _harness.NewContext();

        var created = await _harness.NewConfiguration(db).CreateCorpusAsync(
            new CreateCorpusRequest("papers", ChunkSize: size, ChunkOverlap: overlap, BoundaryMode: mode), default);

        created.Refusal.ShouldBeNull();
        var set = await db.ChunkSets.AsNoTracking().SingleAsync(s => s.CorpusId == created.Value!.Id);
        (set.ChunkSize, set.ChunkOverlap, set.BoundaryMode, set.IsDefault).ShouldBe((size, overlap, mode, true));
        _embedder.Probes.ShouldBe(1);
    }

    [Fact]
    public async Task ACorpusCreatedWithNoChunkSettingsTakesTheConfiguredOnes()
    {
        await using var db = _harness.NewContext();

        var created = await _harness.NewConfiguration(db).CreateCorpusAsync(new CreateCorpusRequest("papers"), default);

        created.Refusal.ShouldBeNull();
        var set = await db.ChunkSets.AsNoTracking().SingleAsync(s => s.CorpusId == created.Value!.Id);
        var indexing = _harness.Settings.Value.Indexing;
        (set.ChunkSize, set.ChunkOverlap, set.BoundaryMode).ShouldBe((indexing.ChunkSize, indexing.ChunkOverlap, indexing.BoundaryMode));
    }

    /// <summary>
    /// The chunk-set endpoints answered these before the rules were shared, and the answers stay: the title
    /// alone for a range, a sign or a missing pattern, and a detail for the rest.
    /// </summary>
    [Fact]
    public async Task AChunkSetCreatedWithABadSizeAnswersWithATitleAndNoDetailAndSavesNothing()
    {
        await using var db = _harness.NewContext();

        var result = await ChunkSetEndpoints.CreateAsync(
            IndexingHarness.CorpusId, new CreateChunkSetRequest("second", ChunkSize: 10), AsAdmin(),
            new ScopeResolver(db), db, _harness.Vectors, _harness.Embedder, QueueOn(db), _harness.Settings, default);

        var problem = result.ShouldBeOfType<ProblemHttpResult>().ProblemDetails;
        (problem.Status, problem.Title, problem.Detail)
            .ShouldBe((400, "chunkSize must be between 64 and 8192 tokens", null));
        (await db.ChunkSets.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task AChunkSetCreatedWithAnOverlapPastItsSizeNamesBothInTheDetail()
    {
        await using var db = _harness.NewContext();

        var result = await ChunkSetEndpoints.CreateAsync(
            IndexingHarness.CorpusId, new CreateChunkSetRequest("second", ChunkSize: 100, ChunkOverlap: 120), AsAdmin(),
            new ScopeResolver(db), db, _harness.Vectors, _harness.Embedder, QueueOn(db), _harness.Settings, default);

        var problem = result.ShouldBeOfType<ProblemHttpResult>().ProblemDetails;
        (problem.Status, problem.Title, problem.Detail).ShouldBe(
            (400, "chunkOverlap must be smaller than chunkSize", "Asked for overlap 120 with size 100."));
    }

    [Fact]
    public async Task AChunkSetCreatedWithAnUncompilablePatternIsRefusedWithTheParserMessage()
    {
        await using var db = _harness.NewContext();

        var result = await ChunkSetEndpoints.CreateAsync(
            IndexingHarness.CorpusId,
            new CreateChunkSetRequest("second", BoundaryMode: "custom", CustomBoundaryPattern: "[z-a]"), AsAdmin(),
            new ScopeResolver(db), db, _harness.Vectors, _harness.Embedder, QueueOn(db), _harness.Settings, default);

        var problem = result.ShouldBeOfType<ProblemHttpResult>().ProblemDetails;
        (problem.Status, problem.Title).ShouldBe((400, "Invalid custom boundary pattern"));
        problem.Detail.ShouldNotBeNullOrEmpty();
        (await db.ChunkSets.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task AChunkSetUpdatedToABadBoundaryModeIsRefusedAndTheStoredOneStays()
    {
        await using var db = _harness.NewContext();

        var result = await ChunkSetEndpoints.UpdateAsync(
            IndexingHarness.CorpusId, "default", new UpdateChunkSetRequest(BoundaryMode: "paragraph"), AsAdmin(),
            new ScopeResolver(db), db, QueueOn(db), default);

        var problem = result.ShouldBeOfType<ProblemHttpResult>().ProblemDetails;
        (problem.Status, problem.Title, problem.Detail).ShouldBe(
            (400, "Unknown boundary mode", "'paragraph'. Expected none, blank-line, language-aware or custom."));
        await using var fresh = _harness.NewContext();
        (await fresh.ChunkSets.SingleAsync()).BoundaryMode.ShouldBe("blank-line");
        (await fresh.Jobs.CountAsync()).ShouldBe(0);
    }

    private IndexJobQueue QueueOn(CatalogDbContext db) =>
        new(db, new WorkScheduler(_harness.Settings), NullLogger<IndexJobQueue>.Instance);
}
