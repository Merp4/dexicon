---
# dexicon-skill-version: 2
name: dexicon-search
description: Search indexed code and documents by meaning using the Dexicon MCP server. Use when looking for where something is implemented, how a concept is handled, or what a document says about a topic — anything where you know the idea but not the term. Also covers reading an indexed file back and diagnosing an empty result.
---

# Dexicon

Semantic and keyword search over corpora someone has chosen to index: source trees, specs,
manuals, books. Dexicon is a **remote MCP server**; if its tools are not present, it is not
connected, and nothing in this file applies.

## Invoked directly

A slash invocation is a request to search now, not to read guidance. Take the text after the
command as the query, run `search_index`, and report the hits with their paths and line
numbers. If the text names a corpus, search that one. Otherwise, when more than one is
indexed, search the one or two whose `list_corpora` descriptions fit the query, and widen to
all of them only when none fits or that search comes back thin. With nothing after the
command, run `list_corpora` and say what is indexed.

The rest of this file is for deciding when to search without being asked, and does not need
repeating back.

## The tools

Start with `list_corpora`. It returns the names you are allowed to pass, their sizes, their
state and what each holds, and those names are the only legal values for `search_index`'s
`corpus`. Guessing one wastes a call.

## When this beats grep

Reach for `search_index` when you know **what you mean** but not what it is **called**:

- "where do we decide a token is expired" — the code may say `NotAfter`, `ttl`, `lease`.
- "what does the manual say about torque on the rear hub" — the manual may never write
  "torque" in that section.
- code that is *about* a thing without naming it: retry loops, cache invalidation, the
  place two subsystems agree on a format.

Reach for grep/glob instead when you know **the exact string**: a symbol name, an error
message, a config key, an import. Grep is faster, exact, and complete. Dexicon ranks; grep
enumerates — and when you need *every* occurrence, ranking is the wrong tool.

The two compose well: search to find the neighbourhood, grep to sweep it.

A corpus can hold documents that exist nowhere on disk — uploaded PDFs, EPUBs, scraped
pages. For those there is no grep to fall back to, so search is not the faster option, it
is the only one.

## Searching

```
search_index(query, corpus?, mode?, limit?, pathPrefix?, language?, symbol?)
```

Write the query as a **question or a description**, not keywords. "how are refresh tokens
revoked" outperforms "refresh token revoke" — the embedding model was trained on prose.

`mode` is `hybrid` by default, which blends meaning with exact terms and is the right
choice almost always. Use `semantic` when the wording in the source is certainly different
from yours; `keyword` when you want exact-match behaviour, or when embeddings are down.

Narrow with `pathPrefix`, `language` or `symbol` before raising `limit`. Twenty results
across the whole index is usually worse than eight from `src/Auth/`.

Omitting `corpus` searches everything visible to you, and with several corpora of different
kinds that is noisy: asked of five corpora, a question about one project's design drew 6 of
its top 10 hits from the four that did not hold the answer. Pick the one or two whose
descriptions fit and name them. A targeted search ranks better, because the competition is
relevant rather than merely abundant. Search everything when no description fits, or when a
targeted search came back empty and you need to know whether the answer is anywhere.

### Chunk sets: `corpus:set`

A corpus can hold the **same documents chunked several different ways** — a different chunk
size, or a different embedding model. Each chunking is a *set*, and any of them is
addressable as `corpus:set`:

```
search_index(query: "...", corpus: ["handbook"])        # the default set
search_index(query: "...", corpus: ["handbook:large"])  # a specific one
```

`list_corpora` marks the default with `*`. You almost always want the default; a named set
is for comparing two chunkings deliberately, or for reaching one that is still backfilling.

## Reading around a hit

Results are chunks, so a hit is a fragment with a file path and line numbers. When the
fragment is not enough:

```
get_context(corpus, filePath, aroundLine, before?, after?)
```

Pass the path **exactly as `search_index` printed it**. This stitches the surrounding
indexed lines together and reports any gap rather than papering over it.

If the corpus indexes a local source tree you can also just read the file directly — that
is usually better, because it is the live file and Dexicon's copy is as old as the last
index. Use `get_context` when the file is not on your disk (an uploaded document), or when
you want the same view the index has.

To read a whole indexed file, the resource is:

```
dexicon://corpus/{name}/file/{path}
dexicon://corpus/{name}              # the corpus's config and counts, as JSON
```

Both read by filter, not by relevance, so a file comes back whole rather than as its most
interesting parts.

## When search comes back empty

Do not conclude the content is absent. `index_status(corpus?)` gives the honest answer, and
there are four common ones:

- **Still indexing.** A large corpus takes a while; counts climb as it goes.
- **Never indexed.** The corpus exists, the files were never walked.
- **Indexed, but the query missed.** Try `keyword` mode with a term you are sure appears.
  If keyword finds it and hybrid did not, your phrasing was too far from the source.
- **Degraded.** A result beginning `! DEGRADED:` means embeddings were unavailable and you
  got **keyword-only** results. They are real, but they are not semantic — do not report
  "nothing found" from a degraded search without saying it was degraded.

If you know files changed on disk and the index is behind, `index_refresh(corpus)` queues a
reindex and returns immediately; it does not block, and results will not improve in this
turn. `full: true` re-embeds everything and is slow — only for a corpus you believe is
corrupt.

## Reporting what you find

Cite the path and line from the hit, so it can be checked. Dexicon returns ranked
approximate matches: a top result is the best match, not a correct answer. Read it before
you rely on it — especially in a prose corpus, where a confidently-worded passage may be
answering a different question from the one you asked.
