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
- **`filters`** — optional: `source`, `pathPrefix`, `language`, `symbol`, `section`, `phrase`.

  The REST request (`POST /api/search`) and the `search_index` tool take these as top-level
  arguments in camelCase: `corpus`, `mode`, `limit`, `source`, `pathPrefix`, `language`,
  `symbol`, `section`, `phrase`, `maxCharsPerHit` and `distinctTitles`.

  `section` restricts the search to chunks whose `section` is exactly the value given, in the case written. For a
  commit or stash of a git-history corpus the values are `message` (the header, author, date and message),
  `stat` (the files touched) and `diff` (the patch), and every chunk of such a document is one of them, except in a
  document that has no section ([04](04-ingestion.md#git-history--a-repositorys-commits)). A question about why something changed is `message`,
  and a code fragment to find in the changes is `diff`. A source that leaves the diff out has no `diff` chunks, so
  that filter matches nothing there. For a Markdown file the section is the heading the chunk starts under, so
  `Installation` matches the chunks under a heading of that text. A chunk of a PDF, a slide deck or an EPUB has the
  label of the page, slide or chapter it starts in (`Page 3`, `Slide 3`, `Chapter 3`), which names one position in every
  document of the scope and is of little use as a filter. A value that no chunk carries matches nothing. A search over several corpora
  applies the value to all of them, so a search for a commit section names the history corpus beside it. `section`
  selects the chunks that can match. The chunks either side of a hit that `neighbours` adds to `POST /api/context` are the adjacent chunks of
  the document, whatever their section.

  `phrase` keeps the chunks whose text holds the given words in that order, which the
  index cannot answer by itself. [Exact phrases](#exact-phrases) has what it matches and
  what it costs.

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
    // source_id, file_path prefix, language, symbols, and for a phrase a
    // match.phrase condition on content
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

## Exact phrases

`phrase` keeps the chunks whose text contains the given words in that order. The sparse
vector is a bag of terms without positions, so the index cannot match a phrase. Qdrant
checks the stored text of each chunk instead: the request adds a `match.phrase` condition on
`content` to the filter of both prefetches and of the final query. Every chunk in scope that
holds the phrase is then a candidate in `hybrid` and `semantic` mode, and a result shorter than
`limit` is every match there is, after near-duplicate titles are collapsed. In `keyword` mode the
candidates are the chunks that share a word with `query`, so a short result may leave matches
out (see Limits).

Nothing is added to the index. `content` has no payload index and `phrase` does not create
one, so index size, memory and indexing time are as they were, and a search without `phrase`
sends the query it always sent. The cost is paid at query time, by the searches that use it.

`query` ranks and `phrase` filters. Given both, the hits are the best matches for `query`
among the chunks that hold the phrase. Given only `phrase`, with `query` left empty, the
phrase is the query: it is embedded and encoded as one, and the result echoes it as `query`.
The returned window is centred on the phrase rather than on the query terms.

### What matches

Without a full-text index, Qdrant applies its `word` tokenizer and lowercases the text
([filtering](https://qdrant.tech/documentation/search/filtering/), phrase match, available
from Qdrant 1.15.0). A run of letters and digits is a word. Anything else separates words:
spaces, line breaks, punctuation, underscores, hyphens, dots and slashes. The words of the
phrase have to occur in the text next to each other and in the same order, in any case.
Nothing else is folded: there is no stemming, and no mapping of accents, ligatures or Unicode
normalisation forms.

Measured on the docs corpus (297 chunks) against a reference tokeniser that takes a word to
be a run of Unicode letters and digits, over 200 phrase variants (as typed, reversed, upper
case, last word cut to four letters, second word dropped), Qdrant returned the chunks the
reference selected for all 200.

| Phrase | Finds | Chunks it matched |
|---|---|---|
| `chunk set id` | `chunk_set_id` | 8, the same as the underscore form |
| `D 39` | `D-39` | 14, the same as the hyphen form |
| `1 500 characters` | `1,500 characters` | 6, the same as the comma form |
| `dexico` | not `Dexicon` | 0, where `Dexicon` matched 142 |
| `distinct titles` | not `DistinctTitles` | 0, where `DistinctTitles` matched 4 |

So `refresh` does not find `RefreshAsync`: the identifier is one word. A caller used to a
substring search has to name the word, and an empty result over MCP says so.

A phrase is refused with a 400 (an error over MCP) when it has no letter or digit, such as
`=>`, because Qdrant matches it against nothing and an empty answer would read as "not in
the index". It is also refused above 500 characters. The message does not repeat the phrase.

### Cost

A phrase search tokenises the text of the chunks the query reaches, which an index would
have spared. Measured against a live Qdrant 1.20.0 (one collection of 12 corpora and 261,135
chunks), with phrases of 3 to 6 words (at most 90 characters) taken from random chunks of
at least 400 characters, 12 per corpus, `limit` 10 (a window of 160 candidates), median
milliseconds for one query:

| Scope | Chunks | `hybrid` without phrase | `hybrid` with phrase | `keyword` without | `keyword` with |
|---|---|---|---|---|---|
| a code corpus | 24,361 | 79 | 900 | 27 | 363 |
| a library of books | 28,535 | 90 | 770 | 24 | 294 |
| a library of papers | 191,703 | 158 | 2,525 | 16 | 642 |
| every corpus, one phrase | 261,135 | 129 | 2,646 | 18 | 417 |

The slowest query measured was a very common phrase (`in the case of`) across every corpus:
4.7 seconds in `hybrid` and 0.4 seconds in `keyword`. An absent phrase in the papers took 1.8
to 2.4 seconds in `hybrid`. These are single runs on a shared host, so read them as orders of
magnitude. The cost grows with the chunks in scope, which makes naming the corpus the first
thing to do, and `keyword` mode was 2.5 to 6.3 times faster than `hybrid` in every row.
Nothing limits how many phrase searches run at once, as nothing limits searches in general,
and each occupies Qdrant for about the times above.

The chunk a phrase was taken from was returned for 35 of 36 phrases (12 from each of three
corpora), and for 59 of 60 in a second run on the papers corpus. The miss in the second run
was a phrase of three common words that more chunks contain than the window of 160 holds
(160 came back), so the chunk ranked below the cut. The miss in the first run was not
examined.

### Limits

- **Keyword mode needs a term to search on.** The keyword vector drops stop words and
  one-character tokens, so a query, or a phrase with no query, made only of those has
  nothing to retrieve with in `keyword` mode, and the result is empty although the phrase
  is in the text (`it is to be` returned 73 chunks in `hybrid` and none in `keyword`). The
  result's `note` says so.
- **In `keyword` mode the candidates are the chunks that share a term with `query`.** The
  phrase filters them, so a phrase beside a query it has no term in common with returns
  nothing. Leave `query` empty to search for the phrase alone.
- **The text is the extracted text.** Hyphenation across a PDF's line break, a ligature
  such as `ﬁ`, or a table that the extractor reads in another order are matched as they
  were extracted.

### Why not a window over the ranking, or an index

The decision is [D-50](decisions.md#d-50-a-search-can-ask-for-a-phrase-and-qdrant-checks-the-stored-text).

Two alternatives were weighed before this one. The first keeps the hits of the usual
candidate window whose text holds the phrase. It adds little to the query (in the three larger corpora a
window of 400 took a median 71 to 100 ms and carried 1.0 to 2.4 MB of text), but the window
holds the chunk a phrase came from only some of the time. For 320 phrases taken from real
chunks and searched for as the query, with a window of 400 in `hybrid` mode, the chunk was
inside it for 100% of the docs corpus (297 chunks, so the window held everything), 75% of the
books, 60% of the papers and 75% of the code. Across the three larger corpora it was inside
for 93% of phrases whose rarest word is in under 0.2% of chunks, 69% for 0.2% to 2%, and 53%
for 2% to 20% (word frequencies estimated from 1,500 random chunks per corpus; 4 phrases
with a rarest word in over 20% of chunks are left out of those three, and none of them was
inside). A window of 500 added 0 to 4 points by corpus. A short result could not be told from
a complete one.

The second is a full-text payload index on `content`, which makes the filter cheap and costs
index size and memory in every collection, including for corpora that never use a phrase; the
largest corpus here has 191,703 chunks.

## Result contract

`POST /api/search` returns this JSON: camelCase property names, enums as their member
names, and properties with no value omitted (`page`, `degradedReason` and `note` below
appear only when set).

```jsonc
{
  "query": "how do we refresh auth tokens",
  "phrase": "refresh token",                // only when the request had one; whitespace collapsed
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
present when `degraded` is true. `phrase` is present when the request had one, and `query`
is then the phrase if the request's query was blank. `note` carries up to five warnings: a corpus in scope is still
indexing; a corpus's last pass could not read its source (`could not read its source on its last
pass; results are from the index it had before`) or had files that failed to embed, each naming the
corpus with the time of the last pass that completed (`last indexed 2026-10-12 09:30Z`, or `never`);
collapsing duplicates returned fewer hits than the `limit` asked for
(`N of M asked for`); and a `keyword` search with a `phrase` has a query with no term the keyword
index holds. A set's state is its last pass's outcome, so the note appears for as long as
the state does, and clears on the next pass that completes. `scope[].name` is qualified (`corpus:set`) for a set other than the
default.

Over MCP, `search_index` returns one text block for a model to read, not this JSON: a
header (`N results for "query" (mode, corpus: names)`, or `N results containing "phrase" (...)`
for a search by phrase alone, or `N results for "query" containing "phrase" (...)`), then any
`! DEGRADED:` and `!` note lines, then each hit as a numbered location line followed by its content. The structured
result is the response of `POST /api/search` ([13](13-integration.md)).

Three design choices:

1. **`location` is a formatted string** as well as structured fields. `file:line` is
   clickable in every editor and is what an agent will paste back to the user.
2. **`content` is a window centred on the match**: 1,500 characters by default, and the
   whole chunk when `maxCharsPerHit` is 0 (next section). A result that forces a follow-up
   read has cost two round trips to save bytes, so the window is cut around what matched,
   or around the phrase when the search has one.
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
| A search with a `phrase`, 24,000 to 261,000 chunks in scope | 0.3 to 2.6 s median, 4.7 s slowest measured ([Cost](#cost)) |

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
