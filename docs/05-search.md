# 05 — Search

## Shape of a query

```
search(query, scope, mode, limit, filters)
```

- **`query`** — natural language or code fragment.
- **`scope`** — one or more corpus names or ids (the `corpus` argument). Resolved against the caller's visible set.
  **Never optional in effect:** if the caller names nothing, the scope becomes every corpus
  they can see; if that set is empty, the request is an error, not an empty search. See
  [07](07-auth.md).
- **`mode`** — `hybrid` (default) | `semantic` | `keyword`.
- **`limit`** — 1–50, default 10.
- **`filters`** — optional: `source`, `pathPrefix`, `language`, `symbol`.

  The REST request (`POST /api/search`) and the `search_index` tool take these as top-level
  arguments in camelCase: `corpus`, `mode`, `limit`, `source`, `pathPrefix`, `language`,
  `symbol`, `maxCharsPerHit` and `distinctTitles`.

  `source` narrows to one source of a corpus, named by its root path as a search result
  cites it (`manuals/AI`); `index_status` lists the sources of a corpus named in the call.
  A parent folder matches everything beneath it. It is the only
  way to narrow by *where* content came from, because `pathPrefix` matches `file_path`,
  which is relative to a source root: a corpus with a source at `manuals/AI` stores its
  files as bare names, and no prefix matches the folder they live in. Resolution runs
  against the already-authorised scope, so naming a source can only narrow a search,
  never reach into a corpus the caller cannot see.

## Retrieval

Hybrid is a single Qdrant Query API call with two prefetches and server-side fusion. There
is no client-side score merging within a collection. A scope that spans two embedding
models runs one query per collection and merges the hits by score, which is approximate
across models.

```jsonc
POST /collections/dexicon__ollama__embeddinggemma__768/points/query
{
  "prefetch": [
    { "query": { "nearest": [0.01, 0.45, ...] },            // dense, from Ollama
      "using": "dense",  "limit": 40, "filter": { "$scope": "..." } },
    { "query": { "indices": [12, 88, ...],                   // sparse, in-process TF
                 "values":  [2.0, 1.0, ...] },
      "using": "sparse", "limit": 40, "filter": { "$scope": "..." } }
  ],
  "query": { "fusion": "dbsf" },
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
    // plus the chunk_set_id of each corpus in scope, and any caller filters:
    // source_id, file_path prefix, language, symbols
] }
```

**Why DBSF rather than RRF or a weighted merge.** Dense cosine scores and sparse keyword
scores live on different scales, and a weight that balances them is corpus-dependent and
drifts as content changes, so a `SemanticWeight` knob was rejected: nobody could set it
from evidence.

RRF avoids the knob by reading rank rather than magnitude, and that was the default until it
was measured. Rank alone cannot express that one list is worse than the other, so a lexical
match at rank 3 counted for as much as a semantic match at rank 3: a question about
event-driven architecture returned a chapter on C# delegates, the word "event" being in all
of them. DBSF normalises each list's scores before combining, so a weak match contributes in
proportion to how weak it is — and it has no weight either, so the original objection does
not apply to it. Measured better on conceptual questions, on verbatim passages and on exact
identifiers; the numbers are in [D-06](decisions.md#d-06-score-fusion-server-side).

**Prefetch limit is 4× the query's limit** (capped at 800), and the query's limit is itself
16× the requested limit (capped at 400) when near-duplicate documents are being collapsed,
which is the default. The `limit: 40` above is a request for 10 with collapsing off.
Fusion needs enough candidates from each retriever to have something to fuse, and
collapsing needs spare candidates to promote.

### Verified in the M0 spike (2026-09-16)

The spike used RRF. Production moved to DBSF on 2026-09-18
([D-06](decisions.md#d-06-score-fusion-server-side)). The block below records what the
spike measured: `Fusion.Rrf`, the `k=2` formula and the scores are RRF's, and the call
shape is the one that still applies with `Fusion.Dbsf` in its place.

The REST shape above is the wire format. From .NET it is a single call: `Qdrant.Client`
1.19.0 exposes fusion as a first-class `Query`, so the `{"fusion": "rrf"}` versus
`{"rrf": {}}` ambiguity does not reach application code:

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

Fusion is performed server-side and is rank-based. Qdrant uses **RRF with k=2**, so an
item at rank `r₀` and `r₁` in the two prefetches scores `1/(2+r₀) + 1/(2+r₁)`. Measured
output on a seven-document corpus, where a document ranked first in both lists scores
`1.0`, a value no cosine similarity produces:

```
 1.00000  docs/auth.md                 (rank 0 dense, rank 0 sparse)
 0.66667  src/Auth/TokenService.cs     (rank 1 dense, rank 1 sparse)
 0.25000  src/Auth/LoginController.cs  (rank 2 dense, absent from sparse)
```

Every returned score matched the formula to within 1e-4.

### Mode behaviour

| Mode | Behaviour |
|---|---|
| `hybrid` | Both prefetches, DBSF fusion. The default. |
| `semantic` | Dense only. Use when wording matters more than vocabulary. |
| `keyword` | Sparse only. Exact identifiers, error strings, config keys. No embedding call, so it works when the embedding service is down. |

### Degradation

If the embedding service is unavailable, `hybrid` and `semantic` **fall back to keyword**
and set `degraded: true` with `degradedReason` in the response, and log at Warning. The
caller is told; the result is not passed off as a full hybrid search. Over MCP the same
fact is a `! DEGRADED:` line after the header.

If the query vector's length differs from the chunk set's pinned dimensions, the search is
**refused**, not degraded, with a message naming both numbers and the rebuild action.
Returning plausible results from a mismatched space is worse than returning none.

## Result contract

`POST /api/search` returns this JSON: camelCase property names, enums as their member
names, and properties with no value omitted (`page`, `degradedReason` and `note` below
appear only when set).

```jsonc
{
  "query": "how do we refresh auth tokens",
  "mode": "Hybrid",
  "degraded": false,
  "scope": [{ "id": "01JD...", "name": "api-repo", "state": "Ready" }],
  "hits": [
    {
      "score": 1.2,                       // fused score; ordering is meaningful, magnitude is not
      "corpusId": "01JD...",
      "corpusName": "api-repo",
      "sourceId": "01JD...",
      "sourceRoot": "repos/api-repo",     // the source's root; filePath is relative to it
      "filePath": "src/Auth/TokenService.cs",
      "location": "src/Auth/TokenService.cs:120-168",
      "language": "csharp",
      "startLine": 120, "endLine": 168,
      "chunkIndex": 7,
      "section": "TokenService.RefreshAsync",
      "symbols": ["TokenService", "RefreshAsync"],
      "content": "public async Task<TokenPair> RefreshAsync(...) { ... }"
    }
  ],
  "tookMs": 210
}
```

`page` is set for PDF, PPTX and EPUB hits, whose `location` is then
`file#page=n` (`#slide=n` for a deck, `#chapter=n` for an EPUB). `degradedReason` is
present when `degraded` is true. `note` carries two warnings: a corpus in scope is still
indexing, and collapsing duplicates returned fewer hits than the `limit` asked for
(`N of M asked for`). `scope[].name` is qualified (`corpus:set`) for a set other than the
default.

Over MCP, `search_index` returns one text block for a model to read, not this JSON: a
header (`N results for "query" (mode, corpus: names)`), then any `! DEGRADED:` and `!` note
lines, then each hit as a numbered location line followed by its content. The structured
result is the response of `POST /api/search` ([13](13-integration.md)).

Three design choices:

1. **`location` is a formatted string** as well as structured fields. `file:line` is
   clickable in every editor and is what an agent will paste back to the user.
2. **`content` is a window centred on the match**: 1,500 characters by default, and the
   whole chunk when `maxCharsPerHit` is 0 (next section). A result that forces a follow-up
   read has cost two round trips to save bytes, so the window is cut around what matched.
3. **`score` is ordinal.** A fused score is normalised against the other candidates of one
   query and means nothing across queries, and the first thing anyone does with a float is
   threshold it.

## What a result returns, and what it costs

A result is a **window onto the matching passage**, not the whole chunk. The chunk is the
unit that was embedded and the unit that scored; at 2,065 tokens on the book corpus it is
the right size for retrieval and far too large to hand back. Measured over eight questions,
five results each, returning chunks whole cost a mean of 40,797 characters, about 10,200
tokens: an agent with a 200k window could afford twenty searches.

**The window is centred on what matched.** Head truncation was the obvious implementation
and loses the answer. On the same corpus, with a 1,500-character window:

| | |
|---|---|
| first matching term already past the window | 12% of hits |
| every matching term inside the window | 25% of hits |
| where matched terms sit (0 = start, 1 = end) | median 0.10, 90th 0.56 |

So a head-cut preview would have carried none of what the query matched for one hit in
eight. Centring fixes that. The window grows outwards from the matching line, is cut on line
boundaries, and marks an elision with `…` so a window is distinguishable from a whole chunk.
When a chunk has no usable line breaks near the match — a PDF page extracted as one line, a
minified file — it is cut on characters instead.

`maxCharsPerHit` sets the budget, default 1,500. `0` returns whole chunks, which is what
comparing two extractions of one title wants and what an agent almost never does. On the
eight queries: **40,715 → 6,532 characters per call, 10,178 → 1,633 tokens.**

**`distinctTitles`**, default on, returns one result per document. A library holding each
title as both PDF and EPUB returned 3.0 distinct books per 5 results; collapsing them raises
that to 4.8 and takes the text not already returned earlier in the same response from 85% to
100%. This is a **quality** fix and is counted as one: it saved 0.2% of the tokens, because
dropping a duplicate only promotes another chunk of the same size. The limit is applied
after collapsing, and the vector query over-fetches, so a dropped duplicate promotes the
next distinct hit instead of leaving a gap. Collapsing can still return fewer results than
were asked for, and says so.

Turning it off returns every copy, which is how two extractors get compared on one title.

## Retrieving more context

Search returns windows onto chunks. Two ways to get more, and only one of them is a tool:

- **`get_context(corpus, filePath, aroundLine, before, after, lineNumbers)`** — an MCP tool
  ([06](06-mcp-surface.md)). Returns the lines around a location, windowed out of the
  extracted document. Works for uploads with no file on disk.
- **`dexicon://corpus/{name}/file/{path}`** — an MCP *resource*, not a tool. The
  extracted text of one indexed file, whole. It comes from
  `blob_texts` or `file_texts`; where neither holds it, which is a code file on a mount,
  the chunks are stitched back together and the gaps are marked. A path is relative to its
  source root, so where two sources of the corpus hold the same path, the resource reads the
  one with the most chunks, as `get_context` does, and prints the same warning under the
  header ([06](06-mcp-surface.md#resources)).

Whole-file retrieval is a resource rather than another tool. It is a read of a named
thing, which is what resources are for, and D-11 treats the tool count as a budget every
agent pays on every turn. For workspace corpora an agent can usually read the real
file faster anyway; this path exists for uploads and for agents without filesystem access.

## Performance

| Component | Expectation |
|---|---|
| Query embedding (`nomic-embed-text`, warm) | 15–40 ms |
| Sparse encoding (in-process) | < 1 ms |
| Qdrant hybrid query, 400k points, corpus-indexed | 20–80 ms |
| Total p95 | **< 400 ms** |

A cold Ollama, with the model not resident, adds seconds. The authenticated `/healthz`
reports whether the default embedding provider answers (it probes the model's dimensions
when they are not yet known), and the UI shows it.

Query embeddings are cached in memory keyed by provider, model and the query text as
typed, with a 5-minute TTL. Agents repeat queries far more than people do.

## Quality, and how it was judged

No reranker, no query rewriting, no HyDE ([01](01-overview.md)). Before adding any of them
there has to be a measurement, so M3 of the [roadmap](11-roadmap.md) built one: two
committed query sets (55 over `docs/`, 52 over `src/`) scored on MRR and hit@1, 3 and 5 by
`scripts/bench/sweep.py`, run against three embedding models, three chunk sizes and three
boundary modes. Results and reproduction are in [benchmarks](benchmarks.md).

The purpose is to give any proposed reranker a baseline to improve on, and to derive the
defaults in [04](04-ingestion.md) from measurement.
