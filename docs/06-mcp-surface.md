# 06 — MCP surface

## Protocol

| | |
|---|---|
| Spec revision | **2026-07-28** (latest at time of writing), negotiating down to `2025-06-18` for older clients |
| Transport | **Streamable HTTP** at `POST /mcp`. Legacy HTTP+SSE is deprecated in the spec and not implemented. |
| Session mode | **Stateless.** The 2026-07-28 core removed the `initialize` handshake and `Mcp-Session-Id`; every request is self-contained. |
| SDK | `ModelContextProtocol.AspNetCore` 2.2.0 — `SessionMode = HttpServerSessionMode.Stateless` is already its default. |
| Server→client calls | None. Sampling, roots, and logging are deprecated in 2026-07-28 and Dexicon needs none of them. |

The 2026-07-28 revision also requires `Mcp-Method` and `Mcp-Name` request headers, carries
protocol version and client identity in `_meta`, and allows `tools/list` responses to
advertise `ttlMs` / `cacheScope`. The SDK handles the first two. Dexicon sets a `ttlMs` of
60 s on `tools/list` — the tool set only changes when the operator changes configuration.

Multi Round-Trip Requests (`resultType: "input_required"`) replace elicitation. Dexicon has
one plausible use — asking which corpus was meant when a name is ambiguous — and
deliberately does not use it in v1: an ambiguous scope is an error with the candidates
listed in the message, which every client handles today.

## Connecting

```bash
claude mcp add --transport http dexicon http://localhost:8477/mcp \
  --header "Authorization: Bearer ${DEXICON_TOKEN}" \
  --header "X-Dexicon-Tenant: my-project"
```

The tenant header is optional when the token is bound to exactly one tenant, which is the
normal local-development case. See [07](07-tenancy-auth.md).

## Tools

Five tools. The count is a design constraint, not an accident: every tool definition is
context an agent pays for on every turn, and a surface of thirty tools measurably degrades
smaller models.

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
                  "description": "Corpus names to search. Omit to search everything you can see. Use list_corpora to discover them." },
      "mode":   { "type": "string", "enum": ["hybrid", "semantic", "keyword"], "default": "hybrid",
                  "description": "hybrid blends meaning and exact terms; keyword is exact-match only and works when embeddings are unavailable." },
      "limit":  { "type": "integer", "minimum": 1, "maximum": 50, "default": 10 },
      "path_prefix": { "type": "string", "description": "Restrict to files under this path, e.g. src/Auth/." },
      "language":    { "type": "string", "description": "Restrict to one language, e.g. csharp, python." },
      "symbol":      { "type": "string", "description": "Restrict to chunks declaring this symbol." }
    },
    "required": ["query"]
  }
}
```

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

No inputs beyond an optional `include_stats`. Returns what the caller can see: name,
description, whether it is shared, file and chunk counts, embedding model, last indexed
time, current state. This is how an agent learns what `corpus` values are legal, so its
description says so explicitly.

### `get_context`

`(corpus, file_path, around_line, before = 30, after = 30)` — neighbouring indexed lines,
stitched from stored chunks. For when a search hit needs its surroundings and the agent
cannot open the file itself.

### `index_refresh`

`(corpus, full = false)` — queues an incremental (or full) reindex, returns `{ jobId,
state }` immediately. Requires the `ingest` scope. Never blocks: indexing a large repo
outlasts any sensible tool timeout.

### `index_status`

`(corpus?)` — current job phase and counts, last indexed time, last error, embedding model
and whether it is reachable. Also the honest answer to "why did search return nothing" —
it will say `indexing, 12% (1,204 / 9,880 files)` or `degraded: embedding service
unreachable since 14:02`.

## Resources

Corpora are exposed as MCP resources so clients with a resource picker can browse them:

```
dexicon://corpus/{name}              -> a JSON summary: sources, counts, model, state
dexicon://corpus/{name}/file/{path}  -> reconstructed text of one indexed file
```

Resource reads carry `ttlMs` (60 s for summaries, 300 s for file text). Listing respects
the same scope resolution as search; a corpus you cannot see does not appear.

## Prompts

None in v1. A prompt template that tells an agent how to use a search tool is a substitute
for a good tool description, and Dexicon would rather fix the description.

## Errors

MCP tool errors are returned as `isError: true` with a message written for a model to act
on, not a stack trace to display.

| Condition | Message shape |
|---|---|
| Unknown corpus | `Unknown corpus 'api'. Visible corpora: api-repo, rfc-library.` |
| No visible corpora | `No corpora are visible to tenant 'my-project'. Create one in the UI, or check the X-Dexicon-Tenant header.` |
| Embeddings down, hybrid asked | Results returned with `degraded: true` — not an error. |
| Dimension mismatch | `Corpus 'api-repo' was indexed with nomic-embed-text (768 dims); the configured model produces 1024. Rebuild the corpus or restore the original model.` |
| Indexing in progress, no results | Results plus a note: `corpus 'api-repo' is 12% indexed; results are incomplete.` |

That last one is the difference between an agent concluding "this codebase has no auth
code" and an agent waiting thirty seconds.

## Version handling

The client's requested protocol version arrives in `_meta`. Dexicon serves 2026-07-28 and
falls back to 2025-06-18 semantics for older clients, as the SDK supports. An unsupported
version is refused with the list of supported versions rather than best-effort guessing —
and the negotiated version is logged per connection, because "which revision did that
client actually get" is the first question when a client misbehaves.
