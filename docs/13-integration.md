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
  -d '{"query":"how does promotion work","corpus":["docs"],"maxChars":6000}'
```

```json
{
  "query": "how does promotion work",
  "mode": "Hybrid",
  "context": "docs/04-ingestion.md:228-265 (corpus: docs)\nA chunk set is promoted …",
  "citations": [
    {
      "corpus": "docs",
      "filePath": "docs/04-ingestion.md",
      "location": "docs/04-ingestion.md:228-265",
      "startLine": 228,
      "endLine": 265,
      "score": 0.81,
      "chars": 1840
    }
  ],
  "usedChars": 5820,
  "maxChars": 6000,
  "truncated": true,
  "droppedHits": 2,
  "degraded": false,
  "note": null
}
```

`context` is the passage, ready to paste into a prompt. `citations` says where each block
of it came from, in the same order.

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
  "note": "No result fitted a budget of 500 characters; the smallest is 8,142. Raise maxChars, or narrow the query." }
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
