using System.Data.Common;
using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Embedding;
using Dexicon.Core.Indexing;
using Dexicon.Core.Search;
using Dexicon.Core.Vectors;
using Dexicon.Infrastructure;
using Dexicon.Mcp;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace Dexicon.Tests;

/// <summary>
/// A change that is saved is not left without what makes it take effect.
///
/// The configuration is committed first and its job queued second, and a cancel between the
/// two left the filters changed with no refresh. Sending the same values again reads as no
/// change, so nothing queued one either. And a corpus a key creates is mapped to it in the
/// same save, so a failure between the two cannot leave one the key can neither reach nor
/// create again.
/// </summary>
public sealed class CommittedChangeTests
{
    /// <summary>
    /// Cancels the caller's token on the first command after a write to a table, which is
    /// inside the window between the save and the next step. Opening the window rather than
    /// hoping to land in it, and `Fired` is asserted, because a race test that quietly stops
    /// racing passes for ever.
    ///
    /// Armed by the test just before the call under test. Left running from the start it fired
    /// on the seed's own writes, before the token existed, and every assertion held: the first
    /// version of these tests passed with the fix reverted.
    ///
    /// The command then honours the token it was given, as a driver that supports cancellation
    /// does: SQLite's does not, so without it a cancel in this window is invisible here.
    /// </summary>
    internal sealed class CancelAfterWriteTo(string table) : DbCommandInterceptor
    {
        private bool _written;

        public CancellationTokenSource? Cts { get; set; }

        public bool Armed { get; set; }

        /// <summary>Fail the next command with an error of its own, as a database failing would, and not by cancel.</summary>
        public bool FailAfterWrite { get; set; }

        public bool Fired { get; private set; }

        private void Observe(DbCommand command, CancellationToken token)
        {
            if (!Armed) return;

            if (_written)
            {
                if (FailAfterWrite && !Fired)
                {
                    Fired = true;
                    throw new InvalidOperationException("the write after the save failed");
                }

                if (!Fired) Cts?.Cancel();
                Fired = true;
                token.ThrowIfCancellationRequested();
                return;
            }

            var text = command.CommandText;
            if ((text.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                 || text.Contains("INSERT", StringComparison.OrdinalIgnoreCase))
                && text.Contains(table, StringComparison.OrdinalIgnoreCase))
                _written = true;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Observe(command, cancellationToken);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Observe(command, cancellationToken);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task DefaultsThatAreSavedStillQueueTheirRefreshWhenTheCallerIsCancelled()
    {
        var watcher = new CancelAfterWriteTo("corpora");
        await using var harness = await IndexingHarness.StartAsync(watcher, "notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        using var cts = new CancellationTokenSource();
        watcher.Cts = cts;
        await using var db = harness.NewContext();
        var corpus = await db.Corpora.SingleAsync();
        watcher.Armed = true;

        var outcome = await harness.NewConfiguration(db).UpdateCorpusAsync(corpus,
            new UpdateCorpusRequest(Defaults: new CorpusDefaults(null, null, null, ["**/bin/**"])), cts.Token);

        watcher.Fired.ShouldBeTrue("the window has to have been opened");
        outcome.Refusal.ShouldBeNull();
        outcome.Value.ShouldBeTrue();
        (await db.Jobs.CountAsync(j => j.Kind == JobKind.Refresh)).ShouldBe(1, "the saved change has its job");
    }

    [Fact]
    public async Task ASourceThatIsSavedStillQueuesItsRefreshWhenTheCallerIsCancelled()
    {
        var watcher = new CancelAfterWriteTo("sources");
        await using var harness = await IndexingHarness.StartAsync(watcher, "notes", "docs");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        Directory.CreateDirectory(Path.Combine(harness.DataPath, "workspace", "extra"));
        using var cts = new CancellationTokenSource();
        watcher.Cts = cts;
        await using var db = harness.NewContext();
        var corpus = await db.Corpora.SingleAsync();
        watcher.Armed = true;

        var outcome = await harness.NewConfiguration(db).AddSourceAsync(corpus, new AddSourceRequest("extra"), cts.Token);

        watcher.Fired.ShouldBeTrue("the window has to have been opened");
        outcome.Refusal.ShouldBeNull();
        (await db.Sources.CountAsync(s => s.RootPath == "extra")).ShouldBe(1);
        (await db.Jobs.CountAsync(j => j.Kind == JobKind.Refresh)).ShouldBe(1, "the saved source has its job");
    }

    /// <summary>A vector store that passes everything to <paramref name="inner"/>, for a test to override one call.</summary>
    private class ForwardingStore(IVectorStore inner) : IVectorStore
    {
        public string CollectionNameFor(EmbeddingTarget target, int dimensions) => inner.CollectionNameFor(target, dimensions);

        public virtual Task EnsureCollectionAsync(string collection, int dimensions, CancellationToken ct = default) =>
            inner.EnsureCollectionAsync(collection, dimensions, ct);

        public Task UpsertAsync(string collection, IReadOnlyList<Chunk> chunks, IReadOnlyList<float[]> vectors,
            CancellationToken ct = default) => inner.UpsertAsync(collection, chunks, vectors, ct);

        public Task DeleteFileChunksAsync(string collection, string chunkSetId, string sourceId, string filePath,
            CancellationToken ct = default) => inner.DeleteFileChunksAsync(collection, chunkSetId, sourceId, filePath, ct);

        public Task DeleteChunkSetAsync(string collection, string chunkSetId, CancellationToken ct = default) =>
            inner.DeleteChunkSetAsync(collection, chunkSetId, ct);

        public Task<IReadOnlyDictionary<string, int>?> CountByFileAsync(string collection, string chunkSetId,
            string sourceId, CancellationToken ct = default) => inner.CountByFileAsync(collection, chunkSetId, sourceId, ct);

        public Task<int> PurgeUnsetChunksAsync(CancellationToken ct = default) => inner.PurgeUnsetChunksAsync(ct);

        public Task DeleteCorpusAsync(string collection, string corpusId, CancellationToken ct = default) =>
            inner.DeleteCorpusAsync(collection, corpusId, ct);

        public Task<IReadOnlyList<SearchHit>> GetFileChunksAsync(string collection, string chunkSetId, string filePath,
            CancellationToken ct = default) => inner.GetFileChunksAsync(collection, chunkSetId, filePath, ct);

        public Task<SearchResponse> SearchAsync(SearchQuery query, float[]? denseVector, SparseVector sparse,
            CancellationToken ct = default) => inner.SearchAsync(query, denseVector, sparse, ct);

        public Task<(long Points, int Dimensions)> GetStatsAsync(string collection, CancellationToken ct = default) =>
            inner.GetStatsAsync(collection, ct);

        public Task<bool> PingAsync(CancellationToken ct = default) => inner.PingAsync(ct);
    }

    /// <summary>A vector store whose collection setup fails, as Qdrant being down does, after the corpus is saved.</summary>
    private sealed class EnsureThrows(IVectorStore inner) : ForwardingStore(inner)
    {
        public override Task EnsureCollectionAsync(string collection, int dimensions, CancellationToken ct = default) =>
            throw new InvalidOperationException("Qdrant is not answering");
    }

    /// <summary>
    /// A vector store whose collection setup is where the caller cancels, as a client leaving during the
    /// call to Qdrant does. It then honours the token it was given, as a client library does.
    /// </summary>
    private sealed class EnsureCancels(IVectorStore inner, CancellationTokenSource cts) : ForwardingStore(inner)
    {
        public bool Called { get; private set; }

        public override Task EnsureCollectionAsync(string collection, int dimensions, CancellationToken ct = default)
        {
            Called = true;
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ANewCorpusWithAFolderIsQueuedWhenTheCallerIsCancelledDuringCollectionSetup()
    {
        // The corpus and its source are saved before the collection is prepared, and the full pass is
        // queued after it. A cancel inside the call to the vector store threw before the job was queued.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        Directory.CreateDirectory(Path.Combine(harness.DataPath, "workspace", "extra"));
        using var cts = new CancellationTokenSource();
        await using var db = harness.NewContext();
        var vectors = new EnsureCancels(harness.Vectors, cts);

        var outcome = await harness.NewConfiguration(db, vectors).CreateCorpusAsync(
            new CreateCorpusRequest("papers", WorkspacePath: "extra"), cts.Token);

        vectors.Called.ShouldBeTrue("the window has to have been opened");
        outcome.Refusal.ShouldBeNull();
        var papers = await db.Corpora.AsNoTracking().SingleAsync(c => c.Name == "papers");
        (await db.Jobs.CountAsync(j => j.CorpusId == papers.Id && j.Kind == JobKind.Full)).ShouldBe(1, "the new corpus has its job");
    }

    [Fact]
    public async Task ACreationIsRecordedWhenItIsSavedAndAFailureAfterItIsSaidToHaveHappenedAfter()
    {
        // The corpus and the key's access are committed before the collection is prepared. A
        // failure there left the corpus in place, no entry under the key's name, and an error
        // that read as "nothing happened" while a retry of create was refused as taken.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await using var db = harness.NewContext();
        var logs = new RecordingLoggerFactory();
        var rc = new RequestContext
        {
            Principal = new Principal("k9", "agent-nine", new HashSet<string>(StringComparer.Ordinal) { Scopes.Configure }),
        };

        var thrown = await Should.ThrowAsync<McpException>(() => ConfigureTools.ConfigureCorpusAsync(
            rc, new ScopeResolver(db), db, harness.NewConfiguration(db, new EnsureThrows(harness.Vectors)), logs,
            "papers", create: true));

        thrown.Message.ShouldContain("was created, but preparing its index failed");
        (await db.Corpora.AnyAsync(c => c.Name == "papers")).ShouldBeTrue("the corpus is saved");
        logs.Lines.ShouldContain(l => l.Contains("agent-nine") && l.Contains("created corpus papers"));
    }

    private static RequestContext Configure(string tokenId, string name) => new()
    {
        Principal = new Principal(tokenId, name, new HashSet<string>(StringComparer.Ordinal) { Scopes.Configure }),
    };

    [Fact]
    public async Task TheFiltersOfANewCorpusAreSavedWithItAndRecordedWithItsCreation()
    {
        // They were applied by a second call. A failure there left the corpus saved without
        // them, reported a generic error, and refused a retry of create as taken. Here setup
        // fails after the save, so a second call would not even have been reached.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await using var db = harness.NewContext();
        var logs = new RecordingLoggerFactory();

        await Should.ThrowAsync<McpException>(() => ConfigureTools.ConfigureCorpusAsync(
            Configure("k8", "agent-eight"), new ScopeResolver(db), db,
            harness.NewConfiguration(db, new EnsureThrows(harness.Vectors)), logs,
            "papers", create: true, description: "Conference papers", exclude: ["**/draft/**"], maxFileKb: 512));

        var saved = await db.Corpora.AsNoTracking().SingleAsync(c => c.Name == "papers");
        saved.DefaultsOf().ShouldBe(new CorpusDefaults(null, 512 * 1024, null, ["**/draft/**"]), new SameDefaults());
        logs.Lines.ShouldContain(l => l.Contains("agent-eight") && l.Contains("created corpus papers")
                                      && l.Contains("description, the filters its sources inherit"));
    }

    [Fact]
    public async Task ACancelAfterACorpusChangeIsSavedStillReachesTheAuditLine()
    {
        // The service finishes the queue write after committing whatever the caller does. A
        // follow-up query on the caller's token then threw before the line was written, leaving
        // a persisted change with no entry under the key's name.
        var watcher = new CancelAfterWriteTo("corpora");
        await using var harness = await IndexingHarness.StartAsync(watcher, "notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        using var cts = new CancellationTokenSource();
        watcher.Cts = cts;
        await using var db = harness.NewContext();
        var logs = new RecordingLoggerFactory();
        watcher.Armed = true;

        var reply = await ConfigureTools.ConfigureCorpusAsync(
            Configure("k7", "agent-seven"), new ScopeResolver(db), db, harness.NewConfiguration(db), logs,
            "notes", exclude: ["**/bin/**"], ct: cts.Token);

        watcher.Fired.ShouldBeTrue("the window has to have been opened");
        reply.ShouldContain("A refresh is queued.");
        logs.Lines.ShouldContain(l => l.Contains("agent-seven") && l.Contains("changed corpus notes"));
    }

    [Fact]
    public async Task ASourceThatIsSavedIsReportedAddedWhenTheCallerIsCancelledAfterwards()
    {
        // The summary in the reply was read on the caller's token, so a cancel after the save
        // reported an add nothing can undo as failed, and a retry was refused as a duplicate.
        var watcher = new CancelAfterWriteTo("sources");
        await using var harness = await IndexingHarness.StartAsync(watcher, "notes", "docs");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        Directory.CreateDirectory(Path.Combine(harness.DataPath, "workspace", "extra"));
        using var cts = new CancellationTokenSource();
        watcher.Cts = cts;
        await using var db = harness.NewContext();
        watcher.Armed = true;

        var reply = await ConfigureTools.ConfigureSourceAsync(
            Configure("k6", "agent-six"), new ScopeResolver(db), db, harness.NewConfiguration(db), harness.Settings,
            new RecordingLoggerFactory(), "notes", "extra", create: true, ct: cts.Token);

        watcher.Fired.ShouldBeTrue("the window has to have been opened");
        reply.ShouldStartWith("Added a source for the files under extra");
    }

    [Fact]
    public async Task ADocumentThatIsAttachedIsQueuedForIndexingWhenTheCallerIsCancelledAfterwards()
    {
        // The attachment is saved, and the job that indexes it was queued on the caller's token.
        // Watched on the chunk-state rows, which are the last written by the save.
        var watcher = new CancelAfterWriteTo("file_chunk_states");
        await using var harness = await IndexingHarness.StartAsync(watcher, "notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        using var cts = new CancellationTokenSource();
        watcher.Cts = cts;
        await using var db = harness.NewContext();
        var documents = harness.NewDocumentService(db);
        var stored = await documents.StoreAsync(new MemoryStream("some text to index"u8.ToArray()), "doc.txt");
        var rc = new RequestContext
        {
            Principal = new Principal("k", "agent", new HashSet<string>(StringComparer.Ordinal) { Scopes.Search, Scopes.Ingest }),
        };
        watcher.Armed = true;

        var result = await DocumentEndpoints.AttachAsync(
            IndexingHarness.CorpusId, new AttachDocumentRequest(stored.Sha256), rc, new ScopeResolver(db), documents, db,
            new IndexJobQueue(db, new WorkScheduler(harness.Settings), NullLogger<IndexJobQueue>.Instance), cts.Token);

        watcher.Fired.ShouldBeTrue("the window has to have been opened");
        result.ShouldBeOfType<Accepted<DocumentAttached>>();
        (await db.Files.CountAsync()).ShouldBe(1);
        (await db.Jobs.CountAsync(j => j.Kind == JobKind.Refresh)).ShouldBe(1, "the attached document has its job");
    }

    [Fact]
    public async Task ANewCorpusWithAFolderIsQueuedForIndexingWhenTheCallerIsCancelledAfterwards()
    {
        // Naming a folder is asking for it to be indexed. The corpus and its source are saved, and
        // the full pass was queued on the caller's token.
        var watcher = new CancelAfterWriteTo("sources");
        await using var harness = await IndexingHarness.StartAsync(watcher, "notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        Directory.CreateDirectory(Path.Combine(harness.DataPath, "workspace", "extra"));
        using var cts = new CancellationTokenSource();
        watcher.Cts = cts;
        await using var db = harness.NewContext();
        watcher.Armed = true;

        var outcome = await harness.NewConfiguration(db).CreateCorpusAsync(
            new CreateCorpusRequest("papers", WorkspacePath: "extra"), cts.Token);

        watcher.Fired.ShouldBeTrue("the window has to have been opened");
        outcome.Refusal.ShouldBeNull();
        var papers = await db.Corpora.AsNoTracking().SingleAsync(c => c.Name == "papers");
        (await db.Jobs.CountAsync(j => j.CorpusId == papers.Id && j.Kind == JobKind.Full)).ShouldBe(1, "the new corpus has its job");
    }

    private static RequestContext AsAdmin() => new()
    {
        Principal = new Principal("k", "admin", new HashSet<string>(StringComparer.Ordinal) { Scopes.Admin }),
    };

    private static IndexJobQueue QueueOn(IndexingHarness harness, CatalogDbContext db) =>
        new(db, new WorkScheduler(harness.Settings), NullLogger<IndexJobQueue>.Instance);

    [Fact]
    public async Task AChunkSetThatIsSavedIsBuiltWhenTheCallerIsCancelledDuringCollectionSetup()
    {
        // The set is saved as Degraded, marked as built by nothing until its job runs. A cancel inside
        // the call to the vector store threw before the job was queued.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        using var cts = new CancellationTokenSource();
        await using var db = harness.NewContext();
        var vectors = new EnsureCancels(harness.Vectors, cts);

        var result = await ChunkSetEndpoints.CreateAsync(
            IndexingHarness.CorpusId, new CreateChunkSetRequest("second"), AsAdmin(), new ScopeResolver(db), db, vectors,
            harness.Embedder, QueueOn(harness, db), harness.Settings, cts.Token);

        vectors.Called.ShouldBeTrue("the window has to have been opened");
        var second = await db.ChunkSets.AsNoTracking().SingleAsync(s => s.Name == "second");
        (await db.Jobs.CountAsync(j => j.Kind == JobKind.Rebuild && j.ChunkSetId == second.Id)).ShouldBe(1, "the saved set has its job");
        result.ShouldBeOfType<Accepted<ChunkSetCreated>>().Value!.ChunkSet.Name.ShouldBe("second", "the reply reports the saved set");
    }

    [Fact]
    public async Task AChunkSetThatIsSavedIsBuiltWhenTheCallerIsCancelledBeforeItsJobIsQueued()
    {
        var watcher = new CancelAfterWriteTo("chunk_sets");
        await using var harness = await IndexingHarness.StartAsync(watcher, "notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        using var cts = new CancellationTokenSource();
        watcher.Cts = cts;
        await using var db = harness.NewContext();
        watcher.Armed = true;

        var result = await ChunkSetEndpoints.CreateAsync(
            IndexingHarness.CorpusId, new CreateChunkSetRequest("second"), AsAdmin(), new ScopeResolver(db), db, harness.Vectors,
            harness.Embedder, QueueOn(harness, db), harness.Settings, cts.Token);

        watcher.Fired.ShouldBeTrue("the window has to have been opened");
        var second = await db.ChunkSets.AsNoTracking().SingleAsync(s => s.Name == "second");
        (await db.Jobs.CountAsync(j => j.Kind == JobKind.Rebuild && j.ChunkSetId == second.Id)).ShouldBe(1, "the saved set has its job");
        result.ShouldBeOfType<Accepted<ChunkSetCreated>>().Value!.BackfillJob.Id.ShouldNotBeNullOrEmpty("the reply reports the job");
    }

    [Fact]
    public async Task AChunkSetChangeThatIsSavedIsRechunkedWhenTheCallerIsCancelledBeforeItsJobIsQueued()
    {
        // Sending the same values again reads as no change, so nothing would queue the job a second time.
        var watcher = new CancelAfterWriteTo("chunk_sets");
        await using var harness = await IndexingHarness.StartAsync(watcher, "notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        using var cts = new CancellationTokenSource();
        watcher.Cts = cts;
        await using var db = harness.NewContext();
        watcher.Armed = true;

        var result = await ChunkSetEndpoints.UpdateAsync(
            IndexingHarness.CorpusId, "default", new UpdateChunkSetRequest(ChunkSize: 400), AsAdmin(),
            new ScopeResolver(db), db, QueueOn(harness, db), cts.Token);

        watcher.Fired.ShouldBeTrue("the window has to have been opened");
        (await db.ChunkSets.AsNoTracking().SingleAsync()).ChunkSize.ShouldBe(400, "the change is saved");
        (await db.Jobs.CountAsync(j => j.Kind == JobKind.Refresh && j.ChunkSetId == "set-1")).ShouldBe(1, "the saved change has its job");
        result.ShouldBeOfType<Ok<ChunkSetUpdated>>().Value!.RechunkJob.ShouldNotBeNull("the reply reports the job");
    }

    [Fact]
    public async Task ASourceIsRecordedWhenItIsSavedEvenIfQueuingItsRefreshThenFails()
    {
        // The source is committed before its job is queued. A failure there left the source in
        // place and no entry under the key's name, and a retry of create was refused as taken.
        var watcher = new CancelAfterWriteTo("sources") { FailAfterWrite = true };
        await using var harness = await IndexingHarness.StartAsync(watcher, "notes", "docs");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        Directory.CreateDirectory(Path.Combine(harness.DataPath, "workspace", "extra"));
        await using var db = harness.NewContext();
        var logs = new RecordingLoggerFactory();
        watcher.Armed = true;

        await Should.ThrowAsync<InvalidOperationException>(() => ConfigureTools.ConfigureSourceAsync(
            Configure("k4", "agent-four"), new ScopeResolver(db), db, harness.NewConfiguration(db), harness.Settings,
            logs, "notes", "extra", create: true));

        watcher.Fired.ShouldBeTrue("the window has to have been opened");
        (await db.Sources.CountAsync(s => s.RootPath == "extra")).ShouldBe(1, "the source is saved");
        logs.Lines.ShouldContain(l => l.Contains("agent-four") && l.Contains("added the source for the files under extra"));
    }

    [Fact]
    public async Task ACorpusChangeIsRecordedWhenItIsSavedEvenIfTheNextStepFails()
    {
        // The refresh is queued after the save and can still fail. The change stays, and the line
        // that says which key made it was written only after the method returned.
        var watcher = new CancelAfterWriteTo("corpora") { FailAfterWrite = true };
        await using var harness = await IndexingHarness.StartAsync(watcher, "notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await using var db = harness.NewContext();
        var logs = new RecordingLoggerFactory();
        watcher.Armed = true;

        await Should.ThrowAsync<InvalidOperationException>(() => ConfigureTools.ConfigureCorpusAsync(
            Configure("k3", "agent-three"), new ScopeResolver(db), db, harness.NewConfiguration(db), logs,
            "notes", description: "Notes, edited", exclude: ["**/bin/**"]));

        watcher.Fired.ShouldBeTrue("the window has to have been opened");
        (await db.Corpora.AsNoTracking().SingleAsync()).Description.ShouldBe("Notes, edited");
        logs.Lines.ShouldContain(l => l.Contains("agent-three") && l.Contains("changed corpus notes")
                                      && l.Contains("description, the filters its sources inherit"));
    }

    [Fact]
    public async Task AScopeChangeThatIsSavedIsLoggedWhenTheCallerIsCancelledAfterwards()
    {
        var watcher = new CancelAfterWriteTo("tokens");
        await using var harness = await IndexingHarness.StartAsync(watcher, "notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await using var db = harness.NewContext();
        var tokens = new TokenService(db, TimeProvider.System);
        var (key, _) = await tokens.CreateAsync("agent", [Scopes.Search, Scopes.Configure], null);
        using var cts = new CancellationTokenSource();
        watcher.Cts = cts;
        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var logs = new RecordingLoggerFactory();
        watcher.Armed = true;

        var result = await SystemEndpoints.SetScopesAsync(key.Id, new UpdateTokenScopesRequest([Scopes.Search]),
            tokens, db, new MemoryCacheEvictor(cache), logs.CreateLogger("test"), cts.Token);

        watcher.Fired.ShouldBeTrue("the window has to have been opened");
        ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)result).StatusCode.ShouldBe(200);
        logs.Lines.ShouldContain(l => l.Contains("scopes set to") && l.Contains("agent"));
    }

    [Fact]
    public async Task AScopeErrorNamesAKeyAndCorporaOnOneLine()
    {
        // The resolver's message lists the key and every corpus it reaches, typed text that an
        // older catalogue's can hold a line break in.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await using var db = harness.NewContext();
        await db.Corpora.ExecuteUpdateAsync(u => u.SetProperty(c => c.Name, "two\nlines"));

        var thrown = await Should.ThrowAsync<McpException>(() => ConfigureTools.ConfigureCorpusAsync(
            Configure("k5", "agent\nforged"), new ScopeResolver(db), db, harness.NewConfiguration(db),
            new RecordingLoggerFactory(), "missing", description: "x"));

        thrown.Message.ShouldContain("two lines");
        thrown.Message.ShouldNotContain("\n");
    }

    [Fact]
    public async Task ACorpusThatIsCreatedIsReportedWhenTheCallerIsCancelledAfterwards()
    {
        // The summary in the reply was read on the caller's token after the corpus was saved, so a cancel
        // there threw from a handler whose creation had happened. Watched on the chunk sets, the last rows
        // the save writes.
        var watcher = new CancelAfterWriteTo("chunk_sets");
        await using var harness = await IndexingHarness.StartAsync(watcher, "notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        using var cts = new CancellationTokenSource();
        watcher.Cts = cts;
        await using var db = harness.NewContext();
        watcher.Armed = true;

        var result = await CorpusEndpoints.CreateAsync(new CreateCorpusRequest("papers"), AsAdmin(), db,
            harness.NewConfiguration(db), harness.Settings, cts.Token);

        watcher.Fired.ShouldBeTrue("the window has to have been opened");
        result.ShouldBeOfType<Created<CorpusSummary>>().Value!.Name.ShouldBe("papers");
    }

    [Fact]
    public async Task ACorpusChangeThatIsSavedIsReportedWhenTheCallerIsCancelledAfterwards()
    {
        var watcher = new CancelAfterWriteTo("corpora");
        await using var harness = await IndexingHarness.StartAsync(watcher, "notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        using var cts = new CancellationTokenSource();
        watcher.Cts = cts;
        await using var db = harness.NewContext();
        watcher.Armed = true;

        var result = await CorpusEndpoints.UpdateAsync(IndexingHarness.CorpusId,
            new UpdateCorpusRequest(Defaults: new CorpusDefaults(null, null, null, ["**/bin/**"])), AsAdmin(),
            new ScopeResolver(db), db, harness.NewConfiguration(db), harness.Settings, cts.Token);

        watcher.Fired.ShouldBeTrue("the window has to have been opened");
        result.ShouldBeOfType<Ok<CorpusUpdated>>().Value!.Corpus.Name.ShouldBe("notes");
        (await db.Jobs.CountAsync(j => j.Kind == JobKind.Refresh)).ShouldBe(1, "the saved change has its job");
    }

    [Fact]
    public async Task AKeyIsNeverSavedWithoutTheCorporaItWasIssuedFor()
    {
        // The key and its corpora were two saves, and the second ran on the caller's token. A cancel between
        // them left a key with no mapping, and no mapping means every corpus.
        var watcher = new CancelAfterWriteTo("tokens");
        await using var harness = await IndexingHarness.StartAsync(watcher, "notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        using var cts = new CancellationTokenSource();
        watcher.Cts = cts;
        await using var db = harness.NewContext();
        watcher.Armed = true;

        try
        {
            await SystemEndpoints.CreateTokenAsync(new CreateTokenRequest("agent", CorpusIds: [IndexingHarness.CorpusId]),
                AsAdmin(), new TokenService(db, TimeProvider.System), db, cts.Token);
        }
        catch (OperationCanceledException) { /* the caller left */ }

        watcher.Fired.ShouldBeTrue("the window has to have been opened");
        await using var check = harness.NewContext();
        (await check.Tokens.Where(t => t.Name == "agent").Select(t => t.Corpora.Count).ToListAsync())
            .ShouldAllBe(n => n == 1, "a key that is saved reaches only the corpus it was issued for");
    }

    [Fact]
    public async Task AKeyIssuedForTwoCorporaIsSavedMappedToBothAndSaysSo()
    {
        // The test above cancels the save, which rolls the key back, so it cannot tell a key saved with
        // its corpora from no key at all. This is the save that completes.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await using var db = harness.NewContext();
        var papers = (await harness.NewConfiguration(db).CreateCorpusAsync(new CreateCorpusRequest("papers"), default)).Value!;

        var result = await SystemEndpoints.CreateTokenAsync(
            new CreateTokenRequest("agent", CorpusIds: [IndexingHarness.CorpusId, papers.Id]),
            AsAdmin(), new TokenService(db, TimeProvider.System), db, default);

        var created = result.ShouldBeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<CreatedTokenResponse>>().Value!;
        created.Token.CorpusIds.ShouldBe([IndexingHarness.CorpusId, papers.Id], ignoreOrder: true);
        await using var check = harness.NewContext();
        (await check.TokenCorpora.Where(tc => tc.TokenId == created.Token.Id).Select(tc => tc.CorpusId).ToListAsync())
            .ShouldBe([IndexingHarness.CorpusId, papers.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task AKeyIssuedForACorpusThatDoesNotExistIsRefusedAndNotSaved()
    {
        // The corpora were checked after the key was saved, so the refusal left a key behind that reached
        // every corpus, with a secret nobody was shown.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await using var db = harness.NewContext();

        var result = await SystemEndpoints.CreateTokenAsync(new CreateTokenRequest("agent", CorpusIds: ["missing"]),
            AsAdmin(), new TokenService(db, TimeProvider.System), db, default);

        ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)result).StatusCode.ShouldBe(400);
        (await db.Tokens.CountAsync(t => t.Name == "agent")).ShouldBe(0, "a refused key is not saved");
    }

    [Fact]
    public async Task AReplacedMappingIsReportedAsSavedWithNothingReadAfterTheSave()
    {
        // The reply once reloaded the key's corpora on the caller's token after the save. The collection was
        // already loaded, so that reload read nothing and this test passes on the old code too; it guards
        // against a read being added after the save. The reply comes from the tracked key, whose
        // collection the save fixes up, including the corpus the replacement dropped.
        var watcher = new CancelAfterWriteTo("token_corpora");
        await using var harness = await IndexingHarness.StartAsync(watcher, "notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await using var db = harness.NewContext();
        var papers = (await harness.NewConfiguration(db).CreateCorpusAsync(new CreateCorpusRequest("papers"), default)).Value!;
        var (key, _) = await new TokenService(db, TimeProvider.System)
            .CreateAsync("agent", [Scopes.Search], null, [IndexingHarness.CorpusId]);
        db.ChangeTracker.Clear();
        using var cts = new CancellationTokenSource();
        watcher.Cts = cts;
        watcher.Armed = true;

        var result = await SystemEndpoints.ReplaceCorporaAsync(key.Id,
            new UpdateTokenCorporaRequest([papers.Id]), AsAdmin(), db, cts.Token);

        result.ShouldBeOfType<Ok<TokenSummary>>().Value!.CorpusIds.ShouldBe([papers.Id]);
        watcher.Fired.ShouldBeFalse("nothing reads the catalogue after the mapping is saved");
    }

    [Fact]
    public async Task AnUploadSourceIsSavedWithTheFirstDocumentAttachedToItOrNotAtAll()
    {
        // A corpus's upload source was saved on its own, and the attachment it was made for was then read
        // and saved on the caller's token. A cancel between the two left a source with nothing in it.
        var watcher = new CancelAfterWriteTo("sources");
        await using var harness = await IndexingHarness.StartAsync(watcher, "notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        using var cts = new CancellationTokenSource();
        watcher.Cts = cts;
        await using var db = harness.NewContext();
        var documents = harness.NewDocumentService(db);
        var stored = await documents.StoreAsync(new MemoryStream("some text to index"u8.ToArray()), "doc.txt");
        var corpus = await db.Corpora.SingleAsync();
        watcher.Armed = true;

        try { await documents.AttachAsync(corpus, stored.Sha256, "doc.txt", cts.Token); }
        catch (OperationCanceledException) { /* the caller left */ }

        watcher.Fired.ShouldBeTrue("the window has to have been opened");
        await using var check = harness.NewContext();
        var uploads = await check.Sources.CountAsync(s => s.Kind == SourceKind.Upload);
        var attached = await check.Files.CountAsync(f => f.BlobSha256 == stored.Sha256);
        uploads.ShouldBe(attached, "an upload source exists only with the document it was made for");
    }

    private sealed class SameDefaults : IEqualityComparer<CorpusDefaults>
    {
        public bool Equals(CorpusDefaults? a, CorpusDefaults? b) =>
            a is not null && b is not null && a.UseGitignore == b.UseGitignore && a.MaxFileBytes == b.MaxFileBytes
            && (a.IncludeGlobs ?? []).SequenceEqual(b.IncludeGlobs ?? [])
            && (a.ExcludeGlobs ?? []).SequenceEqual(b.ExcludeGlobs ?? []);

        public int GetHashCode(CorpusDefaults d) => 0;
    }

    [Fact]
    public async Task AKeyMappingIsNotReplacedWhileACorpusIsBeingCreated()
    {
        // A creation reads whether its key has a mapping and adds the new corpus to it. The
        // admin clearing the mapping between the two (empty means every corpus) left the key
        // reaching only the corpus it had just made. Both take the one lock, so while a creation
        // holds it the replacement waits, and goes ahead when it is released.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await using var db = harness.NewContext();
        var (key, _) = await new TokenService(db, TimeProvider.System).CreateAsync("mapped", [Scopes.Configure], null);
        db.TokenCorpora.Add(new TokenCorpus { TokenId = key.Id, CorpusId = IndexingHarness.CorpusId });
        await db.SaveChangesAsync();
        await using var admin = harness.NewContext();

        Task<Microsoft.AspNetCore.Http.IResult?> replacement;
        await CorpusConfiguration.Naming.WaitAsync();
        try
        {
            replacement = SystemEndpoints.MapCorporaAsync(admin, key.Id, [], default);
            await Task.Delay(300);
            replacement.IsCompleted.ShouldBeFalse("the replacement waits for the creation to finish");
        }
        finally
        {
            CorpusConfiguration.Naming.Release();
        }

        (await replacement.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeNull();
        (await db.TokenCorpora.CountAsync(tc => tc.TokenId == key.Id)).ShouldBe(0, "cleared, which means every corpus");
    }

    [Fact]
    public async Task ACorpusMadeForAMappedKeyJoinsItsMappingAndOneMadeForAnUnmappedKeyDoesNot()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await using var db = harness.NewContext();
        var tokens = new TokenService(db, TimeProvider.System);
        var (mapped, _) = await tokens.CreateAsync("mapped", [Scopes.Configure], null);
        var (unmapped, _) = await tokens.CreateAsync("unmapped", [Scopes.Configure], null);
        db.TokenCorpora.Add(new TokenCorpus { TokenId = mapped.Id, CorpusId = IndexingHarness.CorpusId });
        await db.SaveChangesAsync();
        var config = harness.NewConfiguration(db);

        var forMapped = (await config.CreateCorpusAsync(new CreateCorpusRequest("papers"), default, mapped.Id)).Value!;
        await config.CreateCorpusAsync(new CreateCorpusRequest("books"), default, unmapped.Id);
        await config.CreateCorpusAsync(new CreateCorpusRequest("misc"), default);

        (await db.TokenCorpora.Where(tc => tc.TokenId == mapped.Id).Select(tc => tc.CorpusId).ToListAsync())
            .ShouldBe([IndexingHarness.CorpusId, forMapped.Id], ignoreOrder: true);
        (await db.TokenCorpora.CountAsync(tc => tc.TokenId == unmapped.Id)).ShouldBe(0, "no mapping means every corpus");
    }
}
