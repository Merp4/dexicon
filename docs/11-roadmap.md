# 11 — Roadmap

Six milestones. Each has a definition of done that is observable by running something, not
by reading the diff. A milestone is not done because its tests pass; it is done because the
thing works when you use it.

---

## M0 — Spike: prove the unknowns ✅ **COMPLETE (2026-09-16)**

**Outcome: all four assumptions hold. Three documents were corrected, and one real defect
was caught before it could ship.**

| # | Verdict | What was actually measured |
|---|---|---|
| 1 | ✅ **Confirmed** | `Qdrant.Client` 1.19.0 exposes fusion as a first-class `Query` (`Fusion.Rrf`) with `PrefetchQuery` — one call, server-side. The `{"fusion":"rrf"}` vs `{"rrf":{}}` ambiguity never reaches our code. Qdrant uses **RRF k=2**; every returned score matched `1/(2+r₀)+1/(2+r₁)` to 1e-4. [05](05-search.md) updated. |
| 2 | ✅ **Confirmed** | Sparse `modifier: idf` ranks correctly from client-supplied term frequencies. A rare term isolated its one document at score 13.39. **No corpus statistics need maintaining in Dexicon** — the expensive fallback in D-07 is not needed. |
| 3 | ⚠️ **Confirmed with a correction** | Stateless streamable HTTP works; no `Mcp-Session-Id` is ever emitted; static bearer auth rejects with 401 ahead of the handler; **Claude Code 2.1.248 connects and reports `✔ Connected`**. But `initialize` **rejects `2026-07-28`** — SDK 2.2.0 negotiates up to `2025-11-25`, because 2026-07-28 removed the handshake and its clients do not call `initialize` at all. Handshake-free `tools/list` and `tools/call` both verified working cold. [06](06-mcp-surface.md) rewritten. |
| 4 | ✅ **Confirmed** | Collection reports `m=0, payload_m=16` server-side. At **50,007 points across 20 corpora**, filtered 1.5 ms vs unfiltered 3.1 ms — **2.0× slower unfiltered**. Isolation verified: a query embedded from another corpus's most distinctive content returned zero of its points. [03](03-data-model.md) updated with the numbers and the caveat that 2× is a deterrent, not a safety mechanism. |

**Defect caught:** `QDRANT__SERVICE__API_KEY` set to an empty string does not disable
Qdrant auth — it **enables** it with an unmatchable key, 401-ing everything including the
dashboard. `${QDRANT_API_KEY:-}` with a blank `.env` reproduced it on the first run. Fixed
with a YAML anchor carrying a non-empty default, shared by server and client so they cannot
drift; `.env.example` now ships the line commented out rather than blank.
See [09](09-deployment.md).

**Method note.** The first version of assumption 1 asserted that a specific document should
rank first, and "failed" when a different — better — document won. That was a bad oracle:
it tested retrieval quality, which is M3's job, instead of testing the capability. Rewritten
to assert the RRF formula itself. Likewise the first latency test ran against seven points,
where brute force beats any index and the result was meaningless; rerun at 50k.

---

<details>
<summary>The original assumptions, as written before the spike ran</summary>

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

</details>

---

## M1 — Walking skeleton ✅ **COMPLETE (2026-09-16)**

**Done when:** *open the file at the line and find the matched text there.* Verified: a
corpus over `docs/` indexed 12 files into 148 chunks, and a hybrid search for
*"how does tenant isolation get enforced"* returned `07-tenancy-auth.md:22-40` — which is
exactly where `### Tenant` sits in the file.

Two defects were found by running it rather than by reading it, and both are the kind that
only surface in use:

- **`/healthz` was unreachable.** The auth middleware prefix-matched the whole `/healthz`
  family as anonymous, so the detailed endpoint skipped the middleware, arrived with no
  principal, and then failed its own scope check. Only the two probes are anonymous now.
- **Progress sat at `done=0` for 24 seconds** on a 12-file corpus, because it flushed every
  25 files. Indistinguishable from a hung job.

<details>
<summary>The original M1 scope</summary>

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

</details>

---

## M2 — The product — **largely complete (2026-09-16)**

| Area | State |
|---|---|
| **MCP** | ✅ All five tools live. `tools/list` and `tools/call` verified working with no handshake; Claude Code 2.1.248 connected and searched. |
| **Tenancy** | ✅ Tokens, scopes, scope resolution, corpus visibility, all three enforcement layers — and the isolation test, shipped in the same commit as the enforcement. |
| **Hybrid search** | ✅ Server-side RRF, sparse encoding with identifier splitting, filters, audible degradation. |
| **Ingestion** | ✅ Language-aware chunking, PDF/DOCX/PPTX/EPUB/HTML extraction with page provenance, incremental refresh, per-file status, backoff. ✅ **Uploads**, with a content-addressed blob store and cached extraction — so one document can be attached to several corpora and chunked differently in each. |
| **Documents** | ✅ Library view, drag-and-drop upload, attach-to-another-corpus, extracted-text inspection, per-corpus chunking editor. |
| **Jobs** | ✅ Queue, phases, SSE progress, `degraded` as a distinct state, orphan reconciliation on restart. |
| **UI** | ✅ Search, Corpora, Documents, Jobs, Models, Access, Settings. |
| **Chunk sets** | ✅ A corpus carries several chunkings over the same documents, addressed as `corpus:set`. Changing embedding model is add-set → backfill → promote, so search never sees a partial index ([D-21](decisions.md)). |
| **Providers** | ✅ Ollama, OpenAI and Azure OpenAI through `IEmbeddingGenerator`, model chosen per call. Model list/pull/delete, and a probe that measures a model's real input limit without indexing anything ([D-24](decisions.md)). |
| **Tests** | ✅ 163 passing, including guards for the "configured but unread" defect class and for chunk-then-stitch round-tripping. |
| **CI** | ✅ Build, test, type-check, gitleaks over full history, vulnerable-dependency checks, and an image build that starts the container. |

**Since closed:** `get_context` de-overlapping is now tested, including a chunk-then-stitch
round-trip property, and verified against this repository's own docs — twelve files
reconstruct byte-identically. The MCP `dexicon://` resources are implemented. `get_context`
and the file resource both read by FILTER rather than by search, after an earlier version
let relevance decide which parts of a file came back.

**Still not done, named rather than glossed:** uploaded documents are not exposed to MCP as
a distinct concept, so an agent sees them as ordinary files in a corpus — arguably correct,
but still an assumption nobody has tested.

**Defects this milestone found by running it, all invisible to a reader:**

- **`chunk_size` did nothing.** The chunker split at every boundary, so two corpora
  configured 768 and 256 tokens produced byte-identical output at a 252-character mean.
  Every prose corpus indexed before the fix was affected. See
  [04](04-ingestion.md#size-decides-when-to-split-a-boundary-decides-where).
- **`MaxConcurrency` did nothing.** Configured, documented and passed by compose, read by
  nothing — embedding batches ran strictly sequentially. The generalised guard that now
  catches this class immediately found a second instance (`Bootstrap.Token`), and later a
  third when `OllamaOptions.Timeout` was orphaned by a refactor.
- **An EPUB lost ~95% of its content and reported success.** The extractor emitted one line
  per chapter, the chunker splits on lines, so a 578,000-character book became 18 chunks of
  ~32,000 characters each — every one silently truncated by the embedding model. Nothing in
  the system could detect it, which is why there is now a probe that measures a model's real
  limit ([D-24](decisions.md)).
- **The embedding model was ignored.** `EmbedAsync` read the globally configured model
  rather than the one its caller asked for, so a chunk set pinned to another model would
  have filled its collection with the wrong vectors. No error; just wrong results.
- **Jobs were ordered by a nullable column.** A job that failed *before* starting has a null
  `StartedUtc`, so dead failures sat permanently above the running job and anything reading
  the first entry got a stale answer with complete confidence.

**Still open from the original definition of done:** *a second person clones, runs
`docker compose up`, indexes their own repository, connects their agent, and uses it
without asking a question.* That has not been attempted, so M2 is not closed.

<details>
<summary>The original M2 scope</summary>

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

</details>
---

## M3 — Make the defaults earned (1 week) — **started**

Every number in [04](04-ingestion.md) is currently a reasonable guess. This milestone
replaces the ones that matter with measurements.

**Done so far.** `scripts/retrieval-bench.py` runs a query set against two chunk sets and
reports where the expected file ranked — a comparison chunk sets make honest, because the
two can hold identical chunking over identical documents with one variable changed. A model
probe measures each model's real input ceiling rather than trusting a documented one.

Two runs, twelve queries, recorded in [decisions.md](decisions.md): the first put
`nomic-embed-text` clearly ahead, and fixing one confound — Dexicon was sending raw text to
models that expect task framing — moved `embeddinggemma` from 0.632 to 0.799 MRR and
flipped the ranking. **A result that reverses when one variable is corrected is the
strongest evidence yet that twelve hand-written queries cannot settle this**, which is
precisely what the full milestone is for.

- Build a fixed evaluation set: 40–60 `(query, expected file)` pairs over a real repository
  and a real document set, committed to the repo.
- Score recall@10 and MRR across: `nomic-embed-text` / `embeddinggemma` /
  `qwen3-embedding:0.6b`; chunk sizes 512 / 768 / 1024; boundary modes `none` /
  `blank-line` / `language-aware`; modes `semantic` / `keyword` / `hybrid`.
- Publish the table in the repo. Change the defaults if the evidence says so — and if it
  does not, say that too.

**Done when:** `docs/benchmarks.md` exists with reproducible numbers, the defaults cite it,
and anyone proposing a reranker has a baseline to beat.

**Also open here:** the probe measures ~1,962 tokens as the safe ceiling for both local
models while the default chunk size is 768. Raising it is a retrieval-quality decision, not
a safety one, and belongs in this milestone's sweep rather than as a guess.

---

## M4 — Open-source ready — **partly done**

- ✅ `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, `SECURITY.md`, issue and PR templates.
- ✅ `docs/troubleshooting.md` covering the real first-hour failures.
- ✅ Full-history secret scan in CI, with rules for the `dex_` token format and provider
  API keys. Dependency vulnerability checks for NuGet and npm.
- ✅ A restore rehearsal — and it was *run*, not just written: volumes destroyed, restored
  from the tarballs, catalogue intact and **search returning results** afterwards. Liveness
  alone would have passed a broken restore, because a catalogue with no vectors comes up
  perfectly healthy and answers every query with nothing.
- ⬜ `README` screenshot and a 60-second quickstart.
- ⬜ Multi-arch images published to GHCR on tag, with an SBOM and digest-pinned bases.
- ⬜ Licence review of the dependency tree.

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
| ~~Additional embedding providers~~ | **Done.** OpenAI and Azure OpenAI ship; adding another is a registration. |
| Persisted audit table | A deployment that needs an audit trail outliving container logs. |

---

## Sequencing notes

- **Infrastructure before features.** CI, gitleaks, and `.env.example` land in M1 before any
  feature. Retrofitting secret hygiene onto a public history is not possible — which is why
  `.gitignore` was commit one. The licence binds at **first public push**, not at the first
  commit; it is already in the tree (Apache-2.0, [D-14](decisions.md#d-14-licence)).
- **The isolation test is not a QA task.** It ships with the enforcement code in M2 or the
  enforcement is not done.
- **M3 is not optional polish.** Shipping guessed defaults and calling them tuned is the
  kind of thing that reads as finished and is not.
