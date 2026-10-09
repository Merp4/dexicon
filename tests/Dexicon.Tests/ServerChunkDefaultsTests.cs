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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
[Collection(ConsoleOutputCollection.Name)]
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
        (await db.Sources.CountAsync()).ShouldBe(1);
        (await db.Jobs.CountAsync()).ShouldBe(0);
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
        refusal.Detail.ShouldNotContain("send chunkSize", Case.Insensitive, "an agent has no such argument");
        refusal.AgentDetail.ShouldNotBeNull().ShouldContain("DEXICON__INDEXING__CHUNKSIZE=10");
        refusal.AgentDetail.ShouldContain("Ask whoever runs Dexicon to correct them.");
        refusal.AgentDetail.ShouldNotContain("send ", Case.Insensitive);
        await NothingWasCreatedAsync(db);
    }

    [Fact]
    public async Task AnUnusableServerSettingRefusesBeforeTheEmbeddingServiceIsAsked()
    {
        var probes = new ProbeCounter();
        _harness.Embedder = probes;
        await using var db = _harness.NewContext();

        var created = await ConfiguredWith(db, size: 10).CreateCorpusAsync(new CreateCorpusRequest("papers"), default);

        created.Refusal.ShouldNotBeNull().Status.ShouldBe(503);
        probes.Probes.ShouldBe(0);
        await NothingWasCreatedAsync(db);
    }

    /// <summary>
    /// The size in force is quoted, whether it is the server's or the request's, and the setting the request
    /// did not send is named as the server's.
    /// </summary>
    [Theory]
    [InlineData(1000, 1200, "Asked for overlap 1200 with size 1000.", "DEXICON__INDEXING__CHUNKSIZE=1000")]
    [InlineData(200, 300, "Asked for overlap 300 with size 200.", "DEXICON__INDEXING__CHUNKSIZE=200")]
    [InlineData(8192, 9000, "Asked for overlap 9000 with size 8192.", "DEXICON__INDEXING__CHUNKSIZE=8192")]
    public async Task AnOverlapPastTheServersSizeQuotesTheSizeInForce(int serverSize, int overlap, string quoted, string named)
    {
        await using var db = _harness.NewContext();

        var refusal = (await ConfiguredWith(db, size: serverSize, overlap: 0).CreateCorpusAsync(
            new CreateCorpusRequest("papers", ChunkOverlap: overlap), default)).Refusal.ShouldNotBeNull();

        refusal.Status.ShouldBe(400);
        refusal.Detail.ShouldContain(quoted);
        refusal.Detail.ShouldContain(named);
        refusal.Detail.ShouldNotContain("size 256");
        refusal.Detail.ShouldNotContain("CHUNKOVERLAP");
    }

    [Fact]
    public async Task AnUnusableServerSizeIsTheCauseWhateverOverlapTheRequestSent()
    {
        await using var db = _harness.NewContext();

        var refusal = (await ConfiguredWith(db, size: 10).CreateCorpusAsync(
            new CreateCorpusRequest("papers", ChunkOverlap: 300), default)).Refusal.ShouldNotBeNull();

        refusal.Status.ShouldBe(503);
        refusal.Detail.ShouldContain("DEXICON__INDEXING__CHUNKSIZE=10");
        refusal.Detail.ShouldNotContain("CHUNKOVERLAP");
    }

    [Fact]
    public async Task OnlyTheServerSettingThatFailsIsNamed()
    {
        // The mode is the one that is wrong. The size of 64 is fine, and the request's overlap of 70 is fine
        // until the mode is mended, so it is the mode that is named.
        await using var db = _harness.NewContext();

        var refusal = (await ConfiguredWith(db, size: 64, mode: "zzz").CreateCorpusAsync(
            new CreateCorpusRequest("papers", ChunkOverlap: 70), default)).Refusal.ShouldNotBeNull();

        refusal.Status.ShouldBe(503);
        refusal.Detail.ShouldContain("DEXICON__INDEXING__BOUNDARYMODE=zzz");
        refusal.Detail.ShouldNotContain("CHUNKSIZE");
        refusal.Detail.ShouldNotContain("CHUNKOVERLAP");
    }

    [Fact]
    public async Task ARequestSizeBesideAUsableServerOverlapNamesTheOverlapAndNotTheSize()
    {
        await using var db = _harness.NewContext();

        var refusal = (await ConfiguredWith(db, overlap: 150).CreateCorpusAsync(
            new CreateCorpusRequest("papers", ChunkSize: 100), default)).Refusal.ShouldNotBeNull();

        refusal.Status.ShouldBe(400);
        refusal.Detail.ShouldContain("Asked for overlap 150 with size 100.");
        refusal.Detail.ShouldContain("DEXICON__INDEXING__CHUNKOVERLAP=150");
        refusal.Detail.ShouldNotContain("CHUNKSIZE");
    }

    [Fact]
    public async Task ACorpusIsMadeWithTheServersConfiguredValuesWhenTheyAreNotTheDefaults()
    {
        await using var db = _harness.NewContext();

        var created = await ConfiguredWith(db, size: 512, overlap: 64, mode: "blank-line")
            .CreateCorpusAsync(new CreateCorpusRequest("papers"), default);
        var mixed = await ConfiguredWith(db, size: 512, overlap: 64, mode: "blank-line")
            .CreateCorpusAsync(new CreateCorpusRequest("books", ChunkSize: 1024), default);

        created.Refusal.ShouldBeNull();
        mixed.Refusal.ShouldBeNull();
        var papers = await db.ChunkSets.AsNoTracking().SingleAsync(s => s.CorpusId == created.Value!.Id);
        var books = await db.ChunkSets.AsNoTracking().SingleAsync(s => s.CorpusId == mixed.Value!.Id);
        (papers.ChunkSize, papers.ChunkOverlap, papers.BoundaryMode).ShouldBe((512, 64, "blank-line"));
        (books.ChunkSize, books.ChunkOverlap, books.BoundaryMode).ShouldBe((1024, 64, "blank-line"));
    }

    [Fact]
    public async Task ANullChunkSizeInTheJsonBodyIsTheServersConfiguredOne()
    {
        var body = System.Text.Json.JsonSerializer.Deserialize<CreateCorpusRequest>(
            """{"name":"papers","chunkSize":null,"chunkOverlap":null,"boundaryMode":null}""",
            JsonOptions.Web)!;
        await using var db = _harness.NewContext();

        var created = await ConfiguredWith(db, size: 512, overlap: 64, mode: "none").CreateCorpusAsync(body, default);

        created.Refusal.ShouldBeNull();
        var set = await db.ChunkSets.AsNoTracking().SingleAsync(s => s.CorpusId == created.Value!.Id);
        (set.ChunkSize, set.ChunkOverlap, set.BoundaryMode).ShouldBe((512, 64, "none"));
    }

    [Fact]
    public void TheSettingNamesInTheMessagesAreTheOnesTheEnvironmentBinds()
    {
        var names = new Dictionary<string, string>
        {
            ["DEXICON__INDEXING__CHUNKSIZE"] = "10",
            ["DEXICON__INDEXING__CHUNKOVERLAP"] = "300",
            ["DEXICON__INDEXING__BOUNDARYMODE"] = "zzz",
        };
        var saved = names.ToDictionary(n => n.Key, n => Environment.GetEnvironmentVariable(n.Key));
        try
        {
            foreach (var (name, value) in names) Environment.SetEnvironmentVariable(name, value);
            var options = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .Build()
                .GetSection(DexiconOptions.SectionName)
                .Get<DexiconOptions>()!;

            (options.Indexing.ChunkSize, options.Indexing.ChunkOverlap, options.Indexing.BoundaryMode).ShouldBe((10, 300, "zzz"));
            var refusal = ChunkSettingRules.CheckServerDefaults(options.Indexing).ShouldNotBeNull();
            refusal.Detail.ShouldContain("DEXICON__INDEXING__CHUNKSIZE=10");
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public async Task AChunkSetOnACorpusWithNoSetNamesTheServerSettingThatFails()
    {
        await using var db = _harness.NewContext();
        await db.ChunkSets.ExecuteDeleteAsync();
        var options = Options.Create(new DexiconOptions { Indexing = new IndexingOptions { ChunkSize = 10 } });

        var result = await ChunkSetEndpoints.CreateAsync(
            IndexingHarness.CorpusId, new CreateChunkSetRequest("first", EmbeddingModel: "m"), AsAdmin(), new ScopeResolver(db), db,
            _harness.Vectors, _harness.Embedder,
            new IndexJobQueue(db, new WorkScheduler(options), NullLogger<IndexJobQueue>.Instance), options, default);

        var problem = result.ShouldBeOfType<ProblemHttpResult>().ProblemDetails;
        problem.Status.ShouldBe(503);
        problem.Detail.ShouldNotBeNull().ShouldContain("DEXICON__INDEXING__CHUNKSIZE=10");
        (await db.ChunkSets.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task AChunkSetThatInheritsFromASetIsJudgedInTheChunkSetWordsAsBefore()
    {
        await using var db = _harness.NewContext();

        var result = await ChunkSetEndpoints.CreateAsync(
            IndexingHarness.CorpusId, new CreateChunkSetRequest("second", ChunkSize: 10), AsAdmin(), new ScopeResolver(db), db,
            _harness.Vectors, _harness.Embedder,
            new IndexJobQueue(db, new WorkScheduler(_harness.Settings), NullLogger<IndexJobQueue>.Instance), _harness.Settings, default);

        var problem = result.ShouldBeOfType<ProblemHttpResult>().ProblemDetails;
        (problem.Status, problem.Title, problem.Detail).ShouldBe((400, "chunkSize must be between 64 and 8192 tokens", null));
    }

    [Fact]
    public async Task ASetStoredWithAnOldSizeCanStillHaveItsDescriptionEditedButNotItsSizeSetToAnotherBadOne()
    {
        // The corpus dialog used to send 29,491 tokens for a model with a 32k context.
        await using var db = _harness.NewContext();
        await db.ChunkSets.ExecuteUpdateAsync(u => u.SetProperty(s => s.ChunkSize, 29_491));
        var queue = new IndexJobQueue(db, new WorkScheduler(_harness.Settings), NullLogger<IndexJobQueue>.Instance);

        var description = await ChunkSetEndpoints.UpdateAsync(IndexingHarness.CorpusId, "default",
            new UpdateChunkSetRequest(Description: "edited"), AsAdmin(), new ScopeResolver(db), db, queue, default);
        var overlap = await ChunkSetEndpoints.UpdateAsync(IndexingHarness.CorpusId, "default",
            new UpdateChunkSetRequest(ChunkOverlap: 10), AsAdmin(), new ScopeResolver(db), db, queue, default);
        var worse = await ChunkSetEndpoints.UpdateAsync(IndexingHarness.CorpusId, "default",
            new UpdateChunkSetRequest(ChunkSize: 9_000), AsAdmin(), new ScopeResolver(db), db, queue, default);
        var mended = await ChunkSetEndpoints.UpdateAsync(IndexingHarness.CorpusId, "default",
            new UpdateChunkSetRequest(ChunkSize: 4_000), AsAdmin(), new ScopeResolver(db), db, queue, default);

        description.ShouldBeOfType<Ok<ChunkSetUpdated>>().Value!.RechunkJob.ShouldBeNull("a description needs no re-chunk");
        overlap.ShouldBeOfType<Ok<ChunkSetUpdated>>();
        worse.ShouldBeOfType<ProblemHttpResult>().ProblemDetails.Title.ShouldBe("chunkSize must be between 64 and 8192 tokens");
        mended.ShouldBeOfType<Ok<ChunkSetUpdated>>();
        await using var fresh = _harness.NewContext();
        var set = await fresh.ChunkSets.SingleAsync();
        (set.Description, set.ChunkSize, set.ChunkOverlap).ShouldBe(("edited", 4_000, 10));
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
        refusal.Detail.ShouldNotContain("CHUNKOVERLAP", Case.Sensitive, "the overlap is the request's");
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
        thrown.Message.ShouldContain("Ask whoever runs Dexicon to correct them.");
        thrown.Message.ShouldNotContain("send ", Case.Insensitive, "the tool takes no chunk arguments to send");
        thrown.Message.ShouldNotContain("chunkSize", Case.Sensitive);
    }

    [Fact]
    public void TheStartupCheckLogsAnErrorNamingTheSettingAndNothingForUsableOnes()
    {
        var bad = new RecordingLoggerFactory();
        var good = new RecordingLoggerFactory();

        Bootstrapper.CheckChunkDefaults(bad.CreateLogger("t"), new DexiconOptions { Indexing = new IndexingOptions { ChunkSize = 10 } });
        Bootstrapper.CheckChunkDefaults(good.CreateLogger("t"), new DexiconOptions());

        bad.Lines.ShouldHaveSingleItem().ShouldContain("DEXICON__INDEXING__CHUNKSIZE=10");
        bad.Levels.ShouldBe([LogLevel.Error]);
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
        var blank = (await config.CreateCorpusAsync(
            new CreateCorpusRequest("  ", ChunkSize: 10), default)).Refusal.ShouldNotBeNull();
        var tooLong = (await config.CreateCorpusAsync(
            new CreateCorpusRequest(new string('n', 201), ChunkSize: 10), default)).Refusal.ShouldNotBeNull();

        blank.Title.ShouldBe("Name is required");
        tooLong.Title.ShouldBe("A corpus name is too long");
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

    [Fact]
    public async Task TheLoggedRefusalNamesTheKeyAndTheDetailOnOneLineWhateverTheyHold()
    {
        // The key's name is typed text, and an adopted key's can hold anything. The provider's message is
        // the provider's.
        _harness.Embedder = new UnavailableProbe();
        await using var db = _harness.NewContext();
        var logs = new RecordingLoggerFactory();
        var forged = "[10:00:00Z INF] forged";
        var rc = new RequestContext
        {
            Principal = new Principal("k", $"agent\n{forged}{TestText.Csi}",
                new HashSet<string>(StringComparer.Ordinal) { Scopes.Configure }),
        };

        await Should.ThrowAsync<McpException>(() => ConfigureTools.ConfigureCorpusAsync(
            rc, new ScopeResolver(db), db, _harness.NewConfiguration(db), logs, "papers", create: true));

        var line = logs.Lines.Single(l => l.Contains("was refused", StringComparison.Ordinal));
        line.ShouldStartWith("Key agent�" + forged);
        line.ShouldContain("Embedding model unavailable: Could not probe");
        line.ShouldContain("Connection refused (ollama:11434)");
        line.Any(c => char.IsControl(c)).ShouldBeFalse();
        logs.Levels.ShouldContain(LogLevel.Warning);
    }

    [Fact]
    public async Task AWorkspacePathTheResolverRefusesIsGivenToAnAgentWithoutTheHostsWordsAtCreationAndAtAddingASource()
    {
        await using var db = _harness.NewContext();
        var config = _harness.NewConfiguration(db);

        var created = (await config.CreateCorpusAsync(new CreateCorpusRequest("papers", WorkspacePath: "../outside"), default))
            .Refusal.ShouldNotBeNull();
        var corpus = await db.Corpora.SingleAsync();
        var added = (await config.AddSourceAsync(corpus, new AddSourceRequest("../outside"), default)).Refusal.ShouldNotBeNull();

        foreach (var refusal in new[] { created, added })
        {
            refusal.Title.ShouldBe("Invalid workspace path");
            refusal.AgentDetail.ShouldBe(
                "The path is outside the workspace, or passes through a link, and links are not followed. "
                + "list_folders shows what is mounted.");
            refusal.Detail.ShouldNotBe(refusal.AgentDetail, "the admin is given the resolver's own message");
        }
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
