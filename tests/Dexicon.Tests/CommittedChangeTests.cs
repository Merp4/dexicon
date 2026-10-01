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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    private sealed class CancelAfterWriteTo(string table) : DbCommandInterceptor
    {
        private bool _written;

        public CancellationTokenSource? Cts { get; set; }

        public bool Armed { get; set; }

        public bool Fired { get; private set; }

        private void Observe(DbCommand command, CancellationToken token)
        {
            if (!Armed) return;

            if (_written)
            {
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
    public async Task Defaults_that_are_saved_still_queue_their_refresh_when_the_caller_is_cancelled()
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
    public async Task A_source_that_is_saved_still_queues_its_refresh_when_the_caller_is_cancelled()
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

    /// <summary>A vector store whose collection setup fails, as Qdrant being down does, after the corpus is saved.</summary>
    private sealed class EnsureThrows(IVectorStore inner) : IVectorStore
    {
        public string CollectionNameFor(EmbeddingTarget target, int dimensions) => inner.CollectionNameFor(target, dimensions);

        public Task EnsureCollectionAsync(string collection, int dimensions, CancellationToken ct = default) =>
            throw new InvalidOperationException("Qdrant is not answering");

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

    [Fact]
    public async Task A_creation_is_recorded_when_it_is_saved_and_a_failure_after_it_is_said_to_have_happened_after()
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

    [Fact]
    public async Task A_corpus_made_for_a_mapped_key_joins_its_mapping_and_one_made_for_an_unmapped_key_does_not()
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
