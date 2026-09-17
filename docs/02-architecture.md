# 02 — Architecture

## Topology

Three containers. Dexicon is the only one we build.

```
                      ┌──────────────────────────────────────────┐
  Browser ───────────▶│ dexicon  (ASP.NET Core, .NET 10)         │
  MCP clients ───────▶│                                          │
                      │  /            SPA (static, wwwroot)      │
                      │  /api/*       REST + SSE                 │
                      │  /mcp         MCP streamable HTTP        │
                      │  /healthz     liveness / readiness       │
                      │                                          │
                      │  ┌────────────────────────────────────┐  │
                      │  │ IndexingService (hosted, queued)   │  │
                      │  └────────────────────────────────────┘  │
                      │  ┌────────────────────────────────────┐  │
                      │  │ catalog.db (SQLite, volume)        │  │
                      │  └────────────────────────────────────┘  │
                      └──────┬──────────────────────┬────────────┘
                             │ gRPC 6334 (unpublished)  │ HTTP 11434 (unpublished)
                      ┌──────▼──────┐        ┌──────▼──────┐
                      │   qdrant    │        │   ollama    │
                      │ (vectors)   │        │ (embeddings)│
                      └─────────────┘        └─────────────┘

  bind mounts:  ${WORKSPACE_ROOT} ─▶ /workspaces  (read-only)
                dexicon_data      ─▶ /data        (catalog.db, uploads)
```

**Why one container for UI + API + MCP + indexer.** The alternative — splitting the
indexer into a sidecar — earns its keep when the indexer runs inside a per-tenant container
with a different security boundary. Dexicon has no such boundary: it is one operator's tool
on one machine. Splitting would add a control API, a
token, a network, and a failure mode, and buy nothing. See [D-01](decisions.md#d-01-single-container).

## Process layout inside the container

| Component | Kind | Responsibility |
|---|---|---|
| `Api` | ASP.NET Core minimal API | REST for the SPA; SSE for progress |
| `Mcp` | `ModelContextProtocol.AspNetCore`, stateless | Tool surface for agents ([06](06-mcp-surface.md)) |
| `IndexingService` | `BackgroundService` + bounded channel | Runs one indexing job at a time; emits progress events |
| `Catalog` | EF Core + SQLite | Tenants, corpora, sources, files, jobs, tokens |
| `VectorStore` | `Qdrant.Client` (gRPC) | Collection lifecycle, upsert, query |
| `Embedder` | `IEmbeddingProvider` → Ollama | Dense vectors; batching, retry, backoff |
| `SparseEncoder` | in-process | Term-frequency sparse vectors for BM25 ([05](05-search.md)) |
| `Extractors` | per-format loaders | Bytes/path → text + metadata ([04](04-ingestion.md)) |
| `Chunkers` | code-aware, document-aware | Text → chunks with line/page provenance |

Everything is in one process; the seams above are interfaces, so the indexer can be lifted
into its own container later without touching the API.

## Stack

| Layer | Choice | Version at time of writing |
|---|---|---|
| Runtime | .NET | 10 LTS (.NET 11 lands 2026-11-10; stay on LTS) |
| Web | ASP.NET Core minimal APIs | 10 |
| MCP | `ModelContextProtocol.AspNetCore` | 2.2.0 |
| Vectors | `Qdrant.Client` (gRPC) | 1.19.0 |
| Catalog | EF Core + `Microsoft.Data.Sqlite` | 10 |
| Embeddings | Ollama over HTTP (`Microsoft.Extensions.AI` abstractions) | — |
| SPA | React 19 + Vite + Tailwind v4 | — |
| Logging | Serilog → console, structured | — |

Third-party extraction libraries and their licences are listed in [04](04-ingestion.md#extraction);
all are permissive, which matters for open-sourcing.

## Request flows

### Search (the hot path)

```
MCP client ──POST /mcp {tools/call search_index}──▶ Mcp
    │  headers: Authorization: Bearer <token>, X-Dexicon-Tenant: <slug>
    ▼
AuthN: token → principal            (SQLite, hashed lookup, cached)
    ▼
Scope resolution: principal + tenant + requested corpora
    → concrete list of visible corpus ids           (SQLite)
    → EMPTY LIST IS A HARD ERROR, never "all"
    ▼
Embed query (Ollama)  ─┐
Encode query sparse   ─┤─▶ Qdrant Query API: prefetch[dense, sparse] + RRF fusion
                       ─┘   filter: corpus_id ANY [resolved ids]
    ▼
Hydrate: chunk payload → file path, line span, snippet, score
    ▼
Response
```

Two round trips to external services on the hot path (Ollama embed, Qdrant query). The
sparse encoding is in-process. Target p95 under 400 ms for a warm `embeddinggemma`.

### Indexing (the slow path)

```
UI / MCP ──POST /api/corpora/{id}/reindex──▶ enqueue job ──▶ 202 + jobId
                                                  │
                          IndexingService picks up │ (one at a time)
                                                  ▼
  ┌─ discover ──▶ walk /workspaces/<mount> honouring .gitignore + .dexiconignore
  │               or enumerate uploaded blobs
  ├─ triage ────▶ skip binaries, oversize, unchanged (content hash vs catalog)
  ├─ extract ───▶ per-format loader → text + metadata
  ├─ chunk ─────▶ language/format-aware, with line or page provenance
  ├─ embed ─────▶ Ollama, batched, bounded concurrency, capped backoff
  ├─ upsert ────▶ Qdrant, batched; delete-then-insert per changed file
  └─ reconcile ─▶ drop chunks for files that vanished; write file hashes
                                                  │
                     progress events ─────────────┴──▶ SSE /api/events ──▶ UI
```

Job state, per-file outcomes, and failures land in SQLite so the UI can show what happened
after the fact, not only while it is happening.

## Failure behaviour

Stated up front because these are the cases that get fudged.

| Failure | Behaviour |
|---|---|
| Ollama unreachable | Indexing job pauses with capped exponential backoff (5s → 320s), job marked `degraded` with the reason. Search falls back to **keyword-only** and flags `degraded: true` in the response. |
| One file fails to embed | File is skipped, recorded in `file_status` with the error, scan continues. Its hash is *not* written, so it retries next scan. |
| Qdrant unreachable | Search returns 503 with the underlying reason. Indexing job fails fast and is retryable; no partial hash writes. |
| Embedding dimensions ≠ collection dimensions | Search on that corpus is **refused** with an actionable message naming both values and the rebuild command. Never silently mismatched. |
| PDF with no text layer | Ingest records `extracted_chars: 0` and a `no-text-layer` warning; the file appears in the UI as ingested-but-empty rather than silently absent. |
| Workspace mount missing | Corpus marked `unavailable`; existing index retained and still searchable, no destructive reconcile. |

## What is deliberately absent

- **No message broker.** One in-process channel, one worker. Jobs are local and short.
- **No Redis.** Nothing to share between instances, because there is one instance.
- **No relational server.** SQLite in WAL mode on a volume covers the catalogue.
- **No FileSystemWatcher.** Watch events go missing across Docker bind mounts without
  reporting anything, so refresh polls and compares content hashes
  ([D-09](decisions.md#d-09-polling-with-content-hashes)). The interval is configurable;
  manual reindex is always available.
