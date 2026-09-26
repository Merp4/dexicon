# 06 — MCP surface

## Protocol

> **Verified in the M0 spike (2026-09-16)** against `ModelContextProtocol.AspNetCore`
> 2.2.0 and Claude Code 2.1.248. What the handshake will and will not negotiate is in
> [Version handling](#version-handling).

| | |
|---|---|
| Transport | **Streamable HTTP** at `POST /mcp`. Legacy HTTP+SSE is deprecated in the spec and not implemented. |
| Handshake revisions | `2024-11-05`, `2025-03-26`, `2025-06-18`, **`2025-11-25`** — what SDK 2.2.0 will negotiate through `initialize`. |
| Handshake-free | **`2026-07-28`** clients send no `initialize` at all. Measured: `tools/list` and `tools/call` both succeed cold, with no prior handshake. |
| Session mode | **Stateless.** Measured: no `Mcp-Session-Id` response header is ever emitted. |
| SDK | `ModelContextProtocol.AspNetCore` **2.2.0**. The option is `WithHttpTransport(o => o.Stateless = true)`. |
| Server→client calls | None. Sampling, roots and logging are deprecated in 2026-07-28 and Dexicon needs none of them. |

The 2026-07-28 revision also defines `Mcp-Method` and `Mcp-Name` request headers, carries
protocol version and client identity in `_meta`, and allows `tools/list` responses to
advertise `ttlMs` / `cacheScope`. Dexicon sets a `ttlMs` of 60 s on `tools/list`; the tool
set only changes when the operator changes configuration.

Multi Round-Trip Requests (`resultType: "input_required"`) replace elicitation. Dexicon has
one plausible use, asking which corpus was meant when a name is ambiguous, and
deliberately does not use it in v1: an ambiguous scope is an error with the candidates
listed in the message, which every client handles today.

## Connecting

```bash
claude mcp add --transport http dexicon http://localhost:8477/mcp \
  --header "Authorization: Bearer ${DEXICON_TOKEN}" \
```

No other header is needed. Which corpora the key reaches is set in the UI, which is the
normal local-development case. See [07](07-auth.md).

## Naming a corpus and a chunk set

Everywhere a tool takes a corpus, it accepts either form:

```
books           the corpus's DEFAULT chunk set
books:fine      a named set within it
```

A corpus can be cut several ways at once: a coarse set and a fine one, or the live set
and its replacement on a new model while that replacement backfills
([D-21](decisions.md#d-21-chunk-sets-not-corpus-level-chunking)). Qualifying the name
rather than adding a `chunk_set` parameter to four tools keeps the parameter count down
(see [Tools](#tools)), and an agent that has never heard of chunk sets sends a bare name
and gets the sensible answer.

`list_corpora` names every set and marks the default with `*`. Without this, an agent
given only the corpus name cannot reach the others.

An unknown set is an error that names the real ones, the same way an unknown corpus does:

```
Corpus 'books' has no chunk set named 'nope'. Its sets: default, fine.
```

## Tools

Five tools. The count is a design constraint: every tool definition is context an agent
pays for on every turn, and a surface of thirty tools measurably degrades smaller
models.

### `search_index`

The one that matters.

```jsonc
{
  "name": "search_index",
  "description": "Semantic and keyword search over indexed code and documents. Returns matching chunks with file paths and line numbers.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "query":  { "type": "string", "description": "Natural language question or code fragment." },
      "corpus": { "type": "array", "items": { "type": "string" },
                  "description": "Corpus names to search, optionally qualified as corpus:set. With several corpora, name the one or two whose list_corpora descriptions fit the question. Omit to search everything you can see." },
      "mode":   { "type": "string", "enum": ["hybrid", "semantic", "keyword"], "default": "hybrid",
                  "description": "hybrid blends meaning and exact terms; keyword is exact-match only and works when embeddings are unavailable." },
      "limit":  { "type": "integer", "minimum": 1, "maximum": 50, "default": 10 },
      "source": { "type": "string", "description": "Restrict to one source of the corpus, by root path as list_corpora reports it, e.g. manuals/AI. A parent matches everything beneath it." },
      "path_prefix": { "type": "string", "description": "Restrict to files under this path, e.g. src/Auth/. Relative to the source root, not the corpus." },
      "language":    { "type": "string", "description": "Restrict to one language, e.g. csharp, python." },
      "symbol":      { "type": "string", "description": "Restrict to chunks declaring this symbol." },
      "max_chars_per_hit": { "type": "integer", "default": 1500,
                  "description": "Characters of each result, centred on the matching passage. 0 returns whole chunks." },
      "distinct_titles":   { "type": "boolean", "default": true,
                  "description": "Collapse results that are the same document in another format." }
    },
    "required": ["query"]
  }
}
```

`corpus` is the only array on the surface — `source`, `language`, `symbol` and
`path_prefix` are all scalars — so `"corpus": "docs"` is the shape a client reaches for
when it wants one. It is accepted, and read as a list of one. The schema above still
describes an array, which is the contract worth advertising; accepting a bare name is
leniency in binding, not a second type.

One shape is refused that the *generated* schema allows: a null inside the list. What the
SDK emits is `items: {"type": ["string", "null"]}`, which describes a nullable reference
type rather than a nameless corpus. A null element reached `ScopeResolver.Split` as a
`NullReferenceException` and came back as the generic message below.

Before that, a bare name was refused while the arguments were being bound, so the tool
body never ran and the caller got `An error occurred invoking 'search_index'.` with
nothing to act on. Reported from a live instance as "`search_index` errors whenever a
`corpus` argument is passed", reproduced four times across two corpora, and read —
reasonably — as scoped search being broken.

Each result is a window centred on the matching passage rather than the whole chunk, and
one result per document by default; both are described in [05](05-search.md), along with
what they were measured to cost and why the window is centred rather than cut from the head.

Returns the contract in [05](05-search.md), rendered as structured content plus a compact
text block. The text block is what most clients show the model, so it is formatted for
reading, not for parsing:

```
3 results for "how do we refresh auth tokens" (hybrid, corpus: api-repo)

1. src/Auth/TokenService.cs:120-168  · TokenService.RefreshAsync
   public async Task<TokenPair> RefreshAsync(...)
   ...

2. docs/auth.md:40-72  · Token lifetimes
   ...
```

### `list_corpora`

No inputs. Returns what the caller can see: name, description, whether it is shared, file
and chunk counts, last indexed time, current state, and every chunk set with its model,
dimensionality, chunk size and overlap, and the default marked. This is how an agent learns
what `corpus` values are legal, including the `corpus:set` ones, so its description says so
explicitly.

**The description leads**, before any of the machinery. An agent calls this to answer one
question: which of these should I search? The only line that answers it is the one a human
wrote, so it is placed before the state, the counts, the chunk sets, the dimensions and the
overlap. A corpus with no description states that explicitly, since an absent value reads
as "no information" rather than as an undescribed corpus.

**An empty corpus is marked `NOT SEARCHABLE`.** It is a legal value for `search_index` that
cannot answer anything, and listed identically to a full one it reads as a reasonable place
to look, costing the agent a call to discover otherwise. "Still indexing" and "empty" are
reported distinctly, because the first is worth retrying and the second is not.

### `get_context`

`(corpus, file_path, around_line, before = 30, after = 30, line_numbers = true)`:
the lines around a location. For when a search hit needs its surroundings and the agent
cannot open the file itself.

Taken from the extracted document, so the window is the range asked for and cannot have
a hole in it. Where no document is stored, which is a code file on a mount, or where two
sources of the corpus hold the same path, it falls back to stitching the chunks that
cover the line.

This is also how an agent **reads on**. Chunks overlap and tile the file, so calling it
again further down the file walks forwards through a document: a search hit in a book,
then the next few pages of it, without the agent ever holding the file. It reads by
filter, not by relevance: an early version keyword-searched for the path, which let
ranking decide which of a file's chunks came back, and asking for the lines around line
2,625 of a book returned nothing at all.

`around_line` is a line number, and a hit in a PDF or an EPUB is cited by its unit:
`moby-dick.epub#chapter=7`. So `search_index` prints the line span alongside that
citation, because otherwise an agent could find a passage in a book and have nothing to
pass in order to continue from it.

`line_numbers` prefixes each line with its number in the file, on by default. The passage
is what a model reads before quoting or editing, and the alternative is counting lines
down from the header, over a passage from which overlap has been removed. Gap markers
stay unnumbered: the lines they stand for are
the ones that are not there. The `dexicon://` file resource leaves numbering off, because
a file read back should be the file rather than a listing of it.

### `index_refresh`

`(corpus, full = false)`: queues an incremental (or full) reindex, returns `{ jobId,
state }` immediately. Requires the `ingest` scope. Never blocks: indexing a large repo
outlasts any sensible tool timeout.

### `index_status`

`(corpus?)`: current job phase and counts, last indexed time, last error, embedding model
and whether it is reachable. It also answers "why did search return nothing":
it will say `indexing, 12% (1,204 / 9,880 files)` or `degraded: embedding service
unreachable since 14:02`.

It also reports files no source covers. A file outside every source root has no row in any
of the counts above: it is not skipped and not failed, it is absent, and a search for it
returns other documents instead. The check reports a directory when **two or more of the
corpus's sources share it as their parent**, which is the case where the children were
enumerated deliberately and a file left loose among them was passed over. One source under
a directory says nothing about that directory and is not reported, so a corpus that indexes
a single folder stays silent.

```
NOT INDEXED: 1 file(s) in books/manuals are covered by no source, though its subfolders are.
  An Invented Handbook.pdf
Add a source on books/manuals, or move the file into one of its subfolders.
```

The directory has no source, so it has no include or exclude globs to apply. What is
applied is what holds for any path: the always-exclude list, a `.gitignore` and a
`.git/info/exclude` in the directory itself — only that one, since this does not descend —
the size caps and binary sniffing. A reported file is one that would have been indexed had
a source covered it, so a file git excludes locally is not reported as missing. Five files
are listed per directory and the rest counted.

## Resources

Corpora are exposed as MCP resources so clients with a resource picker can browse them:

```
dexicon://corpus/{name}               -> a JSON summary: sources, counts, chunk sets, state
dexicon://corpus/{name}/file/{+path}  -> reconstructed text of one indexed file
```

`{name}` takes the same `corpus:set` form as the tools, so a file can be read as one set
cut it.

Resources are for **browsing**; tools are for asking questions. A client with a resource
picker can attach "this corpus" or "that file" to a conversation without the model having
to guess a search query first.

Both use the same scope resolution as search. A corpus a key cannot search must not
become readable because it was reached by URI instead: what a key reaches is the security
model, and a second route into it is a second opportunity for error.

File text is reconstructed **from the index**, not read from disk. An uploaded PDF has no
file to read, and the original would in any case differ from what was indexed. What is
returned is what search can find. Overlapping chunks are de-overlapped, and any gap is
marked rather than closed without notice.

## Prompts

None in v1. A prompt template that tells an agent how to use a search tool is a substitute
for a good tool description, and Dexicon would rather fix the description.

## Errors

MCP tool errors are returned as `isError: true` with a message written for a model to act
on, not a stack trace to display.

| Condition | Message shape |
|---|---|
| Unknown corpus | `Unknown corpus 'api'. Visible corpora: api-repo, rfc-library.` |
| No reachable corpora | `Key 'claude-code' can reach no corpora. Create one in the Dexicon UI, or map this key to one under Access.` |
| Embeddings down, hybrid asked | Results returned with `degraded: true` — not an error. |
| Dimension mismatch | `Corpus 'api-repo' was indexed with nomic-embed-text (768 dims); the configured model produces 1024. Rebuild the corpus or restore the original model.` |
| Indexing in progress, no results | Results plus a note: `corpus 'api-repo' is 12% indexed; results are incomplete.` |
| Argument of the wrong shape | `corpus takes a name or a list of names, not a number.` |

Binding runs before the tool body, so nothing raised there goes through the tool's own
error handling. The SDK renders an `McpException` as `An error occurred invoking
'<tool>': <message>` and turns every other exception into that sentence with the message
empty, which is what the caller saw. Measured against a running instance:

| Sent as `corpus` | Caller sees |
|---|---|
| `"docs"` | the same result as `["docs"]` |
| `7` | `An error occurred invoking 'search_index': corpus takes a name or a list of names, not a number.` |
| `[{"name":"docs"}]` | `An error occurred invoking 'search_index': corpus takes a name or a list of names; this list holds an object.` |

That last one is the difference between an agent concluding "this codebase has no auth
code" and an agent waiting thirty seconds.

## Version handling

Measured against `ModelContextProtocol.AspNetCore` 2.2.0:

```
POST /mcp  initialize  protocolVersion: "2026-07-28"
  -> -32022  "Protocol version '2026-07-28' is not available through the
              initialize handshake."
              supported: [2024-11-05, 2025-03-26, 2025-06-18, 2025-11-25]

POST /mcp  initialize  protocolVersion: "2025-11-25"
  -> 200     protocolVersion: "2025-11-25"

POST /mcp  tools/list  (no initialize at all)
  -> 200     both tools returned
```

That error message is the key to it, and it is not a defect: **2026-07-28 removed the
handshake**, so there is nothing for `initialize` to negotiate. A 2026-07-28 client does
not call `initialize`; it sends self-contained requests carrying their version in `_meta`,
and those work cold. Two populations, both served:

- **Handshake clients** (≤ 2025-11-25) negotiate normally, highest common revision wins.
- **2026-07-28 clients** skip the handshake entirely. Verified working.

`2025-11-25` is therefore the newest *negotiable* revision, not the newest supported one.
An unsupported version is refused with the supported list, as above, rather than
best-effort guessing. The negotiated revision is logged per request, because "which
revision did that client receive" is the first question when a client misbehaves.

### A 2026-07-28 request, exactly

The revision is strict about its own envelope, and the server enforces all of it. Every
piece below is required, and leaving any one out is a `-32602` or `-32020` naming the
missing part. Assembling one by hand is otherwise several rounds of guessing:

```bash
curl -X POST http://127.0.0.1:8477/mcp   -H 'Authorization: Bearer dex_…'   -H 'Content-Type: application/json'   -H 'Accept: application/json, text/event-stream'   -H 'MCP-Protocol-Version: 2026-07-28'   -H 'Mcp-Method: resources/read'   -H 'Mcp-Name: dexicon://corpus/books'   -d '{
    "jsonrpc": "2.0", "id": 1, "method": "resources/read",
    "params": {
      "uri": "dexicon://corpus/books",
      "_meta": {
        "io.modelcontextprotocol/protocolVersion": "2026-07-28",
        "io.modelcontextprotocol/clientCapabilities": {}
      }
    }
  }'
```

`Mcp-Name` must match the body: the name for a tool call, the URI for a resource read. A
mismatch is refused rather than resolved in favour of either. The response is an SSE
frame: one `data:` line carrying the JSON-RPC result.

**End-to-end**: Claude Code 2.1.248 connects over
`claude mcp add --transport http … --header "Authorization: Bearer …"` and reports
`✔ Connected`. Static bearer auth is enforced ahead of the MCP handler; an unauthenticated
`tools/list` gets a bare 401.
