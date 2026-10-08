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
  --header "Authorization: Bearer ${DEXICON_TOKEN}" --scope user
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

Five tools, three more for a key holding `configure`, and two more for a key holding
`propose`. The count is a design
constraint: every tool definition is context an agent pays for on every turn, and a
surface of thirty tools measurably degrades smaller models. So a key is listed only the
tools its scopes let it call: the four that read the index need `search`, `index_refresh`
needs `ingest`, the [configuration tools](#configuration-tools) need `configure`, and
[`propose_removal` and `removal_status`](#propose_removal) need `propose`.

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

With a corpus named, it adds what an agent needs to say why a file is or is not indexed,
and what to change:

```
  sources:
    files under docs: 16 files found; .gitignore respected; .dexiconignore respected; code and text up to 256 KB; not **/*.tmp (from the corpus defaults: not)
    commit history of the workspace root: 256 commits, follows refs/heads/main (52 behind origin/main as of the fetch at 2026-09-27 08:15 UTC); holds message, stat; newest 24664ac, 2026-09-26
  failed: 1
    docs/papers/broken.pdf — 'broken.pdf' has no PDF trailer (startxref and %%EOF) in its last 4,096 bytes, so it is truncated rather than merely unusual. Its 1,048,576 bytes were not re...
  skipped: 2
    docs/assets/diagram.png — binary content (NUL byte in the first 8 KB)
    docs/data/export.json — over the 262,144 byte size cap (1,048,576 bytes)
```

Each source's filters are its effective values, with the ones it takes from the corpus
named, so a change is made at the level that owns it. `.dexiconignore` is always
respected, whatever the `.gitignore` setting. Failed, skipped and empty files are listed
from the default chunk set, ten per status with the reason recorded for each, and the rest
counted. A file that a filter, a `.gitignore` or a `.dexiconignore` excludes is dropped in
the walk without a row, so it is never among them; the source line is what accounts for
it. Without a corpus named it lists every corpus's counts alone, since the detail for all
of them would be the longest tool result an agent sees.

### Configuration tools

Three more tools, listed only to a key holding `configure`
([D-36](decisions.md#d-36-a-configure-scope-agents-set-up-what-is-indexed)). They create
and change; nothing over MCP removes a corpus or a source, so one added by mistake is
removed in the UI, or asked for with [`propose_removal`](#propose_removal) by a key that holds
`propose`. A narrower filter or limit still drops the files or commits it stops
selecting from the index when the refresh runs, as in the UI, and the reply says so. Every change goes through the same checks as the UI's, and is logged
with the key's name: `Key claude-code added the source for the files under repos/app in
corpus app; job 01K…`.

`list_folders(path?)`: the folders mounted under a path, with how many entries each holds
(counted to 500), whether it is a git repository, and which of the key's corpora already
read it. The heading marks the listed folder the same way. Corpora the key cannot reach are
not named.

```
Folders in repos:
  repos/app/  42 entries; git repository; indexed by app (files), apphistory (history)
  repos/tools/  500+ entries; git repository
```

`configure_corpus(corpus, create?, description?, include?, exclude?, gitignore?,
maxFileKb?, reset?)`: creates a corpus when `create` is true, and otherwise changes an
existing one. A name that matches no corpus is refused rather than taken as a request to
create one. The filters are the defaults every source inherits; each one sent replaces
that value and the others keep theirs, and `reset` names the ones to return to the
server's setting. A change to them queues a refresh when the corpus has sources. A key
mapped to some corpora has a corpus it creates added to its mapping, so it can reach it.
`gitignore` takes `true` only: `.gitignore` is what keeps a file such as Dexicon's own `.env`
out of the index, so turning it off is the UI's. A source cannot be added to a corpus whose
default the UI turned off without passing `gitignore: true`, nor reset to follow that default. A `description` is one line of at most 500
characters.

`configure_source(corpus, folder, kind?, create?, include?, exclude?, gitignore?,
maxFileKb?, history?, reset?)`: adds a folder when `create` is true, and otherwise changes
the source of that `kind` (`files`, the default, or `history`) on that folder. The folder
must exist to be added. It is matched and stored with `.` and `..` collapsed, so `docs/.`
names the source on `docs` rather than adding a second one. A corpus can hold two sources of
one kind on a folder, added in the UI; a folder cannot say which is meant, so a change to
either is refused and made in the UI. `exclude`, `gitignore` and `maxFileKb` apply to a files source; a
history source takes `include`, as the paths whose commits are kept, and `history`: an
object of `follow` (the ref), `message`, `stat`, `diff`, `maxDiffKb`, `merges`,
`maxCommits`, `keepIndexed` and `since`. Settings left out keep their value; `reset`
returns one to its default. The reply shows the source as `index_status` does:

```
Added a source for the files under repos/app to corpus app, and queued a refresh as job 01K…. As of now:
  files under repos/app: 0 files found; .gitignore respected; .dexiconignore respected; code and text up to 256 KB; not **/generated/**
index_status(corpus: "app") reports the refresh, and what it read once it has run.
```

A change that leaves every value as it was queues nothing and says so. A setting both sent
and named in `reset`, a file setting on a history source, and a `since` that is not a
`yyyy-MM-dd` date are refused before anything changes.

### `propose_removal`

Two more tools, listed only to a key holding `propose`
([D-39](decisions.md#d-39-agents-ask-for-removals-and-a-person-decides)), and independent of
`configure`. `propose_removal` asks for a removal and removes nothing: whoever runs Dexicon
approves or rejects each request, and approving runs what the admin's delete runs. The requests
are listed with `GET /api/proposals` and decided with `POST /api/proposals/{id}/approve` or
`/reject`, which take the administrator's session and are not open to a key.

`propose_removal(kind, corpus, target?, reason)`: `kind` is `source`, `chunk_set`, `document`
or `corpus`, and `corpus` is the corpus it is in.

| `kind` | `target` |
|---|---|
| `source` | The folder as `index_status` shows it, written `files:repos/app` or `history:repos/app` when both read the folder. A source id is accepted. |
| `chunk_set` | The set's name, ignoring case, or its id. The default set and the only set are refused when asked, since they cannot be removed. |
| `document` | The path of an uploaded document attached to the corpus, or its file id. |
| `corpus` | Left out. The name is accepted if it is the corpus's own. |

Every kind but a corpus needs a `target`: a request that leaves it out is refused, since a blank
folder would otherwise name a source on the workspace root. That source is asked for as `files:`
or `history:`.

`reason` is required: one line, at most 300 characters, shown to the person beside what would
go. The target is held by id from then on. A target that matches nothing is refused with what
there is to match, and a key may have ten requests waiting. Asking again for something already
waiting returns that request and records nothing new.

```
Recorded proposal 01K… to remove the source files:docs from corpus 'notes'. It is waiting for whoever runs Dexicon to decide it, and nothing has been removed. removal_status shows how it is decided.
```

`removal_status` takes no arguments and lists the requests the key has made, across all
corpora, and how each was decided: waiting, approved (it has been removed), rejected (it
stays), or could not be done with the reason, such as the target being gone by then. Waiting
ones come first, then the newest decided, up to twenty, and it says when older ones are left
out. It needs only `propose`, so a key without `search` can read it, and it still answers
after the corpus a request named has been removed. A key sees only its own requests.
`index_status(corpus)` shows a key that can search the same for one corpus.

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
