# Decisions

Every load-bearing choice, why it was made, and what was rejected. The rejected column is
the useful one: it is what stops the same argument being had again in six months.

Status: **Accepted.** M0 ([roadmap](11-roadmap.md)) ran on 2026-09-16 and confirmed all four
assumptions these decisions rest on. D-06, D-07 and D-12 carry the measured results below.

---

### D-01 Single container

**Decision.** UI, REST API, MCP server, and the indexer run in one process, in one
container. Qdrant and Ollama are separate, as external dependencies.

**Why.** McpToolbox splits the indexer into a sidecar because it runs inside a per-tenant
workspace container with a genuinely different security boundary — untrusted execution on
one side, the platform on the other. Dexicon has no such boundary: it is one operator's
tool on one machine. Splitting would add a control API, a shared token, an internal
network, and a new class of failure, and buy nothing.

**Rejected.** Sidecar indexer (boundary does not exist here); separate UI container (a
static bundle is 200 KB — serving it from the same host costs nothing and removes a CORS
configuration and a reverse proxy); bundling Qdrant and Ollama into the image (breaks the
upgrade path for both, and people already have Ollama running).

**Revisit if.** Indexing load starts affecting search latency measurably. The component
seams in [02](02-architecture.md) are drawn so the indexer can be lifted out without
touching the API.

---

### D-02 .NET 10 LTS

**Decision.** .NET 10 LTS, ASP.NET Core minimal APIs, C#.

**Why.** Every carried-over component — the chunker, the gitignore filter, the Qdrant
repository, the document loaders — is already C#. Rewriting them in Python to follow the
ML ecosystem would be a rewrite of the only part that is already proven. .NET 10 is LTS
until November 2028; .NET 11 ships 2026-11-10 as an STS release with the same end date, so
there is nothing to gain by tracking it.

**Rejected.** Python/FastAPI (better embedding ecosystem, but Dexicon calls Ollama over
HTTP and uses none of it); Node/TypeScript (would unify with the SDK but discards the
carried-over code); .NET 11 (STS, no benefit, ships after work starts).

---

### D-03 SQLite for the catalogue

**Decision.** EF Core + SQLite in WAL mode at `/data/catalog.db` for tenants, corpora,
sources, files, jobs, and tokens. Qdrant holds only chunks and vectors.

**Why.** The control plane needs listing, filtering, joining, counting, and transactional
updates — all of which Qdrant does badly and a relational store does for free. SQLite adds
no container and no configuration. Keeping the two planes strictly separated means the
vector store is fully reconstructible from the catalogue plus the sources.

**Rejected.** Qdrant-only, with catalogue data in payloads (every list becomes a scroll,
every count an aggregation, and there are no transactions); Postgres (a fourth container
for a workload that peaks at thousands of rows); LiteDB or files on disk (no migration
story, no query story).

---

### D-04 Corpus as the Qdrant tenant key

**Decision.** `corpus_id` carries the payload index with `is_tenant: true`. `tenant_id` is
in the payload but is a plain field.

**Why.** A corpus belongs to exactly one tenant and is never split across tenants, so
partitioning by corpus is strictly finer-grained than partitioning by tenant — and it is
what queries actually filter on, because search is scoped to a corpus set
([05](05-search.md)). Co-locating storage by the field the query filters on is the entire
point of `is_tenant`.

**Rejected.** `tenant_id` as the tenant key (coarser, and every query would carry a second
filter on the field that actually selects); a collection per tenant or per corpus (Qdrant
documents this as rarely efficient — per-collection overhead, a 1000-collection ceiling,
and it puts collection lifecycle on the hot path of corpus creation).

**Consequence worth naming.** Authorization is resolved in SQLite ([07](07-tenancy-auth.md))
and enforced as a `corpus_id` filter. The tenant is not part of the Qdrant filter, so a bug
in scope resolution is a leak. That is why three independent guards defend it, one of which
is the storage layout itself.

---

### D-05 One collection per embedding model

**Decision.** Collections are named `dexicon__{model}__{dims}`, shared by all tenants and
all corpora using that model.

**Why.** A collection has one vector size. Encoding model and dimensions in the name makes
a dimension mismatch structurally impossible rather than a runtime check that someone
forgets. Changing a corpus's model becomes an explicit rebuild into a different collection,
which is what it actually is.

**Rejected.** A collection per corpus (loses cross-corpus search in one query, multiplies
collection overhead); a single collection with mixed dimensions (not possible); named
vectors per model within one collection (works, but every point then carries every model's
vector, or sparse point structures with awkward filtering).

---

### D-06 RRF fusion server-side

**Decision.** Hybrid search is one Qdrant Query API call with dense and sparse prefetches
and `fusion: rrf`. No client-side score merging, no weight parameter.

**Why.** Dense cosine and BM25 scores are on incomparable scales, and the weight that
balances them is corpus-dependent and drifts as content changes. McpToolbox carries a
`SemanticWeight` knob defaulted to `0.8` that nobody could set from evidence. RRF reads
rank, not magnitude: no tuning, nothing to mis-set, and one round trip instead of two.

**Rejected.** Client-side weighted fusion (the carried-over approach — a tuning knob with
no way to tune it); DBSF as the default (normalises distributions, which is defensible, but
it is still score-based and per-query sensitive — available as configuration, not default);
dense-only (exact identifiers and error strings are exactly what embeddings are worst at).

**Confirmed by M0** (2026-09-16). `Qdrant.Client` 1.19.0 expresses prefetch + fusion in a
single call, so the fallback to client-side RRF is not needed. Qdrant uses **RRF k=2**;
every score returned matched `1/(2+r₀)+1/(2+r₁)` to 1e-4, which is proof the fusion is
rank-based rather than score-based. Worked example in [05](05-search.md).

---

### D-07 Client-side term frequencies with `modifier: idf`

**Decision.** Sparse vectors are computed in-process — tokenize, split identifiers, drop
stopwords, emit `{term_hash: frequency}` — and the sparse index is declared
`modifier: idf`, so Qdrant applies the IDF component itself.

**Why.** It works on any self-hosted Qdrant with any client, needs no model, needs no
corpus statistics maintained by Dexicon, and costs under a millisecond. Identifier
splitting is what makes it useful on code: a query for "token refresh" has to reach
`TokenService.RefreshAsync`.

**Confirmed by M0** (2026-09-16). `modifier: idf` ranks correctly from client-supplied term
frequencies against a self-hosted Qdrant — a rare term isolated its single document at score
13.39. **Dexicon never has to maintain corpus statistics**, which was the expensive fallback
this decision risked.

**Rejected.** Qdrant's server-side `qdrant/bm25` inference (would remove our tokenizer, but
M0 showed the client-side path already works, so this is now an optimisation rather than a
question); running FastEmbed (a Python dependency for tokenization); no keyword retrieval at
all (dense-only search on code is noticeably worse for exact terms).

---

### D-08 Store chunk content in the payload

**Decision.** The full chunk text is stored in the Qdrant payload and returned by search.

**Why.** A result that only says "this file, these lines" forces the caller to open the
file — two round trips to save a kilobyte, and impossible for uploaded documents where
there is no file to open. Roughly 1.2 KB per chunk at the default size; on a 400k-chunk
index that is around 480 MB, against a dense-vector cost four times larger.

**Rejected.** Pointers only (fails for uploads, doubles round trips); storing a truncated
preview (the caller cannot tell whether truncation lost the answer).

---

### D-09 Polling with content hashes

**Decision.** Reindexing walks the tree on demand or on an interval, comparing SHA-256
content hashes. No filesystem watcher.

**Why.** `FileSystemWatcher` over Docker bind mounts is unreliable — silently so, and worse
on Windows hosts and WSL2, which is the primary environment. Content hashing is the correct
answer regardless, because a watcher tells you a file changed, not whether its content did
(every `git checkout` touches thousands of files whose content is identical). A refresh over
an unchanged tree makes zero embedding calls, which is the property that matters.

**Rejected.** `FileSystemWatcher` (unreliable across the mount, and still needs hashing);
mtime comparison (wrong after checkout, clone, or restore); inotify in the host (outside the
container boundary).

---

### D-10 Static tokens and a tenant header

**Decision.** One credential type: a bearer token bound to a tenant, with `search` /
`ingest` / `admin` scopes. Tenant resolved from the token, or from `X-Dexicon-Tenant` when
the token is bound to several. No inference.

**Why.** It works identically for the SPA, `curl`, and every MCP client — Claude Code's
`--header "Authorization: Bearer …"` is the documented path for a server with a static
token. OIDC would mean an identity provider in the compose file for a tool with three
users.

**Rejected.** OIDC/SSO (disproportionate; the token model is a clean seam if it is ever
needed); no auth on localhost (the MCP endpoint is reachable by anything on the machine,
and tenancy would be decorative); MCP OAuth flows (the spec supports them, but for a
self-hosted local server they add an authorization server for no gain).

**Carried from** McpToolbox ADR-005: target selection is explicit, validated, and
least-privilege; an ambiguous target fails fast rather than being inferred.

---

### D-11 Five MCP tools

**Decision.** `search_index`, `list_corpora`, `get_context`, `index_refresh`,
`index_status`. Nothing else.

**Why.** Every tool definition is context the agent pays for on every turn, and a large
surface measurably degrades smaller models — McpToolbox observed a 12B model exhaust its
generation budget against 35 tool definitions without calling any of them. Five is enough
to find things, understand them, and know whether the index is current.

**Rejected.** Per-format search tools; separate keyword and semantic tools (a `mode`
parameter, not three tools); admin tools over MCP (tenant and token management belongs in
the UI, where a human is present).

---

### D-12 Stateless streamable HTTP, MCP 2026-07-28

**Decision.** `POST /mcp`, streamable HTTP, stateless, protocol revision 2026-07-28, with
negotiation down to 2025-06-18.

**Why.** It is the current revision, all Tier 1 SDKs ship it, and its stateless core — no
handshake, no `Mcp-Session-Id` — matches Dexicon exactly: every search is self-contained
and nothing needs server-to-client calls. The C# SDK already defaults to stateless. Legacy
HTTP+SSE is deprecated in the spec and is not implemented.

**Corrected by M0** (2026-09-16). The decision stands; one premise was wrong. SDK 2.2.0
will not negotiate `2026-07-28` through `initialize` — it tops out at `2025-11-25` — because
**2026-07-28 removed the handshake**, so its clients never call `initialize`. Both
populations are served: handshake clients negotiate to at most 2025-11-25, and 2026-07-28
clients issue self-contained requests that work cold. Verified: stateless confirmed (no
`Mcp-Session-Id` ever emitted), static bearer enforced ahead of the handler, and Claude Code
2.1.248 reports `✔ Connected`. Detail in [06](06-mcp-surface.md).

**Rejected.** stdio (one client per process, no tenancy, no sharing between agents — the
transport Dexicon exists to replace); HTTP+SSE (deprecated); pinning to 2025-06-18 (would
work, but starts the project two revisions behind).

---

### D-13 React SPA, served by the API host

**Decision.** React 19 + Vite + Tailwind v4, built at image build time into `wwwroot`.

**Why.** Static output, no runtime dependency, no CORS, no second container, no reverse
proxy. Typed client generated from the OpenAPI document, so a contract change breaks the
build. It is also the stack McpToolbox's UI uses, so patterns transfer.

**Rejected.** Blazor Server (a stateful circuit for a UI that is mostly forms and a search
box); Blazor WASM (multi-megabyte payload for the same result); server-rendered Razor
(live indexing progress wants a client-side app); a component framework like PrimeReact
("minimal but professional" is better served by Tailwind and a few headless primitives than
by a themed kit).

---

### D-14 Licence

**Decision.** **Apache-2.0.** `LICENSE` holds the canonical text unmodified; copyright sits
in `NOTICE`, which is the split the ASF itself uses and keeps automated licence detection
clean.

**Why, over MIT:**

- **Express patent grant (§3)**, with retaliation termination. Vector search and retrieval
  is a space with real patent activity. MIT is silent on patents, and silence is ambiguity
  nobody wants to test.
- **Trademark clause (§6)** — explicitly withholds trademark rights, so a fork cannot use
  the name established in [D-17](#d-17-name) to imply endorsement. MIT offers nothing here.
- **Contribution terms (§5)** — contributions are under the licence by default, so no CLA
  is needed for basic hygiene.
- **Corporate adoption.** This is a tool people will want to run at work. Apache-2.0 clears
  legal review without a conversation.

**The cost, accepted.** Apache-2.0 is **incompatible with GPLv2** (GPLv3 is fine). Nobody
can vendor Dexicon source into a GPLv2 project. For a self-hosted container application
that is close to hypothetical, and it is the only axis on which MIT wins.

**When MIT would have been right.** If this were a small library meant to be copied into
other codebases. It is an application, so it is not.

**Deadline, corrected.** This was originally recorded as needed *before the first commit*,
alongside the secret-hygiene rules. That conflated two different deadlines. A licence
governs **distribution** — it binds at first publication, not at a local commit, and the
first three commits were made without one with no consequence. Secret hygiene is the rule
that genuinely cannot be retrofitted, because history is what gets scanned, and that one
did land on commit one. The roadmap and open-questions table now say *before first public
push*.

**Compatible with every dependency** in [04](04-ingestion.md#extraction) — PdfPig is
Apache-2.0; Markdig is BSD-2; AngleSharp, VersOne.Epub and DocumentFormat.OpenXml are MIT.

---

### D-15 Read-only workspace mounts

**Decision.** Source trees are bind-mounted read-only at `/workspaces`. A corpus can only
point at a path under that mount.

**Why.** Dexicon reads your code; it must be structurally incapable of writing to it. The
path picker browses the actual mount, so a path that is not mounted cannot be typed. The
constraint is visible rather than a runtime surprise.

**Rejected.** Read-write mounts (nothing needs them); arbitrary host paths through an API
(the container cannot see them, and pretending otherwise produces a confusing failure);
Docker socket access to mount on demand (an enormous privilege for a convenience).

---

### D-16 Approximate token counting

**Decision.** Chunk sizes are measured at four characters per token. No per-model
tokenizer.

**Why.** Exact tokenization means shipping and versioning a tokenizer per embedding model,
and matching it to whatever Ollama actually loaded. The approximation costs a few percent
of the context window on a value that is already a heuristic. Chunk size is a target, not a
contract, and the documentation says so rather than implying precision it does not have.

**Rejected.** Per-model tokenizers (dependency and drift for a rounding error);
word counting (worse approximation, same class of error).

---

### D-17 Name

**Decision.** **Dexicon** — `dex` (index) + `lexicon`. Repository `Merp4/dexicon`, image
`ghcr.io/merp4/dexicon`, config prefix `DEXICON__`, tenant header `X-Dexicon-Tenant`, token
prefix `dex_`, collections `dexicon__{model}__{dims}`, MCP resources `dexicon://`.

**Why.** A lexicon is a reference work you *consult* — you arrive with a question and leave
with an answer. That is the category this tool belongs to, and category signal turned out to
matter more than availability, because on the evidence below almost every candidate was
available and almost none signalled correctly.

**How the candidates were judged.** Two kinds of name collision, with very different costs:

- **Cross-field** — the name is used elsewhere, in a domain nobody would confuse with this
  one. Costs search ranking. Survivable.
- **Same-field** — the name is used by another developer or AI tool. Costs identity, and no
  amount of SEO fixes it.

Availability was checked on GitHub (repo count and top stars), npm, PyPI, NuGet, and by
searching for live products.

| Candidate | Outcome |
|---|---|
| **MrIndex** (working title) | Reads as *MRIndex* — MRI. Collides with the EU [HMA MrIndex portal](https://mri-production.cts-mrp.eu/) (exact casing) and an academic MRI muscle-scoring tool. Cross-field, so survivable — but it owns the search term and the `mri_` token prefix compounded it. |
| Tessera | **Same-field**: an existing AI coding-session workspace tool, plus an ERP-AI startup. Worst outcome tested. |
| Rubric | **Same-field**: Rubric Labs, an AI dev-tools studio. |
| Mnemex | **Same-field**: was an MCP memory server until it was renamed in Nov 2025. Recently vacated in exactly this space — maximum confusion. |
| Semtex | **Live US trademark** held by Explosia a.s. (registered Jan 2025), historically enforced against a drinks brand and against Madonna's production company. npm taken. Ruled out on legal grounds, before taste. |
| Riffle, Jackdaw, Dogear, Corpex, Findex, Engram, Peruse, Corpora | Crowded — multiple existing tools or companies each. |
| SemScan / SemScope / Semdex / Semtext | All clean, all in Semgrep's neighbourhood. **`Scan` in particular reads as security scanning** (SAST, secret scanners), which is the wrong category signal for a retrieval tool. |
| Pericope, Corpuscope, Indexicon | Clean. Rejected on pronunciation (Pericope), instrument connotation (Corpuscope reads as a microscope — the MrIndex failure again), and length (Indexicon). |
| **Dexicon** | 16 GitHub repos, all at 0 stars. npm, PyPI, NuGet all free. No product anywhere. Right category signal. |

**Residual risk, accepted.** A faint Pokédex echo, which aids recall more than it misleads.
No security-tool drag, no overloaded abbreviation, no trademark holder.

---

### D-18 Versioned extraction cache

**Decision.** Extracted text is cached per blob and stamped with
`ExtractorVersions.Current`. A bump re-extracts on next index, and the version is part of
the chunking fingerprint so the fresh text is actually re-chunked.

**Why.** Caching extraction is clearly right — a 437-page PDF costs ~1.8 s and its bytes
never change. But an unversioned cache is *permanent*, and that turns every extractor bug
into a permanent one: a library ingested before a fix keeps the broken text, and no reindex
repairs it, because reindexing re-chunks the cached text rather than re-reading the file.
The version is what lets a fix reach documents that were ingested before it, without anyone
re-uploading anything.

A single global version re-extracts PDFs when only the EPUB path changed. That is a bounded
one-off cost on upgrade, and it is cheaper than the per-extractor bookkeeping needed to
avoid it.

The chunker carries its own version in the same fingerprint, for the same reason one stage
later: an algorithm change that produces different chunks from identical input is invisible
to a hash of the input.

**Rejected.** Per-extractor versions (more machinery than the saving is worth); timestamp
comparison against assembly build date (fires on every rebuild, including ones that change
nothing); no versioning (the status quo, which hid a bug that removed ~95% of a book from
the index while reporting success).

---

### D-19 The chunker guarantees its budget

**Decision.** No chunk exceeds `chunk_size × 4` characters. A single line longer than the
whole budget is split at word boundaries, each piece keeping that line's number.

**Why.** The chunker's original rule — never split within a line — buys exact
`start_line`/`end_line` on every chunk, which is what makes a result openable in an editor.
That is worth keeping, and it was worth relaxing in exactly one case, because an
over-budget chunk is not *rejected* by the embedding model. It is silently truncated. The
text past the context window is reported as indexed and is nowhere, and nothing in the
system can detect it.

A guarantee that only holds for well-behaved input is not a guarantee. It is enforced by a
property test across chunk sizes, including input with no spaces at all.

**Rejected.** Rejecting over-long lines (loses content); truncating them (loses content and
lies about it); trusting extractors not to produce them (they did — see [D-18](#d-18-versioned-extraction-cache)).

---

### D-20 Jobs are ordered by when they were queued

**Decision.** `IndexJob` carries a non-nullable `QueuedUtc`, and every "latest job" query
orders by it.

**Why.** Ordering on `StartedUtc` with nulls treated as newest looks right — queued work
should be at the top — but a job that *failed before starting* also has a null
`StartedUtc`. Two long-dead failures sat permanently above the job that was running, so
anything reading the first entry to find "the current job" got a stale answer with complete
confidence. The `/api/jobs` list and `index_status` both did; so did a watcher written
against them, which reported an index as failed while it was running perfectly.

A nullable column used as an ordering key is a bug waiting for the right null. A queued
timestamp is also just honest data: the UI can now say how long a job has been waiting.

---

### D-21 Chunk sets, not corpus-level chunking

**Decision.** The embedding model and chunk settings belong to a `ChunkSet`, a child of a
corpus. A corpus owns content, sources and visibility; a set owns a vector space and a
strategy, and a corpus may carry several. Sets are addressed as `corpus:set`.

**Why.** The model was the one setting a corpus could never change, because a collection's
name encodes the model and its dimensionality — so changing it means writing into a
different vector space. A re-embed of a three-book corpus measured at roughly twenty
minutes on CPU Ollama, and doing that in place means twenty minutes of half-populated
results. With sets, the replacement is built alongside the live one and promoted when it is
complete: promotion is one `UPDATE` and the only moment search changes.

It also makes "the same document, chunked two ways" honest. That worked before only by
duplicating the corpus, which duplicated its grants and its sources with it.

`corpus:set` rather than a new parameter keeps the MCP surface at five tools ([D-11](#d-11-five-mcp-tools)),
and an unqualified name still means what it always did.

**Rejected.** In-place re-embed (a fifth of the work, but a corpus is degraded for the
duration of every model change); per-document models (different vector spaces cannot be
fused, and a corpus would have to fan out across collections and merge incomparable
scores); keeping chunk settings on the corpus and adding only a model field (the same
problem one field later).

**Cost, accepted.** A workspace tree is walked once per set. Real but bounded, and most
corpora carry one set; sharing a walk would mean holding the whole discovery in memory,
which a large monorepo makes the worse trade.

---

### D-22 The embedding model is a per-call argument

**Decision.** `IEmbeddingProvider.EmbedAsync` takes the model name. Nothing binds a client
to a model at registration.

**Why.** Chunk sets choose their model at runtime, in the UI, and store it in the
catalogue. Anything resolved from configuration at startup — keyed DI included — cannot see
a set created after the process began, and would either fail to resolve or quietly serve a
different model than the one asked for. Ollama takes the model in the request body, so
there is nothing to bind in the first place.

This also closed a live bug: `EmbedAsync` read the globally configured model and ignored
its caller, so a set pinned to `mxbai-embed-large` would have filled an `mxbai` collection
with `nomic` vectors. Nothing errors. The results are simply wrong.

**Rejected.** Keyed singletons per configured model (the pattern a sibling project uses,
and a good one where models come from configuration — here the configuration is a database
row that changes while the process runs); a factory with a per-model cache (the same
lifetime problem with more machinery, for a value that is one field on a request).

**Since revised.** The seam is now `Microsoft.Extensions.AI`'s `IEmbeddingGenerator`, which
makes OpenAI and Azure OpenAI a registration rather than a rewrite. The decision above
survives the change intact, because `EmbeddingGenerationOptions.ModelId` carries the model
per call: generators are cached per PROVIDER — a connection and a credential — and the
model stays an argument. Adopting the abstraction the obvious way, one generator per
configured model, would have reinstated exactly the bug this decision exists to prevent.

---

### D-23 Unit boundaries force a split

**Decision.** With `unitAware`, a page, chapter or slide boundary ends the current chunk
regardless of how little is in it, and no overlap is carried across it.

**Why.** Everywhere else the rule is "size decides when, a boundary decides where"
([D-19](#d-19-the-chunker-guarantees-its-budget) and the section above it), which is right
for prose and useless here. A chapter shorter than the budget would simply be swallowed
into the next one, so asking for chapter-aligned chunks would produce chunks spanning three
chapters — the exact straddling the setting exists to prevent. Carrying overlap across the
boundary would reintroduce it by the back door.

The cost is that a document of very short pages yields short chunks. That is what
page-aligned chunking means, it is off by default, and the caller asked for it.

---

### Q3, first measurement

`scripts/retrieval-bench.py` runs one query set against two chunk sets and reports where
the expected file ranked. Chunk sets are what make this honest: identical chunking over
identical documents, differing in the model and nothing else. Both sets held 108 chunks.

12 queries over this repository's own docs, hybrid mode:

| | hit@1 | hit@3 | hit@5 | MRR |
|---|---|---|---|---|
| `nomic-embed-text` | 9/12 | 10/12 | 12/12 | 0.829 |
| `embeddinggemma` | 6/12 | 9/12 | 10/12 | 0.632 |

`nomic-embed-text` wins on this sample, and missed nothing inside the top 5 where
`embeddinggemma` missed two queries entirely.

**This does not settle Q3, for three reasons, and the third is the interesting one.**

Twelve queries is a smoke test. They were hand-written by someone who knew the corpus, so
they are biased towards questions the corpus can answer. And hybrid mode means the sparse
half contributes identically to both, which compresses the gap — a pure-semantic run would
separate them further.

The third: **Dexicon sends raw text to the embedding model, with no task prefix.** Both
models document one — `search_query:` / `search_document:` for `nomic-embed-text`, a
task-shaped prefix for `embeddinggemma` — and a model that expects one and does not get it
underperforms. So the fair reading is not "gemma is worse" but "with no prefixes, nomic is
more forgiving", and the result says as much about Dexicon as about either model.

That makes prefixes the next thing to measure rather than the next thing to assume. It is
a per-model convention, so it needs a per-model mapping and a re-run of the same harness —
which now exists, which is most of what this exercise bought.


### D-24 Model limits are measured, not assumed

**Decision.** `POST /api/embedding-models/probe` measures a model's real input limit by
embedding throwaway filler and bisecting on whether the tail still affects the vector.
Nothing is indexed.

**Why.** Dexicon had no idea what any model would accept, and the cost of guessing was a
book. An EPUB produced chunks averaging 32,000 characters, the model truncated every one
of them, ~95% of the content was in no index anywhere, and every layer reported success —
because a truncating model returns a perfectly good vector for the part it read. No
assertion could have caught it, because nothing was wrong with any individual result.

Truncation is silent but not invisible. Changing only the END of an input and watching
whether the vector moves answers "did the model read this far", and bisection turns that
into a limit. Roughly two dozen short calls, no documentation to trust, no vendor claim to
take on faith — and it works identically for a provider whose limits are not published.

Measured here: `nomic-embed-text` and `embeddinggemma` both accept ~11,776 characters of
prose and truncate silently. Neither errors, which is the worse of the two behaviours and
worth knowing.

**Rejected.** A table of known models (goes stale, and says nothing about a model someone
pulled yesterday); trusting documented context windows (they are in tokens, the chunker
works in characters, and the ratio depends on the text); doing nothing and keeping the
conservative default (which is what allowed the original bug).

---

### D-24 Task framing is data, not code

**Decision.** Each embedding model's task templates are stored per `(provider, model)` and
editable at runtime. Resolution is: a saved row, then a built-in suggestion for a
recognised name, then raw. Templates carry a `{text}` placeholder rather than being
prefixes.

**Why.** Most embedding models are trained with a task instruction wrapped around the
input and retrieve measurably worse without it — one project measured EmbeddingGemma at
recall@1 16/25 without its prefixes and 23/25 with them. Dexicon sent raw text to every
model, which cost recall on every search and also made the Q3 comparison meaningless: two
models penalised by different amounts are not being compared with each other.

Data rather than code because models are added at RUNTIME through the Models screen. A
build that hard-coded `if (model.StartsWith("nomic"))` would give every model pulled after
it shipped silently wrong framing — and wrong framing does not fail, it just retrieves
badly. Built-ins keep it correct out of the box without becoming the mechanism.

A template, not a prefix, because EmbeddingGemma's document form wraps the text
(`title: none | text: …`) rather than preceding it. One field expresses both, and whatever
the next model wants.

Unknown models are embedded raw and labelled as such, rather than guessed at from the
name: a wrong prefix is worse than none, because the model embeds the literal string
`search_query:` as content.

**Rejected.** A per-model switch in code (breaks the moment someone pulls a model, which is
a supported action); prefixes rather than templates (cannot express Gemma's form);
inferring from the model name (a guess that fails silently); applying it at the call sites
(two places, and forgetting one is undetectable — so it is an argument to `EmbedAsync`
instead, which cannot be omitted).

**Consequence.** The framing is part of the chunking fingerprint, so editing a profile
re-indexes every chunk set on that model. Correct — documents embedded one way and queries
framed another is exactly the silent mismatch this exists to prevent — but it means the
editor says how many sets it is about to re-index.

---

## What was carried over from McpToolbox

| Component | Treatment |
|---|---|
| `WorkspaceChunker` — language-aware code chunking | **Carried**, largely intact. The per-language boundary table is hard-won; the HTML-templates-are-not-documents decision in particular. |
| `GitignoreFilter` — gitignore + workspaceignore + size caps | **Carried.** Renamed `.workspaceignore` to `.dexiconignore`. |
| Document loaders — PDF, DOCX, PPTX, EPUB, HTML, Markdown | **Carried**, same libraries. |
| `QdrantWorkspaceRepository` | **Rewritten.** Collection naming, tenancy layout, and hybrid search all change (D-04, D-05, D-06). The structure and the payload design survive. |
| Embedding backoff and per-file failure isolation | **Carried**, including the `continue`-not-`break` fix that stopped one bad file starving an entire index. |
| ADR-004 tenancy, ADR-005 auth | **Adapted.** Explicit target selection and fail-fast resolution survive; workspaces, published endpoints, and derived sessions do not — Dexicon has corpora, not conversations. |
| Sidecar control API and per-tenant containers | **Dropped** (D-01). |
| `SemanticWeight` client-side fusion | **Dropped** (D-06). |
| Session/conversation model, jobs bus, agent host | **Dropped** — out of scope ([01](01-overview.md)). |

## Open questions

| # | Question | Needed by | Current lean |
|---|---|---|---|
| ~~Q1~~ | ~~Licence — Apache-2.0 or MIT?~~ | — | **Resolved** — Apache-2.0, see [D-14](#d-14-licence) |
| ~~Q2~~ | ~~Repository name and GHCR namespace~~ | — | **Resolved** — see [D-17](#d-17-name) |
| Q3 | Default embedding model — `nomic-embed-text` or `embeddinggemma`? | M3 | **First numbers in, see below.** Keep `nomic-embed-text`; nothing yet says switch |
| Q4 | Should `index_refresh` require the `ingest` scope, or be admin-only? | M2 | `ingest` — an agent noticing a stale index and refreshing it is the point |
| Q5 | Git history indexing in v1? | M2 scope freeze | No. M5, and only on request |
