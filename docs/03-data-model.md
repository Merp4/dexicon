# 03 — Data model

Two stores, with a strict division:

- **SQLite (`/data/catalog.db`)** — the control plane. Who exists, what is indexed, what is
  visible to whom, what the indexer did. Everything that needs to be listed, filtered,
  joined, or audited.
- **Qdrant** — the retrieval plane. Chunks and their vectors. Everything that needs to be
  searched by similarity.

Nothing is authoritative in both. Where a fact appears in both (file hash, chunk count) the
catalogue is authoritative and Qdrant is a derived view that can be rebuilt from scratch.

## Core concepts

| Concept | Definition |
|---|---|
| **Tenant** | The isolation boundary. A slug (`^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$`). Every request resolves to exactly one. |
| **Corpus** | A named, searchable body of content owned by one tenant. The unit of visibility, of reindexing, and of search scope — **and of chunk settings**. |
| **Source** | Where a corpus gets its content: a `workspace` mount path, or `upload` (files pushed through the UI/API). A corpus has one or more. |
| **Blob** | An uploaded document's bytes, content-addressed by SHA-256. Carries no name. |
| **File** | One *attachment* within a source: a path, plus (for uploads) the blob it points at. Several corpora may attach the same blob. |
| **Chunk** | One embedded span of a file. The unit stored in Qdrant and returned by search. |

**The split that matters.** Bytes, extracted text and chunking are three separate
things, deliberately:

```
  blobs        the bytes            content-addressed, stored once
  blob_texts   the extracted text   cached per blob, extracted once ever
  files        an attachment        one per (corpus, document)
  corpora      the chunk settings   what actually varies
```

One document can therefore live in several corpora, each chunked its own way, with the
expensive half — storage and extraction — paid exactly once. See
[04](04-ingestion.md#upload--files-pushed-through-the-ui-or-api).

A corpus is the right grain for visibility because it is the thing a human names
("the API repo", "the RFC library") and the thing an agent scopes a query to.

## SQLite schema

```sql
-- Identity ------------------------------------------------------------------
CREATE TABLE tenants (
  id            TEXT PRIMARY KEY,           -- slug
  display_name  TEXT NOT NULL,
  created_utc   TEXT NOT NULL,
  disabled      INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE tokens (
  id            TEXT PRIMARY KEY,           -- public id, shown in UI
  name          TEXT NOT NULL,
  token_hash    BLOB NOT NULL,              -- see 10-security-secrets.md
  token_salt    BLOB NOT NULL,
  tenant_id     TEXT NOT NULL REFERENCES tenants(id),
  scopes        TEXT NOT NULL,              -- csv: search, ingest, admin
  created_utc   TEXT NOT NULL,
  last_used_utc TEXT,
  expires_utc   TEXT,
  revoked_utc   TEXT
);
CREATE INDEX ix_tokens_tenant ON tokens(tenant_id);

-- Content -------------------------------------------------------------------
CREATE TABLE corpora (
  id                   TEXT PRIMARY KEY,    -- ULID
  tenant_id            TEXT NOT NULL REFERENCES tenants(id),
  name                 TEXT NOT NULL,
  description          TEXT,
  visibility           TEXT NOT NULL,       -- private | shared
  embedding_model      TEXT NOT NULL,       -- pinned at creation
  embedding_dimensions INTEGER NOT NULL,    -- pinned at creation
  collection_name      TEXT NOT NULL,       -- derived; see below
  chunk_size           INTEGER NOT NULL,
  chunk_overlap        INTEGER NOT NULL,
  boundary_mode        TEXT NOT NULL,       -- none | blank-line | language-aware | custom
  state                TEXT NOT NULL,       -- ready | indexing | degraded | unavailable
  created_utc          TEXT NOT NULL,
  UNIQUE (tenant_id, name)
);

-- Explicit grants. A corpus with visibility 'shared' and no rows here is readable by
-- every tenant; with rows, only by the tenants listed. 'private' ignores this table.
CREATE TABLE corpus_grants (
  corpus_id  TEXT NOT NULL REFERENCES corpora(id) ON DELETE CASCADE,
  tenant_id  TEXT NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  PRIMARY KEY (corpus_id, tenant_id)
);

CREATE TABLE sources (
  id             TEXT PRIMARY KEY,
  corpus_id      TEXT NOT NULL REFERENCES corpora(id) ON DELETE CASCADE,
  kind           TEXT NOT NULL,             -- workspace | upload
  root_path      TEXT,                      -- workspace: path under /workspaces
  include_globs  TEXT,                      -- json array, optional
  exclude_globs  TEXT,                      -- json array, optional
  use_gitignore  INTEGER NOT NULL DEFAULT 1,
  max_file_bytes INTEGER NOT NULL DEFAULT 262144,
  created_utc    TEXT NOT NULL
);

CREATE TABLE files (
  id              TEXT PRIMARY KEY,
  source_id       TEXT NOT NULL REFERENCES sources(id) ON DELETE CASCADE,
  relative_path   TEXT NOT NULL,            -- forward slashes, always
  -- NOT a bare content hash: a CHUNKING FINGERPRINT over
  -- (content | chunk_size | chunk_overlap | boundary_mode | model). With a content
  -- hash alone, changing a corpus's chunk size left every file looking unchanged,
  -- so a refresh re-chunked nothing and the new setting silently did not apply.
  content_hash    TEXT,                     -- NULL until successfully indexed
  -- Upload-sourced files only: the blob this is an attachment OF. Several corpora
  -- can point at one blob and chunk it differently — the point of the split.
  blob_sha256     TEXT REFERENCES blobs(sha256),
  size_bytes      INTEGER NOT NULL,
  media_type      TEXT,
  language        TEXT,
  chunk_count     INTEGER NOT NULL DEFAULT 0,
  extracted_chars INTEGER NOT NULL DEFAULT 0,
  status          TEXT NOT NULL,            -- indexed | skipped | failed | empty
  status_detail   TEXT,                     -- why, in words, for skipped/failed/empty
  indexed_utc     TEXT,
  UNIQUE (source_id, relative_path)
);
CREATE INDEX ix_files_status ON files(source_id, status);

-- Blob store for uploads. Content lives at /data/blobs/<sha256[0:2]>/<sha256>.
-- Carries no name: the same PDF can be attached to different corpora under
-- different names, so the name belongs to the attachment, not the bytes.
CREATE TABLE blobs (
  sha256             TEXT PRIMARY KEY,
  size_bytes         INTEGER NOT NULL,
  media_type         TEXT,
  original_file_name TEXT,               -- display only
  created_utc        TEXT NOT NULL
);

-- Extracted text, cached against the bytes. THE table that makes re-chunking cheap
-- and lets one document be chunked differently per corpus.
--
-- Extraction is deterministic in the bytes and expensive (1.5 s of layout analysis
-- for a 437-page PDF); chunking is cheap and corpus-specific. Splitting them means
-- changing a chunk size, or attaching a document to a second corpus, never re-opens
-- the file.
CREATE TABLE blob_texts (
  sha256          TEXT PRIMARY KEY REFERENCES blobs(sha256) ON DELETE CASCADE,
  text            TEXT NOT NULL,
  units_json      TEXT,                  -- page/slide/chapter offsets, for provenance
  title           TEXT,                  -- from the document's own metadata
  extracted_chars INTEGER NOT NULL,
  extractor       TEXT NOT NULL,         -- so a loader upgrade can invalidate the cache
  extracted_utc   TEXT NOT NULL,
  empty_reason    TEXT                   -- readable but yielded nothing: a scanned PDF
);

-- Operations ----------------------------------------------------------------
CREATE TABLE jobs (
  id             TEXT PRIMARY KEY,
  corpus_id      TEXT NOT NULL REFERENCES corpora(id) ON DELETE CASCADE,
  kind           TEXT NOT NULL,             -- full | refresh | rebuild | delete
  state          TEXT NOT NULL,             -- queued | running | succeeded | failed | degraded | cancelled
  phase          TEXT,                      -- discover | extract | embed | upsert | reconcile
  files_total    INTEGER NOT NULL DEFAULT 0,
  files_done     INTEGER NOT NULL DEFAULT 0,
  files_skipped  INTEGER NOT NULL DEFAULT 0,
  files_failed   INTEGER NOT NULL DEFAULT 0,
  chunks_written INTEGER NOT NULL DEFAULT 0,
  error          TEXT,
  started_utc    TEXT,
  finished_utc   TEXT
);
CREATE INDEX ix_jobs_corpus ON jobs(corpus_id, started_utc DESC);
```

`chunk_count` on `files` is written by the indexer **and read back** by the corpus detail
screen and by `index_status`. Every column above has a named reader; a column nothing reads
is a feature that does not exist.

## Qdrant collections

### Naming

One collection **per embedding model and dimensionality**, shared by all tenants:

```
dexicon__{model_slug}__{dimensions}

e.g.  dexicon__nomic-embed-text__768
      dexicon__embeddinggemma__768
      dexicon__qwen3-embedding-0-6b__1024
```

`model_slug` is the Ollama model name lowercased with `[^a-z0-9]` collapsed to `-`.
Encoding the model and dimensions in the name makes a mismatch structurally impossible:
a corpus pinned to a model can only ever point at that model's collection.

### Vectors

| Name | Kind | Config |
|---|---|---|
| `dense` | dense | size = model dimensions, distance = Cosine |
| `sparse` | sparse | `modifier: idf` — clients push term frequencies, Qdrant applies IDF |

### Collection config — multitenancy

```json
{
  "vectors":        { "dense":  { "size": 768, "distance": "Cosine" } },
  "sparse_vectors": { "sparse": { "modifier": "idf" } },
  "hnsw_config":    { "m": 0, "payload_m": 16 }
}
```

`m: 0` disables the global HNSW graph; `payload_m: 16` builds a graph **per tenant value**
instead. Combined with the payload index below, this is Qdrant's recommended many-tenant
layout. It has a consequence worth stating loudly:

> **An unfiltered query against this collection has no index to use.** It degrades to brute
> force. Forgetting the scope filter is therefore both *blocked* (by the guard in
> [07](07-tenancy-auth.md)) and *slow*. Two independent mechanisms, on purpose.

**Measured, not assumed** (M0 spike, 2026-09-16). 50,007 points across 20 corpora, Qdrant
1.16.3, collection reporting `m=0, payload_m=16` server-side, status `Green`, all vectors
indexed:

| Query | Mean over 25 runs |
|---|---|
| Filtered to one corpus | **1.5 ms** |
| No filter | **3.1 ms** — 2.0× slower |

Two caveats worth keeping honest. The gap is a factor of two, not a cliff — this is a
deterrent and a signal, not a safety mechanism, and the application and repository guards
in [07](07-tenancy-auth.md) remain the things that actually prevent a leak. And it will
widen with corpus size: 50k points is small enough that a brute-force scan is still cheap.

Isolation itself was also verified rather than assumed: a query embedded from another
corpus's most distinctive content (`"doomsday launch code hunter2"`) returned **zero**
points from that corpus when filtered to a different one.

### Payload indexes

```json
{ "field_name": "corpus_id", "field_schema": { "type": "keyword", "is_tenant": true } }
{ "field_name": "file_path", "field_schema": "keyword" }
{ "field_name": "language",  "field_schema": "keyword" }
{ "field_name": "kind",      "field_schema": "keyword" }
{ "field_name": "symbols",   "field_schema": "keyword" }
{ "field_name": "content",   "field_schema": "text"    }
```

**`corpus_id` is the `is_tenant` field, not `tenant_id`.** A corpus belongs to exactly one
tenant, is never split across tenants, and is what every query actually filters on — so
co-locating storage by corpus is strictly finer-grained than by tenant, and matches the
access pattern. `tenant_id` is still carried in the payload for auditing and for bulk
deletes when a tenant is removed. See [D-04](decisions.md#d-04-corpus-as-the-qdrant-tenant-key).

### Point payload

```jsonc
{
  "kind":        "chunk",            // chunk | file_marker
  "corpus_id":   "01JD...",          // scope key — indexed, is_tenant
  "tenant_id":   "acme",             // owning tenant, audit + bulk delete
  "source_id":   "01JD...",
  "file_path":   "src/Auth/TokenService.cs",   // relative to source root
  "file_hash":   "a1b2c3...",        // lets a reindex detect staleness without a catalog hit
  "media_type":  "text/x-csharp",
  "language":    "csharp",
  "start_line":  120,                // text sources
  "end_line":    168,
  "page":        null,               // PDF/PPTX sources; null for line-addressed content
  "section":     "TokenService.Refresh",  // heading, chapter, or nearest symbol
  "symbols":     ["TokenService", "Refresh"],
  "chunk_index": 7,
  "content":     "…the chunk text…",  // stored: results must be usable without a file read
  "indexed_utc": "2026-09-16T12:00:00Z"
}
```

Point id is a deterministic UUIDv5 over `corpus_id | file_path | chunk_index`, so a
re-index of an unchanged file is idempotent and a changed file's stale chunks are
addressable without a scroll.

**Content is stored in the payload.** It costs storage and it is the right call: an agent
that must open the file to see what it matched has gained nothing over grep, and a corpus
built from uploads may have no file to open.

## Identifier conventions

- Corpus, source, file, job, token ids: **ULID** — sortable, URL-safe, no coordination.
- Qdrant point ids: **UUIDv5**, derived as above. Never random.
- Tenant ids: **operator-chosen slug**. They appear in headers and UI; readability wins.
- Paths: always forward slashes, always relative to the source root, never absolute. An
  absolute host path in a payload is a leak.

## Lifecycle and cascades

| Action | Effect |
|---|---|
| Delete file from disk | Next reconcile deletes its chunks (`corpus_id` + `file_path` filter) and its `files` row. |
| Delete source | Chunks deleted by `source_id` filter; rows cascade. |
| Delete corpus | Chunks deleted by `corpus_id` filter; rows cascade; grants cascade. |
| Delete tenant | Refused while it owns corpora. The operator must move or delete them first — an implicit cascade over someone's whole index is not a thing a button should do. |
| Change embedding model | Not an edit. Creates a new corpus, or a `rebuild` job that re-embeds into the new model's collection and drops the old points on success. Never in place. |

## Storage budget

Rough, for planning. 768-dim float32 dense vector = 3 KB; sparse vector ≈ 0.4 KB; payload
with stored content ≈ 1.2 KB at a 768-token chunk.

| Corpus | Files | Chunks | Qdrant |
|---|---|---|---|
| Medium repo | 5 000 | ~40 000 | ~180 MB |
| Large repo | 50 000 | ~400 000 | ~1.8 GB |
| 200 PDFs (~100 pp each) | 200 | ~16 000 | ~75 MB |

Quantization (scalar `int8`) cuts the dense component by ~4× and is a configuration change,
not a schema change. Left off by default; revisit with measurements, not in advance.
