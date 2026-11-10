using Dexicon.Core.Catalog;
using Dexicon.Core.Embedding;
using Dexicon.Core.Indexing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// The comparison alone: which pairs of recorded and current templates count as a changed framing.
/// </summary>
public sealed class ModelFramingMeasurementTests
{
    private static readonly ModelTemplates Framed =
        new("title: none | text: {text}", "task: search result | query: {text}", TemplateOrigin.BuiltIn);

    [Fact]
    public void ASetWithNoRecordIsNotReported() => ModelFraming.Differs(null, Framed).ShouldBeFalse();

    [Fact]
    public void ATemplateEqualToTheRecordIsNotReported() =>
        ModelFraming.Differs("title: none | text: {text}", Framed).ShouldBeFalse();

    [Fact]
    public void ADifferentDocumentTemplateIsReported() => ModelFraming.Differs("{text}", Framed).ShouldBeTrue();

    [Fact]
    public void ADifferenceOfCaseIsReported() =>
        ModelFraming.Differs("TITLE: none | text: {text}", Framed).ShouldBeTrue();

    [Fact]
    public void ADifferentQueryTemplateAloneIsNotReported()
    {
        // The query template changes what a search sends. The stored vectors were made from the document template.
        var queryOnly = Framed with { Query = "search_query: {text}" };

        ModelFraming.Differs("title: none | text: {text}", queryOnly).ShouldBeFalse();
    }
}

/// <summary>
/// What is written to the catalogue: the first record of a set, its replacement, and the list the health report
/// reads.
/// </summary>
public sealed class ModelFramingRecords : IAsyncLifetime
{
    private static readonly ModelTemplates Framed =
        new("title: none | text: {text}", "task: search result | query: {text}", TemplateOrigin.BuiltIn);

    private static readonly ModelTemplates Prefixed =
        new("search_document: {text}", "search_query: {text}", TemplateOrigin.BuiltIn);

    private SqliteConnection _connection = null!;
    private ServiceProvider _services = null!;
    private readonly IndexingHarness.FixedProfiles _profiles = new();

    public async ValueTask InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<CatalogDbContext>(o => o.UseSqlite(_connection));
        _services = services.BuildServiceProvider();

        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
        // The last two have ids that sort the other way from their names, so that an order by name cannot be met by
        // the order of an index on the id.
        foreach (var (id, name) in new[]
            { ("c-alpha", "alpha"), ("c-beta", "beta"), ("c-first", "zeta"), ("c-second", "apex") })
            db.Corpora.Add(new Corpus { Id = id, Name = name, CreatedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private CatalogDbContext NewContext() => new(_services.GetRequiredService<DbContextOptions<CatalogDbContext>>());

    private async Task AddSet(string id, string corpus, string name, string model = "embeddinggemma", string? framing = null)
    {
        await using var db = NewContext();
        db.ChunkSets.Add(new ChunkSet
        {
            Id = id,
            CorpusId = corpus,
            Name = name,
            EmbeddingModel = model,
            EmbeddingProvider = "ollama",
            CollectionName = $"dexicon_{id}",
            BoundaryMode = "blank-line",
            CreatedUtc = DateTime.UtcNow,
            FramingRecorded = framing,
        });
        await db.SaveChangesAsync();
    }

    private async Task<ChunkSet> Stored(string id)
    {
        await using var db = NewContext();
        return await db.ChunkSets.AsNoTracking().SingleAsync(s => s.Id == id, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ASetWithNoRecordGetsTheDocumentTemplate()
    {
        await AddSet("a", "c-alpha", "one");
        await using var db = NewContext();

        var written = await ModelFraming.EnsureRecordedAsync(
            db, await Stored("a"), Framed, NullLogger.Instance, TestContext.Current.CancellationToken);

        written.ShouldBeTrue();
        (await Stored("a")).FramingRecorded.ShouldBe("title: none | text: {text}");
    }

    [Fact]
    public async Task ASetWithARecordKeepsIt()
    {
        await AddSet("a", "c-alpha", "one", framing: "{text}");
        await using var db = NewContext();

        var written = await ModelFraming.EnsureRecordedAsync(
            db, await Stored("a"), Framed, NullLogger.Instance, TestContext.Current.CancellationToken);

        written.ShouldBeFalse();
        (await Stored("a")).FramingRecorded.ShouldBe("{text}");
    }

    [Fact]
    public async Task ARecordWrittenWhileTheCallerHeldAStaleCopyIsNotOverwritten()
    {
        // The caller read the set before another writer recorded it: its copy says "no record", the row does not.
        await AddSet("a", "c-alpha", "one");
        var stale = await Stored("a");
        await AddFramingBehindTheCallersBack("a", "{text}");
        await using var db = NewContext();

        var written = await ModelFraming.EnsureRecordedAsync(
            db, stale, Framed, NullLogger.Instance, TestContext.Current.CancellationToken);

        written.ShouldBeFalse();
        (await Stored("a")).FramingRecorded.ShouldBe("{text}");
    }

    private async Task AddFramingBehindTheCallersBack(string id, string framing)
    {
        await using var db = NewContext();
        await db.ChunkSets.Where(s => s.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.FramingRecorded, framing), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReplacingTheRecordChangesOnlyThatSet()
    {
        await AddSet("a", "c-alpha", "one", framing: "{text}");
        await AddSet("b", "c-alpha", "two", framing: "{text}");
        await using var db = NewContext();

        await ModelFraming.RecordAsync(db, await Stored("a"), Framed, TestContext.Current.CancellationToken);

        (await Stored("a")).FramingRecorded.ShouldBe("title: none | text: {text}");
        (await Stored("b")).FramingRecorded.ShouldBe("{text}");
    }

    private static readonly string[] AllCorpora = ["c-alpha", "c-beta"];

    [Fact]
    public async Task ASetEmbeddedUnderAnotherTemplateIsListedWithBothTemplates()
    {
        await AddSet("a", "c-alpha", "one", framing: "{text}");
        _profiles.Templates = Framed;
        await using var db = NewContext();

        var changed = await Dexicon.Api.SystemEndpoints.ChangedFramingsAsync(
            db, _profiles, AllCorpora, TestContext.Current.CancellationToken);

        var entry = changed.ShouldHaveSingleItem();
        entry.Set.ShouldBe("alpha:one");
        entry.Provider.ShouldBe("ollama");
        entry.Model.ShouldBe("embeddinggemma");
        entry.Recorded.ShouldBe("{text}");
        entry.Current.ShouldBe("title: none | text: {text}");
    }

    [Fact]
    public async Task ASetThatMatchesTheProfileOrHasNoRecordIsNotListed()
    {
        await AddSet("a", "c-alpha", "same", framing: "title: none | text: {text}");
        await AddSet("b", "c-alpha", "unrecorded");
        _profiles.Templates = Framed;
        await using var db = NewContext();

        (await Dexicon.Api.SystemEndpoints.ChangedFramingsAsync(
            db, _profiles, AllCorpora, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task OnlyTheCorporaTheCallerReachesAreListedInOrder()
    {
        await AddSet("a", "c-first", "x", framing: "{text}");
        await AddSet("b", "c-second", "y", framing: "{text}");
        await AddSet("c", "c-second", "a", framing: "{text}");
        await AddSet("d", "c-beta", "unreached", framing: "{text}");
        _profiles.Templates = Framed;
        await using var db = NewContext();

        var reached = await Dexicon.Api.SystemEndpoints.ChangedFramingsAsync(
            db, _profiles, ["c-first", "c-second"], TestContext.Current.CancellationToken);
        var second = await Dexicon.Api.SystemEndpoints.ChangedFramingsAsync(
            db, _profiles, ["c-second"], TestContext.Current.CancellationToken);

        // By corpus name and then set name, which is not the order of the ids.
        reached.Select(c => c.Set).ShouldBe(["apex:a", "apex:y", "zeta:x"]);
        second.Select(c => c.Set).ShouldBe(["apex:a", "apex:y"]);
    }

    [Fact]
    public async Task TwoSetsOnOneModelAreComparedEachWithItsOwnRecord()
    {
        // One built before the template changed and one after: only the first is reported.
        await AddSet("a", "c-alpha", "old", framing: "{text}");
        await AddSet("b", "c-alpha", "new", framing: "title: none | text: {text}");
        _profiles.Templates = Framed;
        await using var db = NewContext();

        var changed = await Dexicon.Api.SystemEndpoints.ChangedFramingsAsync(
            db, _profiles, AllCorpora, TestContext.Current.CancellationToken);

        changed.ShouldHaveSingleItem().Set.ShouldBe("alpha:old");
    }

    [Fact]
    public async Task EachModelsProfileIsLookedUpOnceHoweverManySetsUseIt()
    {
        await AddSet("a", "c-alpha", "one", framing: "{text}");
        await AddSet("b", "c-alpha", "two", framing: "{text}");
        await AddSet("c", "c-beta", "three", model: "nomic-embed-text", framing: "{text}");
        _profiles.Templates = Prefixed;
        await using var db = NewContext();

        var changed = await Dexicon.Api.SystemEndpoints.ChangedFramingsAsync(
            db, _profiles, AllCorpora, TestContext.Current.CancellationToken);

        changed.Count.ShouldBe(3);
        _profiles.Lookups.ShouldBe(2, "embeddinggemma once for two sets, nomic-embed-text once");
    }
}

/// <summary>
/// The indexer's part: when a pass records the document template and when it does not.
/// </summary>
public sealed class ModelFramingIndexing
{
    private static readonly ModelTemplates Framed =
        new("title: none | text: {text}", "task: search result | query: {text}", TemplateOrigin.BuiltIn);

    private static readonly ModelTemplates Prefixed =
        new("search_document: {text}", "search_query: {text}", TemplateOrigin.Configured);

    private static async Task<IndexingHarness> StartAsync()
    {
        var harness = await IndexingHarness.StartAsync("notes");
        await harness.WriteFileAsync("one.txt", IndexingHarness.Prose("first"));
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        harness.Embedder = new FixedEmbedder();
        return harness;
    }

    private static async Task<string?> RecordedAsync(IndexingHarness harness)
    {
        await using var db = harness.NewContext();
        return (await db.ChunkSets.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).FramingRecorded;
    }

    [Theory]
    [InlineData(JobKind.Full)]
    [InlineData(JobKind.Refresh)]
    public async Task AFirstPassRecordsTheTemplateItEmbedsWith(JobKind kind)
    {
        // A refresh replaces no record, so it is the record made before the first write that a refresh-only set has.
        await using var harness = await StartAsync();
        harness.Profiles.Templates = Framed;

        await harness.RunIndexAsync(kind, cancel: TestContext.Current.CancellationToken);

        (await RecordedAsync(harness)).ShouldBe("title: none | text: {text}");
    }

    [Fact]
    public async Task ARefreshAfterTheTemplateChangedLeavesTheRecordAsItWas()
    {
        await using var harness = await StartAsync();
        await harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);
        harness.Profiles.Templates = Framed;

        await harness.RunIndexAsync(JobKind.Refresh, cancel: TestContext.Current.CancellationToken);

        // A refresh re-embeds the files whose fingerprint changed and says nothing about the set as a whole.
        (await RecordedAsync(harness)).ShouldBe("{text}");
    }

    [Theory]
    [InlineData(JobKind.Full)]
    [InlineData(JobKind.Rebuild)]
    public async Task AFullPassAfterTheTemplateChangedRecordsTheNewTemplate(JobKind kind)
    {
        await using var harness = await StartAsync();
        await harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);
        harness.Profiles.Templates = Framed;

        await harness.RunIndexAsync(kind, cancel: TestContext.Current.CancellationToken);

        (await RecordedAsync(harness)).ShouldBe("title: none | text: {text}");
    }

    [Fact]
    public async Task AFullPassThatLeftAFileOnItsEarlierVectorsKeepsTheEarlierRecord()
    {
        await using var harness = await StartAsync();
        await harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);
        harness.Profiles.Templates = Framed;
        harness.Vectors.DeleteFailsForPath = "one.txt";

        await harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);

        // The file's old vectors were embedded raw and are still in the store, so the set is not one framing.
        (await RecordedAsync(harness)).ShouldBe("{text}");
    }

    [Fact]
    public async Task AFullPassDuringWhichTheProfileChangedLeavesTheEarlierRecord()
    {
        await using var harness = await StartAsync();
        await harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);
        harness.Profiles.Templates = Framed;
        // The profile is saved while the pass runs: at the second read of the store, which is the record gate's, after
        // the files are embedded. The first is the pass's reconcile at its start.
        var counts = 0;
        harness.Vectors.OnCountAsync = () =>
        {
            if (++counts == 2) harness.Profiles.Templates = Prefixed;
            return Task.CompletedTask;
        };

        await harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);

        counts.ShouldBe(2, "the profile was changed at the gate's read");
        // Neither the template the pass resolved nor the one the profile gives at the end describes its vectors.
        (await RecordedAsync(harness)).ShouldBe("{text}");
    }

    [Fact]
    public async Task AFullPassDuringWhichTheProfileWasSavedAndSavedBackLeavesTheEarlierRecord()
    {
        await using var harness = await StartAsync();
        await harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);
        harness.Profiles.Templates = Framed;
        // Two saves leave the template as it was, and the files embedded between them hold the other one.
        var counts = 0;
        harness.Vectors.OnCountAsync = async () =>
        {
            if (++counts == 2) await SaveProfileRowAsync(harness, updated: DateTime.UtcNow);
        };

        await harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);

        counts.ShouldBe(2);
        (await RecordedAsync(harness)).ShouldBe("{text}");
    }

    [Fact]
    public async Task AProfileSavedBeforeThePassBeganDoesNotKeepItFromRecording()
    {
        await using var harness = await StartAsync();
        await harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);
        harness.Profiles.Templates = Framed;
        // The save that queued this rebuild.
        await SaveProfileRowAsync(harness, updated: DateTime.UtcNow.AddMinutes(-5));

        await harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);

        (await RecordedAsync(harness)).ShouldBe("title: none | text: {text}");
    }

    private static async Task SaveProfileRowAsync(IndexingHarness harness, DateTime updated)
    {
        await using var db = harness.NewContext();
        db.ModelProfiles.Add(new EmbeddingModelProfile
        {
            Provider = "ollama",
            Model = EmbeddingTarget.Canonical(IndexingHarness.Model),
            DocumentTemplate = "title: none | text: {text}",
            QueryTemplate = "task: search result | query: {text}",
            CreatedUtc = updated,
            UpdatedUtc = updated,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task AFullPassWhoseCanaryRecordFaultsStillRecordsTheTemplateAndIsNotFailed()
    {
        await using var harness = await StartAsync();
        var embedder = new FixedEmbedder();
        harness.Embedder = embedder;
        await harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);
        harness.Profiles.Templates = Framed;
        // The pass asks the canary twice: to check the set before it writes, and to record it at the end.
        var calls = 0;
        embedder.OnCanary = () =>
        {
            if (++calls == 2) throw new InvalidOperationException("the provider faulted");
        };

        var job = await harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);

        calls.ShouldBe(2);
        job.State.ShouldBe(JobState.Succeeded);
        (await RecordedAsync(harness)).ShouldBe("title: none | text: {text}");
    }

    /// <summary>Every embedding is zeros but for the canary, which answers a fixed unit vector.</summary>
    private sealed class FixedEmbedder : IEmbeddingService
    {
        /// <summary>Runs when the canary is asked, before it answers.</summary>
        public Action? OnCanary { get; set; }

        public Task<IReadOnlyList<float[]>> EmbedAsync(EmbeddingTarget target, EmbedPurpose purpose,
            IReadOnlyList<string> inputs, string? source = null, CancellationToken ct = default)
        {
            if (IndexingHarness.IsCanary(inputs)) OnCanary?.Invoke();

            return Task.FromResult<IReadOnlyList<float[]>>(
                [.. inputs.Select(_ => IndexingHarness.IsCanary(inputs) ? new float[] { 1, 0, 0, 0 } : new float[768])]);
        }

        public Task<int?> CountTokensAsync(EmbeddingTarget target, string text, CancellationToken ct = default) =>
            Task.FromResult<int?>(null);

        public Task<int> ProbeDimensionsAsync(EmbeddingTarget target, CancellationToken ct = default) =>
            Task.FromResult(768);

        public int KnownDimensions(EmbeddingTarget target) => 768;
    }
}
