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
                      │  │ WorkScheduler + WorkerPool         │  │
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

**Why one container for UI + API + MCP + indexer.** The alternative, a separate indexer
sidecar, is worthwhile when the indexer runs inside a per-customer container with a different
security boundary. Dexicon has no such boundary: it is a single-operator tool on one
machine. Separating the indexer would add a control API, a token, a network hop and an
additional failure mode without benefit. See
[D-01](decisions.md#d-01-single-container).

## Process layout inside the container

| Component | Kind | Responsibility |
|---|---|---|
| `Api` | ASP.NET Core minimal API | REST for the SPA and for integrations ([13](13-integration.md)); SSE for progress |
| `Mcp` | `ModelContextProtocol.AspNetCore`, stateless | Tool surface for agents ([06](06-mcp-surface.md)) |
| `WorkScheduler` | in-process queue, one dispatch rule | Holds pending work; hands out a slot when the type has one free and the corpus is idle |
| `WorkerPool` | `BackgroundService`, one worker per slot | Runs sweeps, index passes and rebuilds; emits progress events |
| `Catalog` | EF Core + SQLite | Corpora, sources, files, jobs, keys and what each reaches |
| `VectorStore` | `Qdrant.Client` (gRPC) | Collection lifecycle, upsert, query |
| `Embedder` | `IEmbeddingService` over `IEmbeddingGenerator` (Ollama, OpenAI, Azure OpenAI) | Dense vectors; batching, jittered retry |
| `SparseEncoder` | in-process | Term-frequency sparse vectors; Qdrant applies the IDF weighting ([05](05-search.md)) |
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
| Embeddings | Ollama over HTTP by default; OpenAI and Azure OpenAI as configured providers (`Microsoft.Extensions.AI` abstractions) | — |
| SPA | React 19 + Vite + Tailwind v4 | — |
| Logging | Serilog → console, structured | — |

Third-party extraction libraries and their licences are listed in [04](04-ingestion.md#extraction);
all are permissive, which matters for open-sourcing.

## Request flows

### Search (the hot path)

```
MCP client ──POST /mcp {tools/call search_index}──▶ Mcp
    │  header: Authorization: Bearer <key>
    ▼
AuthN: token → principal            (SQLite, hashed lookup, cached)
    ▼
Scope resolution: principal + its mapped corpora + requested corpora
    → concrete list of visible corpus ids           (SQLite)
    → EMPTY LIST IS A HARD ERROR, never "all"
    ▼
Embed query (set's provider) ─┐
Encode query sparse          ─┤─▶ Qdrant Query API: prefetch[dense, sparse] + DBSF fusion
                              ─┘   filter: corpus_id ANY [resolved ids]
    ▼
Hydrate: chunk payload → file path, line span, snippet, score
    ▼
Response
```

Two round trips to external services on the hot path (embedding provider, Qdrant query). The
sparse encoding is in-process. Target p95 under 400 ms for a warm `embeddinggemma`.

### Indexing (the slow path)

```
UI / MCP ──POST /api/corpora/{nameOrId}/reindex──▶ enqueue job ──▶ 202 + job summary
                                                  │
                           the scheduler takes it │ (a slot for its type, and its corpus free)
                                                  ▼
  ┌─ discover ──▶ walk /workspaces/<mount> honouring .gitignore + .dexiconignore
  │               or enumerate uploaded blobs
  ├─ triage ────▶ skip binaries, oversize, unchanged (content hash vs catalog)
  ├─ extract ───▶ per-format loader → text + metadata
  ├─ chunk ─────▶ language/format-aware, with line or page provenance
  ├─ embed ─────▶ the set's provider, batched, jittered retry, concurrency bounded per provider
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
| Ollama unreachable | Each file whose embedding fails after two retries (about 0.5 s and 1 s plus jitter) is skipped and recorded `failed`; the job ends `degraded` with the reason, and the next scan retries those files. Search falls back to **keyword-only** and flags `degraded: true` in the response. |
| One file fails to embed | File is skipped, recorded in `file_chunk_states` with `status: Failed` and the error in `status_detail`, scan continues. Its hash is *not* written, so it retries next scan. |
| Qdrant unreachable | Search returns a generic problem response with a trace id (500); the cause is in the log. An index job that cannot reach Qdrant is recorded `failed`. |
| Query embedding dimensions ≠ the chunk set's pinned dimensions | Search on that corpus is **refused** (409) with an actionable message naming both values and the rebuild action. Never silently mismatched. |
| PDF with no text layer | Recorded with `status: Empty` and `status_detail: "no text layer: this is a scanned PDF, and OCR is not supported"`; the file appears in the UI as ingested-but-empty rather than silently absent. |
| Workspace mount missing | Corpus marked `unavailable`; existing index retained and still searchable, no destructive reconcile. |
| Data disk full | Catalogue writes are refused. A job retries the save that records its outcome five times, then logs that it gave up. A job that could not save reads `running` until the next start, and stops counting once its lease lapses: whether a corpus is `indexing` is read from the jobs and the lease, not stored, so the scheduled refresh runs again with no restart. See [04](04-ingestion.md#when-the-catalogue-cannot-be-written). |

## What is deliberately absent

- **No message broker.** One in-process queue (`WorkScheduler`) and a pool of workers, one
  per concurrency slot. Jobs are local, and the coordination between two parts of one
  process is a queue.
- **No Redis.** Nothing to share between instances, because there is one instance.
- **No relational server.** SQLite in WAL mode on a volume covers the catalogue.
- **No FileSystemWatcher.** Watch events go missing across Docker bind mounts without
  reporting anything, so refresh polls and compares content hashes
  ([D-09](decisions.md#d-09-polling-with-content-hashes)). The interval is configurable;
  manual reindex is always available.
