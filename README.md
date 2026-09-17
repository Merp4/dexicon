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

**Working, and young.** Published as `ghcr.io/merp4/dexicon`; the current release is
[`0.2.0`](CHANGELOG.md). Indexing, hybrid search, chunk sets, the MCP surface, the web UI
and the container all exist and are covered by tests.

What that does not yet mean: nobody has run it but its author. The defaults are
[measured](docs/benchmarks.md) rather than guessed — 81 retrieval configurations over a
document corpus and again over a code corpus — and the extractors have been run over a real
shelf of 95 books, which is where most of the recent bug fixes came from. Both corpora are
this repository's own, and [the roadmap](docs/11-roadmap.md) says what is still owed.

## Quickstart

Sixty seconds, assuming Docker.

```bash
git clone https://github.com/Merp4/dexicon.git && cd dexicon
cp .env.example .env
docker compose up -d
```

The first start pulls an embedding model (a few hundred MB), so give it a minute. Then
take the bootstrap token it printed once:

```bash
docker compose logs dexicon | grep bootstrap
```

Open <http://127.0.0.1:8477>, paste the token, and point a corpus at something. Anything
under `./workspaces` is visible to the indexer and mounted **read-only** — Dexicon reads
your source, and is structurally incapable of writing to it.

To connect an agent, issue a token under **Access** and run the command it gives you.
[docs/12](docs/12-clients.md) has the per-client setup for Claude Code, Cursor, VS Code,
Windsurf, Cline, Claude Desktop and Zed.

```
search_index("how does promotion work")   -> 04-ingestion.md:228-265, and nine more
get_context("docs", "04-ingestion.md", 246)  -> the passage around it, with line numbers
```

Nothing leaves your machine: the embedder is a local Ollama by default, and the index is
a local Qdrant. Hosted embedding providers are opt-in, and say so on the screen where you
choose them.

## Documents

| Doc | What it covers |
|---|---|
| [01 — Overview](docs/01-overview.md) | Problem, scope, explicit non-goals, who it is for |
| [02 — Architecture](docs/02-architecture.md) | Container topology, processes, request and data flows |
| [03 — Data model](docs/03-data-model.md) | Qdrant collections and payloads, SQLite catalog, identifiers |
| [04 — Ingestion](docs/04-ingestion.md) | Sources, extraction, chunking, embedding, incremental reindex |
| [05 — Search](docs/05-search.md) | Hybrid dense + sparse retrieval, fusion, result contract |
| [06 — MCP surface](docs/06-mcp-surface.md) | Protocol version, transport, the tools, the `dexicon://` resources, wire examples |
| [07 — Tenancy & auth](docs/07-tenancy-auth.md) | Tenant header, tokens, corpus visibility, enforcement |
| [08 — UI](docs/08-ui.md) | Screens, interactions, live progress |
| [09 — Deployment](docs/09-deployment.md) | Compose topology, configuration, volumes, GPU, healthchecks |
| [10 — Security & secrets](docs/10-security-secrets.md) | Secret handling from commit one, container hardening, CI |
| [11 — Roadmap](docs/11-roadmap.md) | Milestones M0–M5 with definitions of done |
| [Decisions](docs/decisions.md) | Every load-bearing choice, its rationale, and what was rejected |
| [Benchmarks](docs/benchmarks.md) | 81 retrieval configurations swept, and which defaults that earns |
| [12 — Connecting an agent](docs/12-clients.md) | Per-client MCP setup: Claude Code, Cursor, VS Code, Windsurf, Cline, Claude Desktop, Zed |
| [Troubleshooting](docs/troubleshooting.md) | The failures that actually happen in the first hour |
| [Changelog](CHANGELOG.md) | What changed per release, and what an upgrade needs |

## Contributing

[CONTRIBUTING.md](CONTRIBUTING.md) covers getting it running, what a good change looks like,
and the handful of things this project will push back on. Security problems go through
[SECURITY.md](SECURITY.md), not the issue tracker.

## Provenance

Dexicon carves a subset out of McpToolbox, a private sibling project: the workspace indexer,
the Qdrant repository, the language-aware code chunker, the document loaders, and the
tenancy/auth ADRs. McpToolbox has grown into a full agent platform; Dexicon keeps only the
indexing and search parts, aimed at local agentic development, and re-specifies them against
current MCP, Qdrant, and .NET releases. See [Decisions](docs/decisions.md) for what was
carried over, what was changed, and why.

## Licence

[Apache-2.0](LICENSE). Copyright 2026 Martyn Mcvay — see [NOTICE](NOTICE).
Chosen for the express patent grant and the trademark clause; the reasoning and the
rejected alternative are in [Decisions, D-14](docs/decisions.md#d-14-licence).
