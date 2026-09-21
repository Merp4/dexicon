# 13 — The integration API

Dexicon answers over [MCP](06-mcp-surface.md) for agents, and over HTTP for everything
else: a git hook, a CI step, a shell script, a webhook receiver, anything that wants
retrieval without an agent loop.

The HTTP surface is the same server, the same keys and the same per-corpus scoping. What
this document covers is the part of it meant for other software to depend on.

---

## The contract

The REST surface is described by two generated OpenAPI documents, both written by
`dotnet build` and committed:

| Document | Describes | Generated to |
|---|---|---|
| Full | Every endpoint, the admin screens included | `clients/web-ui/Dexicon.json` |
| Integration | The seven endpoints below | `clients/web-ui/Dexicon_integration.json` |

Point a client generator at the integration document. The full one also describes the
workspace browser, the model probe, the sign-in endpoint and the progress stream, which
exist to serve the web UI and change with it. Both carry the same `major.minor` version,
and CI fails when either has drifted from the code ([D-29](decisions.md#d-29-an-integration-document-and-retrieval-in-one-call)).

A running instance also serves its own integration document, behind the same bearer as
everything else:

```bash
curl -s http://127.0.0.1:8477/openapi/integration.json   -H "Authorization: Bearer $DEXICON_TOKEN" -o dexicon.json
```

Generate against this rather than the committed file when the instance may not be the
revision you have checked out. No other document name is served: `/openapi/v1.json` is a
404, because the full surface is the UI's contract rather than yours.

The `info.version` there is the version of the **running build**. A release image carries
its tag, so a 0.4.x instance reports `0.4`. An `edge` or a hand-built image reports `0.0`,
which is the Dockerfile saying the build is not a release rather than a version to compare
against.

| Endpoint | Scope | For |
|---|---|---|
| `POST /api/context` | `search` | One assembled passage for a query |
| `POST /api/search` | `search` | Ranked hits, with a preview of each |
| `GET /api/corpora` | `search` | What this key can reach |
| `GET /api/corpora/{nameOrId}` | `search` | One corpus, with its state and counts |
| `POST /api/corpora/{nameOrId}/reindex` | `ingest` | Queue a reindex; returns immediately |
| `GET /api/jobs` | `search` | Indexing jobs, newest first |
| `GET /api/jobs/{id}` | `search` | One job, for polling a reindex to completion |

Creating corpora, issuing keys and managing models are admin operations and are not here.
They need the admin password rather than a key; see [07](07-auth.md).

## Authentication

A key issued under **Access** in the UI, in the `Authorization` header, exactly as an MCP
client sends it:

```bash
curl -s http://127.0.0.1:8477/api/corpora \
  -H "Authorization: Bearer $DEXICON_TOKEN"
```

The key reaches the corpora it is mapped to, and a key mapped to none reaches all of them.
Naming a corpus it cannot read is a 400 that names what is visible, rather than a result
set quietly narrowed to what was permitted.

## `POST /api/context`

Search returns ranked hits with a preview of each, sized for judging whether a hit is
worth reading. Assembling those previews into something worth putting in a prompt means a
second call per hit, and then joining chunks that overlap. This endpoint does that
server-side and returns one passage.

```bash
curl -s http://127.0.0.1:8477/api/context \
  -H "Authorization: Bearer $DEXICON_TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"query":"how does chunk set promotion work","corpus":["docs"],"maxChars":4000}'
```

```json
{
  "query": "how does chunk set promotion work",
  "mode": "Hybrid",
  "context": "04-ingestion.md:353-390 (corpus: docs) · Embedding providers\n…",
  "citations": [
    {
      "corpus": "docs",
      "filePath": "04-ingestion.md",
      "location": "04-ingestion.md:353-390",
      "startLine": 353,
      "endLine": 390,
      "section": "Embedding providers",
      "score": 1.924,
      "chars": 2027
    }
  ],
  "usedChars": 3581,
  "maxChars": 4000,
  "truncated": true,
  "droppedHits": 8,
  "degraded": false,
  "tookMs": 15
}
```

`context` is the passage, ready to paste into a prompt. `citations` says where each block
of it came from, in the same order. A file path is relative to its source root rather than
to the corpus, so a corpus with several sources also carries `sourceRoot`, and the block
headers name it when the passage spans more than one. Null fields are omitted, so `note`
and `degradedReason` are absent rather than null when there is nothing to report.

**Request**

| Field | Default | Meaning |
|---|---|---|
| `query` | — | Required. Natural language, or a code fragment. |
| `corpus` | every visible corpus | Names, `corpus` or `corpus:set`. |
| `mode` | `hybrid` | `hybrid`, `semantic` or `keyword`. |
| `limit` | 10 | Hits to consider. The budget decides how many reach the passage. |
| `maxChars` | 8000 | Characters for the whole passage, headers included. |
| `neighbours` | 0 | Chunks to include either side of each hit. |
| `lineNumbers` | false | Prefix each line with its number in the file. |
| `pathPrefix`, `source`, `language`, `symbol` | — | The search filters, unchanged. |
| `distinctTitles` | true | Collapse one document held in two formats. |

**Budgets are characters, not tokens.** No tokenizer ships with Dexicon, and the model
reading the passage is not the model that embedded it, so a token figure here would be an
estimate wearing a budget's clothes. Four characters per token is the working
approximation elsewhere in the project ([D-16](decisions.md#d-16-approximate-token-counting)),
which makes the 8,000-character default roughly 2,000 tokens.

**What the response admits to.** `truncated` and `droppedHits` say that hits were left
out. `degraded` says the embedding service was unavailable and the results are keyword
matches, which matters most here: a script pastes this into a prompt without reading it.
`note` carries anything else worth knowing, including a corpus still indexing, and the
case where nothing fitted:

```json
{ "context": "", "citations": [],
  "droppedHits": 10,
  "note": "No result fitted a budget of 300 characters; the smallest is 3,847. Raise maxChars, or narrow the query." }
```

**Reading around a hit.** `neighbours` adds whole chunks either side of each match, which
is how a passage continues past the edge of what matched. Each one competes with a further
hit for the same budget, so raise `maxChars` with it.

## Reindexing from a script

`POST /api/corpora/{name}/reindex` queues the job and returns it; indexing a large
repository outlasts any sensible request timeout. Poll the job to wait for it:

```bash
job=$(curl -s -X POST "http://127.0.0.1:8477/api/corpora/docs/reindex" \
  -H "Authorization: Bearer $DEXICON_TOKEN" | jq -r .id)

while :; do
  state=$(curl -s "http://127.0.0.1:8477/api/jobs/$job" \
    -H "Authorization: Bearer $DEXICON_TOKEN" | jq -r .state)
  case "$state" in queued|running) sleep 5 ;; *) echo "$state"; break ;; esac
done
```

The terminal states are `succeeded`, `failed`, `degraded` and `cancelled`. `degraded`
means one or more files could not be embedded and were skipped; they are retried on the
next run, and `error` says so.

The key needs the `ingest` scope for this, which is off unless it was ticked when the key
was issued.

There is no outbound webhook. `GET /api/jobs` answers the same question for any caller
that can poll, `/api/events` streams progress to one that can hold a connection, and
posting to a URL from inside the container brings signing, retry and a dead-letter story
with it. The reasoning is in [D-29](decisions.md#d-29-an-integration-document-and-retrieval-in-one-call).

## Which endpoint

Use `POST /api/context` when something will read the result as prose: a prompt, a summary,
a commit-message draft. Use `POST /api/search` when something will read it as data:
ranking, counting, deciding which file to open. An agent with an MCP client should use
neither and call `search_index` and `get_context`, which are shaped for a conversation
([06](06-mcp-surface.md)).

### "Context" names two different operations

The MCP tool and the endpoint that share the word do not do the same thing, which is worth
stating because the names invite the assumption that they do.

| Ask | MCP | HTTP |
|---|---|---|
| A query, answered as one passage within a budget | — | `POST /api/context` |
| Ranked hits for a query | `search_index` | `POST /api/search` |
| Text from a known file, without a query | `get_context` | `GET /api/corpora/{name}/file` * |

\* On the full surface, not in the integration document above. An HTTP integrator reading
on from a hit has to generate against the full document or call the path directly, which
is a gap rather than a decision: nothing about the endpoint is UI-specific.

`get_context(corpus, file_path, around_line, before, after)` is a lookup: it takes a place
and returns what is there. `POST /api/context` takes a *query*, runs the search, and packs
the results into one passage that stops at `maxChars` (D-29). Its handle is a question,
not a location, and there is no MCP tool for it — an agent already has a loop, so it
searches and then reads.

The two lookups answer the same need with different arguments, and the arguments are not
interchangeable. `get_context` centres on a line and takes `before` and `after` in lines.
`GET /api/corpora/{name}/file` takes `path` and `start`, where `start` is a **character
offset**, and returns a 400,000-character window with the line range it turned out to
cover: it is how the file viewer pages through a book of two or three million characters,
not a line-addressed read.

Where the text comes from is the same question for all of them, and the answer has two
cases. Where an extracted document is stored — anything that went through an extractor, so
every PDF, EPUB and uploaded document — the passage is read from it and the chunks only
decide which lines. Where none is, which is code and plain text on a mount, because
reading those IS the extraction and nothing is cached, the passage is stitched back
together from the chunk payloads, and it can then carry the gaps the chunker left, which
`get_context` and the file endpoint both disclose in the text as `… lines N-M not indexed …`.
The file endpoint also falls back when a path is ambiguous, because two sources of one
corpus can hold the same path and neither document is the right one.

That split is what `POST /api/context` did not do until recently: it stitched chunk
payloads in every case, including for a book that had a document sitting in the catalogue.
Both surfaces now make the same choice on the same evidence.

### Why the two reads are POST

`POST /api/search` and `POST /api/context` are reads that take a structured body: a list
of corpora, a mode, filters, a budget. As a GET each corpus would be a repeated query
parameter and the query text would live in the URL, where it is length-limited and lands
in every proxy log and browser history. Neither is idempotency-sensitive and neither is
cached, so the body is the only thing GET would have bought back.

Lookups are GET, including the file endpoint, whose arguments are a path and an offset.
