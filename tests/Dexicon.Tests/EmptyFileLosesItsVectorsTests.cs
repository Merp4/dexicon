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
/// A file the catalogue records as having no chunks must not still have vectors.
///
/// Every route to that row is an early exit that skips the success path's delete, and
/// the reconcile pass at the end of a job only removes files the walk stopped seeing.
/// A file that is still on disk and still walked is therefore nobody's job to clean up,
/// so it kept answering searches with text it no longer contains.
/// </summary>
public sealed class EmptyFileLosesItsVectorsTests : IAsyncLifetime
{
    private string _dataPath = null!;
    private string _workspace = null!;
    private ServiceProvider _services = null!;
    private RecordingVectorStore _vectors = null!;

    private const string SourceRoot = "notes";
    private const string Model = "nomic-embed-text";
    private const string Collection = $"dexicon__{Model}__768";

    public async Task InitializeAsync()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), $"dexicon-empty-{Guid.NewGuid():N}");
        _workspace = Path.Combine(_dataPath, "workspace");
        Directory.CreateDirectory(Path.Combine(_workspace, SourceRoot));

        var options = Options.Create(new DexiconOptions
        {
            Storage = new StorageOptions { DataPath = _dataPath },
            Indexing = new IndexingOptions { WorkspaceRoot = _workspace },
        });

        // A file rather than :memory:, because the reader gives every file its own scope
        // and so its own connection. An in-memory database is per-connection, so the
        // extraction cache would write into a catalogue nothing else can see.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(options);
        services.AddDbContext<CatalogDbContext>(
            o => o.UseSqlite($"Data Source={Path.Combine(_dataPath, "catalog.db")}"));
        services.AddSingleton<IndexingLimits>();
        services.AddScoped<ExtractedTextCache>();
        _services = services.BuildServiceProvider();

        await using (var scope = _services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database.EnsureCreatedAsync();

        _vectors = new RecordingVectorStore();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        try { Directory.Delete(_dataPath, recursive: true); } catch { /* best effort */ }
    }

    // ---- the harness ------------------------------------------------------------

    /// <summary>
    /// A vector store that actually holds points, so "the vectors are gone" is a
    /// question about its contents rather than about which methods were called. An
    /// assertion on a call would pass against a delete aimed at the wrong file.
    /// </summary>
    private sealed class RecordingVectorStore : IVectorStore
    {
        private readonly List<Chunk> _points = [];

        public IReadOnlyList<Chunk> Points => _points;

        public int CountFor(string filePath) =>
            _points.Count(p => p.FilePath == filePath);

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
    private sealed class FixedEmbedder : IEmbeddingService
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

    private sealed class RawProfiles : IModelProfiles
    {
        public Task<ModelTemplates> ForAsync(EmbeddingTarget t, CancellationToken ct = default) =>
            Task.FromResult(ModelTemplates.Raw);
        public ModelTemplates? Suggest(string model) => ModelTemplates.Raw;
    }

    private CatalogDbContext NewContext() =>
        _services.CreateScope().ServiceProvider.GetRequiredService<CatalogDbContext>();

    /// <summary>A fresh indexer on a fresh context, which is how the worker runs one.</summary>
    private (CorpusIndexer Indexer, CatalogDbContext Db) NewIndexer()
    {
        var db = NewContext();
        var options = _services.GetRequiredService<IOptions<DexiconOptions>>();
        var scopes = _services.GetRequiredService<IServiceScopeFactory>();

        var indexer = new CorpusIndexer(
            db,
            new WorkspaceFileReader(scopes, options),
            _vectors,
            new FixedEmbedder(),
            new RawProfiles(),
            new DocumentService(db, options, NullLogger<DocumentService>.Instance),
            new CorpusLeases(scopes, NullLogger<CorpusLeases>.Instance),
            options,
            NullLogger<CorpusIndexer>.Instance);

        return (indexer, db);
    }

    private async Task<Corpus> SeedCorpusAsync(SourceKind kind)
    {
        await using var db = NewContext();
        var corpus = new Corpus
        {
            Id = "corpus-1",
            Name = "notes",
            State = CorpusState.Ready,
            CreatedUtc = DateTime.UtcNow,
        };
        corpus.ChunkSets.Add(new ChunkSet
        {
            Id = "set-1",
            CorpusId = corpus.Id,
            Name = "default",
            EmbeddingModel = Model,
            EmbeddingDimensions = 768,
            CollectionName = Collection,
            ChunkSize = 256,
            ChunkOverlap = 0,
            BoundaryMode = "blank-line",
            IsDefault = true,
            State = CorpusState.Ready,
            CreatedUtc = DateTime.UtcNow,
        });
        corpus.Sources.Add(new Source
        {
            Id = "source-1",
            CorpusId = corpus.Id,
            Kind = kind,
            RootPath = kind == SourceKind.Workspace ? SourceRoot : null,
            CreatedUtc = DateTime.UtcNow,
        });
        db.Corpora.Add(corpus);
        await db.SaveChangesAsync();
        return corpus;
    }

    private async Task<IndexJob> RunIndexAsync()
    {
        await using var db = NewContext();
        var job = new IndexJob
        {
            Id = Ulid.NewUlid().ToString(),
            CorpusId = "corpus-1",
            // Refresh, not Full: a full run rebuilds everything and would reach the
            // success path's delete anyway. The defect is on the incremental path.
            Kind = JobKind.Refresh,
            State = JobState.Queued,
            QueuedUtc = DateTime.UtcNow,
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();

        var (indexer, runDb) = NewIndexer();
        try
        {
            return await indexer.RunAsync(job.Id, null, CancellationToken.None);
        }
        finally
        {
            await runDb.DisposeAsync();
        }
    }

    private async Task<FileChunkState> StateOfAsync(string relativePath)
    {
        await using var db = NewContext();
        return await db.FileChunkStates
            .Where(s => db.Files.Any(f => f.Id == s.FileId && f.RelativePath == relativePath))
            .SingleAsync();
    }

    private static string Prose(string word) =>
        string.Join("\n\n", Enumerable.Range(1, 12).Select(i => $"Paragraph {i} about {word}."));

    // ---- the tests --------------------------------------------------------------

    [Fact]
    public async Task AFileWhoseTextDisappears_LosesTheVectorsItHad()
    {
        await SeedCorpusAsync(SourceKind.Workspace);
        var path = Path.Combine(_workspace, SourceRoot, "note.md");
        await File.WriteAllTextAsync(path, Prose("retrieval"));

        var first = await RunIndexAsync();
        first.State.ShouldBe(JobState.Succeeded);
        _vectors.CountFor("note.md").ShouldBeGreaterThan(0, "the first run must actually index something");

        // Whitespace, not zero bytes: the file is still walked and still extracted, and
        // the extraction is what comes back empty. This is the branch a scanned PDF and
        // an extractor regression both arrive at.
        await File.WriteAllTextAsync(path, "   \n\n   \n");

        await RunIndexAsync();

        var state = await StateOfAsync("note.md");
        state.Status.ShouldBe(FileStatus.Empty);
        state.ChunkCount.ShouldBe(0);
        _vectors.CountFor("note.md").ShouldBe(0,
            "the catalogue says the file has no chunks, so no chunk of it may still be searchable");
    }

    [Fact]
    public async Task AFileTruncatedToNothing_LosesTheVectorsItHad()
    {
        await SeedCorpusAsync(SourceKind.Workspace);
        var path = Path.Combine(_workspace, SourceRoot, "note.md");
        await File.WriteAllTextAsync(path, Prose("chunking"));

        await RunIndexAsync();
        _vectors.CountFor("note.md").ShouldBeGreaterThan(0);

        // Zero bytes is skipped by the walk itself, before extraction, so it reaches the
        // catalogue down a different branch from the one above: Skipped, not Empty. The
        // file is still there, so the reconcile pass never considers it gone either.
        await File.WriteAllTextAsync(path, "");

        await RunIndexAsync();

        var state = await StateOfAsync("note.md");
        state.Status.ShouldBe(FileStatus.Skipped);
        state.ChunkCount.ShouldBe(0);
        _vectors.CountFor("note.md").ShouldBe(0);
    }

    [Fact]
    public async Task AnUploadedDocumentThatExtractsToNothing_LosesTheVectorsItHad()
    {
        await SeedCorpusAsync(SourceKind.Upload);

        await using (var db = NewContext())
        {
            var documents = new DocumentService(db,
                _services.GetRequiredService<IOptions<DexiconOptions>>(),
                NullLogger<DocumentService>.Instance);

            var corpus = await db.Corpora.Include(c => c.Sources).Include(c => c.ChunkSets)
                .FirstAsync(c => c.Id == "corpus-1");
            using var bytes = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Prose("embedding")));
            var stored = await documents.StoreAsync(bytes, "paper.md");
            await documents.AttachAsync(corpus, stored.Sha256, "paper.md");
        }

        await RunIndexAsync();
        _vectors.CountFor("paper.md").ShouldBeGreaterThan(0);

        // What an extractor that now yields nothing leaves behind. Written straight to
        // the cache at the CURRENT version, so the read path returns it rather than
        // re-extracting: the bytes are unchanged, and it is the extraction that changed.
        await using (var db = NewContext())
        {
            var text = await db.BlobTexts.FirstAsync();
            text.Text = "";
            text.ExtractedChars = 0;
            await db.SaveChangesAsync();
        }

        await RunIndexAsync();

        var state = await StateOfAsync("paper.md");
        state.Status.ShouldBe(FileStatus.Empty);
        state.ChunkCount.ShouldBe(0);
        _vectors.CountFor("paper.md").ShouldBe(0,
            "uploads have no reconcile pass, so this branch is the only thing that can remove them");
    }
}
