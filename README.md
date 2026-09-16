# Dexicon

**Semantic indexing and search for local agentic development, served over MCP.**

*dex* (index) + *lexicon* — a reference work you consult. You arrive with a question and
leave with an answer, which is the whole job.

Dexicon is a single container that indexes your files — source trees, PDFs, docs — into a
vector store, and exposes semantic + keyword search to coding agents (Claude Code, and any
other MCP client) over HTTP. It ships a small web UI for managing what is indexed, what is
visible to whom, and what the indexer is currently doing.

```
┌─ your machine ────────────────────────────────────────────────┐
│                                                               │
│   Claude Code ──MCP/HTTP──┐                                   │
│   Other agents ───────────┤                                   │
│   Browser ─────HTTP───────┤                                   │
│                           ▼                                   │
│                     ┌───────────┐   ┌──────────┐              │
│                     │  dexicon  │──▶│  qdrant  │              │
│                     │ UI+API+MCP│   └──────────┘              │
│                     │ + indexer │   ┌──────────┐              │
│                     └─────┬─────┘──▶│  ollama  │              │
│                           │         └──────────┘              │
│                    /workspaces (ro)                           │
└───────────────────────────────────────────────────────────────┘
```

## Status

**Specification only.** No code yet. These documents are the design being agreed before
implementation starts.

## Documents

| Doc | What it covers |
|---|---|
| [01 — Overview](docs/01-overview.md) | Problem, scope, explicit non-goals, who it is for |
| [02 — Architecture](docs/02-architecture.md) | Container topology, processes, request and data flows |
| [03 — Data model](docs/03-data-model.md) | Qdrant collections and payloads, SQLite catalog, identifiers |
| [04 — Ingestion](docs/04-ingestion.md) | Sources, extraction, chunking, embedding, incremental reindex |
| [05 — Search](docs/05-search.md) | Hybrid dense + sparse retrieval, fusion, result contract |
| [06 — MCP surface](docs/06-mcp-surface.md) | Protocol version, transport, the tool set, wire examples |
| [07 — Tenancy & auth](docs/07-tenancy-auth.md) | Tenant header, tokens, corpus visibility, enforcement |
| [08 — UI](docs/08-ui.md) | Screens, interactions, live progress |
| [09 — Deployment](docs/09-deployment.md) | Compose topology, configuration, volumes, GPU, healthchecks |
| [10 — Security & secrets](docs/10-security-secrets.md) | Secret handling from commit one, container hardening, CI |
| [11 — Roadmap](docs/11-roadmap.md) | Milestones M0–M5 with definitions of done |
| [Decisions](docs/decisions.md) | Every load-bearing choice, its rationale, and what was rejected |

## Provenance

Dexicon carves a subset out of [McpToolbox](../McpToolbox): the workspace sidecar indexer,
the Qdrant repository, the language-aware code chunker, the document loaders, and the
tenancy/auth ADRs. McpToolbox has grown into a full agent platform; Dexicon keeps only the
indexing and search parts, aimed at local agentic development, and re-specifies them against
current MCP, Qdrant, and .NET releases. See [Decisions](docs/decisions.md) for what was
carried over, what was changed, and why.

## Licence

[Apache-2.0](LICENSE). Copyright 2026 Martyn Mcvay — see [NOTICE](NOTICE).
Chosen for the express patent grant and the trademark clause; the reasoning and the
rejected alternative are in [Decisions, D-14](docs/decisions.md#d-14-licence).
