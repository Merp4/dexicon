using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Dexicon.Core.Configuration;
using Dexicon.Core.Embedding;
using Dexicon.Core.Search;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;

// Qdrant's generated gRPC types collide by name with ours. Alias rather than
// fully-qualify at every use site, so the domain names stay readable.
using SparseVector = Dexicon.Core.Search.SparseVector;
using SearchResponse = Dexicon.Core.Search.SearchResponse;
using QdrantVectors = Qdrant.Client.Grpc.Vectors;

namespace Dexicon.Core.Vectors;

public interface IVectorStore
{
    string CollectionNameFor(string model, int dimensions);
    Task EnsureCollectionAsync(string collection, int dimensions, CancellationToken ct = default);
    Task UpsertAsync(string collection, IReadOnlyList<Chunk> chunks, IReadOnlyList<float[]> vectors, CancellationToken ct = default);
    Task DeleteFileChunksAsync(string collection, string chunkSetId, string filePath, CancellationToken ct = default);

    /// <summary>Drop one chunk set's vectors, leaving the rest of the corpus alone.</summary>
    Task DeleteChunkSetAsync(string collection, string chunkSetId, CancellationToken ct = default);

    /// <summary>
    /// Delete points written before chunk sets existed, which carry no chunk_set_id and
    /// therefore match no query. Returns how many collections were touched.
    /// </summary>
    Task<int> PurgeUnsetChunksAsync(CancellationToken ct = default);
    Task DeleteCorpusAsync(string collection, string corpusId, CancellationToken ct = default);

    /// <summary>
    /// Every chunk of one file, in file order. A FILTER, not a search: reconstructing a
    /// file is a lookup, and letting relevance decide which parts of it come back returns
    /// a plausible-looking file with holes in it.
    ///
    /// Returns the whole file even when the caller wants a window of it. A range condition
    /// on start_line would narrow the scroll, but it needs a payload index to be worth
    /// anything, and one file's chunks are bounded and partition-local already. Worth
    /// revisiting if a profile ever says so; not worth guessing at now.
    /// </summary>
    Task<IReadOnlyList<SearchHit>> GetFileChunksAsync(string collection, string chunkSetId, string filePath,
        CancellationToken ct = default);
    Task<SearchResponse> SearchAsync(SearchQuery query, float[]? denseVector, SparseVector sparse, CancellationToken ct = default);
    Task<(long Points, int Dimensions)> GetStatsAsync(string collection, CancellationToken ct = default);
    Task<bool> PingAsync(CancellationToken ct = default);
}

public sealed class QdrantVectorStore : IVectorStore, IDisposable
{
    public const string DenseVector = "dense";
    public const string SparseVectorName = "sparse";

    private readonly QdrantClient _client;
    private readonly ILogger<QdrantVectorStore> _log;
    private readonly HashSet<string> _ensured = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _ensureLock = new(1, 1);

    public QdrantVectorStore(IOptions<DexiconOptions> options, ILogger<QdrantVectorStore> log)
    {
        _log = log;
        var uri = new Uri(options.Value.Qdrant.Endpoint);
        _client = new QdrantClient(
            host: uri.Host,
            port: uri.Port > 0 ? uri.Port : 6334,
            https: uri.Scheme == "https",
            apiKey: options.Value.Qdrant.ApiKey);
    }

    /// <summary>
    /// <c>dexicon__{model}__{dims}</c>. Encoding the model and dimensionality in the name
    /// makes a mismatch structurally impossible: a corpus pinned to a model can only ever
    /// address that model's collection. See docs/03-data-model.md.
    /// </summary>
    public void Dispose()
    {
        _client.Dispose();
        _ensureLock.Dispose();
    }

    public string CollectionNameFor(string model, int dimensions)
    {
        var slug = new StringBuilder(model.Length);
        foreach (var ch in model.ToLowerInvariant())
            slug.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '-');
        var collapsed = string.Join('-', slug.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        return $"dexicon__{collapsed}__{dimensions}";
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try { await _client.ListCollectionsAsync(ct); return true; }
        catch (Exception ex) { _log.LogWarning(ex, "Qdrant ping failed"); return false; }
    }

    public async Task EnsureCollectionAsync(string collection, int dimensions, CancellationToken ct = default)
    {
        if (_ensured.Contains(collection)) return;

        await _ensureLock.WaitAsync(ct);
        try
        {
            if (_ensured.Contains(collection)) return;

            if (!await _client.CollectionExistsAsync(collection, ct))
            {
                await _client.CreateCollectionAsync(
                    collection,
                    vectorsConfig: new VectorParamsMap
                    {
                        Map = { [DenseVector] = new VectorParams { Size = (ulong)dimensions, Distance = Distance.Cosine } }
                    },
                    sparseVectorsConfig: new SparseVectorConfig
                    {
                        // Qdrant applies the IDF component, so we push raw term frequencies
                        // and never maintain corpus statistics ourselves.
                        Map = { [SparseVectorName] = new SparseVectorParams { Modifier = Modifier.Idf } }
                    },
                    // m=0 disables the global HNSW graph; payload_m builds one per corpus.
                    // A query without a corpus filter therefore has no index to traverse.
                    hnswConfig: new HnswConfigDiff { M = 0, PayloadM = 16 },
                    cancellationToken: ct);

                _log.LogInformation("Created collection {Collection} ({Dimensions}d, m=0 payload_m=16)",
                    collection, dimensions);
            }

            await EnsureIndexAsync(collection, "corpus_id", PayloadSchemaType.Keyword,
                new PayloadIndexParams { KeywordIndexParams = new KeywordIndexParams { IsTenant = true } }, ct);
            await EnsureIndexAsync(collection, "kind", PayloadSchemaType.Keyword, null, ct);
            await EnsureIndexAsync(collection, "file_path", PayloadSchemaType.Keyword, null, ct);
            await EnsureIndexAsync(collection, "language", PayloadSchemaType.Keyword, null, ct);
            await EnsureIndexAsync(collection, "symbols", PayloadSchemaType.Keyword, null, ct);
            await EnsureIndexAsync(collection, "source_id", PayloadSchemaType.Keyword, null, ct);

            _ensured.Add(collection);
        }
        finally { _ensureLock.Release(); }
    }

    private async Task EnsureIndexAsync(string collection, string field, PayloadSchemaType type,
        PayloadIndexParams? indexParams, CancellationToken ct)
    {
        try
        {
            await _client.CreatePayloadIndexAsync(collection, field, type, indexParams, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            // Creating an index that already exists is not an error worth failing a
            // startup over, but it IS worth saying out loud — a genuinely broken index
            // creation would otherwise look identical to a no-op.
            _log.LogDebug(ex, "Payload index {Field} on {Collection} not created (likely already present)",
                field, collection);
        }
    }

    public async Task UpsertAsync(string collection, IReadOnlyList<Chunk> chunks,
        IReadOnlyList<float[]> vectors, CancellationToken ct = default)
    {
        if (chunks.Count == 0) return;
        if (chunks.Count != vectors.Count)
            throw new ArgumentException($"chunk/vector count mismatch: {chunks.Count} vs {vectors.Count}");

        var points = new List<PointStruct>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            var sparse = SparseEncoder.Encode(c.TextToEmbed);

            var named = new NamedVectors();
            named.Vectors[DenseVector] = vectors[i];
            // (values, indices) converts implicitly to a sparse Vector. The older
            // Vector.Data/Vector.Indices pair is obsolete in Qdrant.Client 1.19.
            if (!sparse.IsEmpty) named.Vectors[SparseVectorName] = (sparse.Values, sparse.Indices);

            var p = new PointStruct
            {
                // Keyed on the SET, not the corpus: two sets hold the same file at the
                // same chunk index, and a corpus-keyed id would make them overwrite each
                // other — silently, and only for the file paths they happen to share.
                Id = new PointId { Uuid = DeterministicId(c.ChunkSetId, c.FilePath, c.ChunkIndex) },
                Vectors = new QdrantVectors { Vectors_ = named },
            };
            p.Payload.Add("kind", "chunk");
            p.Payload.Add("corpus_id", c.CorpusId);
            p.Payload.Add("chunk_set_id", c.ChunkSetId);
            p.Payload.Add("tenant_id", c.TenantId);
            p.Payload.Add("source_id", c.SourceId);
            p.Payload.Add("file_path", c.FilePath);
            p.Payload.Add("file_hash", c.FileHash);
            p.Payload.Add("start_line", c.StartLine);
            p.Payload.Add("end_line", c.EndLine);
            p.Payload.Add("chunk_index", c.ChunkIndex);
            p.Payload.Add("content", c.Content);
            p.Payload.Add("indexed_utc", DateTime.UtcNow.ToString("O"));
            if (c.MediaType is { } mt) p.Payload.Add("media_type", mt);
            if (c.Language is { } lang) p.Payload.Add("language", lang);
            if (c.Section is { } sec) p.Payload.Add("section", sec);
            if (c.Page is { } page) p.Payload.Add("page", page);
            if (c.Symbols.Count > 0) p.Payload.Add("symbols", c.Symbols.ToArray());

            points.Add(p);
        }

        await _client.UpsertAsync(collection, points, cancellationToken: ct);
    }

    /// <summary>
    /// UUIDv5-style deterministic id over corpus + path + index, so re-indexing an
    /// unchanged file is idempotent and a changed file's stale chunks are addressable
    /// without a scroll.
    /// </summary>
    internal static string DeterministicId(string corpusId, string filePath, int chunkIndex)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{corpusId}|{filePath}|{chunkIndex}"));
        var guid = new byte[16];
        Array.Copy(bytes, guid, 16);
        guid[6] = (byte)((guid[6] & 0x0F) | 0x50); // version 5
        guid[8] = (byte)((guid[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(guid).ToString();
    }

    public Task DeleteFileChunksAsync(string collection, string chunkSetId, string filePath, CancellationToken ct = default)
    {
        var filter = new Filter();
        filter.Must.Add(Keyword("chunk_set_id", chunkSetId));
        filter.Must.Add(Keyword("file_path", filePath));
        return _client.DeleteAsync(collection, filter, cancellationToken: ct);
    }

    public Task DeleteChunkSetAsync(string collection, string chunkSetId, CancellationToken ct = default)
    {
        var filter = new Filter();
        filter.Must.Add(Keyword("chunk_set_id", chunkSetId));
        return _client.DeleteAsync(collection, filter, cancellationToken: ct);
    }

    public async Task<int> PurgeUnsetChunksAsync(CancellationToken ct = default)
    {
        // Points from before chunk sets are unreachable, not merely stale: the search
        // filter requires a chunk_set_id and their ids were derived from the corpus, so a
        // re-index writes NEW points beside them rather than replacing them. Left alone
        // they would sit in the index forever, costing memory and matching nothing.
        var collections = await _client.ListCollectionsAsync(ct);
        var touched = 0;

        foreach (var collection in collections.Where(c => c.StartsWith("dexicon__", StringComparison.Ordinal)))
        {
            var filter = new Filter();
            filter.Must.Add(Keyword("kind", "chunk"));
            filter.Must.Add(new Condition { IsEmpty = new IsEmptyCondition { Key = "chunk_set_id" } });

            await _client.DeleteAsync(collection, filter, cancellationToken: ct);
            touched++;
        }

        return touched;
    }

    public Task DeleteCorpusAsync(string collection, string corpusId, CancellationToken ct = default)
    {
        var filter = new Filter();
        filter.Must.Add(Keyword("corpus_id", corpusId));
        return _client.DeleteAsync(collection, filter, cancellationToken: ct);
    }

    public async Task<IReadOnlyList<SearchHit>> GetFileChunksAsync(string collection, string chunkSetId,
        string filePath, CancellationToken ct = default)
    {
        if (!await _client.CollectionExistsAsync(collection, ct)) return [];

        var filter = new Filter();
        filter.Must.Add(Keyword("chunk_set_id", chunkSetId));
        filter.Must.Add(Keyword("kind", "chunk"));
        filter.Must.Add(Keyword("file_path", filePath));

        var hits = new List<SearchHit>();
        PointId? offset = null;

        while (true)
        {
            var page = await _client.ScrollAsync(collection, filter, limit: 1000, offset: offset,
                vectorsSelector: false, cancellationToken: ct);

            foreach (var p in page.Result) hits.Add(ToHit(p.Payload, 0f));

            if (page.Result.Count < 1000 || page.NextPageOffset is null) break;
            offset = page.NextPageOffset;
        }

        // Chunk index, not start line: slices of one over-long line share a line number.
        return [.. hits.OrderBy(h => h.ChunkIndex)];
    }

    public async Task<(long Points, int Dimensions)> GetStatsAsync(string collection, CancellationToken ct = default)
    {
        if (!await _client.CollectionExistsAsync(collection, ct)) return (0, 0);
        var info = await _client.GetCollectionInfoAsync(collection, ct);
        var dims = info.Config?.Params?.VectorsConfig?.ParamsMap?.Map is { } map &&
                   map.TryGetValue(DenseVector, out var vp)
            ? (int)vp.Size
            : 0;
        return ((long)info.PointsCount, dims);
    }

    public async Task<SearchResponse> SearchAsync(SearchQuery query, float[]? denseVector, SparseVector sparse,
        CancellationToken ct = default)
    {
        // The repository-level guard. Not a belt-and-braces check: with hnsw m=0 an
        // unfiltered query is also a full scan, so this catches a scope bug before it
        // becomes both a leak and an outage.
        if (query.CorpusIds.Count == 0) throw new UnscopedQueryException(nameof(SearchAsync));

        var sw = Stopwatch.StartNew();
        var filter = BuildFilter(query);
        var limit = (ulong)Math.Clamp(query.Limit, 1, 50);
        var prefetchLimit = (ulong)Math.Min(query.Limit * 4, 200);

        var mode = query.Mode;
        var degradedReason = (string?)null;

        // No dense vector means the embedding service was unavailable. Keyword still
        // works, so degrade to it and SAY SO rather than returning a thin hybrid.
        if (denseVector is null && mode is SearchMode.Hybrid or SearchMode.Semantic)
        {
            degradedReason = "embedding service unavailable — keyword-only results";
            mode = SearchMode.Keyword;
        }

        if (mode == SearchMode.Keyword && sparse.IsEmpty)
            return new SearchResponse
            {
                Hits = [],
                Mode = mode,
                Degraded = degradedReason is not null,
                DegradedReason = degradedReason,
                TookMs = sw.ElapsedMilliseconds,
            };

        IReadOnlyList<ScoredPoint> points;
        switch (mode)
        {
            case SearchMode.Semantic:
                points = await _client.QueryAsync(query.CollectionName, query: denseVector!, usingVector: DenseVector,
                    filter: filter, limit: limit, payloadSelector: true, cancellationToken: ct);
                break;

            case SearchMode.Keyword:
                points = await _client.QueryAsync(query.CollectionName, query: (sparse.Values, sparse.Indices),
                    usingVector: SparseVectorName, filter: filter, limit: limit, payloadSelector: true,
                    cancellationToken: ct);
                break;

            default:
                var prefetch = new List<PrefetchQuery>
                {
                    new() { Query = denseVector!, Using = DenseVector, Limit = prefetchLimit, Filter = filter },
                };
                if (!sparse.IsEmpty)
                    prefetch.Add(new PrefetchQuery
                    {
                        Query = (sparse.Values, sparse.Indices),
                        Using = SparseVectorName,
                        Limit = prefetchLimit,
                        Filter = filter,
                    });

                // Server-side reciprocal rank fusion. Rank-based, so no weight to tune
                // and nothing to mis-set. Verified against the RRF k=2 formula in M0.
                points = await _client.QueryAsync(query.CollectionName, query: Fusion.Rrf, prefetch: prefetch,
                    filter: filter, limit: limit, payloadSelector: true, cancellationToken: ct);
                break;
        }

        return new SearchResponse
        {
            Hits = points.Select(p => ToHit(p.Payload, p.Score)).ToList(),
            Mode = mode,
            Degraded = degradedReason is not null,
            DegradedReason = degradedReason,
            TookMs = sw.ElapsedMilliseconds,
        };
    }

    private static Filter BuildFilter(SearchQuery q)
    {
        var f = new Filter();
        f.Must.Add(Keyword("kind", "chunk"));

        // corpus_id first: it is the is_tenant key, so it selects the partition. The set
        // filter then narrows within it, which is why chunk_set_id does not need to be a
        // tenant key of its own.
        var corpora = new Match { Keywords = new RepeatedStrings() };
        corpora.Keywords.Strings.AddRange(q.CorpusIds);
        f.Must.Add(new Condition { Field = new FieldCondition { Key = "corpus_id", Match = corpora } });

        if (q.ChunkSetIds.Count > 0)
        {
            var sets = new Match { Keywords = new RepeatedStrings() };
            sets.Keywords.Strings.AddRange(q.ChunkSetIds);
            f.Must.Add(new Condition { Field = new FieldCondition { Key = "chunk_set_id", Match = sets } });
        }

        if (!string.IsNullOrWhiteSpace(q.Language)) f.Must.Add(Keyword("language", q.Language));
        if (!string.IsNullOrWhiteSpace(q.Symbol)) f.Must.Add(Keyword("symbols", q.Symbol));
        if (!string.IsNullOrWhiteSpace(q.PathPrefix))
            f.Must.Add(new Condition
            {
                Field = new FieldCondition
                {
                    Key = "file_path",
                    Match = new Match { Text = q.PathPrefix },
                },
            });

        return f;
    }

    private static Condition Keyword(string key, string value) =>
        new() { Field = new FieldCondition { Key = key, Match = new Match { Keyword = value } } };

    /// <summary>
    /// Payload and score separately, because chunks arrive two ways: scored, from a
    /// search, and unscored, from a filtered scroll that reconstructs a whole file.
    /// </summary>
    private static SearchHit ToHit(MapField<string, Value> payload, float score)
    {
        string? Str(string k) => payload.TryGetValue(k, out var v) ? v.StringValue : null;
        int Int(string k) => payload.TryGetValue(k, out var v) ? (int)v.IntegerValue : 0;

        return new SearchHit
        {
            CorpusId = Str("corpus_id") ?? "",
            FilePath = Str("file_path") ?? "",
            Language = Str("language"),
            StartLine = Int("start_line"),
            EndLine = Int("end_line"),
            ChunkIndex = Int("chunk_index"),
            Page = payload.TryGetValue("page", out var pg) ? (int)pg.IntegerValue : null,
            Section = Str("section"),
            Symbols = payload.TryGetValue("symbols", out var sym) && sym.ListValue is not null
                ? sym.ListValue.Values.Select(v => v.StringValue).ToList()
                : [],
            Content = Str("content") ?? "",
            Score = score,
        };
    }
}
