using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Embedding;
using Dexicon.Core.Indexing;
using Dexicon.Infrastructure;
using Dexicon.Mcp;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace Dexicon.Tests;

/// <summary>
/// A corpus made without chunk settings of its own takes the server's configured ones. When those cannot
/// make a corpus, the refusal names the setting and answers as a server fault, and a bad value in the
/// request is answered in the request's words. A custom boundary cannot be set at creation, and the
/// order in which the rules are applied is fixed.
/// </summary>
public sealed class ServerChunkDefaultsTests : IAsyncLifetime
{
    private IndexingHarness _harness = null!;

    public async Task InitializeAsync()
    {
        _harness = await IndexingHarness.StartAsync("notes");
        await _harness.SeedCorpusAsync(SourceKind.Workspace);
    }

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    private static RequestContext AsAdmin() => new()
    {
        Principal = new Principal("k", "admin", new HashSet<string>(StringComparer.Ordinal) { Scopes.Admin, Scopes.Configure }),
    };

    /// <summary>The service over the harness's catalogue with the server configured as given.</summary>
    private CorpusConfiguration ConfiguredWith(CatalogDbContext db, int size = 256, int overlap = 32, string mode = "language-aware")
    {
        var options = Options.Create(new DexiconOptions
        {
            Storage = _harness.Settings.Value.Storage,
            Indexing = new IndexingOptions
            {
                WorkspaceRoot = _harness.Settings.Value.Indexing.WorkspaceRoot,
                ChunkSize = size,
                ChunkOverlap = overlap,
                BoundaryMode = mode,
            },
        });
        var scheduler = new WorkScheduler(options);
        return new CorpusConfiguration(db, _harness.Vectors, _harness.Embedder, options,
            new IndexJobQueue(db, scheduler, NullLogger<IndexJobQueue>.Instance), new SweepQueue(scheduler));
    }

    private static async Task NothingWasCreatedAsync(CatalogDbContext db)
    {
        (await db.Corpora.CountAsync()).ShouldBe(1, "only the seeded corpus");
        (await db.ChunkSets.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task AnUnusableServerSizeRefusesACreationThatSentNoSizeAndNamesTheSetting()
    {
        await using var db = _harness.NewContext();

        var created = await ConfiguredWith(db, size: 10).CreateCorpusAsync(new CreateCorpusRequest("papers"), default);

        var refusal = created.Refusal.ShouldNotBeNull();
        (refusal.Status, refusal.Title).ShouldBe((503, "Server chunk settings unusable"));
        refusal.Detail.ShouldContain("chunkSize must be between 64 and 8192 tokens.");
        refusal.Detail.ShouldContain("DEXICON__INDEXING__CHUNKSIZE=10");
        refusal.Detail.ShouldContain("not from the request");
        await NothingWasCreatedAsync(db);
    }

    [Fact]
    public async Task ABadValueInTheRequestIsAnsweredInTheRequestsWordsWithNoServerSettingNamed()
    {
        await using var db = _harness.NewContext();

        var created = await ConfiguredWith(db).CreateCorpusAsync(new CreateCorpusRequest("papers", ChunkSize: 10), default);

        var refusal = created.Refusal.ShouldNotBeNull();
        (refusal.Status, refusal.Title).ShouldBe((400, "chunkSize must be between 64 and 8192 tokens"));
        refusal.Detail.ShouldNotContain("DEXICON__");
        refusal.Detail.ShouldNotContain("server setting");
    }

    [Fact]
    public async Task ABadServerDefaultAndABadRequestValueGiveDifferentMessagesAndStatuses()
    {
        await using var db = _harness.NewContext();
        var config = ConfiguredWith(db, size: 10);

        var server = (await config.CreateCorpusAsync(new CreateCorpusRequest("papers"), default)).Refusal.ShouldNotBeNull();
        var request = (await ConfiguredWith(db).CreateCorpusAsync(new CreateCorpusRequest("papers", ChunkSize: 10), default))
            .Refusal.ShouldNotBeNull();

        server.Status.ShouldNotBe(request.Status);
        server.Detail.ShouldNotBe(request.Detail);
    }

    [Fact]
    public async Task ARequestThatSendsItsOwnSizeIsNotRefusedForTheServersBadOne()
    {
        await using var db = _harness.NewContext();

        var created = await ConfiguredWith(db, size: 10).CreateCorpusAsync(
            new CreateCorpusRequest("papers", ChunkSize: 512, ChunkOverlap: 64), default);

        created.Refusal.ShouldBeNull();
    }

    [Fact]
    public async Task AServerOverlapPastTheServerSizeNamesBothSettings()
    {
        await using var db = _harness.NewContext();

        var refusal = (await ConfiguredWith(db, overlap: 300).CreateCorpusAsync(new CreateCorpusRequest("papers"), default))
            .Refusal.ShouldNotBeNull();

        refusal.Status.ShouldBe(503);
        refusal.Detail.ShouldContain("DEXICON__INDEXING__CHUNKSIZE=256");
        refusal.Detail.ShouldContain("DEXICON__INDEXING__CHUNKOVERLAP=300");
    }

    [Fact]
    public async Task ARequestValueBesideAUsableServerValueIsA400ThatNamesTheServersSetting()
    {
        await using var db = _harness.NewContext();

        var refusal = (await ConfiguredWith(db, size: 64).CreateCorpusAsync(
            new CreateCorpusRequest("papers", ChunkOverlap: 100), default)).Refusal.ShouldNotBeNull();

        (refusal.Status, refusal.Title).ShouldBe((400, "chunkOverlap must be smaller than chunkSize"));
        refusal.Detail.ShouldContain("Asked for overlap 100 with size 64.");
        refusal.Detail.ShouldContain("DEXICON__INDEXING__CHUNKSIZE=64");
    }

    [Fact]
    public async Task AServerBoundaryModeOfCustomRefusesACreationThatSentNoMode()
    {
        await using var db = _harness.NewContext();

        var config = ConfiguredWith(db, mode: "custom");
        var refusal = (await config.CreateCorpusAsync(new CreateCorpusRequest("papers"), default)).Refusal.ShouldNotBeNull();
        var withMode = await config.CreateCorpusAsync(new CreateCorpusRequest("papers", BoundaryMode: "none"), default);

        refusal.Status.ShouldBe(503);
        refusal.Title.ShouldBe("Server chunk settings unusable");
        refusal.Detail.ShouldContain("DEXICON__INDEXING__BOUNDARYMODE=custom");
        withMode.Refusal.ShouldBeNull();
    }

    [Fact]
    public async Task ACustomBoundaryAtCreationPointsToTheRouteThatCanSetOne()
    {
        await using var db = _harness.NewContext();

        var refusal = (await _harness.NewConfiguration(db).CreateCorpusAsync(
            new CreateCorpusRequest("papers", BoundaryMode: "custom"), default)).Refusal.ShouldNotBeNull();

        (refusal.Status, refusal.Title).ShouldBe((400, "boundaryMode 'custom' cannot be set when a corpus is created"));
        refusal.Detail.ShouldContain("PATCH /api/corpora/{name}/chunk-sets/default");
        refusal.Detail.ShouldContain("customBoundaryPattern");
        await NothingWasCreatedAsync(db);
    }

    [Fact]
    public async Task TheRestHandlerAnswers503ForTheServersSettingAnd400ForTheRequests()
    {
        await using var db = _harness.NewContext();

        var server = await CorpusEndpoints.CreateAsync(new CreateCorpusRequest("papers"), AsAdmin(), db,
            ConfiguredWith(db, size: 10), _harness.Settings, default);
        var request = await CorpusEndpoints.CreateAsync(new CreateCorpusRequest("papers", ChunkSize: 10), AsAdmin(), db,
            ConfiguredWith(db), _harness.Settings, default);

        server.ShouldBeOfType<ProblemHttpResult>().ProblemDetails.Status.ShouldBe(503);
        request.ShouldBeOfType<ProblemHttpResult>().ProblemDetails.Status.ShouldBe(400);
        await NothingWasCreatedAsync(db);
    }

    [Fact]
    public async Task ARefusalWhoseTitleSaysItAllHasNoDetailInTheRestBody()
    {
        await using var db = _harness.NewContext();

        var size = await CorpusEndpoints.CreateAsync(new CreateCorpusRequest("papers", ChunkSize: 10), AsAdmin(), db,
            ConfiguredWith(db), _harness.Settings, default);
        var overlap = await CorpusEndpoints.CreateAsync(
            new CreateCorpusRequest("papers", ChunkSize: 100, ChunkOverlap: 150), AsAdmin(), db,
            ConfiguredWith(db), _harness.Settings, default);

        size.ShouldBeOfType<ProblemHttpResult>().ProblemDetails.Detail.ShouldBeNull();
        overlap.ShouldBeOfType<ProblemHttpResult>().ProblemDetails.Detail.ShouldBe("Asked for overlap 150 with size 100.");
    }

    [Fact]
    public async Task ConfigureCorpusNamesTheServerSettingForArgumentsItNeverSent()
    {
        await using var db = _harness.NewContext();
        var rc = AsAdmin();

        var thrown = await Should.ThrowAsync<McpException>(() => ConfigureTools.ConfigureCorpusAsync(
            rc, new ScopeResolver(db), db, ConfiguredWith(db, size: 10), new RecordingLoggerFactory(), "papers", create: true));

        thrown.Message.ShouldContain("DEXICON__INDEXING__CHUNKSIZE=10");
        thrown.Message.ShouldContain("server setting");
    }

    [Fact]
    public void TheStartupCheckLogsAnErrorNamingTheSettingAndNothingForUsableOnes()
    {
        var bad = new RecordingLoggerFactory();
        var good = new RecordingLoggerFactory();

        Bootstrapper.CheckChunkDefaults(bad.CreateLogger("t"), new DexiconOptions { Indexing = new IndexingOptions { ChunkSize = 10 } });
        Bootstrapper.CheckChunkDefaults(good.CreateLogger("t"), new DexiconOptions());

        bad.Lines.ShouldHaveSingleItem().ShouldContain("DEXICON__INDEXING__CHUNKSIZE=10");
        good.Lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task ACreationIsJudgedAfterTheNameChecksAndBeforeTheTakenNameQueryAndTheProbe()
    {
        await using var db = _harness.NewContext();
        var probes = new ProbeCounter();
        _harness.Embedder = probes;
        var config = _harness.NewConfiguration(db);
        var badSettings = new CreateCorpusRequest("notes", ChunkSize: 10);

        // A name that is taken and settings that are bad: the settings are what is refused.
        var taken = (await config.CreateCorpusAsync(badSettings, default)).Refusal.ShouldNotBeNull();
        // A name with a control character and bad settings: the name is what is refused.
        var control = (await config.CreateCorpusAsync(
            new CreateCorpusRequest("two\nlines", ChunkSize: 10), default)).Refusal.ShouldNotBeNull();
        var colon = (await config.CreateCorpusAsync(
            new CreateCorpusRequest("a:b", ChunkSize: 10), default)).Refusal.ShouldNotBeNull();

        taken.Title.ShouldBe("chunkSize must be between 64 and 8192 tokens");
        control.Title.ShouldBe("A corpus name cannot contain a control character");
        colon.Title.ShouldBe("A corpus name cannot contain ':'");
        probes.Probes.ShouldBe(0);
    }

    [Fact]
    public async Task AProviderErrorIsLoggedAndNotShownToAnAgentButStaysInTheAdminsAnswer()
    {
        _harness.Embedder = new UnavailableProbe();
        await using var db = _harness.NewContext();
        var logs = new RecordingLoggerFactory();

        var thrown = await Should.ThrowAsync<McpException>(() => ConfigureTools.ConfigureCorpusAsync(
            AsAdmin(), new ScopeResolver(db), db, _harness.NewConfiguration(db), logs, "papers", create: true));
        var rest = await CorpusEndpoints.CreateAsync(new CreateCorpusRequest("papers"), AsAdmin(), db,
            _harness.NewConfiguration(db), _harness.Settings, default);

        thrown.Message.ShouldNotContain("ollama:11434");
        thrown.Message.ShouldContain("Whoever runs Dexicon can see why in its log.");
        logs.Lines.ShouldContain(l => l.Contains("Connection refused (ollama:11434)", StringComparison.Ordinal));
        var problem = rest.ShouldBeOfType<ProblemHttpResult>().ProblemDetails;
        (problem.Status, problem.Title).ShouldBe((503, "Embedding model unavailable"));
        problem.Detail.ShouldNotBeNull().ShouldContain("Connection refused (ollama:11434)");
        await NothingWasCreatedAsync(db);
    }

    private sealed class UnavailableProbe : IEmbeddingService
    {
        public Task<IReadOnlyList<float[]>> EmbedAsync(EmbeddingTarget target, EmbedPurpose purpose,
            IReadOnlyList<string> inputs, string? source = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int?> CountTokensAsync(EmbeddingTarget t, string text, CancellationToken ct = default) =>
            Task.FromResult<int?>(null);

        public Task<int> ProbeDimensionsAsync(EmbeddingTarget t, CancellationToken ct = default) =>
            throw new EmbeddingUnavailableException("Connection refused (ollama:11434)");

        public int KnownDimensions(EmbeddingTarget t) => 768;
    }

    private sealed class ProbeCounter : IEmbeddingService
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
}
