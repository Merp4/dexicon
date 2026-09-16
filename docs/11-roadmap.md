# 11 — Roadmap

Six milestones. Each has a definition of done that is observable by running something, not
by reading the diff. A milestone is not done because its tests pass; it is done because the
thing works when you use it.

---

## M0 — Spike: prove the unknowns (2–3 days)

Four assumptions this design rests on that have not been verified against running software.
If any is false, the design changes, and better now than in M2.

| # | Assumption | How it is checked | If false |
|---|---|---|---|
| 1 | `Qdrant.Client` 1.19.0 exposes the Query API with `prefetch` + RRF fusion from .NET | Console app: create a collection with dense + sparse vectors, upsert 100 points, run a fused query. **Also pin down the exact fusion syntax** — [05](05-search.md) writes `"query": {"fusion": "rrf"}`, but Qdrant's own docs show `{"rrf": {}}` in at least one example, and the spec should carry whichever the client actually emits | Fall back to two queries and client-side RRF in `HybridSearch`; [05](05-search.md) changes, nothing else does |
| 2 | Sparse vectors with `modifier: idf` work as documented, with client-supplied term frequencies | Same app: assert BM25-like ordering on a known corpus | Compute IDF in-process and maintain corpus statistics — materially more work, so worth knowing early |
| 3 | `ModelContextProtocol.AspNetCore` 2.2.0 serves 2026-07-28 statelessly and Claude Code connects to it with a static bearer header | Minimal server with one echo tool; `claude mcp add`; `/mcp` shows connected | Pin to `2025-06-18` semantics and revisit |
| 4 | `hnsw_config.m = 0` + `payload_m = 16` behaves as expected with `is_tenant` on `corpus_id` | Two corpora, 50k points, compare filtered and unfiltered query latency | Use per-tenant collections; [03](03-data-model.md) changes |

**Done when:** a throwaway repository demonstrates all four, and each finding is written
back into the affected document. The spike code is deleted.

---

## M1 — Walking skeleton (1 week)

The narrowest path that is genuinely end to end.

- Solution layout, `Directory.Build.props`, `Directory.Packages.props`, `.gitignore`,
  `.gitattributes`, `.env.example`, `LICENSE`, `SECURITY.md`, CI with build + test +
  gitleaks — **all before the first feature**.
- ASP.NET Core host; SQLite catalogue with migrations; Qdrant collection bootstrap.
- Workspace source: walk a mounted folder, gitignore filter, hash triage, line-based
  chunking (no language awareness yet), Ollama embeddings, Qdrant upsert.
- `POST /api/search` — dense only.
- Compose with Qdrant + Ollama, model auto-pulled.
- No UI, no MCP, no tenancy beyond a hard-coded `default`.

**Done when:** `docker compose up`, then `curl` a search against an indexed repository and
get chunks back with correct `file:line`. Verified by opening the file at the line and
finding the matched text there.

---

## M2 — The product (2–3 weeks)

- **MCP**: all five tools, resources, streamable HTTP, error contracts ([06](06-mcp-surface.md)).
  Verified by connecting Claude Code and using it for real work for a day.
- **Tenancy**: tenants, tokens, scope resolution, corpus visibility, all three enforcement
  layers, and the isolation test — the test lands in the same commit as the enforcement
  ([07](07-tenancy-auth.md)).
- **Hybrid search**: sparse encoding with identifier splitting, RRF fusion, filters,
  degradation behaviour ([05](05-search.md)).
- **Ingestion**: language-aware chunking, all document loaders, uploads, blob store,
  incremental refresh, per-file status, backoff ([04](04-ingestion.md)).
- **Jobs**: queue, phases, SSE progress, `degraded` as a distinct state.
- **UI**: all six views ([08](08-ui.md)).

**Done when:** a second person clones, runs `docker compose up`, indexes their own
repository through the UI, connects their agent, and uses it — **without asking a
question.** Every question asked is a defect, logged and fixed before the milestone closes.

---

## M3 — Make the defaults earned (1 week)

Every number in [04](04-ingestion.md) is currently a reasonable guess. This milestone
replaces the ones that matter with measurements.

- Build a fixed evaluation set: 40–60 `(query, expected file)` pairs over a real repository
  and a real document set, committed to the repo.
- Score recall@10 and MRR across: `nomic-embed-text` / `embeddinggemma` /
  `qwen3-embedding:0.6b`; chunk sizes 512 / 768 / 1024; boundary modes `none` /
  `blank-line` / `language-aware`; modes `semantic` / `keyword` / `hybrid`.
- Publish the table in the repo. Change the defaults if the evidence says so — and if it
  does not, say that too.

**Done when:** `docs/benchmarks.md` exists with reproducible numbers, the defaults cite it,
and anyone proposing a reranker has a baseline to beat.

---

## M4 — Open-source ready (3–4 days)

- `README` with a screenshot, a 60-second quickstart, and an honest capability list —
  including what it does not do.
- `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, issue and PR templates.
- Multi-arch images published to GHCR on tag, with an SBOM and digest-pinned bases.
- `docs/troubleshooting.md` covering the real first-hour failures: scanned PDFs, Ollama not
  resident, mount path not visible in the picker, agent connected but seeing no corpora.
- Full-history secret scan, dependency and licence review.
- A restore rehearsal: back up, destroy the volumes, restore, confirm search still works.

**Done when:** the repository is public and the quickstart has been followed on a clean
machine by someone who did not write it.

---

## M5 — Earned extras (unscheduled)

Nothing here is started until someone asks for it with a concrete case. Listed so the
answer to "what about…" is "yes, here, later" rather than an argument.

| Candidate | Trigger |
|---|---|
| Git history indexing (commits, messages, diffs) | Asked for more than once. McpToolbox has a working implementation to carry over. |
| Scalar quantization | An index large enough that memory is the constraint. |
| Reranking | A measured recall gap M3 shows hybrid cannot close. |
| Structure-aware chunking for JSON/YAML | Config-heavy repos returning poor results. |
| Watch mode / push-based reindex | Polling proving too slow in practice, with a number. |
| Additional embedding providers | Someone needing a non-Ollama local runtime. |
| Persisted audit table | A deployment that needs an audit trail outliving container logs. |

---

## Sequencing notes

- **Infrastructure before features.** CI, gitleaks, `.env.example`, and the licence land in
  M1 before any feature. Retrofitting secret hygiene onto a public history is not possible.
- **The isolation test is not a QA task.** It ships with the enforcement code in M2 or the
  enforcement is not done.
- **M3 is not optional polish.** Shipping guessed defaults and calling them tuned is the
  kind of thing that reads as finished and is not.
