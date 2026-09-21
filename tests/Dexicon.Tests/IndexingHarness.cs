using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Documents;
using Dexicon.Core.Embedding;
using Dexicon.Core.Indexing;
using Dexicon.Core.Search;
using Dexicon.Core.Vectors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    private readonly string[] _sourceRoots;

    /// <summary>Each source's root on disk, in the order they were declared.</summary>
    public IReadOnlyList<string> SourceDirectories { get; }

    /// <summary>Where the catalogue and the workspace live, for a test that has to reach them.</summary>
    public string DataPath => _dataPath;

    /// <summary>The first source's root. Files written here are what a pass walks.</summary>
    public string SourceDirectory => SourceDirectories[0];

    /// <summary>The id the nth declared source is seeded with.</summary>
    public static string SourceIdFor(int index) => $"source-{index + 1}";

    public RecordingVectorStore Vectors { get; } = new();

    private IndexingHarness(string dataPath, string[] sourceRoots, string[] sourceDirectories,
        ServiceProvider services)
    {
        _dataPath = dataPath;
        _sourceRoots = sourceRoots;
        SourceDirectories = sourceDirectories;
        _services = services;
    }

    /// <summary>
    /// A workspace with one directory per source root. Roots may nest ("outer",
    /// "outer/inner"), which is how a corpus with a more specific source is built.
    /// </summary>
    public static Task<IndexingHarness> StartAsync(params string[] sourceRoots) =>
        StartAsync(null, sourceRoots);

    /// <param name="watcher">
    /// An extra command interceptor on the catalogue, for a test that has to act at a
    /// point inside a pass. Opening a window deliberately is the only way to be in one
    /// from outside, and hoping to land in it is a test that quietly stops testing.
    /// </param>
    public static async Task<IndexingHarness> StartAsync(
        IInterceptor? watcher, params string[] sourceRoots)
    {
        if (sourceRoots.Length == 0) sourceRoots = ["notes"];

        var dataPath = Path.Combine(Path.GetTempPath(), $"dexicon-indexing-{Guid.NewGuid():N}");
        var workspace = Path.Combine(dataPath, "workspace");
        var sourceDirectories = sourceRoots.Select(r => Path.Combine(workspace, r)).ToArray();
        foreach (var dir in sourceDirectories) Directory.CreateDirectory(dir);

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
        // The same interceptor the app registers, and for the reason this harness exists:
        // the reader opens a connection per file on several threads, and SQLite's default
        // busy_timeout of 0 turns any overlap into SQLITE_BUSY rather than a short wait.
        // Left off, these tests would diverge from production in the one respect their
        // concurrency is meant to exercise, and fail intermittently.
        services.AddDbContext<CatalogDbContext>(
            o => o.UseSqlite($"Data Source={Path.Combine(dataPath, "catalog.db")}")
                  .AddInterceptors(watcher is null
                      ? [new SqlitePragmas(TimeSpan.FromSeconds(30), NullLogger<SqlitePragmas>.Instance)]
                      : new IInterceptor[]
                      {
                          new SqlitePragmas(TimeSpan.FromSeconds(30), NullLogger<SqlitePragmas>.Instance),
                          watcher,
                      }));
        services.AddSingleton<IndexingLimits>();
        services.AddScoped<ExtractedTextCache>();
        var provider = services.BuildServiceProvider();

        await using (var db = new CatalogDbContext(
            provider.GetRequiredService<DbContextOptions<CatalogDbContext>>()))
            await db.Database.EnsureCreatedAsync();

        return new IndexingHarness(dataPath, sourceRoots, sourceDirectories, provider);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        try { Directory.Delete(_dataPath, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// A catalogue context the caller owns and disposes.
    ///
    /// Built from the registered options rather than resolved out of a scope: a scope
    /// created to reach one service and then dropped is never disposed, so it holds
    /// everything it resolved for the rest of the run. The indexer's own internals still
    /// take real scopes from the provider, and they dispose them.
    /// </summary>
    public CatalogDbContext NewContext() =>
        new(_services.GetRequiredService<DbContextOptions<CatalogDbContext>>());

    /// <summary>
    /// A document service on the harness's own storage, so blobs land where the indexer
    /// will look for them. Building one from fresh options puts them somewhere else and
    /// the pass then reports every attachment as having no stored text.
    /// </summary>
    public DocumentService NewDocumentService(CatalogDbContext db) =>
        new(db, _services.GetRequiredService<IOptions<DexiconOptions>>(),
            NullLogger<DocumentService>.Instance);

    /// <summary>
    /// One corpus with <paramref name="sets"/> chunk sets, over one source per root the
    /// harness was started with. An upload corpus gets a single source with no root.
    /// </summary>
    public async Task SeedCorpusAsync(SourceKind kind, int sets = 1, string? gitOptions = null)
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

        // A git-history source is rooted like a workspace one: it is a directory in the
        // workspace, and what differs is that its units are commits rather than files.
        if (kind is SourceKind.Workspace or SourceKind.GitHistory)
        {
            for (var i = 0; i < _sourceRoots.Length; i++)
                corpus.Sources.Add(new Source
                {
                    Id = SourceIdFor(i),
                    CorpusId = corpus.Id,
                    Kind = kind,
                    RootPath = _sourceRoots[i],
                    GitOptions = gitOptions,
                    CreatedUtc = DateTime.UtcNow,
                });
        }
        else
        {
            corpus.Sources.Add(new Source
            {
                Id = SourceId,
                CorpusId = corpus.Id,
                Kind = kind,
                RootPath = null,
                CreatedUtc = DateTime.UtcNow,
            });
        }

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

    /// <summary>
    /// One file's row in one chunk set.
    ///
    /// The set has to be named. There is a row per set, so an unfiltered read returns
    /// whichever the provider happens to order first, and an assertion about the set
    /// under test would pass or fail on the other one's state.
    /// </summary>
    public async Task<FileChunkState> StateOfAsync(string relativePath, string chunkSetId = "set-1",
        string? sourceId = null)
    {
        await using var db = NewContext();
        return await db.FileChunkStates
            .Where(s => s.ChunkSetId == chunkSetId
                     && db.Files.Any(f => f.Id == s.FileId
                                       && f.RelativePath == relativePath
                                       && (sourceId == null || f.SourceId == sourceId)))
            .SingleAsync();
    }

    public Task WriteFileAsync(string name, string content, int source = 0) =>
        File.WriteAllTextAsync(Path.Combine(SourceDirectories[source], name), content);

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

        /// <summary>
        /// Points held for a file, optionally within one set or one source. The scoped
        /// form is what distinguishes a delete that respected its filter from one that
        /// reached across sets or sources.
        /// </summary>
        public int CountFor(string filePath, string? chunkSetId = null, string? sourceId = null) =>
            _points.Count(p => p.FilePath == filePath
                            && (chunkSetId is null || p.ChunkSetId == chunkSetId)
                            && (sourceId is null || p.SourceId == sourceId));

        /// <summary>
        /// Make the count come back incomplete, as a facet at its cap does. Absent and
        /// zero are the same shape in that answer, so a caller must not act on it.
        /// </summary>
        public bool CountsAreIncomplete { get; set; }

        /// <summary>Make the count read fail, as an unreachable vector store does.</summary>
        public bool CountsThrow { get; set; }

        /// <summary>
        /// Remove a file's points behind the indexer's back, which is what an
        /// interrupted pass leaves: vectors gone, catalogue row untouched.
        /// </summary>
        public int DropSilently(string filePath, string? chunkSetId = null, string? sourceId = null) =>
            _points.RemoveAll(p => p.FilePath == filePath
                                && (chunkSetId is null || p.ChunkSetId == chunkSetId)
                                && (sourceId is null || p.SourceId == sourceId));

        /// <summary>
        /// An extra point for a file, beyond what the catalogue recorded. The opposite
        /// disagreement to a loss, and one a stale chunk at a high index produces.
        /// </summary>
        public void AddStraySilently(string filePath)
        {
            var existing = _points.First(p => p.FilePath == filePath);
            _points.Add(existing with { ChunkIndex = _points.Max(p => p.ChunkIndex) + 1 });
        }

        /// <summary>One point of a file, for a partial loss rather than a total one.</summary>
        public void DropOne(string filePath)
        {
            var i = _points.FindIndex(p => p.FilePath == filePath);
            if (i < 0) throw new InvalidOperationException($"no points held for {filePath}");
            _points.RemoveAt(i);
        }

        /// <summary>
        /// Make the next delete abandon the pass the way an interruption does, by
        /// throwing the one exception the per-file catch blocks deliberately do not
        /// handle. A plain exception is caught and turned into a Failed row, which nulls
        /// the hash on its way past and hides whether the row was safe beforehand; only
        /// this reaches the state an interrupted pass actually leaves.
        /// </summary>
        public bool AbandonNextDelete { get; set; }

        public Task<IReadOnlyDictionary<string, int>?> CountByFileAsync(string collection,
            string chunkSetId, string sourceId, CancellationToken ct = default)
        {
            if (CountsThrow) throw new InvalidOperationException("the vector store is unreachable");
            if (CountsAreIncomplete) return Task.FromResult<IReadOnlyDictionary<string, int>?>(null);

            var counts = _points
                .Where(p => p.ChunkSetId == chunkSetId && p.SourceId == sourceId)
                .GroupBy(p => p.FilePath, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            return Task.FromResult<IReadOnlyDictionary<string, int>?>(counts);
        }

        public Task UpsertAsync(string collection, IReadOnlyList<Chunk> chunks,
            IReadOnlyList<float[]> vectors, CancellationToken ct = default)
        {
            _points.AddRange(chunks);
            return Task.CompletedTask;
        }

        public Task DeleteFileChunksAsync(string collection, string chunkSetId, string sourceId,
            string filePath, CancellationToken ct = default)
        {
            if (AbandonNextDelete)
            {
                AbandonNextDelete = false;
                throw new OperationCanceledException("the pass was interrupted mid-file");
            }

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
