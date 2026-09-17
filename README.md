# Dexicon

**Semantic search over your own files, for coding agents. One container, on your machine.**

[![Release](https://img.shields.io/github/v/tag/Merp4/dexicon?label=release&sort=semver)](CHANGELOG.md)
[![Licence](https://img.shields.io/badge/licence-Apache--2.0-blue)](LICENSE)
[![CI](https://github.com/Merp4/dexicon/actions/workflows/ci.yml/badge.svg)](https://github.com/Merp4/dexicon/actions/workflows/ci.yml)

Point it at a folder. It indexes what is there — source trees, PDFs, EPUBs, Word, docs —
and answers questions about it over [MCP](https://modelcontextprotocol.io), so Claude Code
and anything else that speaks MCP can search your material instead of guessing at it.

```
search_index("how does promotion work")      → 04-ingestion.md:228-265, and nine more
get_context("docs", "04-ingestion.md", 246)  → the passage around it, with line numbers
```

Nothing leaves your machine: the embedder is a local Ollama, the index is a local Qdrant,
and hosted providers are opt-in and say so on the screen where you choose them.

![The Dexicon search screen: a natural-language question answered from the project's own documentation, each result cited by file and line range.](docs/images/search.png)

---

## Quickstart

Sixty seconds, assuming Docker.

```bash
git clone https://github.com/Merp4/dexicon.git && cd dexicon
cp .env.example .env
docker compose up -d
```

The first start pulls an embedding model (a few hundred MB), so give it a minute. Then take
the bootstrap token, which is printed once:

```bash
docker compose logs dexicon | grep bootstrap
```

Open <http://127.0.0.1:8477>, paste the token, and point a corpus at something. Anything
under `./workspaces` is visible to the indexer and mounted **read-only** — Dexicon reads
your files and is structurally incapable of writing to them.

Then connect an agent. Issue a token under **Access**, and:

```bash
claude mcp add --transport http dexicon http://localhost:8477/mcp \
  --header "Authorization: Bearer dex_…"
```

[docs/12](docs/12-clients.md) has the same thing for Cursor, VS Code, Windsurf, Cline,
Claude Desktop and Zed, and `./scripts/install-mcp.ps1` writes any of them for you.

## What it does

- **Hybrid retrieval.** Dense and sparse in a single Qdrant query with server-side fusion,
  not two searches merged in the client. Keyword-only still works when embeddings are down,
  and the response says it degraded rather than quietly returning worse answers.
- **Documents, not just code.** PDF, EPUB, DOCX, PPTX and HTML are extracted with their
  natural unit intact, so a result cites `#page=201` or `#chapter=8` — somewhere a reader
  can actually look — rather than a line number into extracted text.
- **Chunk sets.** One corpus, several chunkings, addressed as `corpus:set`. Changing the
  embedding model is add-set → backfill → promote, so search never sees a half-built index.
- **Incremental refresh.** Content-hashed, so re-indexing touches only what changed. A
  chunker or extractor change bumps a version and re-does exactly the work it invalidated.
- **Multi-tenant from the first commit.** Tokens, scopes, per-corpus visibility, enforced at
  three layers — with the isolation test in the same commit as the enforcement.
- **It tells you when it is wrong.** A half-built index says so in the search response, a
  skipped file records why it was skipped, and a corpus with nothing in it is reported as
  unsearchable rather than as an empty result.

## Status

**Working, and young.** The current release is [`0.2.1`](CHANGELOG.md), published as
`ghcr.io/merp4/dexicon`. Indexing, hybrid search, chunk sets, the MCP surface, the web UI
and the container all exist and are covered by tests.

What that does not yet mean: **nobody has run it but its author.** The defaults are
[measured](docs/benchmarks.md) rather than guessed — 81 retrieval configurations over a
document corpus and again over a code corpus — and the extractors have been run over a real
shelf of 95 books, which is where most of the recent bug fixes came from. But both corpora
are this repository's own, and [the roadmap](docs/11-roadmap.md) says what is still owed.

## How it fits together

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

One container for the UI, API, MCP server and indexer; Qdrant and Ollama alongside it.
[02](docs/02-architecture.md) explains why that is one process and not four.

## Documentation

New here? [01 — Overview](docs/01-overview.md) for the problem and the explicit non-goals,
or [12 — Connecting an agent](docs/12-clients.md) to wire it up.

| Doc | What it covers |
|---|---|
| [01 — Overview](docs/01-overview.md) | Problem, scope, explicit non-goals, who it is for |
| [02 — Architecture](docs/02-architecture.md) | Container topology, processes, request and data flows |
| [03 — Data model](docs/03-data-model.md) | Qdrant collections and payloads, SQLite catalogue, identifiers |
| [04 — Ingestion](docs/04-ingestion.md) | Sources, extraction, chunking, embedding, incremental reindex |
| [05 — Search](docs/05-search.md) | Hybrid dense + sparse retrieval, fusion, result contract |
| [06 — MCP surface](docs/06-mcp-surface.md) | Protocol, transport, the tools, `dexicon://` resources, wire examples |
| [07 — Tenancy & auth](docs/07-tenancy-auth.md) | Tenant header, tokens, corpus visibility, enforcement |
| [08 — UI](docs/08-ui.md) | Screens, interactions, live progress |
| [09 — Deployment](docs/09-deployment.md) | Compose topology, configuration, volumes, GPU, healthchecks |
| [10 — Security & secrets](docs/10-security-secrets.md) | Secret handling, container hardening, CI, licence review |
| [11 — Roadmap](docs/11-roadmap.md) | Milestones M0–M5, with definitions of done |
| [12 — Connecting an agent](docs/12-clients.md) | Per-client MCP setup |
| [Decisions](docs/decisions.md) | Every load-bearing choice, its rationale, and what was rejected |
| [Benchmarks](docs/benchmarks.md) | 81 retrieval configurations per corpus, and which defaults that earns |
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
current MCP, Qdrant and .NET releases. [Decisions](docs/decisions.md) records what was
carried over, what changed, and why.

## Licence

[Apache-2.0](LICENSE). Copyright 2026 Martyn Mcvay — see [NOTICE](NOTICE). Chosen for the
express patent grant and the trademark clause; the reasoning and the rejected alternative
are in [Decisions, D-14](docs/decisions.md#d-14-licence).

Every dependency is permissively licensed, checked across the whole transitive tree — see
[the licence review](docs/10-security-secrets.md#licence-review-of-the-dependency-tree),
and `scripts/licence-review.py` to re-run it.
