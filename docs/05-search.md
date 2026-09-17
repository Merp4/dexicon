# 05 — Search

## Shape of a query

```
search(query, scope, mode, limit, filters)
```

- **`query`** — natural language or code fragment.
- **`scope`** — one or more corpus names or ids. Resolved against the caller's visible set.
  **Never optional in effect:** if the caller names nothing, the scope becomes every corpus
  they can see; if that set is empty, the request is an error, not an empty search. See
  [07](07-tenancy-auth.md).
- **`mode`** — `hybrid` (default) | `semantic` | `keyword`.
- **`limit`** — 1–50, default 10.
- **`filters`** — optional: `source`, `path_prefix`, `language`, `symbol`, `media_type`.

  `source` narrows to one source of a corpus, named by its root path as `list_corpora`
  reports it (`orly/AI`); a parent folder matches everything beneath it. It is the only
  way to narrow by WHERE content came from, because `path_prefix` matches `file_path`,
  which is relative to a source root — a corpus with a source at `orly/AI` stores its
  files as bare names, and no prefix matches the folder they live in. Resolution runs
  against the already-authorised scope, so naming a source can only narrow a search,
  never reach into a corpus the caller cannot see.

## Retrieval

Hybrid is a single Qdrant Query API call with two prefetches and server-side fusion. There
is no client-side score merging.

```jsonc
POST /collections/dexicon__nomic-embed-text__768/points/query
{
  "prefetch": [
    { "query": { "nearest": [0.01, 0.45, ...] },            // dense, from Ollama
      "using": "dense",  "limit": 40, "filter": { "$scope": "..." } },
    { "query": { "indices": [12, 88, ...],                   // sparse, in-process TF
                 "values":  [2.0, 1.0, ...] },
      "using": "sparse", "limit": 40, "filter": { "$scope": "..." } }
  ],
  "query": { "fusion": "rrf" },
  "filter": { "$scope": "..." },
  "limit": 10,
  "with_payload": true
}
```

where `$scope` expands to

```jsonc
{ "must": [
    { "key": "kind",      "match": { "value": "chunk" } },
    { "key": "corpus_id", "match": { "any": ["01JD...", "01JE..."] } }
    // plus any caller filters: file_path prefix, language, symbols, media_type
] }
```

**Why RRF rather than weighted score fusion.** Dense cosine scores and BM25 scores live on
different scales, and the weight that balances them is corpus-dependent and drifts as
content changes. RRF reads rank, not magnitude, so it needs no tuning and cannot be
silently mis-weighted. DBSF is available as a configuration option for anyone who wants
distribution-normalised scores; it is not the default. This replaces the client-side
weighted fusion carried over from McpToolbox, which required a `SemanticWeight` knob nobody
could set from evidence. See [D-06](decisions.md#d-06-rrf-fusion-server-side).

**Prefetch limit is 4× the requested limit** (capped at 200). Fusion needs enough candidates
from each retriever to have something to fuse.

### Verified in the M0 spike (2026-09-16)

The REST shape above is the wire format. From .NET it is one call — `Qdrant.Client` 1.19.0
exposes fusion as a first-class `Query`, so the ambiguity this document used to flag
(`{"fusion": "rrf"}` vs `{"rrf": {}}`) never reaches our code:

```csharp
var hits = await client.QueryAsync(
    collection,
    query: Fusion.Rrf,                       // implicitly converts to Query
    prefetch:
    [
        new PrefetchQuery { Query = denseVector,        Using = "dense",  Limit = 40, Filter = scope },
        new PrefetchQuery { Query = (values, indices),  Using = "sparse", Limit = 40, Filter = scope },
    ],
    filter: scope,
    limit: 10,
    payloadSelector: true);
```

The fusion is genuinely server-side and genuinely rank-based. Qdrant uses **RRF with k=2**,
so an item at rank `r₀` and `r₁` in the two prefetches scores `1/(2+r₀) + 1/(2+r₁)`.
Measured output on a seven-document corpus — a document top of both lists scores exactly
`1.0`, which no cosine similarity produces:

```
 1.00000  docs/auth.md                 (rank 0 dense, rank 0 sparse)
 0.66667  src/Auth/TokenService.cs     (rank 1 dense, rank 1 sparse)
 0.25000  src/Auth/LoginController.cs  (rank 2 dense, absent from sparse)
```

Every returned score matched the formula to within 1e-4.

### Mode behaviour

| Mode | Behaviour |
|---|---|
| `hybrid` | Both prefetches, RRF. The default. |
| `semantic` | Dense only. Use when wording matters more than vocabulary. |
| `keyword` | Sparse only. Exact identifiers, error strings, config keys. No Ollama call — works when embeddings are down. |

### Degradation

If the embedding service is unavailable, `hybrid` and `semantic` **fall back to keyword**
and set `degraded: true` with `degraded_reason` in the response, and log at Warning. The
caller is told; the result is not passed off as a full hybrid search.

If the corpus's pinned dimensions do not match what the collection reports, the search is
**refused** — not degraded — with a message naming both numbers and the rebuild action.
Returning plausible results from a mismatched space is worse than returning none.

## Result contract

```jsonc
{
  "query": "how do we refresh auth tokens",
  "mode": "hybrid",
  "degraded": false,
  "scope": [{ "id": "01JD...", "name": "api-repo" }],
  "count": 3,
  "results": [
    {
      "score": 0.0312,                    // RRF score; ordering is meaningful, magnitude is not
      "corpus": "api-repo",
      "file_path": "src/Auth/TokenService.cs",
      "location": "src/Auth/TokenService.cs:120-168",
      "language": "csharp",
      "start_line": 120, "end_line": 168,
      "page": null,
      "section": "TokenService.RefreshAsync",
      "symbols": ["TokenService", "RefreshAsync"],
      "content": "public async Task<TokenPair> RefreshAsync(...) { ... }"
    }
  ],
  "took_ms": 210
}
```

Three deliberate choices:

1. **`location` is a formatted string** as well as structured fields. `file:line` is
   clickable in every editor and is what an agent will paste back to the user.
2. **`content` is the full chunk**, not a snippet. The caller decides what to trim; a
   truncated result that forces a follow-up read has cost two round trips to save bytes.
3. **`score` is documented as ordinal.** RRF magnitudes mean nothing across queries, and
   the first thing anyone does with a float is threshold it. Say so in the schema
   description, not only here.

## Retrieving more context

Search returns chunks. Two ways to get more, and only one of them is a tool:

- **`get_context(corpus, file_path, around_line, before, after, line_numbers)`** — an MCP tool
  ([06](06-mcp-surface.md)). Returns the neighbouring lines from the stored chunks for that
  file, stitched and de-overlapped. Works for uploads with no file on disk.
- **`dexicon://corpus/{name}/file/{path}`** — an MCP *resource*, not a tool. The
  reconstructed text of one indexed file, capped at a configurable size.

Whole-file retrieval is a resource rather than a sixth tool deliberately. It is a read of a
named thing, which is what resources are for, and D-11 treats the tool count as a budget
every agent pays on every turn. For workspace corpora an agent can usually read the real
file faster anyway; this path exists for uploads and for agents without filesystem access.

## Performance

| Component | Expectation |
|---|---|
| Query embedding (`nomic-embed-text`, warm) | 15–40 ms |
| Sparse encoding (in-process) | < 1 ms |
| Qdrant hybrid query, 400k points, tenant-indexed | 20–80 ms |
| Total p95 | **< 400 ms** |

Cold Ollama — model not resident — adds seconds. Readiness on `/healthz` reports whether
the embedding model is loaded, and the UI shows it, so "first search is slow" is visible
rather than mysterious.

Query embeddings are cached in memory keyed by `(model, normalized query)` with a short
TTL. Agents repeat queries far more than people do.

## Quality, and how it will be judged

No reranker, no query rewriting, no HyDE ([01](01-overview.md)). Before adding any of them
there has to be a measurement, so M3 of the [roadmap](11-roadmap.md) builds a small fixed
evaluation set: 40–60 (query, expected file) pairs over a real repository, scored on
recall@10 and MRR, run against each candidate embedding model and each chunking mode.

The point is not the score. It is that the next person who wants to add a reranker has a
number to beat, and the defaults in [04](04-ingestion.md) stop being guesses.
