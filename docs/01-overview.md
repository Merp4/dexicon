# 01 — Overview

## The problem

A coding agent working in a local repository reads files by grep and by path. That works
until the question is semantic (*"where do we handle token refresh?"*, *"what does the
spec say about retention?"*), where the useful answer lives in wording the agent cannot
guess, in a file it has no reason to open, or in a PDF it cannot grep at all.

Running a vector index for this is normally a project in itself: a store, an embedding
service, an ingestion pipeline, a schema, and a protocol adapter. Dexicon is that project,
done once, in one container.

## What Dexicon is

A single service that:

1. **Indexes** recursive folder trees (source code, docs) and uploaded files (PDF, DOCX,
   PPTX, EPUB, HTML, Markdown, text) into Qdrant, embedding via Ollama.
2. **Searches** them with hybrid dense + keyword retrieval, returning chunks with
   `file:line` provenance.
3. **Serves** that search to agents over MCP (streamable HTTP) and to humans over a small
   web UI.
4. **Scopes** every key to the corpora it is mapped to, so one deployment can serve several agents,
   people, or agent identities without them seeing each other's content.

## Who it is for

- **Primary**: a developer running Claude Code against local repositories who wants
  semantic recall over the code, the docs, and the reference PDFs, on their own hardware,
  with nothing leaving the machine.
- **Secondary**: a small team running one shared Dexicon, a key per agent, some
  reference material shared across all of them.

## Scope

**In scope**

- Recursive workspace indexing from read-only bind mounts, with gitignore-aware filtering
  and content-hash incremental refresh.
- File upload and ingestion for the document formats listed above.
- Hybrid semantic + keyword search, scoped to what the calling key can reach.
- MCP server (streamable HTTP) exposing search and catalogue tools.
- Web UI for corpora, API keys and what each reaches, indexing control, and a search playground.
- Docker Compose bringing up Dexicon + Qdrant + Ollama.

**Out of scope.** Permanently, unless re-argued.

| Not doing | Why |
|---|---|
| Chat, agents, LLM orchestration | Out of scope. Dexicon retrieves; the agent reasons. |
| Reranking models, query rewriting, HyDE | Adds a second model dependency and latency for gains the caller can get by asking better. Revisit only with measurements. |
| OCR of scanned PDFs | Needs a vision model and a GPU budget. Text-layer PDFs only; say so plainly when a PDF yields nothing. |
| Graph/AST-level code understanding | Chunk-level retrieval is the target. Symbol extraction is metadata, not a call graph. |
| Cloud embedding providers | Local-first is the product. An `IEmbeddingProvider` seam exists; no hosted implementation ships. |
| SSO / OIDC / user accounts | An admin password and API keys are the auth model. See [07](07-auth.md). |
| Horizontal scaling, HA, sharding | One container, one Qdrant. If you outgrow it, you outgrew Dexicon. |

## Design principles

These are recorded here because they constrain later design choices.

1. **A search that cannot name its scope is an error, not a broad search.** There is no
   "search everything" path. See [05](05-search.md) and [07](07-auth.md).
2. **The embedding model is part of the collection's identity.** Changing model or
   dimensions means a new collection and a rebuild, never a silent mismatch.
3. **Degradation must be audible.** Every fallback (keyword-only because embeddings are
   down, a PDF with no text layer, a skipped oversized file) is logged at Warning and
   surfaced in the UI. A fallback that cannot say it fired is indistinguishable from a
   feature that is switched off.
4. **One bad input must not starve the rest.** A file that cannot be embedded is skipped
   and recorded, not allowed to abort the scan.
5. **Secrets are a day-one concern, not a pre-release cleanup.** See [10](10-security-secrets.md).

## Success criteria

Dexicon v1 is done when, on a clean machine:

- `docker compose up` yields a working UI, API, and MCP endpoint with no manual steps
  beyond copying `.env.example` to `.env`.
- `claude mcp add --transport http dexicon http://localhost:8477/mcp --header ...` connects,
  and `search_index` returns relevant chunks from a repository indexed through the UI.
- A repository of tens of thousands of files indexes without manual intervention, and a
  subsequent refresh re-embeds what changed and nothing else. Measured on a 27,001-file
  repository: 26,992 files and 63,540 chunks in one unattended pass, and a later refresh
  that found one changed file re-embedded that one file as 11 chunks.

  The bar is the two properties — unattended at that scale, and incremental cost
  proportional to the change — rather than a particular file count.
- A key cannot retrieve content from a corpus it is not mapped to, through any surface, proven by
  a test that asserts it.
