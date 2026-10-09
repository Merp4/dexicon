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
advertise `ttlMs` / `cacheScope`.

A key is shown only the tools its scopes allow. The transport is stateless, so the server
sends no `notifications/tools/list_changed`: a client lists on connect and caches, and a
scope granted to a key reaches its agent's tool list when the client reconnects
([07](07-auth.md#the-mcp-tool-list-is-not-live)).

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

The tools that read the index (`search_index`, `get_context`, `index_status`) and the
`dexicon://` resources accept either form:

```
books           the corpus's DEFAULT chunk set
books:fine      a named set within it
```

`index_refresh`, `propose_removal` and the configuration tools take a corpus name: they
match the whole string against corpus names and ids, and a corpus name cannot contain a
colon.

A corpus can be cut several ways at once: a coarse set and a fine one, or the live set
and its replacement on a new model while that replacement backfills
([D-21](decisions.md#d-21-chunk-sets-not-corpus-level-chunking)). Qualifying the name
instead of adding a `chunk_set` parameter keeps the parameter count down
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

Only `query` is required. Argument names are camelCase. The descriptions are the ones the
tool advertises.

| Argument | Type | Default | Description |
|---|---|---|---|
| `query` | string | required | Natural language question or code fragment. |
| `corpus` | array of string | everything the key can reach | Corpus names to search. With several corpora, name the one or two whose list_corpora descriptions fit the question: searching all of them lets hits from the others crowd out the answer. Omit to search everything visible to you. |
| `mode` | string | `hybrid` | hybrid blends meaning with exact terms; semantic is meaning only; keyword is exact-match only and keeps working when embeddings are unavailable. |
| `limit` | integer | 10 | Maximum results, 1-50. |
| `pathPrefix` | string | none | Restrict to files under this path, e.g. src/Auth/. Relative to the source root, not the corpus. Use `source` to narrow by folder instead. |
| `source` | string | none | Restrict to one source, by the root path a search result cites, e.g. books/manuals/Architecture. A parent matches everything beneath it, so books/manuals covers every topic folder under it. list_corpora does not list these; run a search first, or pass a wrong one and the error names them all. |
| `language` | string | none | Restrict to one language, e.g. csharp, python, typescript. |
| `symbol` | string | none | Restrict to chunks declaring this symbol, e.g. TokenService. |
| `maxCharsPerHit` | integer | 1500 | Characters of each result to return, centred on the matching passage. The default is enough to read the match in context; raise it when a hit is clearly the right passage and you need more of it, or use get_context. 0 returns whole chunks, which on a book corpus is about 8,000 characters each. |
| `distinctTitles` | boolean | `true` | Collapse results that are the same document in another format, e.g. a book held as both PDF and EPUB. On by default. Turn it off to compare how the two were extracted. |

The schema carries no bounds: `limit` is clamped to 1-50 and `maxCharsPerHit` to 0-100,000
in the tool body. `mode` is a plain string, and a value other than `hybrid`, `semantic` or
`keyword` (in any letter case; empty is `hybrid`) fails with `An error occurred invoking
'search_index': Unknown search mode '<mode>'. Expected hybrid, semantic or keyword.`, with the mode
on one line, cut to 40 characters followed by `...`, and any control character in it replaced. Each name in `corpus` may be
`corpus` or `corpus:set` ([above](#naming-a-corpus-and-a-chunk-set)).

`corpus` is the only array on the tools that read the index: `source`, `language`, `symbol`
and `pathPrefix` are scalars, so `"corpus": "docs"` is the shape a client reaches for when
it wants one. It is accepted, and read as a list of one. The advertised schema describes an
array, which is the contract worth advertising; accepting a bare name is leniency in
binding, not a second type. On the configuration tools, `include`, `exclude` and `reset`
are lists that likewise accept a single string.

A null inside the `corpus` list is refused with `corpus takes a name or a list of names;
this list holds null.`, although the generated schema allows it (`items: {"type":
["string", "null"]}`, which describes a nullable reference type rather than a nameless
corpus).

Each result is a window centred on the matching passage rather than the whole chunk, and
one result per document by default; both are described in [05](05-search.md), along with
what they were measured to cost and why the window is centred rather than cut from the head.

Returns one text block, formatted for a model to read. The structured result described in
[05](05-search.md) is the response of `POST /api/search`; the tools return text only:

```
3 results for "how do we refresh auth tokens" (hybrid, corpus: api-repo)

1. src/Auth/TokenService.cs:120-168  · TokenService.RefreshAsync
   public async Task<TokenPair> RefreshAsync(...)
   ...

2. docs/auth.md:40-72  · Token lifetimes
   ...
```

### `list_corpora`

No inputs. Returns what the caller can reach: name, description, state, file and chunk
counts, last indexed time, a failed-file count when there is one, and every chunk set with
its model, dimensions, chunk size, overlap and chunk count, the default marked. This is how
an agent learns what `corpus` values are legal. When a corpus has more than one set the
listing ends its entry with `(* is the default; name another with corpus:set)`.

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

`(corpus, filePath, aroundLine, before = 30, after = 30, lineNumbers = true)`:
the lines around a location. For when a search hit needs its surroundings and the agent
cannot open the file itself.

Taken from the extracted document, so the window is the range asked for and cannot have
a hole in it. Where no document is stored, which is a code file on a mount, it stitches the
chunks that cover the line.

A file path is relative to its source root, so two sources of one corpus can hold the same
path for different files. Then `get_context` reads the source with the most chunks (ties go
to the lower source id) and puts this line under the header: `! 2 sources in this corpus
contain a file at that path. They are different files with the same name. This is one of
them, the largest; the others are not shown and not mixed in.` (the count is the number of
sources, counted from the catalogue, so a source whose file at that path has no text, such
as a scanned PDF, is counted too). The file resource ([below](#resources)) and `GET /api/corpora/{name}/file` choose
the source the same way and carry the same sentence.

This is also how an agent **reads on**. Chunks overlap and tile the file, so calling it
again further down the file walks forwards through a document: a search hit in a book,
then the next few pages of it, without the agent ever holding the file. It reads by
filter, so ranking never decides which of a file's chunks come back.

`aroundLine` is a line number, and a hit in a PDF or an EPUB is cited by its unit:
`moby-dick.epub#chapter=7`. So `search_index` prints the line span alongside that
citation (`· lines 120-160`). `aroundLine` takes one integer, so an agent passes a line
from that span, `120` to read the hit and `160` to read on past it, never the span itself.

`filePath` is the path without the `:start-end` range (or `:line`, for a hit of one line) or the `#page=`, `#chapter=` or
`#slide=` anchor that `search_index` prints after it. The lookup matches the stored path
exactly, so a path with its anchor finds nothing: `No indexed file 'moby-dick.epub#chapter=7'
in corpus 'books'. Check the path is exactly as search_index returned it.`

`lineNumbers` prefixes each line with its number in the file, on by default. The passage
is what a model reads before quoting or editing, and the alternative is counting lines
down from the header, over a passage from which overlap has been removed. Gap markers
stay unnumbered: the lines they stand for are
the ones that are not there. The `dexicon://` file resource leaves numbering off, because
a file read back should be the file rather than a listing of it.

### `index_refresh`

`(corpus, full = false)`: queues an incremental or full reindex and returns a sentence
naming the job and its state at once:

```
Queued incremental reindex of 'docs' as job 01K… (state: queued). Poll index_status for progress.
```

A full reindex says `full` in place of `incremental`. Requires the `ingest` scope. Never
blocks: indexing a large repo outlasts any sensible tool timeout. The reply points to
`index_status`, which needs `search`, so a key holding only `ingest` is not listed that tool.

### `index_status`

`(corpus?)`: the corpus state, file and chunk counts (with skipped and failed files when
there are any), each chunk set with its model and dimensions, the last indexed time, and the
latest job with its phase, progress and error. It also answers "why did search return
nothing": while indexing runs it says

```
  latest job 01K…: running (<phase>) — 12% (1,204 / 9,880 files)
```

and when files failed or were skipped, the counts and the reasons follow. It does not
report whether the embedding service is reachable: a search that cannot embed says so itself
with a `! DEGRADED:` line (see [Errors](#errors)), and `/healthz` reports the service on the
full API surface.

It also reports files no source covers. A file outside every source root has no row in any
of the counts above: it is not skipped and not failed, it is absent, and a search for it
returns other documents instead. The check reports a directory when **two or more of the
corpus's sources share it as their parent**, which is the case where the children were
enumerated deliberately and a file left loose among them was passed over. One source under
a directory says nothing about that directory and is not reported, so a corpus that indexes
a single folder stays silent.

```
  NOT INDEXED: 1 file(s) in books/manuals are covered by no source, though its subfolders are. Searching will never return them.
    An Invented Handbook.pdf
  Add a source on books/manuals, or move the file into one of its subfolders.
```

The directory has no source, so it has no include or exclude globs to apply. What is
applied is what holds for any path: the always-exclude list, a `.gitignore` and a
`.git/info/exclude` in the directory itself — only that one, since this does not descend —
the size caps and binary sniffing. A reported file is one that would have been indexed had
a source covered it, so a file git excludes locally is not reported as missing. Five files
are listed per directory and the rest counted (`    ... and 3 more`).

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
selecting from the index when the refresh runs, as in the UI, and the reply says so. Every
change goes through the same checks as the UI's, and is logged with the key's name. Adding a
source writes two lines, one when it is saved and one when its job is queued:

```
Key claude-code added the source for the files under repos/app in corpus app
Key claude-code queued job 01K… for the source for the files under repos/app in corpus app
```

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
index_status(corpus: "app") reports it, and what it read once it has run.
```

The last line is left out for a key that does not hold `search`, since `index_status` is not
listed to it.

A change that leaves every value as it was queues nothing and says so. A setting both sent
and named in `reset`, a file setting on a history source, a `since` that is not a
`yyyy-MM-dd` date, an `include` or `exclude` list of more than 200 elements or with an element of
more than 500 characters, a null element or a pattern that does not compile (such as `[z-a]`; a
history source's `include` is held to null, empty, null-character, `..`-segment and `//`-leading
elements instead, because git reads it as pathspecs, and a leading `/` is accepted and removed
before git is asked), and a `folder` holding a null character are refused before anything
changes. A corpus's default `include` list, which sources of both kinds inherit, is held to both
sets of rules. `configure_corpus` sends a corpus's defaults whole whenever it changes a filter, so
a list already stored with such a pattern, or past the caps, is refused again when only the other
filter is changed; `reset` clears it. The message names the list and the position and does not
repeat the pattern.

### `propose_removal`

Two more tools, listed only to a key holding `propose`
([D-39](decisions.md#d-39-agents-ask-for-removals-and-a-person-decides)), and independent of
`configure`. `propose_removal` asks for a removal and removes nothing: whoever runs Dexicon
approves or rejects each request, and approving runs what the admin's delete runs. They are decided
on the [Approvals](08-ui.md#approvals) screen, which uses `GET /api/proposals` and
`POST /api/proposals/{id}/approve` or `/reject`. Those take the administrator's session and are
not open to a key.

`propose_removal(kind, corpus, reason, target?)`: `kind` is `source`, `chunk_set`, `document`
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
For a key that holds `propose` and `search`, `index_status(corpus)` shows the same list for
one corpus, up to eight.

## Resources

Two MCP resource templates read a corpus and a file. The server registers the URI
templates; it does not list one resource per corpus.

```
dexicon://corpus/{name}               -> a JSON summary: sources, counts, chunk sets, state
dexicon://corpus/{name}/file/{+path}  -> the text of one indexed file
```

`{name}` takes the same `corpus:set` form as the tools, so a file can be read as one set
cut it.

Resources are for reading a known corpus or file; tools are for asking questions. A client
can attach "this corpus" or "that file" to a conversation without the model having to guess
a search query first.

Both use the same scope resolution as search. A corpus a key cannot search must not
become readable because it was reached by URI instead: what a key reaches is the security
model, and a second route into it is a second opportunity for error.

File text comes from the index and the catalogue, not from disk. An uploaded PDF has no
file to read, and the original would in any case differ from what was indexed. Where an
extracted document is stored for the source read, the document is returned whole.
Otherwise the chunks of the path are put back together: overlapping chunks are
de-overlapped, and any gap is marked with `… lines N-M not indexed …`. Where two sources
of the corpus hold the path, the resource reads the one with the most chunks, as
[`get_context`](#get_context) does, and the line `! 2 sources in this corpus contain a
file at that path. …` follows the header (the sentence is quoted under `get_context`).

## Prompts

None in v1. A prompt template that tells an agent how to use a search tool is a substitute
for a good tool description, and Dexicon would rather fix the description.

## Errors

MCP tool errors are returned as `isError: true` with a message written for a model to act
on, not a stack trace to display. The messages below are the ones the tools raise; the SDK
adds its `An error occurred invoking '<tool>': ` prefix, as the second table in this
section shows.

| Condition | Message |
|---|---|
| Unknown corpus, `search_index`, `get_context`, `index_status`, resources | `Unknown corpus 'api'. Corpora this key can reach: api-repo, rfc-library.` |
| Unknown corpus, `index_refresh`, `propose_removal`, configuration tools | `No corpus named 'api' is reachable by key 'claude-code'. Corpora this key can reach: api-repo, rfc-library.` |
| No reachable corpora, `search_index` | `Key 'claude-code' can reach no corpora. Create one in the UI, or check which corpora this key is mapped to under Access.` |
| Dimension mismatch | `Collection '<collection>' was indexed with 768-dimension vectors but the configured model produces 1024. Rebuild the corpus with the current model, or restore the original one.` |
| Key lacks the scope | `This key has scopes [search] and needs 'ingest'. Whoever runs Dexicon can grant it on the Access page.` |
| No principal on the request | `Not authenticated. Add an Authorization: Bearer dex_… header to the MCP server configuration.` |
| No such file, `get_context` | `No indexed file 'src/Missing.cs' in corpus 'api-repo'. Check the path is exactly as search_index returned it.` |
| Line past the end, `get_context` | `'docs/auth.md' in corpus 'api-repo' has no line 900; it runs to line 120.` |
| Argument of the wrong shape | `corpus takes a name or a list of names, not a number.` |

Three conditions are results and not errors. A `hybrid` or `semantic` search whose embedding
service is down returns keyword results, a line `! DEGRADED: embedding service unavailable; keyword-only
results` after the header, and `keyword` as the mode in that header. A search of a corpus
that is still indexing returns its results with the line `! Corpus 'api-repo' is still
indexing; results are incomplete.` after the header. `list_corpora` and `index_status`
answer a key that reaches no corpora with text: `Key 'claude-code' can reach no corpora.
Create one in the Dexicon UI, or map this key to one under Access.` from `list_corpora`, and
`Key 'claude-code' can reach no corpora.` from `index_status`.

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
  -> 200     the tools returned
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
`tools/list` gets a 401 with a problem body titled `Missing credentials`.
