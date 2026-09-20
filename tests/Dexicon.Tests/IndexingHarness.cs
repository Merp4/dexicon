using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Documents;
using Dexicon.Core.Embedding;
using Dexicon.Core.Indexing;
using Dexicon.Core.Search;
using Dexicon.Core.Vectors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dexicon.Tests;

/// <summary>
/// A real <see cref="CorpusIndexer"/> over a temporary workspace, a file-backed catalogue
/// and a vector store that holds its points.
///
/// The indexer's own tests had only ever reached its static helpers, so anything about a
/// whole pass — what it deletes, what it counts — was unreachable. Shared rather than
/// written per test class, because the wiring is what makes those tests expensive and
/// none of it is what any one test is about.
/// </summary>
internal sealed class IndexingHarness : IAsyncDisposable
{
    public const string Model = "nomic-embed-text";
    public const string Collection = $"dexicon__{Model}__768";
    public const string CorpusId = "corpus-1";
    public const string SourceId = "source-1";

    private readonly string _dataPath;
    private readonly ServiceProvider _services;

    /// <summary>The source root on disk. Files written here are what a pass walks.</summary>
    public string SourceDirectory { get; }

    public RecordingVectorStore Vectors { get; } = new();

    private IndexingHarness(string dataPath, string sourceDirectory, ServiceProvider services)
    {
        _dataPath = dataPath;
        SourceDirectory = sourceDirectory;
        _services = services;
    }

    public static async Task<IndexingHarness> StartAsync(string sourceRoot = "notes")
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"dexicon-indexing-{Guid.NewGuid():N}");
        var workspace = Path.Combine(dataPath, "workspace");
        var sourceDirectory = Path.Combine(workspace, sourceRoot);
        Directory.CreateDirectory(sourceDirectory);

        var options = Options.Create(new DexiconOptions
        {
            Storage = new StorageOptions { DataPath = dataPath },
            Indexing = new IndexingOptions { WorkspaceRoot = workspace },
        });

        // A file rather than :memory:, because the reader gives every file its own scope
        // and so its own connection. An in-memory database is per-connection, so the
        // extraction cache would write into a catalogue nothing else can see.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(options);
        services.AddDbContext<CatalogDbContext>(
            o => o.UseSqlite($"Data Source={Path.Combine(dataPath, "catalog.db")}"));
        services.AddSingleton<IndexingLimits>();
        services.AddScoped<ExtractedTextCache>();
        var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database.EnsureCreatedAsync();

        return new IndexingHarness(dataPath, sourceDirectory, provider);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        try { Directory.Delete(_dataPath, recursive: true); } catch { /* best effort */ }
    }

    public CatalogDbContext NewContext() =>
        _services.CreateScope().ServiceProvider.GetRequiredService<CatalogDbContext>();

    /// <summary>
    /// A document service on the harness's own storage, so blobs land where the indexer
    /// will look for them. Building one from fresh options puts them somewhere else and
    /// the pass then reports every attachment as having no stored text.
    /// </summary>
    public DocumentService NewDocumentService(CatalogDbContext db) =>
        new(db, _services.GetRequiredService<IOptions<DexiconOptions>>(),
            NullLogger<DocumentService>.Instance);

    /// <summary>One corpus, one source, and <paramref name="sets"/> chunk sets over it.</summary>
    public async Task SeedCorpusAsync(SourceKind kind, string sourceRoot = "notes", int sets = 1)
    {
        await using var db = NewContext();
        var corpus = new Corpus
        {
            Id = CorpusId,
            Name = "notes",
            State = CorpusState.Ready,
            CreatedUtc = DateTime.UtcNow,
        };

        for (var i = 0; i < sets; i++)
            corpus.ChunkSets.Add(new ChunkSet
            {
                Id = $"set-{i + 1}",
                CorpusId = corpus.Id,
                Name = i == 0 ? "default" : $"alt-{i}",
                EmbeddingModel = Model,
                EmbeddingDimensions = 768,
                CollectionName = Collection,
                ChunkSize = 256,
                ChunkOverlap = 0,
                BoundaryMode = "blank-line",
                IsDefault = i == 0,
                State = CorpusState.Ready,
                CreatedUtc = DateTime.UtcNow,
            });

        corpus.Sources.Add(new Source
        {
            Id = SourceId,
            CorpusId = corpus.Id,
            Kind = kind,
            RootPath = kind == SourceKind.Workspace ? sourceRoot : null,
            CreatedUtc = DateTime.UtcNow,
        });

        db.Corpora.Add(corpus);
        await db.SaveChangesAsync();
    }

    /// <summary>Queue a job and run it to completion, as the worker does.</summary>
    public async Task<IndexJob> RunIndexAsync(JobKind kind = JobKind.Refresh)
    {
        string jobId;
        await using (var db = NewContext())
        {
            var job = new IndexJob
            {
                Id = Ulid.NewUlid().ToString(),
                CorpusId = CorpusId,
                Kind = kind,
                State = JobState.Queued,
                QueuedUtc = DateTime.UtcNow,
            };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        var options = _services.GetRequiredService<IOptions<DexiconOptions>>();
        var scopes = _services.GetRequiredService<IServiceScopeFactory>();

        await using var runDb = NewContext();
        var indexer = new CorpusIndexer(
            runDb,
            new WorkspaceFileReader(scopes, options),
            Vectors,
            new FixedEmbedder(),
            new RawProfiles(),
            new DocumentService(runDb, options, NullLogger<DocumentService>.Instance),
            new CorpusLeases(scopes, NullLogger<CorpusLeases>.Instance),
            options,
            NullLogger<CorpusIndexer>.Instance);

        return await indexer.RunAsync(jobId, null, CancellationToken.None);
    }

    public async Task<FileChunkState> StateOfAsync(string relativePath)
    {
        await using var db = NewContext();
        return await db.FileChunkStates
            .Where(s => db.Files.Any(f => f.Id == s.FileId && f.RelativePath == relativePath))
            .FirstAsync();
    }

    public Task WriteFileAsync(string name, string content) =>
        File.WriteAllTextAsync(Path.Combine(SourceDirectory, name), content);

    /// <summary>Enough paragraphs to chunk, without being about anything.</summary>
    public static string Prose(string word) =>
        string.Join("\n\n", Enumerable.Range(1, 12).Select(i => $"Paragraph {i} about {word}."));

    // ---- doubles ----------------------------------------------------------------

    /// <summary>
    /// A vector store that actually holds points, so "the vectors are gone" is a question
    /// about its contents rather than about which methods were called. An assertion on a
    /// call would pass against a delete aimed at the wrong file.
    /// </summary>
    internal sealed class RecordingVectorStore : IVectorStore
    {
        private readonly List<Chunk> _points = [];

        public int CountFor(string filePath) => _points.Count(p => p.FilePath == filePath);

        public Task UpsertAsync(string collection, IReadOnlyList<Chunk> chunks,
            IReadOnlyList<float[]> vectors, CancellationToken ct = default)
        {
            _points.AddRange(chunks);
            return Task.CompletedTask;
        }

        public Task DeleteFileChunksAsync(string collection, string chunkSetId, string sourceId,
            string filePath, CancellationToken ct = default)
        {
            // The same four-part key Qdrant filters on. Matching on filePath alone would
            // make the test agree with a delete that drops another source's file too.
            _points.RemoveAll(p => p.ChunkSetId == chunkSetId
                                && p.SourceId == sourceId
                                && p.FilePath == filePath);
            return Task.CompletedTask;
        }

        public Task EnsureCollectionAsync(string collection, int dimensions, CancellationToken ct = default) =>
            Task.CompletedTask;

        public string CollectionNameFor(EmbeddingTarget target, int dimensions) => Collection;

        // Not reached by an indexing run. Throwing rather than returning a default, so a
        // change that starts calling one of these is visible instead of silently passing.
        public Task DeleteChunkSetAsync(string c, string s, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<int> PurgeUnsetChunksAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteCorpusAsync(string c, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<SearchHit>> GetFileChunksAsync(string c, string s, string f,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SearchResponse> SearchAsync(SearchQuery q, float[]? d, SparseVector s,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(long Points, int Dimensions)> GetStatsAsync(string c, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    /// <summary>One vector per input, of the right width. Nothing here is about ranking.</summary>
    internal sealed class FixedEmbedder : IEmbeddingService
    {
        public Task<IReadOnlyList<float[]>> EmbedAsync(EmbeddingTarget target, EmbedPurpose purpose,
            IReadOnlyList<string> inputs, string? source = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<float[]>>(inputs.Select(_ => new float[768]).ToList());

        public Task<int?> CountTokensAsync(EmbeddingTarget t, string text, CancellationToken ct = default) =>
            Task.FromResult<int?>(null);
        public Task<int> ProbeDimensionsAsync(EmbeddingTarget t, CancellationToken ct = default) =>
            Task.FromResult(768);
        public int KnownDimensions(EmbeddingTarget t) => 768;
    }

    internal sealed class RawProfiles : IModelProfiles
    {
        public Task<ModelTemplates> ForAsync(EmbeddingTarget t, CancellationToken ct = default) =>
            Task.FromResult(ModelTemplates.Raw);
        public ModelTemplates? Suggest(string model) => ModelTemplates.Raw;
    }
}
