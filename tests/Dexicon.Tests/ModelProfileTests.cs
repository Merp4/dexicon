using Dexicon.Core.Catalog;
using Dexicon.Core.Embedding;
using Dexicon.Core.Indexing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Dexicon.Tests;

/// <summary>
/// Task templates per embedding model.
///
/// Most embedding models are trained with a task instruction wrapped around the input and
/// retrieve measurably worse without it — and nothing fails, so the only symptom is worse
/// ranking. One project measured EmbeddingGemma at recall@1 16/25 without its prefixes and
/// 23/25 with them.
///
/// The design constraint these tests exist to protect: models are added at RUNTIME through
/// the UI. A build that hard-coded the framing for models it knew about would give every
/// model pulled afterwards silently wrong framing, which would quietly undo the point of
/// runtime model management. So resolution is: saved row, then built-in suggestion, then
/// raw — and anything built-in must be overridable.
/// </summary>
public sealed class ModelProfileTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private CatalogDbContext _db = null!;
    private ModelProfiles _profiles = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        _profiles = new ModelProfiles(_db, new MemoryCache(new MemoryCacheOptions()));
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static EmbeddingTarget Target(string model) => new("ollama", model);

    // ── The out-of-the-box path ──────────────────────────────────────────────

    [Fact]
    public async Task AKnownModelIsFramedCorrectlyWithNothingConfigured()
    {
        // The bug this closes: Dexicon sent raw text to nomic-embed-text, whose model card
        // calls the prefixes required rather than optional.
        var templates = await _profiles.ForAsync(Target("nomic-embed-text"));

        templates.Apply(EmbedPurpose.Document, "hello").ShouldBe("search_document: hello");
        templates.Apply(EmbedPurpose.Query, "hello").ShouldBe("search_query: hello");
        templates.Origin.ShouldBe(TemplateOrigin.BuiltIn);
    }

    [Fact]
    public async Task ATemplateCanWrapRatherThanPrefix()
    {
        // EmbeddingGemma's document form is not a prefix — it surrounds the text. This is
        // why the stored value is a template with a {text} placeholder and not a prefix
        // string: a prefix-shaped design could not express it.
        var templates = await _profiles.ForAsync(Target("embeddinggemma"));

        templates.Apply(EmbedPurpose.Document, "hello").ShouldBe("title: none | text: hello");
        templates.Apply(EmbedPurpose.Query, "hello").ShouldBe("task: search result | query: hello");
    }

    [Fact]
    public async Task ATaggedModelResolvesToItsFamily()
    {
        // Ollama reports `nomic-embed-text:latest`; a chunk set may record
        // `nomic-embed-text` or `nomic-embed-text:v1.5`. All the same model.
        foreach (var name in new[] { "nomic-embed-text", "nomic-embed-text:latest", "nomic-embed-text:v1.5" })
            (await _profiles.ForAsync(Target(name)))
                .Apply(EmbedPurpose.Query, "x").ShouldBe("search_query: x");
    }

    // ── The runtime-model path, which is the point ───────────────────────────

    [Fact]
    public async Task AnUnknownModelIsEmbeddedRawRatherThanGuessedAt()
    {
        // Someone pulls a model this build has never heard of. Inventing a prefix for it
        // would be worse than none: the model would embed the literal text
        // "search_query:" as content. Raw, and the UI says so.
        var templates = await _profiles.ForAsync(Target("some-model-released-next-year"));

        templates.IsRaw.ShouldBeTrue();
        templates.Origin.ShouldBe(TemplateOrigin.None);
        templates.Apply(EmbedPurpose.Query, "hello").ShouldBe("hello");
    }

    [Fact]
    public async Task ASavedRowConfiguresAModelThisBuildNeverHeardOf()
    {
        // The whole design constraint: a model added at runtime must be configurable at
        // runtime, without a code change.
        _db.ModelProfiles.Add(new EmbeddingModelProfile
        {
            Provider = "ollama",
            Model = "brand-new-embed",
            DocumentTemplate = "passage: {text}",
            QueryTemplate = "question: {text}",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var templates = await _profiles.ForAsync(Target("brand-new-embed"));

        templates.Apply(EmbedPurpose.Document, "hi").ShouldBe("passage: hi");
        templates.Origin.ShouldBe(TemplateOrigin.Configured);
    }

    [Fact]
    public async Task ASavedRowOverridesTheBuiltInSuggestion()
    {
        // Built-ins are a fallback, not the mechanism. If a model card changes, or this
        // build simply got one wrong, the operator wins without waiting for a release.
        _db.ModelProfiles.Add(new EmbeddingModelProfile
        {
            Provider = "ollama",
            Model = "nomic-embed-text",
            DocumentTemplate = "corrected-doc: {text}",
            QueryTemplate = "corrected-query: {text}",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var templates = await _profiles.ForAsync(Target("nomic-embed-text"));

        templates.Apply(EmbedPurpose.Query, "x").ShouldBe("corrected-query: x");
        templates.Origin.ShouldBe(TemplateOrigin.Configured);
    }

    [Fact]
    public async Task TheSameModelOnTwoProvidersIsTwoProfiles()
    {
        // Keyed on both, because a hosted provider may frame a same-named model
        // differently — and their vectors are not interchangeable either way.
        _db.ModelProfiles.Add(new EmbeddingModelProfile
        {
            Provider = "openai",
            Model = "nomic-embed-text",
            DocumentTemplate = "{text}",
            QueryTemplate = "{text}",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        (await _profiles.ForAsync(new EmbeddingTarget("openai", "nomic-embed-text"))).IsRaw.ShouldBeTrue();
        (await _profiles.ForAsync(new EmbeddingTarget("ollama", "nomic-embed-text"))).IsRaw.ShouldBeFalse();
    }

    [Fact]
    public void AModelDeliberatelyWithoutFramingIsRecordedAsSuch()
    {
        // BGE-M3 is trained without task prefixes. Listed explicitly so that "no template"
        // reads as a decision someone made rather than a model nobody got round to.
        var suggestion = _profiles.Suggest("bge-m3");

        suggestion.ShouldNotBeNull();
        suggestion.IsRaw.ShouldBeTrue();
        suggestion.Origin.ShouldBe(TemplateOrigin.BuiltIn);
    }

    // ── Staleness ────────────────────────────────────────────────────────────

    [Fact]
    public void ChangingTheFramingInvalidatesExistingChunks()
    {
        // Both sides of a retrieval must agree. If documents were embedded raw and queries
        // start arriving framed, nothing errors — the ranking just quietly gets worse. So
        // the framing is part of the staleness key, and editing a profile re-indexes.
        var set = new ChunkSet
        {
            Id = "s", CorpusId = "c", Name = "default",
            EmbeddingProvider = "ollama", EmbeddingModel = "nomic-embed-text",
            EmbeddingDimensions = 768, CollectionName = "x",
            ChunkSize = 768, ChunkOverlap = 100, BoundaryMode = "none",
            CreatedUtc = DateTime.UtcNow,
        };

        var raw = CorpusIndexer.ChunkingFingerprint(set, "blob", ModelTemplates.Raw);
        var framed = CorpusIndexer.ChunkingFingerprint(
            set, "blob", new ModelTemplates("search_document: {text}", "search_query: {text}", TemplateOrigin.BuiltIn));

        framed.ShouldNotBe(raw, "text framed differently is a different vector, so it is stale");
    }

    [Fact]
    public void TheSameFramingIsStable()
    {
        // The other half: resolving the same profile twice must not look like a change, or
        // every refresh would re-embed the entire corpus.
        var set = new ChunkSet
        {
            Id = "s", CorpusId = "c", Name = "default",
            EmbeddingProvider = "ollama", EmbeddingModel = "nomic-embed-text",
            EmbeddingDimensions = 768, CollectionName = "x",
            ChunkSize = 768, ChunkOverlap = 100, BoundaryMode = "none",
            CreatedUtc = DateTime.UtcNow,
        };

        var a = new ModelTemplates("search_document: {text}", "search_query: {text}", TemplateOrigin.BuiltIn);
        var b = new ModelTemplates("search_document: {text}", "search_query: {text}", TemplateOrigin.Configured);

        // Origin differs — where it came from is not part of what it does.
        CorpusIndexer.ChunkingFingerprint(set, "blob", a)
            .ShouldBe(CorpusIndexer.ChunkingFingerprint(set, "blob", b));
    }
}
