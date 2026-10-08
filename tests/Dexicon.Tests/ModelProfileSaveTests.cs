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

    public async Task InitializeAsync()
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

    public async Task DisposeAsync()
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

        var job = await _db.Jobs.SingleAsync();
        job.Kind.ShouldBe(JobKind.Rebuild);
        job.ChunkSetId.ShouldBe(set.Id);
    }

    [Fact]
    public async Task SavingFromTheTaggedNameStoresOneRowUnderTheNameWithoutTheTag()
    {
        await SetAsync("s1", "default", "embeddinggemma");

        var saved = await SaveAsync("embeddinggemma:latest");

        saved.Model.ShouldBe("embeddinggemma");
        (await _db.ModelProfiles.Select(p => p.Model).ToListAsync()).ShouldBe(["embeddinggemma"]);
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
        var rows = await _db.ModelProfiles.ToListAsync();
        rows.Count.ShouldBe(1);
        rows[0].Model.ShouldBe("embeddinggemma");
        rows[0].QueryTemplate.ShouldBe("q: {text}");
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
        (await _db.Jobs.Select(j => j.ChunkSetId).ToListAsync()).Order().ToList().ShouldBe(["s1", "s2"]);
    }

    [Fact]
    public async Task AModelWithNoSetsAnswersWithNothingToRebuild()
    {
        var saved = await SaveAsync("embeddinggemma:latest");

        saved.Reindexing.ShouldBeEmpty();
        saved.Note.ShouldBeNull();
        (await _db.Jobs.CountAsync()).ShouldBe(0);
    }
}
