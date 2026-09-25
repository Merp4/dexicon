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
| **Key** | An agent's credential. Carries `search` and optionally `ingest`, never `admin`, and maps to the corpora it may reach. |
| **Corpus** | A named, searchable body of content. The unit of reindexing and of search scope. It has no owner: which keys reach it is a property of those keys. |
| **Chunk set** | One way of cutting and embedding a corpus: a model, a vector space, a chunking strategy. A corpus carries one or more, over the same documents. Addressed as `corpus:set`. |
| **Source** | Where a corpus gets its content: a `workspace` mount path, or `upload` (files pushed through the UI/API). A corpus has one or more. |
| **Blob** | An uploaded document's bytes, content-addressed by SHA-256. Carries no name. |
| **File** | One *attachment* within a source: a path, plus (for uploads) the blob it points at. Several corpora may attach the same blob. |
| **Chunk** | One embedded span of a file, produced by one chunk set. The unit stored in Qdrant and returned by search. |

**The split that matters.** Bytes, extracted text and chunking are three separate
things, deliberately:

```
  blobs              the bytes            content-addressed, stored once
  blob_texts         the extracted text   cached per blob, extracted once ever
  file_texts         the extracted text   the same, for files on a mount
  files              an attachment        one per (corpus, document)
  chunk_sets         the chunk settings   what actually varies
  file_chunk_states  a file, per set      indexed here, pending there
```

One document can therefore live in several corpora AND be cut several ways within one of
them, with the expensive part, storage and extraction, performed once. See
[04](04-ingestion.md#chunk-sets--a-corpus-can-be-cut-several-ways-at-once).

A corpus is the right grain for visibility because it is the thing a human names
("the API repo", "the RFC library") and the thing an agent scopes a query to. It is the
wrong grain for chunk settings, which is why those moved: the model in particular could
never be changed while it lived on the corpus, because a different model is a different
collection ([D-21](decisions.md#d-21-chunk-sets-not-corpus-level-chunking)).

Indexing state is per `(file, chunk set)`, not per file. A hash, a chunk count and a
status describe a file *as cut by a particular set*: the same document may be freshly
indexed in one set and still pending in another.

## SQLite schema

```sql
-- Identity ------------------------------------------------------------------
-- One row. The only route to the admin scope, so nothing that can delete a corpus
-- ever lives in an agent's configuration file. See 07-auth.md.
CREATE TABLE admin_credential (
  id             TEXT PRIMARY KEY,          -- always 'admin'
  password_hash  BLOB NOT NULL,             -- see 10-security-secrets.md
  password_salt  BLOB NOT NULL,
  updated_utc    TEXT NOT NULL
);

CREATE TABLE tokens (
  id            TEXT PRIMARY KEY,           -- public id, shown in UI
  name          TEXT NOT NULL,
  token_hash    BLOB NOT NULL,              -- see 10-security-secrets.md
  token_salt    BLOB NOT NULL,
  scopes        TEXT NOT NULL,              -- csv: search, ingest. Never admin.
  created_utc   TEXT NOT NULL,
  last_used_utc TEXT,
  expires_utc   TEXT,
  revoked_utc   TEXT
);

-- What a key reaches. NO ROWS MEANS EVERY CORPUS, which is what keeps a single-user
-- install from having to configure anything; a key that should reach nothing is
-- revoked instead. Read per request rather than cached on the principal, so an edit
-- in the UI reaches the agent on its next call.
CREATE TABLE token_corpora (
  token_id   TEXT NOT NULL REFERENCES tokens(id) ON DELETE CASCADE,
  corpus_id  TEXT NOT NULL REFERENCES corpora(id) ON DELETE CASCADE,
  PRIMARY KEY (token_id, corpus_id)
);
CREATE INDEX ix_token_corpora_corpus ON token_corpora(corpus_id);

-- Content -------------------------------------------------------------------
CREATE TABLE corpora (
  id                   TEXT PRIMARY KEY,    -- ULID
  name                 TEXT NOT NULL,
  description          TEXT,
  state                TEXT NOT NULL,       -- ready | indexing | degraded | unavailable
  created_utc          TEXT NOT NULL,
  last_indexed_utc     TEXT,
  -- Globally unique, because the name is what an agent passes to search_index and it
  -- has to resolve to one corpus.
  UNIQUE (name)
);

-- One way of cutting and embedding this corpus. Several may exist over the same
-- documents: a coarse set and a fine one, or the live set and its replacement on a new
-- model while that replacement backfills.
CREATE TABLE chunk_sets (
  id                      TEXT PRIMARY KEY,    -- ULID
  corpus_id               TEXT NOT NULL REFERENCES corpora(id) ON DELETE CASCADE,
  name                    TEXT NOT NULL,       -- addressed as corpus:name; no colons
  description             TEXT,
  -- The vector space. Pinned per set: changing a set's model in place would strand its
  -- vectors in a collection nothing addresses. Changing model means a NEW set.
  embedding_model         TEXT NOT NULL,
  embedding_dimensions    INTEGER NOT NULL,
  collection_name         TEXT NOT NULL,       -- derived; see below
  chunk_size              INTEGER NOT NULL,
  chunk_overlap           INTEGER NOT NULL,
  boundary_mode           TEXT NOT NULL,       -- none | blank-line | language-aware | custom
  custom_boundary_pattern TEXT,                -- required when boundary_mode = custom
  unit_aware              INTEGER NOT NULL DEFAULT 0,  -- page/chapter/slide forces a split
  sentence_aware          INTEGER NOT NULL DEFAULT 0,  -- cut at a sentence, not a word
  heading_context         INTEGER NOT NULL DEFAULT 0,  -- embed under the heading trail
  -- Exactly one per corpus. It is what an unqualified corpus name resolves to, and
  -- promotion — flipping this flag — is the only moment search changes.
  is_default              INTEGER NOT NULL DEFAULT 0,
  state                   TEXT NOT NULL,
  created_utc             TEXT NOT NULL,
  last_indexed_utc        TEXT,
  UNIQUE (corpus_id, name)
);

CREATE TABLE sources (
  id                TEXT PRIMARY KEY,
  corpus_id         TEXT NOT NULL REFERENCES corpora(id) ON DELETE CASCADE,
  kind              TEXT NOT NULL,          -- workspace | upload | githistory
  root_path         TEXT,                   -- workspace, githistory: path under /workspaces
  -- The four filters are NULL to inherit: 04 lists the three layers they resolve through.
  include_globs     TEXT,                   -- json array
  exclude_globs     TEXT,                   -- json array
  use_gitignore     INTEGER,
  max_file_bytes    INTEGER,
  git_options       TEXT,                   -- githistory: json, the settings in 04
  newest_commit_sha TEXT,                   -- githistory: what the last pass found
  newest_commit_utc TEXT,                   --   and its author date
  git_tracking      TEXT,                   -- githistory: json, how current the followed ref was
  created_utc       TEXT NOT NULL
);

-- The ATTACHMENT. What the file is, shared by every chunk set that reads it. What each
-- set MADE of it lives in file_chunk_states below.
CREATE TABLE files (
  id              TEXT PRIMARY KEY,
  source_id       TEXT NOT NULL REFERENCES sources(id) ON DELETE CASCADE,
  relative_path   TEXT NOT NULL,            -- forward slashes, always
  -- Upload-sourced files only: the blob this is an attachment OF. Several corpora
  -- can point at one blob and chunk it differently — the point of the split.
  blob_sha256     TEXT REFERENCES blobs(sha256),
  size_bytes      INTEGER NOT NULL,
  media_type      TEXT,
  language        TEXT,
  extracted_chars INTEGER NOT NULL DEFAULT 0,
  UNIQUE (source_id, relative_path)
);

-- One file as ONE chunk set sees it. This is where indexing state lives, because a hash,
-- a chunk count and a status are properties of a file *as cut by a particular set* — not
-- of the attachment. The same document can be freshly indexed in one set and pending in
-- another, and while a replacement set backfills that is the normal state of affairs.
CREATE TABLE file_chunk_states (
  file_id       TEXT NOT NULL REFERENCES files(id) ON DELETE CASCADE,
  chunk_set_id  TEXT NOT NULL REFERENCES chunk_sets(id) ON DELETE CASCADE,
  -- NOT a bare content hash: a CHUNKING FINGERPRINT over the content AND everything that
  -- decides what ends up in Qdrant. With a content hash alone, changing a chunk size left
  -- every file looking unchanged, so a refresh re-chunked nothing and the new setting
  -- silently did not apply. See 04-ingestion.md.
  content_hash  TEXT,                       -- NULL until successfully indexed
  -- SHA-256 of the file's BYTES as THIS set last read them: the key into file_texts,
  -- and so the only way to reach a workspace document whole. Per set rather than on
  -- the attachment, because a job can target one set while the others keep serving,
  -- and a file-wide hash would point a reader for a still-old set at the new document.
  source_sha256 TEXT,                       -- NULL for uploads; files.blob_sha256 serves
  chunk_count   INTEGER NOT NULL DEFAULT 0,
  status        TEXT NOT NULL,              -- pending | indexed | skipped | failed | empty
  status_detail TEXT,                       -- why, in words, for skipped/failed/empty
  indexed_utc   TEXT,
  PRIMARY KEY (file_id, chunk_set_id)
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
  sha256            TEXT PRIMARY KEY REFERENCES blobs(sha256) ON DELETE CASCADE,
  text              TEXT NOT NULL,
  units_json        TEXT,                -- page/slide/chapter offsets, for provenance
  title             TEXT,                -- from the document's own metadata
  extracted_chars   INTEGER NOT NULL,
  extractor         TEXT NOT NULL,
  extractor_version INTEGER NOT NULL,    -- so an extractor fix invalidates the cache
  extracted_utc     TEXT NOT NULL,
  empty_reason      TEXT                 -- readable but yielded nothing: a scanned PDF
);

-- The same, for files that live on a mount rather than in the blob store.
--
-- Keyed on a hash of the FILE'S BYTES, not of the text extracted from them: the key has
-- to be computable without doing the work it exists to avoid. And on the extractor with
-- it, because which extractor runs is decided by extension, so the same bytes under two
-- extensions are two different parses; DOCX, PPTX and EPUB are all zip containers that a
-- rename moves between. No foreign key, because there is no row for a file on disk to
-- point at, and two corpora indexing the same file share one row.
--
-- This is also the only place a workspace document exists whole. Chunks carry their own
-- text and nothing else did, so without this the content survives only as pieces.
CREATE TABLE file_texts (
  sha256            TEXT NOT NULL,       -- SHA-256 of the file's bytes
  extractor         TEXT NOT NULL,
  text              TEXT NOT NULL,
  units_json        TEXT,
  title             TEXT,
  extracted_chars   INTEGER NOT NULL,
  extractor_version INTEGER NOT NULL,
  extracted_utc     TEXT NOT NULL,
  empty_reason      TEXT,
  PRIMARY KEY (sha256, extractor)
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

One collection **per provider, embedding model and dimensionality**, shared by every corpus:

```
dexicon__{provider_slug}__{model_slug}__{dimensions}

e.g.  dexicon__ollama__embeddinggemma__768
      dexicon__ollama__nomic-embed-text__768
      dexicon__ollama__qwen3-embedding-0-6b__1024
      dexicon__openai__text-embedding-3-small__1536
```

Each part is lowercased with `[^a-z0-9]` collapsed to `-`. Encoding provider, model and
dimensions in the name makes a mismatch structurally impossible: a chunk set pinned to a
model can only ever point at that model's collection.

**The provider is in the name** because two providers can serve a model of the same name,
and those are different vectors. Without it an OpenAI set and a local set would share a
collection, each writing vectors into the other's space.

**`:latest` is stripped first.** Ollama lists `embeddinggemma:latest` and a configuration
file says `embeddinggemma`; they are one model and one vector space, and slugging them raw
produced `embeddinggemma-latest__768` alongside `embeddinggemma__768`: two collections
holding vectors that belong together, neither aware of the other. Only `:latest` goes:
`:v1.5` and `:0.6b` are different weights producing different vectors.

A chunk set stores the collection name it was built with, so a change to this scheme leaves
existing sets where they are rather than moving them underneath a running system.

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

`m: 0` disables the global HNSW graph; `payload_m: 16` builds a graph **per corpus**
instead. Combined with the payload index below, this is Qdrant's recommended many-tenant
layout. This has one significant consequence:

> **An unfiltered query against this collection has no index to use.** It degrades to brute
> force. Forgetting the scope filter is therefore both *blocked* (by the guard in
> [07](07-auth.md)) and *slow*. Two independent mechanisms, on purpose.

**Measured** in the M0 spike, 2026-09-16. 50,007 points across 20 corpora, Qdrant
1.16.3, collection reporting `m=0, payload_m=16` server-side, status `Green`, all vectors
indexed:

| Query | Mean over 25 runs |
|---|---|
| Filtered to one corpus | **1.5 ms** |
| No filter | **3.1 ms** — 2.0× slower |

Two caveats. The gap is a factor of two rather than a hard barrier: it is a deterrent and
a signal, not a safety mechanism. The application and repository guards in
[07](07-auth.md) are what prevent a leak. And it will
widen with corpus size: 50k points is small enough that a brute-force scan is still cheap.

Isolation itself was verified too: a query embedded from another
corpus's most distinctive content (`"doomsday launch code hunter2"`) returned **zero**
points from that corpus when filtered to a different one.

### Payload indexes

```json
{ "field_name": "corpus_id",    "field_schema": { "type": "keyword", "is_tenant": true } }
{ "field_name": "chunk_set_id", "field_schema": "keyword" }
{ "field_name": "file_path",    "field_schema": "keyword" }
{ "field_name": "language",  "field_schema": "keyword" }
{ "field_name": "kind",      "field_schema": "keyword" }
{ "field_name": "symbols",   "field_schema": "keyword" }
{ "field_name": "content",   "field_schema": "text"    }
```

**`corpus_id` is the `is_tenant` field.** It is what every query filters on, so
co-locating storage by corpus matches the access pattern exactly. Qdrant's name for the
key is historical here: there are no tenants, and there is no `tenant_id` in the payload.
It used to be written on every point and read by nothing, which is a field that invites
someone to filter on it. Removed by
[D-28](decisions.md#d-28-an-admin-password-and-scoped-api-keys). See
[D-04](decisions.md#d-04-corpus-as-the-qdrant-tenant-key).

**`chunk_set_id` is an ordinary filter, not a second tenant key.** It narrows *within* a
corpus's partition, which `corpus_id` has already selected, so it needs no co-location of
its own. Re-keying the tenant index onto the set would have been invasive and bought
nothing. A point's **id** does derive from the set: `uuid(chunk_set_id, file_path,
chunk_index)`. Keying it on the corpus would cause two sets holding the same file at the
same index to overwrite each other, without error, and only for the paths they share.

### Point payload

```jsonc
{
  "kind":        "chunk",            // chunk | file_marker
  "corpus_id":    "01JD...",         // scope key — indexed, is_tenant
  "chunk_set_id": "01JD...",         // which chunking produced this — indexed, ordinary filter
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

Point id is a deterministic UUIDv5 over `chunk_set_id | file_path | chunk_index`, so a
re-index of an unchanged file is idempotent and a changed file's stale chunks are
addressable without a scroll.

Keyed on the **set**, not the corpus: two sets hold the same file at the same chunk index,
and a corpus-keyed id would cause them to overwrite each other without error, and only
for the file paths they share.

**Content is stored in the payload.** It costs storage and it is the right call: an agent
that must open the file to see what it matched has gained nothing over grep, and a corpus
built from uploads may have no file to open.

## Identifier conventions

- Corpus, source, file, job, token ids: **ULID** — sortable, URL-safe, no coordination.
- Qdrant point ids: **UUIDv5**, derived as above. Never random.
- Paths: always forward slashes, always relative to the source root, never absolute. An
  absolute host path in a payload is a leak.

## Lifecycle and cascades

| Action | Effect |
|---|---|
| Delete file from disk | Next reconcile deletes its chunks (`chunk_set_id` + `file_path`) and its state row, per set. The `files` row survives until the LAST set has let go of it — removing it earlier would strand the other sets' vectors with nothing left to name them. |
| Delete source | Chunks deleted by `source_id` filter; rows cascade. |
| Delete chunk set | Chunks deleted by `chunk_set_id` filter; state rows cascade. Refused for the default set, and for the only set — a corpus with no sets is a corpus nothing can search. |
| Delete corpus | Chunks deleted by `corpus_id` filter, once per distinct collection its sets occupy; rows cascade; grants cascade. |
| Delete corpus | Cascades to its sources, files, chunk sets and the rows mapping keys to it. A key mapped only to that corpus is left mapped to nothing, which means every corpus; revoke it instead if that is not wanted. |
| Change embedding model | Not an edit, and not a corpus-level act at all. Add a chunk set on the new model; it backfills while the live set keeps serving; promote when complete; drop the old set. See [D-21](decisions.md#d-21-chunk-sets-not-corpus-level-chunking). |

## Storage budget

Rough, for planning. 768-dim float32 dense vector = 3 KB; sparse vector ≈ 0.4 KB; payload
with stored content ≈ 1.2 KB at a 768-token chunk.

| Corpus | Files | Chunks | Qdrant |
|---|---|---|---|
| Medium repo | 5 000 | ~40 000 | ~180 MB |
| Large repo | 50 000 | ~400 000 | ~1.8 GB |
| 200 PDFs (~100 pp each) | 200 | ~16 000 | ~75 MB |

Quantization (scalar `int8`) cuts the dense component by ~4× and is a configuration change,
not a schema change. Left off by default; revisit when there are measurements.
