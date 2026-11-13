using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Embedding;
using Dexicon.Core.Search;
using Dexicon.Core.Vectors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dexicon.Tests;

/// <summary>
/// A search over a corpus whose last pass could not read its source answers from the index it had before.
/// Said in the result's note, with the date of the last pass that completed, because the results themselves
/// look the same as current ones.
/// </summary>
public sealed class SearchStateNoteTests : IAsyncLifetime
{
    private IndexingHarness _harness = null!;

    public async ValueTask InitializeAsync()
    {
        _harness = await IndexingHarness.StartAsync("notes");
        await _harness.SeedCorpusAsync(SourceKind.Workspace);
        await _harness.WriteFileAsync("plain.md", "one\n\ntwo\n\nthree");
        await _harness.RunIndexAsync();
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    private async Task SetStateAsync(CorpusState state, DateTime? lastIndexed)
    {
        await using var db = _harness.NewContext();
        await db.ChunkSets.ExecuteUpdateAsync(u => u.SetProperty(s => s.State, state).SetProperty(s => s.LastIndexedUtc, lastIndexed));
    }

    private async Task<string?> NoteAsync()
    {
        await using var db = _harness.NewContext();
        var search = new SearchService(new ScopeResolver(db), new NoHits(_harness.Vectors), _harness.Embedder,
            new MemoryCache(new MemoryCacheOptions()), NullLogger<SearchService>.Instance);
        var principal = new Principal("k", "agent", new HashSet<string>(StringComparer.Ordinal) { Scopes.Search });

        var result = await search.SearchAsync(
            principal, new SearchRequest { Query = "one", Mode = SearchMode.Keyword }, TestContext.Current.CancellationToken);
        return result.Note;
    }

    [Fact]
    public async Task ACorpusThatReadItsSourceHasNoNote()
    {
        await SetStateAsync(CorpusState.Ready, new DateTime(2026, 11, 1, 9, 30, 0, DateTimeKind.Utc));

        (await NoteAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task AnUnavailableCorpusSaysSoWithTheDateOfItsLastIndex()
    {
        await SetStateAsync(CorpusState.Unavailable, new DateTime(2026, 10, 12, 9, 30, 0, DateTimeKind.Utc));

        var note = await NoteAsync();

        note.ShouldNotBeNull();
        note.ShouldContain("could not read its source on its last pass");
        note.ShouldContain("(last indexed 2026-10-12 09:30Z)");
        note.ShouldContain("'");
    }

    [Fact]
    public async Task AnUnavailableCorpusNeverIndexedSaysNever()
    {
        await SetStateAsync(CorpusState.Unavailable, lastIndexed: null);

        (await NoteAsync()).ShouldNotBeNull().ShouldContain("(last indexed never)");
    }

    [Fact]
    public async Task ADegradedCorpusSaysFilesAreMissing()
    {
        await SetStateAsync(CorpusState.Degraded, new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc));

        var note = await NoteAsync();

        note.ShouldNotBeNull();
        note.ShouldContain("had files that failed to embed on its last pass");
        note.ShouldContain("(last indexed 2026-11-02 08:00Z)");
        note.ShouldNotContain("could not read its source");
    }

    /// <summary>The harness's store answers no query. This one answers every query with no hits, so the note is what is read.</summary>
    private sealed class NoHits(IVectorStore inner) : IVectorStore
    {
        public string CollectionNameFor(EmbeddingTarget target, int dimensions) => inner.CollectionNameFor(target, dimensions);
        public Task EnsureCollectionAsync(string collection, int dimensions, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpsertAsync(string collection, IReadOnlyList<Chunk> chunks, IReadOnlyList<float[]> vectors, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteFileChunksAsync(string collection, string chunkSetId, string sourceId, string filePath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteChunkSetAsync(string collection, string chunkSetId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, int>?> CountByFileAsync(string collection, string chunkSetId, string sourceId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> PurgeUnsetChunksAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteCorpusAsync(string collection, string corpusId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SearchHit>> GetFileChunksAsync(string collection, string chunkSetId, string filePath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SearchResponse> SearchAsync(SearchQuery query, float[]? denseVector, SparseVector sparse, CancellationToken ct = default) =>
            Task.FromResult(new SearchResponse { Hits = [] });
        public Task<(long Points, int Dimensions)> GetStatsAsync(string collection, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(true);
    }
}
