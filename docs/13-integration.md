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
its tag, so a 0.6.x instance reports `0.6`. An `edge` or a hand-built image reports `0.0`,
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
set quietly narrowed to what was permitted. The other failures are listed under
[Errors](#errors).

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
  "degraded": false,
  "scope": [{ "id": "01K…", "name": "docs", "state": "Ready" }],
  "context": "04-ingestion.md:353-390 (corpus: docs) · Embedding providers\n…",
  "citations": [
    {
      "corpus": "docs",
      "sourceRoot": "docs",
      "filePath": "04-ingestion.md",
      "location": "04-ingestion.md:353-390",
      "startLine": 353,
      "endLine": 390,
      "section": "Embedding providers",
      "score": 1.924,
      "chars": 2027,
      "partial": false,
      "omittedChars": 0
    }
  ],
  "usedChars": 3581,
  "maxChars": 4000,
  "truncated": true,
  "droppedHits": 8,
  "partialBlocks": 0,
  "tookMs": 15
}
```

`context` is the passage, ready to paste into a prompt. `citations` says where each block
of it came from, in the same order. A file path is relative to its source root rather than
to the corpus, so a citation also carries `sourceRoot`, the root of the source the file came
from (absent for an uploaded document, which has none, and an empty string for a source on
the workspace root). The block headers name it when the passage spans more than one. Null fields are omitted, so `note`
and `degradedReason` are absent rather than null when there is nothing to report. Enum
values in a response are PascalCase (`"mode": "Hybrid"`, `"state": "Ready"`); the request's
`mode` is case-insensitive, so `"hybrid"` is accepted.

**Request**

| Field | Default | Meaning |
|---|---|---|
| `query` | — | Required. Natural language, or a code fragment. |
| `corpus` | every visible corpus | Names, `corpus` or `corpus:set`. |
| `mode` | `hybrid` | `hybrid`, `semantic` or `keyword`. |
| `limit` | 10 | Hits to consider, 1-50. The budget decides how many reach the passage. |
| `maxChars` | 8000 | Characters for the whole passage, headers included, 1-200,000. |
| `neighbours` | 0 | Chunks to include either side of each hit, 0-5. |
| `lineNumbers` | false | Prefix each line with its number in the file. |
| `pathPrefix`, `source`, `language`, `symbol` | — | The search filters, unchanged. |
| `distinctTitles` | true | Collapse one document held in two formats. |

**Budgets are characters, not tokens.** No tokenizer ships with Dexicon, and the model
reading the passage is not the model that embedded it, so a token figure here would be an
estimate wearing a budget's clothes. Four characters per token is the working
approximation elsewhere in the project ([D-16](decisions.md#d-16-approximate-token-counting)),
which makes the 8,000-character default roughly 2,000 tokens.

**What the response admits to.** `truncated` and `droppedHits` say that hits were left
out. `partialBlocks` counts blocks cut to fit the budget: at most one, and always the last.
The last block is cut when at least 300 characters of room remain after its header and the
line that discloses the cut (about 64 characters) and at least one whole line fits in that
room; a chunk whose first line is longer than the room is dropped instead and counted in
`droppedHits`, and when nothing at all fitted, `note` names the smallest budget that would
have. A cut block's citation
has `partial: true`, `omittedChars` and the lines actually present, the passage says
`… N characters of this chunk not shown …`, and `note` reads `The last block is cut to fit
4,000 characters. Its citation reports the lines actually present. Raise maxChars for the
whole chunk.` `degraded` says the embedding service was unavailable and the results are keyword
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

`POST /api/corpora/{name}/reindex` queues the job and answers `202 Accepted` with the job as
the body and `/api/jobs/{id}` as `Location`; `?full=true` queues a full reindex. Indexing a
large repository outlasts any sensible request timeout, so poll the job to wait for it. The
integration document declares the 202.

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
means the pass ran without covering everything: a source could not be reached, or one or
more files could not be embedded and were skipped (those are retried on the next run).
`error` gives every reason, one sentence each.

The key needs the `ingest` scope for this. A key holds it when it was ticked at creation,
when it was added later on the Access page or with `PUT /api/tokens/{id}/scopes`, or when
the key was adopted from `DEXICON__BOOTSTRAP__TOKEN`.

There is no outbound webhook. `GET /api/jobs` answers the same question for any caller
that can poll, `/api/events` streams progress to one that can hold a connection, and
posting to a URL from inside the container brings signing, retry and a dead-letter story
with it. The reasoning is in [D-29](decisions.md#d-29-an-integration-document-and-retrieval-in-one-call).

## Errors

Failures come back as problem details: `application/problem+json` with `type`, `title`,
`status`, a `traceId` and, where there is something to say, `detail`. The two 401 answers
are the exception. The authentication middleware writes them as `application/json` with
`type`, `title`, `status` and `detail` and no `traceId`, so a client that decodes errors by
media type should accept both.

| Status | `title` | `detail` |
|---|---|---|
| 400 | `Unknown or unreadable corpus` | The message, for example `Unknown corpus 'x'. Corpora this key can reach: a, b.` A name the request sent is shown up to 200 characters, then `...`, and only the first three of the unknown names are quoted, then `(and N more)`. A list of corpora is written up to 1,500 characters, ends at a whole name, and is followed by `(and N more)` when names were left out. |
| 400 | `Unknown or unreadable corpus` | `Corpus 'x' was removed while this request waited.` `POST /api/corpora/{x}/documents` and `POST /api/corpora/{x}/documents/attach`, when the corpus is removed after the request resolved it and before the document is attached. Files of an upload that were attached before the removal went with the corpus. |
| 400 | `Query is required` | None. `POST /api/search` and `POST /api/context` with a blank `query`. |
| 400 | `Unknown search mode` | `Unknown search mode 'fuzzy'. Expected hybrid, semantic or keyword.` The value is shown on one line and cut at 40 characters, with `...` after it. `POST /api/search` and `POST /api/context`. |
| 400 | `Invalid expiry` | `expiresInDays is from 0 to 36,500, about 100 years, and 0 or leaving it out means the key does not expire. The key was not created.` `POST /api/tokens`. |
| 400 | `Unusable glob` | `includeGlobs[1] (include[1] for the configure tools) is null, or a pattern that does not compile, such as [z-a]. Nothing was saved.` Creating or updating a corpus's defaults, or adding or updating a source. Also refused, with their own text: a list of more than 200 elements (`includeGlobs (include for the configure tools) holds more than 200 patterns, which is the most a list can hold.`), an element of more than 500 characters, and, for a list git reads as pathspecs, pathspec magic git rejects (`:(bad)x`), an element that is a path climbing out of the repository with `..` (`a/../b` and `:(top)../x` are accepted), a rooted path (`//docs`, `:(glob)/docs`) or an element starting with `/:`. The message never repeats the pattern. The text differs for a history source's include list and a corpus's default include list ([04](04-ingestion.md)). |
| 400 | `Unknown filter` | `'maxfilebyte' is not a filter that can be cleared. Name one of: useGitignore, maxFileBytes, includeGlobs, excludeGlobs.` `PATCH /api/corpora/{x}/sources/{id}` with a `clear` entry that is not one of them. For a null entry the message is `A null entry is not a filter that can be cleared. clear takes field names: ...`. The name is shown on one line and cut at 40 characters, with `...` after it. |
| 400 | `chunkSize must be between 64 and 8192 tokens`, `chunkOverlap cannot be negative`, `chunkOverlap must be smaller than chunkSize`, `Unknown boundary mode`, `customBoundaryPattern is required for boundary mode 'custom'` or `Invalid custom boundary pattern` | The overlap and the mode are named in the detail (`Asked for overlap 300 with size 256.`, `'paragraph'. Expected none, blank-line, language-aware or custom.`), an invalid pattern carries the parser's message, and the other titles have no detail. `POST` or `PATCH` on `/api/corpora/{x}/chunk-sets`, and `POST /api/corpora` with a `chunkSize`, `chunkOverlap` or `boundaryMode`. A setting a request leaves out is judged at the value the server would store: the configured default for a corpus, the set it inherits from for a chunk set. When a request value fails only beside a usable server default, such as an overlap of 100 with a configured size of 64, the detail names that setting (`DEXICON__INDEXING__CHUNKSIZE=64`). Nothing was saved. |
| 400 | `boundaryMode 'custom' cannot be set when a corpus is created` | `A custom boundary needs a customBoundaryPattern, which creating a corpus does not take.` The detail gives the route that can set one: `PATCH /api/corpora/{name}/chunk-sets/default`. `POST /api/corpora`. |
| 503 | `Server chunk settings unusable` | The rule that failed, then `The value comes from the server setting DEXICON__INDEXING__CHUNKSIZE=10, not from the request.` `POST /api/corpora` that sent no value for a setting the server's configuration gets wrong (size outside 64 to 8192, overlap negative or not below the size, a boundary mode that is unknown or `custom`). Startup logs the same error. A REST caller avoids it by sending all three settings with the request. `configure_corpus` sends none, so an agent is told to ask whoever runs Dexicon to correct the named settings. |
| 400 | `fileName is blank` | `Leave fileName out to keep the name the document was uploaded under.` `POST /api/corpora/{x}/documents/attach` with a `fileName` that is empty or only whitespace. |
| 401 | `Missing credentials` | `Provide a key: Authorization: Bearer dex_…, or sign in at / for the UI.` |
| 401 | `Invalid credentials` | `The credential was not recognised, or it has been revoked or has expired.` |
| 403 | `Insufficient scope` | `This key has [search] and needs 'ingest'. Whoever runs Dexicon can grant it on the Access page.` |
| 404 | `Not Found` | None. `GET /api/jobs/{id}` for an unknown job, or one in a corpus the key cannot reach. The handler returns an empty 404 and the status-code middleware fills the body with problem details (`title`, `status`, `traceId`). |
| 409 | `Name already used` | The message says which name is taken. `POST /api/corpora/{x}/documents/attach` with bytes the corpus already holds under another name, sent under a name that a different one of its documents holds. An upload lists that file under `failed` instead. |
| 409 | `Embedding dimension mismatch` | `Collection '<collection>' was indexed with 768-dimension vectors but the configured model produces 1024. Rebuild the corpus with the current model, or restore the original one.` |

The server maps an unhandled embedding failure to 503 `Embedding service unavailable`.
`POST /api/search` and `POST /api/context` catch it and return keyword results with
`degraded: true` instead.

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

`get_context(corpus, filePath, aroundLine, before, after)` is a lookup: it takes a place
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
`POST /api/context` follows the same split for each hit.

**A duplicate path is resolved the same way by the three lookups.** A file path is relative
to its source root, so within a corpus it is not unique: two sources can each hold
`Installation Guide.pdf`, and they are two different books. A lookup is given only the
path. `get_context`, the `dexicon://.../file/` resource and `GET /api/corpora/{name}/file`
read the source with the most chunks (ties go to the lower source id) and warn, because
serving one book's text under the other's name is plausible, quotable and wrong. The
tool and the resource put the warning on the line under the header, starting `! `. The
endpoint returns the same sentence, without the `! `, as the `warning` property of the
JSON body on every window of the file, and omits the property when one source holds the
path. The sentence is `2 sources in this corpus contain a file at that path. They are
different files with the same name. This is one of them, the largest; the others are not
shown and not mixed in.`, where the count is the number of sources of the corpus that have
a file at the path, including one with no text (a scanned PDF) and so no chunks.

`POST /api/context` is answering a search, and a hit carries the source it came from, so
there is no ambiguity to resolve and it reads that source's document.

### Why the two reads are POST

`POST /api/search` and `POST /api/context` are reads that take a structured body: a list
of corpora, a mode, filters, a budget. As a GET each corpus would be a repeated query
parameter and the query text would live in the URL, where it is length-limited and lands
in every proxy log and browser history. Neither is idempotency-sensitive and neither is
cached, so the body is the only thing GET would have bought back.

Lookups are GET, including the file endpoint, whose arguments are a path and an offset.
