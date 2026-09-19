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
Qdrant auth. It **enables** it with an unmatchable key, returning 401 for everything including the
dashboard. `${QDRANT_API_KEY:-}` with a blank `.env` reproduced it on the first run. Fixed
with a YAML anchor carrying a non-empty default, shared by server and client so they cannot
drift; `.env.example` now ships the line commented out rather than blank.
See [09](09-deployment.md).

**Method note.** The first version of assumption 1 asserted that a specific document should
rank first, and reported failure when a different and better document won. That was a poor oracle:
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
*"how does tenant isolation get enforced"* returned `07-tenancy-auth.md:22-40`, the
location of `### Tenant` in the file as it stood then. Both the file and the heading were
renamed when [D-28](decisions.md#d-28-an-admin-password-and-scoped-api-keys) removed
tenancy; the measurement is left as it was taken.

Two defects were found by running it rather than by reading it, and both are the kind that
only surface in use:

- **`/healthz` was unreachable.** The auth middleware prefix-matched the entire `/healthz`
  family as anonymous, so the detailed endpoint skipped the middleware, arrived with no
  principal, and then failed its own scope check. Only the two probes are anonymous now.
- **Progress sat at `done=0` for 24 seconds** on a 12-file corpus, because it flushed every
  25 files. Indistinguishable from a hung job.

<details>
<summary>The original M1 scope</summary>

The narrowest path that is genuinely end to end.

- Solution layout, `Directory.Build.props`, `Directory.Packages.props`, `.gitignore`,
  `.gitattributes`, `.env.example`, `LICENSE`, `SECURITY.md`, CI with build + test +
  gitleaks, **all before the first feature**.
- ASP.NET Core host; SQLite catalogue with migrations; Qdrant collection bootstrap.
- Workspace source: walk a mounted folder, gitignore filter, hash triage, line-based
  chunking (no language awareness yet), Ollama embeddings, Qdrant upsert.
- `POST /api/search`, dense only.
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
| **Tests** | ✅ 307 server + 114 UI passing, including guards for the "configured but unread" defect class and for chunk-then-stitch round-tripping. Every fix is mutation-verified: break it, watch the named test go red, restore. |
| **CI** | ✅ Build, test, type-check, gitleaks over full history, vulnerable-dependency checks, and an image build that starts the container. |

**Since closed:** `get_context` de-overlapping is now tested, including a chunk-then-stitch
round-trip property, and verified against this repository's own docs: twelve files
reconstruct byte-identically. The MCP `dexicon://` resources are implemented. `get_context`
and the file resource both read by *filter* rather than by search, after an earlier version
let relevance decide which parts of a file came back.

**Still not done:** uploaded documents are not exposed to MCP as
a distinct concept, so an agent sees them as ordinary files in a corpus. That may be
correct, but it is an untested assumption.

**Defects found by running it against real data, all invisible to a reader.** The later
ones came from pointing the indexer at a real shelf of 95 books rather than at fixtures:

- **`chunk_size` did nothing.** The chunker split at every boundary, so two corpora
  configured 768 and 256 tokens produced byte-identical output at a 252-character mean.
  Every prose corpus indexed before the fix was affected. See
  [04](04-ingestion.md#size-decides-when-to-split-a-boundary-decides-where).
- **`MaxConcurrency` did nothing.** Configured, documented and passed by compose, read by
  nothing, so embedding batches ran strictly sequentially. The generalised guard that now
  catches this class immediately found a second instance (`Bootstrap.Token`), and later a
  third when `OllamaOptions.Timeout` was orphaned by a refactor.
- **An EPUB lost ~95% of its content and reported success.** The extractor emitted one line
  per chapter, the chunker splits on lines, so a 578,000-character book became 18 chunks of
  ~32,000 characters each, every one truncated by the embedding model. Nothing in the
  system could detect it, hence the probe that measures a model's real limit
  ([D-24](decisions.md)).
- **The embedding model was ignored.** `EmbedAsync` read the globally configured model
  rather than the one its caller asked for, so a chunk set pinned to another model would
  have filled its collection with the wrong vectors. No error; just wrong results.
- **Jobs were ordered by a nullable column.** A job that failed *before* starting has a null
  `StartedUtc`, so dead failures sat permanently above the running job and anything reading
  the first entry got a stale answer.
- **Every O'Reilly EPUB was unreadable, and blamed DRM.** Their toolchain lists the cover
  image twice in the manifest; the spec forbids it, no reader cares, the strict parser
  refused the book. Six of nineteen books on the first shelf, reported to the user as
  "DRM-protected books cannot be read", a plausible message describing a problem they did
  not have. An EPUB is a zip of XHTML, so an unparseable manifest is now salvaged from the
  archive, and DRM is asserted from `META-INF/encryption.xml` rather than guessed.
- **A file path did not name a file.** `file_path` is relative to its *source* root, so two
  sources of one corpus holding "Logic For Dummies.pdf" are two books with one path. The
  delete filter removed both; `get_context` interleaved them into one passage with line
  numbers on it; and the point id, derived from (set, path, index), made the second
  source's upsert overwrite the first. `source_id` had been in every point's
  payload since chunk sets landed and was never read back.
- **A running index job absorbed work it had already passed.** Adding nine folders to a
  corpus mid-index indexed 46 of 96 files and reported the corpus `ready`, with no job
  pending: deduplication handed each request the job already running, which had taken its
  list of sources when it started. Coalescing now happens only onto a *queued* job.
- **Chunking collapsed on prose interleaved with code.** A blank line early, then eleven
  thousand characters of listing with none, made the splitter back up to that early
  boundary, emit a ~400-character chunk, then advance one line and do it again because
  that chunk was smaller than the overlap. One book: 1,051 chunks averaging 388
  characters where 73 of ~8,000 were intended. Its EPUB produced 61 from the same text,
  which is how it was caught.
- **Arctic Embed v2 was sent unframed queries.** Listed as needing no task prefix, on the
  belief that Arctic trains without one. Its model card specifies `query_prefix = 'query: '`.
  Nothing failed; recall was worse. This is the failure the framing table exists to
  prevent, occurring within the table itself.

**Still open from the original definition of done:** *a second person clones, runs
`docker compose up`, indexes their own repository, connects their agent, and uses it
without asking a question.* That has not been attempted, so M2 is not closed.

<details>
<summary>The original M2 scope</summary>

- **MCP**: all five tools, resources, streamable HTTP, error contracts ([06](06-mcp-surface.md)).
  Verified by connecting Claude Code and using it for real work for a day.
- **Tenancy**: tenants, tokens, scope resolution, corpus visibility, all three enforcement
  layers, and the isolation test, which lands in the same commit as the enforcement
  (07). Tenancy was removed in D-28; the three enforcement layers and their test
  survived it and now hold down key scoping ([07](07-auth.md)).
- **Hybrid search**: sparse encoding with identifier splitting, RRF fusion, filters,
  degradation behaviour ([05](05-search.md)).
- **Ingestion**: language-aware chunking, all document loaders, uploads, blob store,
  incremental refresh, per-file status, backoff ([04](04-ingestion.md)).
- **Jobs**: queue, phases, SSE progress, `degraded` as a distinct state.
- **UI**: all six views ([08](08-ui.md)).

**Done when:** a second person clones, runs `docker compose up`, indexes their own
repository through the UI, connects their agent, and uses it **without asking a
question.** Every question asked is a defect, logged and fixed before the milestone closes.

</details>
---

## M3 — Make the defaults earned ✅ **COMPLETE (2026-09-17)**

Every number in [04](04-ingestion.md) started as a reasonable guess. This milestone
replaced the ones that matter with measurements.

`scripts/retrieval-bench.py` runs a query set against two chunk sets and reports where the
expected file ranked. Chunk sets make the comparison fair, because two sets can hold
identical chunking over identical documents with one variable changed. A model probe measures each
model's real input ceiling rather than trusting a documented one.

An early twelve-query run, recorded in [decisions.md](decisions.md), put `nomic-embed-text`
ahead. Correcting one confound, in which Dexicon sent raw text to models that expect task
framing, moved `embeddinggemma` from 0.632 to 0.799 MRR and reversed the ranking, which is
why the milestone needed a committed evaluation set rather than twelve hand-written queries.

**What was done.** Two committed evaluation sets, 55 pairs over `docs/` and 52 over
`src/`, swept across three models × three chunk sizes × three boundary modes × three search
modes. 81 configurations per corpus, 162 in total, in
[benchmarks.md](benchmarks.md) with the raw per-configuration numbers beside the script
that produced them.

**What it changed.** The default model moved to `embeddinggemma`, which led on *both*
corpora: by 0.025 mean MRR on documents and 0.075 on code, the widest margin any single
variable produced. That took documents from 30th of 81 to 5th and code from 44th to 36th. `hybrid`
was confirmed as the default it already was. `language-aware` lost on both corpora and was
*kept*, because the spread across boundary modes on code is 0.009 and changing it costs a
reindex to buy a rounding error.

**What it closed.** The probe measured ~1,962 tokens as the safe ceiling while the default
chunk size was 768. That is no longer a guess in either direction: a new chunk set is sized
from what its model was *measured* to accept. The same measurement is what stops
`mxbai-embed-large`, offered in a dropdown and accepting only 2,816 characters, from
truncating every full-size chunk at the old default.

**Done when:** ✅ `docs/benchmarks.md` exists with reproducible numbers, the defaults cite
it, and anyone proposing a reranker has a baseline to beat.

---

## M4 — Open-source ready — **partly done**

- ✅ `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, `SECURITY.md`, issue and PR templates.
- ✅ `docs/troubleshooting.md` covering the real first-hour failures.
- ✅ Full-history secret scan in CI, with rules for the `dex_` token format and provider
  API keys. Dependency vulnerability checks for NuGet and npm.
- ✅ A restore rehearsal, which was *run* rather than only documented: volumes destroyed,
  restored from the tarballs, catalogue intact and **search returning results** afterwards.
  A liveness check alone would have passed a broken restore, because a catalogue with no
  vectors reports healthy and answers every query with nothing.
- ✅ Multi-arch images (`linux/amd64`, `linux/arm64`) published to GHCR on tag, with an
  SBOM, `provenance: mode=max`, a GitHub attestation, and every base image digest-pinned.
- ✅ A 60-second quickstart in the `README`, and a screenshot generated from this
  repository's own documentation by `scripts/screenshot.mjs`.
- ✅ Licence review of the full transitive dependency tree, NuGet and npm, re-runnable with
  `scripts/licence-review.py` and failing CI on a copyleft dependency.
- ⬜ The repository made public.

**Done when:** the repository is public and the quickstart has been followed on a clean
machine by someone who did not write it.

---

## M5 — Earned extras (unscheduled)

Nothing here is started until someone asks for it with a concrete case. Listed so the
answer to "what about…" is "yes, here, later" rather than an argument.

| Candidate | Trigger |
|---|---|
| Git history indexing (commits, messages, diffs) | Asked for more than once. |
| Scalar quantization | An index large enough that memory is the constraint. |
| Reranking | A measured recall gap M3 shows hybrid cannot close. |
| Structure-aware chunking for JSON/YAML | Config-heavy repos returning poor results. |
| Watch mode / push-based reindex | Polling proving too slow in practice, with a number. |
| ~~Additional embedding providers~~ | **Done.** OpenAI and Azure OpenAI ship; adding another is a registration. |
| Persisted audit table | A deployment that needs an audit trail outliving container logs. |
| ~~Integration OpenAPI document and `POST /api/context`~~ | **Done.** See [D-29](decisions.md#d-29-an-integration-document-and-retrieval-in-one-call) and [13](13-integration.md). |

---

## Sequencing notes

- **Infrastructure before features.** CI, gitleaks, and `.env.example` land in M1 before any
  feature. Secret hygiene cannot be retrofitted onto a public history, which is why
  `.gitignore` was the first commit. The licence binds at **first public push**, not at the first
  commit; it is already in the tree (Apache-2.0, [D-14](decisions.md#d-14-licence)).
- **The isolation test is not a QA task.** It ships with the enforcement code in M2 or the
  enforcement is not done.
- **M3 is not optional polish.** Shipping guessed defaults and calling them tuned is the
  kind of thing that reads as finished and is not.
