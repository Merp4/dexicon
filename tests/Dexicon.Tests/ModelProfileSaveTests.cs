using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Embedding;
using Dexicon.Core.Indexing;
using Dexicon.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dexicon.Tests;

/// <summary>
/// Saving a framing from the row the Models screen shows.
///
/// Ollama lists <c>embeddinggemma:latest</c> and the screen sends that name, while a chunk set
/// records <c>embeddinggemma</c>. The save stored the profile under the tagged name and looked for
/// the sets to rebuild by exact equality, so it answered <c>reindexing: []</c>, rebuilt nothing,
/// and the indexer, which reads the untagged name, never saw the framing. These call the handler
/// the route is mapped to.
/// </summary>
public sealed class ModelProfileSaveTests : IAsyncLifetime
{
    private const string Doc = "title: custom | text: {text}";
    private const string Query = "task: custom | query: {text}";

    private SqliteConnection _connection = null!;
    private CatalogDbContext _db = null!;
    private MemoryCache _cache = null!;
    private ModelProfiles _profiles = null!;
    private IndexJobQueue _queue = null!;
    private IOptions<DexiconOptions> _options = null!;

    public async ValueTask InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();

        _options = Options.Create(new DexiconOptions());
        _cache = new MemoryCache(new MemoryCacheOptions());
        _profiles = new ModelProfiles(_db, _cache);
        _queue = new IndexJobQueue(_db, new WorkScheduler(_options), NullLogger<IndexJobQueue>.Instance);

        _db.Corpora.Add(new Corpus { Id = "c", Name = "docs", CreatedUtc = DateTime.UtcNow });
        await _db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _cache.Dispose();
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task<ChunkSet> SetAsync(string id, string name, string model, string provider = "ollama")
    {
        var set = new ChunkSet
        {
            Id = id,
            CorpusId = "c",
            Name = name,
            EmbeddingProvider = provider,
            EmbeddingModel = model,
            CollectionName = $"dexicon_{id}",
            BoundaryMode = "blank-line",
            CreatedUtc = DateTime.UtcNow,
        };
        _db.ChunkSets.Add(set);
        await _db.SaveChangesAsync();
        return set;
    }

    private static RequestContext AsAdmin() => new()
    {
        Principal = new Principal("k", "admin", new HashSet<string>([Scopes.Admin], StringComparer.Ordinal)),
    };

    private async Task<ModelProfileSaved> SaveAsync(string model, string? provider = null)
    {
        var result = await SystemEndpoints.SaveModelProfileAsync(
            new SaveModelProfileRequest(model, Doc, Query, provider), AsAdmin(), _db, _cache, _queue, _options,
            CancellationToken.None);
        return result.ShouldBeOfType<Ok<ModelProfileSaved>>().Value.ShouldNotBeNull();
    }

    private async Task<ModelProfileSaved> SaveWithAsync(
        string model, string document, string query, string? notes = null)
    {
        var result = await SystemEndpoints.SaveModelProfileAsync(
            new SaveModelProfileRequest(model, document, query, Notes: notes), AsAdmin(), _db, _cache, _queue,
            _options, CancellationToken.None);
        return result.ShouldBeOfType<Ok<ModelProfileSaved>>().Value.ShouldNotBeNull();
    }

    /// <summary>
    /// Sets every job to Running. The queue merges a request into a job that is still Queued, so a test that
    /// counts jobs after a second save has to start the first job. Otherwise the count is 1 under either rule.
    /// </summary>
    private async Task StartQueuedJobsAsync()
    {
        foreach (var job in await _db.Jobs.ToListAsync(TestContext.Current.CancellationToken))
            job.State = JobState.Running;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private Task<int> JobCountAsync() => _db.Jobs.CountAsync(TestContext.Current.CancellationToken);

    private Task<ModelTemplates> EffectiveAsync(string model, string provider = "ollama") =>
        _profiles.ForAsync(new EmbeddingTarget(provider, model));

    [Fact]
    public async Task SavingFromTheTaggedNameFramesAndRebuildsASetRecordedWithoutTheTag()
    {
        var set = await SetAsync("s1", "default", "embeddinggemma");

        var saved = await SaveAsync("embeddinggemma:latest");

        saved.Reindexing.ShouldBe(["docs:default"]);
        (await EffectiveAsync("embeddinggemma")).Apply(EmbedPurpose.Query, "x")
            .ShouldBe("task: custom | query: x", "the set records the untagged name, and that is what the indexer reads");
        (await EffectiveAsync("embeddinggemma")).Origin.ShouldBe(TemplateOrigin.Configured);

        var job = await _db.Jobs.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        job.Kind.ShouldBe(JobKind.Rebuild);
        job.ChunkSetId.ShouldBe(set.Id);
    }

    [Fact]
    public async Task SavingFromTheTaggedNameStoresOneRowUnderTheNameWithoutTheTag()
    {
        await SetAsync("s1", "default", "embeddinggemma");

        var saved = await SaveAsync("embeddinggemma:latest");

        saved.Model.ShouldBe("embeddinggemma");
        (await _db.ModelProfiles.Select(p => p.Model).ToListAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(["embeddinggemma"]);
    }

    [Fact]
    public async Task SavingFromTheUntaggedNameRebuildsASetRecordedWithTheTag()
    {
        await SetAsync("s1", "default", "embeddinggemma:latest");

        var saved = await SaveAsync("embeddinggemma");

        saved.Reindexing.ShouldBe(["docs:default"]);
        (await EffectiveAsync("embeddinggemma:latest")).Apply(EmbedPurpose.Document, "x")
            .ShouldBe("title: custom | text: x");
    }

    [Fact]
    public async Task SavingFromEitherSpellingUpdatesTheOneRow()
    {
        await SaveAsync("embeddinggemma");
        var second = await SystemEndpoints.SaveModelProfileAsync(
            new SaveModelProfileRequest("embeddinggemma:latest", "{text}", "q: {text}"), AsAdmin(), _db, _cache,
            _queue, _options, CancellationToken.None);

        second.ShouldBeOfType<Ok<ModelProfileSaved>>();
        var rows = await _db.ModelProfiles.ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        rows.Count.ShouldBe(1);
        rows[0].Model.ShouldBe("embeddinggemma");
        rows[0].QueryTemplate.ShouldBe("q: {text}");
    }

    [Fact]
    public async Task SavingUnderAnotherLetterCaseStoresOneRowAndRebuildsTheSetsOnTheModel()
    {
        await SetAsync("s1", "default", "embeddinggemma");
        await SetAsync("s2", "mixed", "EmbeddingGemma:Latest");

        var saved = await SaveAsync("EmbeddingGemma:LATEST");
        await SaveAsync("embeddinggemma");

        saved.Model.ShouldBe("embeddinggemma");
        saved.Reindexing.Order().ToList().ShouldBe(["docs:default", "docs:mixed"]);
        (await _db.ModelProfiles.Select(p => p.Model).ToListAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(["embeddinggemma"]);
        foreach (var name in new[] { "embeddinggemma", "EMBEDDINGGEMMA", "EmbeddingGemma:latest" })
            (await EffectiveAsync(name)).Origin.ShouldBe(TemplateOrigin.Configured, name);
    }

    [Fact]
    public async Task TheTwoSpellingsOfOneModelResolveToTheSavedProfileEvenWhenBothWereCached()
    {
        // ForAsync caches per name for 30 seconds. Warmed under both spellings before the save, so
        // a save that clears one key leaves the other answering with the old framing.
        (await EffectiveAsync("embeddinggemma")).Origin.ShouldBe(TemplateOrigin.BuiltIn);
        (await EffectiveAsync("embeddinggemma:latest")).Origin.ShouldBe(TemplateOrigin.BuiltIn);

        await SaveAsync("embeddinggemma:latest");

        (await EffectiveAsync("embeddinggemma")).Origin.ShouldBe(TemplateOrigin.Configured);
        (await EffectiveAsync("embeddinggemma:latest")).Origin.ShouldBe(TemplateOrigin.Configured);
    }

    [Fact]
    public async Task OnlyTheSetsOnThatModelAndProviderAreRebuilt()
    {
        // Controls: a different tag is a different model, another model is another model, and the
        // same name under another provider is another vector space.
        await SetAsync("s1", "tagged", "embeddinggemma:latest");
        await SetAsync("s2", "untagged", "embeddinggemma");
        await SetAsync("s3", "other-tag", "embeddinggemma:v2");
        await SetAsync("s4", "other-model", "nomic-embed-text");
        await SetAsync("s5", "other-provider", "embeddinggemma", provider: "openai");

        var saved = await SaveAsync("embeddinggemma:latest");

        saved.Reindexing.Order().ToList().ShouldBe(["docs:tagged", "docs:untagged"]);
        (await _db.Jobs.Select(j => j.ChunkSetId).ToListAsync(cancellationToken: TestContext.Current.CancellationToken)).Order().ToList().ShouldBe(["s1", "s2"]);
    }

    [Fact]
    public async Task SavingTheSameTemplatesAgainQueuesNoJob()
    {
        await SetAsync("s1", "default", "embeddinggemma");
        await SaveAsync("embeddinggemma");
        await StartQueuedJobsAsync();

        var again = await SaveAsync("embeddinggemma");

        again.Reindexing.ShouldBeEmpty();
        again.Note.ShouldBe("The templates are unchanged: no chunk set was re-indexed.");
        (await JobCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task SavingTheSameTemplatesFromAnotherSpellingOfTheModelQueuesNoJob()
    {
        await SetAsync("s1", "default", "embeddinggemma");
        await SaveAsync("embeddinggemma");
        await StartQueuedJobsAsync();

        var again = await SaveAsync("EmbeddingGemma:latest");

        again.Reindexing.ShouldBeEmpty();
        (await JobCountAsync()).ShouldBe(1);
        (await _db.ModelProfiles.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task ChangingTheDocumentTemplateQueuesOneRebuildForEachSetOnTheModel()
    {
        await SetAsync("s1", "first", "embeddinggemma");
        await SetAsync("s2", "second", "embeddinggemma");
        await SetAsync("s3", "other-model", "nomic-embed-text");
        await SaveAsync("embeddinggemma");
        await StartQueuedJobsAsync();

        var changed = await SaveWithAsync("embeddinggemma", "doc: {text}", Query);

        changed.Reindexing.Order().ToList().ShouldBe(["docs:first", "docs:second"]);
        var queued = await _db.Jobs.Where(j => j.State == JobState.Queued)
            .Select(j => new { j.ChunkSetId, j.Kind }).ToListAsync(TestContext.Current.CancellationToken);
        queued.Select(j => j.ChunkSetId).Order().ToList().ShouldBe(["s1", "s2"]);
        queued.ShouldAllBe(j => j.Kind == JobKind.Rebuild);
    }

    [Fact]
    public async Task ChangingOnlyTheQueryTemplateQueuesARebuild()
    {
        // A file's fingerprint covers both templates, so the next refresh would delete and re-embed every
        // file of the set after a query-only edit, with the same vectors as before. The rebuild is queued
        // here instead, and the caller is told which sets it covers.
        const string otherQuery = "task: other | query: {text}";
        new ModelTemplates(Doc, Query, TemplateOrigin.Configured).Fingerprint
            .ShouldNotBe(new ModelTemplates(Doc, otherQuery, TemplateOrigin.Configured).Fingerprint,
                "if the fingerprint stops covering the query template, a query-only edit can queue nothing");

        await SetAsync("s1", "default", "embeddinggemma");
        await SaveAsync("embeddinggemma");
        await StartQueuedJobsAsync();

        var changed = await SaveWithAsync("embeddinggemma", Doc, otherQuery);

        changed.Reindexing.ShouldBe(["docs:default"]);
        (await JobCountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task ChangingOnlyTheNotesQueuesNoJobAndStoresThem()
    {
        await SetAsync("s1", "default", "embeddinggemma");
        await SaveAsync("embeddinggemma");
        await StartQueuedJobsAsync();

        var again = await SaveWithAsync("embeddinggemma", Doc, Query, notes: "from the model card");

        again.Reindexing.ShouldBeEmpty();
        (await JobCountAsync()).ShouldBe(1);
        (await _db.ModelProfiles.SingleAsync(TestContext.Current.CancellationToken)).Notes
            .ShouldBe("from the model card");
    }

    /// <summary>
    /// Backdates the profile row, then returns a time after it, as the start of a pass that began after the row
    /// was last changed. <see cref="ModelFraming.UnchangedSinceAsync"/> compares the row's UpdatedUtc with it.
    /// </summary>
    private async Task<DateTime> StartPassAfterTheRowAsync()
    {
        var row = await _db.ModelProfiles.SingleAsync(TestContext.Current.CancellationToken);
        row.UpdatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return DateTime.UtcNow;
    }

    [Fact]
    public async Task AnUnchangedSaveWhileAPassRunsLeavesThePassFreeToRecordItsFraming()
    {
        // A pass records the framing it used only if no template was saved since it began. An identical save
        // queues no rebuild, so a pass that it stopped from recording would leave /healthz reporting a stale
        // framing with nothing queued to correct it.
        var set = await SetAsync("s1", "default", "embeddinggemma");
        await SaveAsync("embeddinggemma");
        var passStarted = await StartPassAfterTheRowAsync();
        var resolved = await EffectiveAsync("embeddinggemma");

        await SaveAsync("embeddinggemma");
        await SaveWithAsync("embeddinggemma", Doc, Query, notes: "from the model card");

        (await ModelFraming.UnchangedSinceAsync(_db, _profiles, set, resolved, passStarted,
            TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task ChangingOnlyTheQueryTemplateWhileAPassRunsStopsItRecordingItsFraming()
    {
        // The control for the test above: a template change does move the row's time, and the document
        // template alone cannot show it, since a query-only change leaves that the same.
        var set = await SetAsync("s1", "default", "embeddinggemma");
        await SaveAsync("embeddinggemma");
        var passStarted = await StartPassAfterTheRowAsync();
        var resolved = await EffectiveAsync("embeddinggemma");

        await SaveWithAsync("embeddinggemma", Doc, "task: other | query: {text}");

        (await EffectiveAsync("embeddinggemma")).Document.ShouldBe(resolved.Document);
        (await ModelFraming.UnchangedSinceAsync(_db, _profiles, set, resolved, passStarted,
            TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task SavingARowForTheFirstTimeQueuesARebuildEvenWhenItEqualsTheBuiltInFraming()
    {
        // Sets built before a built-in entry existed were embedded raw. Saving the entry as a row is how
        // they are rebuilt (D-45), so a new row queues whatever it contains.
        await SetAsync("s1", "default", "embeddinggemma");
        var builtIn = _profiles.Suggest("embeddinggemma").ShouldNotBeNull();

        var saved = await SaveWithAsync("embeddinggemma", builtIn.Document, builtIn.Query);

        saved.Reindexing.ShouldBe(["docs:default"]);
        (await JobCountAsync()).ShouldBe(1);
    }

    /// <summary>Cancels the request the moment a save has committed, as a client that goes away would.</summary>
    private sealed class CancelAfterSave(CancellationTokenSource source) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            source.Cancel();
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task ACancelAfterTheSaveCommitsStillRebuildsTheSetsOnTheModel()
    {
        // The framing is committed when the cancel arrives. Dropping the rebuilds then leaves
        // queries framed one way against stored document vectors framed another, and saving the
        // same values again queues nothing.
        await SetAsync("s1", "default", "embeddinggemma");
        using var cancel = new CancellationTokenSource();
        await using var cancelling = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite(_connection).AddInterceptors(new CancelAfterSave(cancel)).Options);

        var result = await SystemEndpoints.SaveModelProfileAsync(
            new SaveModelProfileRequest("embeddinggemma:latest", Doc, Query), AsAdmin(), cancelling, _cache,
            _queue, _options, cancel.Token);

        result.ShouldBeOfType<Ok<ModelProfileSaved>>().Value!.Reindexing.ShouldBe(["docs:default"]);
        (await _db.Jobs.CountAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(1);

        // A second save of the same values queues nothing, so a rebuild dropped by the cancel would not come back.
        await StartQueuedJobsAsync();
        (await SaveAsync("embeddinggemma:latest")).Reindexing.ShouldBeEmpty();
        (await JobCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task AModelWithNoSetsAnswersWithNothingToRebuild()
    {
        var saved = await SaveAsync("embeddinggemma:latest");

        saved.Reindexing.ShouldBeEmpty();
        saved.Note.ShouldBeNull();
        (await _db.Jobs.CountAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(0);
    }
}
