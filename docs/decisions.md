# Decisions

Every load-bearing choice, why it was made, and what was rejected. The rejected column is
the useful one: it is what stops the same argument being had again in six months.

Status: **Accepted.** M0 ([roadmap](11-roadmap.md)) ran on 2026-09-16 and confirmed all four
assumptions these decisions rest on. D-06, D-07 and D-12 carry the measured results below.

---

### D-01 Single container

**Decision.** UI, REST API, MCP server, and the indexer run in one process, in one
container. Qdrant and Ollama are separate, as external dependencies.

**Why.** A sidecar indexer earns its keep when it runs inside a per-tenant workspace
container with a different security boundary: untrusted execution on one side,
the platform on the other. Dexicon has no such boundary: it is one operator's tool on one
machine. Splitting would add a control API, a shared token, an internal
network, and a new class of failure, and buy nothing.

**Rejected.** Sidecar indexer (boundary does not exist here); separate UI container (a
static bundle is 200 KB, so serving it from the same host costs nothing and removes a CORS
configuration and a reverse proxy); bundling Qdrant and Ollama into the image (breaks the
upgrade path for both, and people already have Ollama running).

**Revisit if.** Indexing load starts affecting search latency measurably. The component
seams in [02](02-architecture.md) are drawn so the indexer can be lifted out without
touching the API.

---

### D-02 .NET 10 LTS

**Decision.** .NET 10 LTS, ASP.NET Core minimal APIs, C#.

**Why.** The chunker, the gitignore filter, the Qdrant repository and the document loaders
already existed in C#. Rewriting them in Python to follow the ML ecosystem would mean
rewriting the only part that is already proven. .NET 10 is LTS
until November 2028; .NET 11 ships 2026-11-10 as an STS release with the same end date, so
there is nothing to gain by tracking it.

**Rejected.** Python/FastAPI (better embedding ecosystem, but Dexicon calls Ollama over
HTTP and uses none of it); Node/TypeScript (would unify with the SDK but discards the
that code); .NET 11 (STS, no benefit, ships after work starts).

---

### D-03 SQLite for the catalogue

**Decision.** EF Core + SQLite in WAL mode at `/data/catalog.db` for tenants, corpora,
sources, files, jobs, and tokens. Qdrant holds only chunks and vectors.

**Why.** The control plane needs listing, filtering, joining, counting, and transactional
updates, all of which Qdrant handles poorly and a relational store handles natively. SQLite adds
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
partitioning by corpus is finer-grained than partitioning by tenant, and is what queries
filter on, because search is scoped to a corpus set
([05](05-search.md)). Co-locating storage by the field the query filters on is the entire
point of `is_tenant`.

**Rejected.** `tenant_id` as the tenant key (coarser, and every query would carry a second
filter on the field that actually selects); a collection per tenant or per corpus (Qdrant
documents this as rarely efficient, citing per-collection overhead, a 1000-collection ceiling,
and it puts collection lifecycle on the hot path of corpus creation).

**Consequence.** Authorization is resolved in SQLite ([07](07-tenancy-auth.md))
and enforced as a `corpus_id` filter. The tenant is not part of the Qdrant filter, so a bug
in scope resolution is a leak. That is why three independent guards defend it, one of which
is the storage layout itself.

**Proposed change.** [D-28](#d-28-an-admin-password-and-scoped-api-keys) would keep `corpus_id` as the
tenant key and drop the `tenant_id` payload field.

---

### D-05 One collection per embedding model

**Decision.** Collections are named `dexicon__{model}__{dims}`, shared by all tenants and
all corpora using that model.

**Why.** A collection has one vector size. Encoding model and dimensions in the name makes
a dimension mismatch structurally impossible rather than a runtime check that someone
forgets. Changing a corpus's model becomes an explicit rebuild into a different collection, which
is an accurate description of the operation.

**Rejected.** A collection per corpus (loses cross-corpus search in one query, multiplies
collection overhead); a single collection with mixed dimensions (not possible); named
vectors per model within one collection (works, but every point then carries every model's
vector, or sparse point structures with awkward filtering).

---

### D-06 Score fusion server-side

**Decision.** Hybrid search is one Qdrant Query API call with dense and sparse prefetches
and `fusion: dbsf`. No client-side score merging, no weight parameter.

**Revised 2026-09-18.** This said `fusion: rrf` and rejected DBSF below. The rejection was
reasoning, not measurement, and measurement disagreed with it.

**Why.** Dense cosine and BM25 scores are on incomparable scales, and the weight that
balances them is corpus-dependent and drifts as content changes. The approach this replaces
carried a `SemanticWeight` knob defaulted to `0.8` that nobody could set from evidence. RRF
reads rank, not magnitude: no tuning, nothing to mis-set, and one round trip instead of two.

**Why not RRF, which this used to be.** Reading rank rather than magnitude means there is
nothing to mis-set, which reads as a virtue until the two lists differ in quality. A lexical
match at rank 3 of the sparse list then counts for as much as a semantic match at rank 3 of
the dense one, however much worse it is. Concretely: "when should you use an event-driven
architecture" returned a chapter on C# delegates third, because the word "event" appears in
every one of them.

DBSF normalises each list's scores before combining, so a weak lexical match contributes in
proportion to how weak it is. It has no weight either, so the objection that sank
client-side fusion does not apply to it.

**Measured 2026-09-18** over the 96-book library and this repository's own documentation:

| | RRF | DBSF | semantic only |
|---|---|---|---|
| conceptual question, precision@3 | 0.62 | **0.88** | 0.92 |
| verbatim passage, found in top 5 | 0.94 | **0.97** | 1.00 |
| exact identifier, MRR | 0.69 | **0.70** | 0.46 |

Better on all three. The last row is why hybrid exists at all: semantic search cannot find
`DEXICON__INDEXING__DOCUMENTMAXBYTES` at any rank.

**Rejected.** Client-side weighted fusion (a tuning knob with no way to tune it);
narrowing the sparse prefetch so weak lexical matches fall off the end (tried first,
changed nothing, which is what located the problem: the noise is at the TOP of the lexical
list, not in its tail); dense-only (exact identifiers and error strings are exactly what
embeddings are worst at).

**Confirmed by M0** (2026-09-16). `Qdrant.Client` 1.19.0 expresses prefetch + fusion in a
single call, so the fallback to client-side fusion is not needed. That spike also verified
Qdrant's **RRF k=2**: every score matched `1/(2+r₀)+1/(2+r₁)` to 1e-4, which is how the
fusion was confirmed to be rank-based. Worked example in [05](05-search.md).

---

### D-07 Client-side term frequencies with `modifier: idf`

**Decision.** Sparse vectors are computed in-process (tokenize, split identifiers, drop
stopwords, emit `{term_hash: frequency}`) and the sparse index is declared
`modifier: idf`, so Qdrant applies the IDF component itself.

**Why.** It works on any self-hosted Qdrant with any client, needs no model, needs no
corpus statistics maintained by Dexicon, and costs under a millisecond. Identifier
splitting is what makes it useful on code: a query for "token refresh" has to reach
`TokenService.RefreshAsync`.

**Confirmed by M0** (2026-09-16). `modifier: idf` ranks correctly from client-supplied term
frequencies against a self-hosted Qdrant: a rare term isolated its single document at score
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
file, which is two round trips to save a kilobyte and impossible for uploaded documents where
there is no file to open. Roughly 1.2 KB per chunk at the default size; on a 400k-chunk
index that is around 480 MB, against a dense-vector cost four times larger.

**Rejected.** Pointers only (fails for uploads, doubles round trips); storing a truncated
preview (the caller cannot tell whether truncation lost the answer).

---

### D-09 Polling with content hashes

**Decision.** Reindexing walks the tree on demand or on an interval, comparing SHA-256
content hashes. No filesystem watcher.

**Why.** `FileSystemWatcher` over Docker bind mounts is unreliable, without reporting it, and worse
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

**Why.** It works identically for the SPA, `curl`, and every MCP client. Claude Code's
`--header "Authorization: Bearer …"` is the documented path for a server with a static
token. OIDC would mean an identity provider in the compose file for a tool with three
users.

**Rejected.** OIDC/SSO (disproportionate; the token model is a clean seam if it is ever
needed); no auth on localhost (the MCP endpoint is reachable by anything on the machine,
and tenancy would be decorative); MCP OAuth flows (the spec supports them, but for a
self-hosted local server they add an authorization server for no gain).

**The rule:** target selection is explicit, validated and least-privilege; an ambiguous
target fails fast rather than being inferred.

**Proposed change.** [D-28](#d-28-an-admin-password-and-scoped-api-keys) would keep the credential and
its storage, drop the tenant binding and `X-Dexicon-Tenant`, make `admin` reachable
only through a password, and map each key to corpora in the UI.

---

### D-11 Five MCP tools

**Decision.** `search_index`, `list_corpora`, `get_context`, `index_refresh`,
`index_status`. Nothing else.

**Why.** Every tool definition is context the agent pays for on every turn, and a large
surface measurably degrades smaller models: a 12B model, measured against 35 tool
definitions, exhausted its generation budget without calling any of them. Five is enough to
find things, understand them, and know whether the index is current.

**Rejected.** Per-format search tools; separate keyword and semantic tools (a `mode`
parameter, not three tools); admin tools over MCP (tenant and token management belongs in
the UI, where a human is present).

**Proposed change.** [D-28](#d-28-an-admin-password-and-scoped-api-keys) would make it five tools,
four of which every key sees: `index_refresh` is listed only for a key granted
`ingest`.

---

### D-12 Stateless streamable HTTP, MCP 2026-07-28

**Decision.** `POST /mcp`, streamable HTTP, stateless, protocol revision 2026-07-28, with
negotiation down to 2025-06-18.

**Why.** It is the current revision, all Tier 1 SDKs ship it, and its stateless core, with
no handshake and no `Mcp-Session-Id`, matches Dexicon's model: every search is self-contained
and nothing needs server-to-client calls. The C# SDK already defaults to stateless. Legacy
HTTP+SSE is deprecated in the spec and is not implemented.

**Corrected by M0** (2026-09-16). The decision stands; one premise was wrong. SDK 2.2.0
will not negotiate `2026-07-28` through `initialize`, stopping at `2025-11-25`, because
**2026-07-28 removed the handshake**, so its clients never call `initialize`. Both
populations are served: handshake clients negotiate to at most 2025-11-25, and 2026-07-28
clients issue self-contained requests that work cold. Verified: stateless confirmed (no
`Mcp-Session-Id` ever emitted), static bearer enforced ahead of the handler, and Claude Code
2.1.248 reports `✔ Connected`. Detail in [06](06-mcp-surface.md).

**Rejected.** stdio (one client per process, no tenancy, no sharing between agents, and
transport Dexicon exists to replace); HTTP+SSE (deprecated); pinning to 2025-06-18 (would
work, but starts the project two revisions behind).

---

### D-13 React SPA, served by the API host

**Decision.** React 19 + Vite + Tailwind v4, built at image build time into `wwwroot`.

**Why.** Static output, no runtime dependency, no CORS, no second container, no reverse
proxy. Typed client generated from the OpenAPI document, so a contract change breaks the
build.

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
governs **distribution**: it binds at first publication rather than at a local commit, and the
first three commits were made without one with no consequence. Secret hygiene is the rule
that genuinely cannot be retrofitted, because history is what gets scanned, and that one
did land on commit one. The roadmap and open-questions table now say *before first public
push*.

**Compatible with every dependency** in [04](04-ingestion.md#extraction): PdfPig is
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
and matching it to whichever model Ollama loaded. The approximation costs a few percent
of the context window on a value that is already a heuristic. Chunk size is a target, not a
contract, and the documentation says so rather than implying precision it does not have.

**Rejected.** Per-model tokenizers (dependency and drift for a rounding error);
word counting (worse approximation, same class of error).

---

### D-17 Name

**Decision.** **Dexicon** — `dex` (index) + `lexicon`. Repository `Merp4/dexicon`, image
`ghcr.io/merp4/dexicon`, config prefix `DEXICON__`, tenant header `X-Dexicon-Tenant`, token
prefix `dex_`, collections `dexicon__{model}__{dims}`, MCP resources `dexicon://`.

**Why.** A lexicon is a reference work that is *consulted*: the reader arrives with a
question and leaves with an answer, which is the category this tool belongs to. Category
signal mattered more than availability, because on the evidence below almost every
candidate was available and few signalled the category correctly.

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
the chunking fingerprint so the fresh text is re-chunked.

**Why.** Caching extraction is worthwhile: a 437-page PDF costs ~1.8 s and its bytes
never change. But an unversioned cache is *permanent*, and that turns every extractor bug
into a permanent one
([04](04-ingestion.md#extraction-is-cached-and-the-cache-is-versioned)).
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

**Why.** The chunker's original rule, never splitting within a line, gives exact
`start_line`/`end_line` on every chunk, which makes a result openable in an editor. That
property is retained and relaxed in one case only, because an over-budget chunk is not
*rejected* by the embedding model: it is truncated without error. The text beyond the
context window is reported as indexed but is absent, and nothing in the system detects
it.

A guarantee that only holds for well-behaved input is not a guarantee. It is enforced by a
property test across chunk sizes, including input with no spaces at all.

**Rejected.** Rejecting over-long lines (loses content); truncating them (loses content and
lies about it); trusting extractors not to produce them, which they did (see
[D-18](#d-18-versioned-extraction-cache)).

---

### D-20 Jobs are ordered by when they were queued

**Decision.** `IndexJob` carries a non-nullable `QueuedUtc`, and every "latest job" query
orders by it.

**Why.** Ordering on `StartedUtc` with nulls treated as newest appears correct, since
queued work belongs at the top, but a job that *failed before starting* also has a null
`StartedUtc`. Two long-dead failures sat permanently above the running job, so anything
reading the first entry to find "the current job" received a stale answer. The `/api/jobs` list and `index_status` both did; so did a watcher written
against them, which reported an index as failed while it was running perfectly.

A nullable column used as an ordering key is a bug waiting for the right null. A queued
timestamp is also usable data: the UI can now say how long a job has been waiting.

---

### D-21 Chunk sets, not corpus-level chunking

**Decision.** The embedding model and chunk settings belong to a `ChunkSet`, a child of a
corpus. A corpus owns content, sources and visibility; a set owns a vector space and a
strategy, and a corpus may carry several. Sets are addressed as `corpus:set`.

**Why.** The model was the one setting a corpus could never change, because a collection's
name encodes the model and its dimensionality, so changing it means writing into a
different vector space. A re-embed of a three-book corpus measured at roughly twenty
minutes on CPU Ollama, and doing that in place means twenty minutes of half-populated
results. With sets, the replacement is built alongside the live one and promoted when it is
complete: promotion is one `UPDATE` and the only moment search changes.

It also makes "the same document, chunked two ways" workable. That worked before only by
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
catalogue. Anything resolved from configuration at startup, including keyed DI, cannot see
a set created after the process began, and would either fail to resolve or serve a
different model than the one asked for. Ollama takes the model in the request body, so
there is nothing to bind in the first place.

This also closed a live bug: `EmbedAsync` read the globally configured model and ignored
its caller, so a set pinned to `mxbai-embed-large` would have filled an `mxbai` collection
with `nomic` vectors. No error is raised; the results are wrong.

**Rejected.** Keyed singletons per configured model (the pattern a sibling project uses,
and a good one where models come from configuration. Here the configuration is a database
row that changes while the process runs); a factory with a per-model cache (the same
lifetime problem with more machinery, for a value that is one field on a request).

**Since revised.** The seam is now `Microsoft.Extensions.AI`'s `IEmbeddingGenerator`, which
makes OpenAI and Azure OpenAI a registration rather than a rewrite. The decision above
survives the change intact, because `EmbeddingGenerationOptions.ModelId` carries the model
per call: generators are cached per provider, which is a connection and a credential, and the
model stays an argument. Adopting the abstraction the obvious way, one generator per
configured model, would have reinstated exactly the bug this decision exists to prevent.

---

### D-23 Unit boundaries force a split

**Decision.** With `unitAware`, a page, chapter or slide boundary ends the current chunk
regardless of how little is in it, and no overlap is carried across it.

**Why.** Everywhere else the rule is "size decides when, a boundary decides where"
([D-19](#d-19-the-chunker-guarantees-its-budget) and the section above it), which is right
for prose and unsuitable here. A chapter shorter than the budget would be absorbed into
the next, so requesting chapter-aligned chunks would produce chunks spanning three
chapters: the straddling the setting exists to prevent. Carrying overlap across the
boundary would reintroduce it by the back door.

The cost is that a document of very short pages yields short chunks. That is what
page-aligned chunking means, it is off by default, and the caller asked for it.

---

### Q3, first measurement

`scripts/retrieval-bench.py` runs one query set against two chunk sets and reports where
the expected file ranked. Chunk sets are what make the comparison fair: identical chunking
over identical documents, differing in the model and nothing else. Both sets held 108 chunks.

12 queries over this repository's own docs, hybrid mode:

| | hit@1 | hit@3 | hit@5 | MRR |
|---|---|---|---|---|
| `nomic-embed-text` | 9/12 | 10/12 | 12/12 | 0.829 |
| `embeddinggemma` | 6/12 | 9/12 | 10/12 | 0.632 |

`nomic-embed-text` wins on this sample, and missed nothing inside the top 5 where
`embeddinggemma` missed two queries entirely.

That run had a confound, and naming it turned out to matter more than the numbers:
**Dexicon was sending raw text to the embedding model, with no task framing.** Both models
document one, and a model that expects framing and does not get it underperforms.

### Q3, second measurement — with task framing

Model profiles now apply each model's documented framing, so the same harness was re-run
on the same twelve queries and the same 114 chunks per set:

| | hit@1 | hit@3 | hit@5 | MRR |
|---|---|---|---|---|
| `nomic-embed-text`, raw | 9/12 | 10/12 | 12/12 | 0.829 |
| `nomic-embed-text`, framed | 8/12 | 10/12 | 11/12 | 0.739 |
| `embeddinggemma`, raw | 6/12 | 9/12 | 10/12 | 0.632 |
| `embeddinggemma`, framed | 8/12 | 11/12 | 12/12 | **0.799** |

**`embeddinggemma` improved substantially, 0.632 to 0.799, as the confound predicted.** It was being handicapped by an input format it was never trained on.

`nomic-embed-text` moved the other way, from 0.829 to 0.739. That difference is two
queries out of twelve, which is inside the noise of a sample this small, and it would be
wrong to read it as framing harming it.

**Q3 is still open, and now for a better reason.** The first run suggested a clear winner;
correcting the confound made the two models roughly equivalent, with gemma marginally
ahead. That the ranking flipped when one variable was fixed is the strongest evidence yet
that twelve queries cannot settle this. A real answer needs a query set built from
questions drawn from real use, large enough that a single fortunate retrieval does not
move the result.

What the exercise did settle: task framing is not cosmetic, and Dexicon was getting it
wrong for every model.

### Q3, third measurement — a different chunking, same conclusion

The second run left one obvious question: was gemma's lead an artefact of that particular
chunk size? The same twelve queries were run against a second corpus configuration:
256/40 with unit-aware, sentence-aware and heading context all enabled, 473 chunks per
set, and the model as the only difference between the two sets.

| | hit@1 | hit@3 | hit@5 | MRR |
|---|---|---|---|---|
| `nomic-embed-text`, framed, 256/40 | 8/12 | 10/12 | 11/12 | 0.743 |
| `embeddinggemma`, framed, 256/40 | 10/12 | 10/12 | 12/12 | **0.871** |

**Gemma leads again by a wider margin, and nomic's score changed little between the two
chunkings (0.739 at 768/100, 0.743 at 256/40).** That consistency is what makes the
comparison meaningful: the variable that changed the ranking was the framing, not the
chunking.

`nomic-embed-text` missed one query outright that gemma found at rank 5 ("what is the
Qdrant tenant key and why was it chosen"), and trailed on two others it answered at ranks
3 and 4.

**This still does not settle Q3.** It is an
independent *corpus configuration*, not an independent *query set*: the same twelve
hand-written queries were reused. So it tests whether the finding survives a chunking
change, which it does, and says nothing about whether it survives a different distribution
of questions, which remains the open gap. Two runs agreeing on twelve queries is two
correlated samples, not twenty-four.


### D-24 Model limits are measured

**Decision.** `POST /api/embedding-models/probe` measures a model's real input limit by
embedding throwaway filler and bisecting on whether the tail still affects the vector.
Nothing is indexed.

**Why.** Dexicon had no idea what any model would accept, and the cost of guessing was a
book. An EPUB produced chunks averaging 32,000 characters, the model truncated every one
of them, ~95% of the content was present in no index, and every layer reported success,
because a truncating model returns a valid vector for the part it read. No
assertion could have caught it, because nothing was wrong with any individual result.

Truncation raises no error but is detectable. Changing only the end of an input and
observing whether the vector moves answers whether the model read that far, and bisection
turns that into a limit. This takes roughly two dozen short calls, relies on no
documentation or vendor claim, and works identically for a provider whose limits are not
published.

Measured here: `nomic-embed-text` and `embeddinggemma` both accept ~11,776 characters of
prose and truncate beyond it without raising an error.

**Rejected.** A table of known models (goes stale, and says nothing about a model someone
pulled yesterday); trusting documented context windows (they are in tokens, the chunker
works in characters, and the ratio depends on the text); doing nothing and keeping the
conservative default (which is what allowed the original bug).

---

### D-25 Task framing is data, not code

**Decision.** Each embedding model's task templates are stored per `(provider, model)` and
editable at runtime. Resolution is: a saved row, then a built-in suggestion for a
recognised name, then raw. Templates carry a `{text}` placeholder rather than being
prefixes.

**Why.** Most embedding models are trained with a task instruction wrapped around the
input and retrieve measurably worse without it. One project measured EmbeddingGemma at
recall@1 16/25 without its prefixes and 23/25 with them. Dexicon sent raw text to every
model, which cost recall on every search and also made the Q3 comparison meaningless: two
models penalised by different amounts are not being compared with each other.

Data rather than code because models are added at *runtime* through the Models screen. A
build that hard-coded `if (model.StartsWith("nomic"))` would give every model pulled after
it shipped incorrect framing, and incorrect framing does not fail; it retrieves poorly.
Built-ins keep the common cases correct without becoming the mechanism.

A template, not a prefix, because EmbeddingGemma's document form wraps the text
(`title: none | text: …`) rather than preceding it. One field expresses both, and whatever
the next model wants.

Unknown models are embedded raw and labelled as such, rather than guessed at from the
name: a wrong prefix is worse than none, because the model embeds the literal string
`search_query:` as content.

**Rejected.** A per-model switch in code (breaks the moment someone pulls a model, which is
a supported action); prefixes rather than templates (cannot express Gemma's form);
inferring from the model name (a guess whose failure is not observable); applying it at
the call sites (two places, where omitting one is undetectable, so it is an argument to
`EmbedAsync` instead, which cannot be omitted).

**Consequence.** The framing is part of the chunking fingerprint, so editing a profile
re-indexes every chunk set on that model. This is correct, since documents embedded one
way and queries framed another is the mismatch this exists to prevent, but it means the
editor reports how many sets it is about to re-index.

---

### D-26 The version comes from the git tag, not from a file

**Decision.** MinVer derives the version from the nearest `v*` tag at build time. No
`<Version>` is written down anywhere.

**Why.** A number in a file has to be bumped by somebody, and between cutting a release
and remembering to bump it, the number is wrong. That happened here within an hour: a
build from source reported 0.1.0 while 0.1.1 was the newest release. It is not
decorative: it is what the OpenAPI document carries and what the MCP server reports to
every client, so a stale value misinforms every consumer.

Deriving it also dissolves the question of which commit to tag. There is no bump commit,
so the tag goes on the commit being released, and the tag is the only thing that has to
be right.

**Consequence.** Three, all of them real:

- A build needs history and tags. CI checks out with `fetch-depth: 0`; a shallow clone
  versions itself `0.0.0-alpha.0.N` rather than failing, which states that it does not
  know.
- The container image cannot do this. `.dockerignore` excludes `.git`, because copying
  history into the build context would invalidate the layer cache on every commit, so
  the Dockerfile takes `VERSION` as a build argument, which the release workflow supplies
  from the tag and then verifies against what the built image actually contains. A hand
  build with no argument reports `0.0.0-dev`: an image built outside the pipeline should
  say so rather than impersonate a release.
- The OpenAPI document carries the MAJOR.MINOR only. That document is generated at build
  time and committed, because the image builds the web client from it; a version carrying
  the commit height would make the file differ on every commit and turn CI's
  is-it-current check into noise everyone learns to ignore. A patch does not change the
  contract.

One behaviour to be aware of, since it produces no error: MinVer pins `AssemblyVersion`
to `major.0.0.0` so that a patch release cannot break assembly binding. Reading that as the
product version reports `0.0.0` for any 0.x project, which occurred here. The
informational version is the one to read.

### Q3, fourth measurement — the full sweep (2026-09-17)

81 configurations: three models × three chunk sizes × three boundary modes × three search
modes, over the `docs/` corpus, 55 queries. `scripts/bench/sweep.py`, written up in
[benchmarks.md](benchmarks.md). The first three measurements each varied one thing by hand
and reused twelve queries; this varies everything and reuses none of them.

**What changed as a result:**

- **Hybrid is earned as the default search mode.** Best mean for every model, and the
  highest floor; semantic takes both the single best configuration and the worst.
- **`language-aware` is NOT earned for documents.** It is last of three boundary modes on
  a corpus that is entirely markdown, whose real boundaries are blank lines. It stays the
  default, because it was written for code and the sweep has not been run over a code
  corpus, and changing a default on insufficient evidence is worse than leaving it.
  *(Since revised: the code sweep was run; see the fifth measurement below.)*
- **`nomic-embed-text` stays.** `embeddinggemma` leads by 0.025 mean MRR, consistent with
  the three earlier runs, and still inside the noise of 55 queries. Changing it forces a
  reindex of every corpus; that needs better evidence than "probably".

**And the sweep found something the question was not asking.** `mxbai-embed-large` accepts
2,816 characters. The default chunk size of 768 tokens produces 3,072. Two thirds of that
model's rows measure truncation rather than retrieval, and its steady decline across chunk
sizes is consistent with that. A default that breaks a model the UI offers in a dropdown is
a more significant problem than which model is 2% better, and was the one acted on. See [D-27](#d-27-a-chunk-budget-is-characters-and-the-ratio-is-measured).

### Q3, fifth measurement — the code corpus (2026-09-17)

The fourth measurement said the code sweep had not been run and that two of its
conclusions were therefore provisional. It has now been run: 52 `(query, expected file)`
pairs over `src/`, the same 81 configurations, in `scripts/bench/queries-code.json`.

**Since revised, twice:**

- **`nomic-embed-text` does not stay.** On documents `embeddinggemma` led by 0.025 mean
  MRR, which the fourth measurement correctly called inside the noise. On code it leads by
  **0.075**, the widest margin any single variable produces in either sweep. Two independent
  corpora agreeing is the better evidence that decision asked for, so the default moved.
  The cost is twice the first download, and that is the whole of the case against it.
- **`language-aware` is not earned for code either**, which was the open question. It is
  last of three boundary modes on both corpora. It still stays, for a different and weaker
  reason than before: the spread across all three modes on code is **0.009**, so changing
  it costs a reindex for a rounding error. The boundary mode is the least load-bearing
  setting in the sweep.

**What did not change.** Hybrid wins both corpora on mean, for every model. Keyword is
markedly weaker on code than on prose, 0.579 against 0.677, which is expected when the
identifier a developer half-remembers is rarely the identifier in the file.

**And a footnote to the truncation finding.** `mxbai-embed-large` takes the single best
code configuration (0.829) at 768 tokens, the one swept size its 2,816-character limit
does not compromise. This is not an argument for the model but confirmation of the earlier
reading: its other rows measured truncation rather than quality. The chunk size is now set from
what a model was measured to accept, so that default is no longer reachable.

### D-27 A chunk budget is characters, and the ratio is measured

**Decision.** Chunk size stays a character budget. The chunker converts tokens to
characters once and counts characters; no tokenizer runs in the chunking path. What
changes is that the conversion ratio is now measured per model rather than assumed to be 4.

**Why not a real tokenizer.** Exactness would mean a vocabulary per model, versioned, for
models pulled at runtime that may not exist yet. The only tokenizer guaranteed correct for
an arbitrary model is the one inside it, and asking it costs a round trip per chunk, tens
of thousands of times per index. Counting characters is free.

**Why measuring is not the same as assuming.** Ollama returns `prompt_eval_count` on an
embed call, so the model's own tokenizer can be asked once and the answer kept. Measured
here: `nomic-embed-text` 2.82 characters per token, `embeddinggemma` 3.80,
`mxbai-embed-large` 2.82. Two of the three are well below 4, so a "768 token" chunk was
nearer 1,090 tokens, and the figure shown in the UI was wrong by 40% in the direction that
causes truncation.

**Consequence.** The probe reports the ratio and the Models screen shows it. The chunker
still divides by 4: using the measured ratio there changes every chunk boundary and
invalidates every index, so it is a migration rather than a fix, and belongs behind its own
decision.

---

### D-28 An admin password and scoped API keys

**Status.** Proposed, 2026-09-19. Not implemented; the rest of this document describes
shipped behaviour.

**Decision.** Tenancy goes. One admin password authenticates the UI and is the only route to
the `admin` scope. API keys authenticate agents, carry `search` and optionally `ingest`, and
are mapped to corpora in the UI through a `TokenCorpus` join table where no rows means every
corpus. The mapping is read per request rather than cached into the principal, whose
60-second TTL would otherwise decide how stale a scope change could be. A key that should
reach nothing is revoked, not mapped to an empty set. `X-Dexicon-Tenant` goes, and no header
replaces it.

**Why.** The tenant existed so that several agents could share one endpoint and see
different material, chosen by header. It never could: `ApiToken.TenantId` is a single
column, so `X-Dexicon-Tenant` can only agree with the token or return 400.

A corpus-selecting header would have worked, and is the wrong shape anyway. A header lives
in the client's configuration, so changing what an agent reaches means editing that file and
restarting the client. What an agent should see changes more often than how it connects.

A key is already a stable, authenticated, per-agent identifier that the client never has to
be told about twice. Mapping the key to corpora server-side puts the control somewhere it
can be changed while everything keeps running: tick a corpus in the UI, and the next
`list_corpora` reflects it.

**Scopes.** No key issued in the UI can carry `admin`. Of the four endpoints that required
`ingest`, three are document-library actions that the UI performs and move to `admin`, which
leaves `ingest` meaning one thing: this key may reindex the corpora it is mapped to, as
`POST /api/corpora/{id}/reindex` or the MCP tool `index_refresh`. It is off unless ticked. No
long-lived administrative credential then sits in an agent's configuration. This closes Q4.

**The password itself.** Seeded from `DEXICON__ADMIN__PASSWORD`, or generated and logged
once on first run where that is blank, which is what `DEXICON__BOOTSTRAP__TOKEN` already does.
It is stored hashed in the catalogue rather than read from the environment on each request, so
it can be changed in the UI without a restart.

Failed attempts are throttled by a delay that doubles and is capped, counted globally rather
than per caller: there is one password, so there is one thing to guess, and in a single
container every caller arrives from the same gateway address anyway. A delay and not a
lockout, because with one shared credential a lockout is a denial of service that anyone able
to reach the port can inflict on the owner. The counter resets on success and lives in the
`IMemoryCache` the auth middleware already holds principals in, so a restart clears it, and a
restart needs host access that defeats this model regardless ([07](07-tenancy-auth.md)). Key
authentication is not throttled: a 32-byte secret is not guessable, and throttling it would
let anyone degrade agent traffic by presenting bad bearers.

**The MCP surface varies by key.** A key without `ingest` is not shown `index_refresh`,
rather than being refused when it calls it: an agent that can see a tool will call it, spend
a turn on the error, and sometimes retry. `McpRequestFilters.ListToolsFilters` in SDK 2.2.0
wraps the list-tools pipeline and can modify its response, and `tools/list` carries the
bearer like every other request, so the principal is available when the list is built.

**What it removes.** `Tenant`, `/api/tenants`, the Tenants panel, and
`DEXICON__BOOTSTRAP__TENANT`. `Corpus.TenantId` and the `(TenantId, Name)` unique index,
making corpus names globally unique, which is what an agent passing `corpus: ["books"]`
already assumes. Today that assumption is wrong in a way nothing reports: `VisibleAsync`
concatenates owned corpora ahead of shared ones and `ResolveReadableAsync` takes the first
name match, so a tenant that owns `books` and is also granted someone else's `books` reaches
only its own, and the shared one has no name that addresses it. `CorpusVisibility` and
`CorpusGrant`, 27 references across five files under `src/` excluding migrations, plus the
tests and the Access page that read them. The ownership branch of `ResolveWritableAsync`.
`CorpusSummary.TenantId`, `.Owned` and `.Visibility`, with the `viewerTenant` parameter
threaded through `Summarise` to compute `Owned`. The `tenant_id` Qdrant payload field,
written on every point and read nowhere, which
[D-04](#d-04-corpus-as-the-qdrant-tenant-key) already records as a plain field.

**Rejected.** A corpus-selecting header, `X-Dexicon-Corpus` (works, and every change to what
an agent reaches costs a client restart, which is the requirement). An identity or session
header that the UI maps to corpora (the header is not authenticated, so anyone holding the
key can claim any identity, making it a label rather than a control, and it still has to be
configured in the client once). A named mapping that several keys point at, which is a shelf
(an entity and two join tables to hold a list that each key can hold directly; additive
later, and worth adding when keys start being kept in sync by hand). Keeping tenancy and
deleting the header (about thirty lines and a documentation pass, and it leaves the
requirement unmet). Binding a token to several tenants, which
[D-10](#d-10-static-tokens-and-a-tenant-header) already describes (the same join table for a
smaller change, but a tenant is also the write owner, so such a token writes in one tenant
and not another). A global read-only-MCP setting in the UI (one switch cannot express a
read-only research agent alongside a maintenance agent that may refresh, and it duplicates a
control the key already carries). A cookie session for the password (reintroduces the CSRF
surface that a bearer in `sessionStorage` does not have).

**Assumes.** That what an agent should see changes more often than how that agent connects.
That is the whole case for a server-side mapping over a header, and it is an observation
about how the tool gets used rather than a measurement. If a key turns out to be mapped once
and never edited, a header would have been sufficient and cheaper.

**Cost, accepted.** A password is the first credential here that a human chooses, so it is
the first that can be guessed. PBKDF2 at 600k iterations and the throttle above are what
stand in the way, and what remains is that someone able to reach the port can hold the delay
at its cap and make the owner wait that long to sign in. [07](07-tenancy-auth.md) defers rate
limiting per token as something to add when someone reports a problem; that stays true of
keys and stops being true of the password.

A key with no mapping reads every corpus, and a leaked key reads whatever it is mapped to,
where tenancy confined a leak to one tenant. For one operator on one machine that is the
right trade, and the mapping is the mitigation when it stops being. Writes lose their owner:
`ingest` alone governs reindexing. `POST /api/corpora/{id}/documents` stops being reachable
with a key, so a script that uploads needs the admin session.

**Consequence.** The corpus mapping is live and the tool list is not. The transport is
stateless ([D-12](#d-12-stateless-streamable-http-mcp-2026-07-28)), with no session id and no
server-to-client calls, so there is no `notifications/tools/list_changed` to send and a
client lists on connect and caches. Ticking a corpus lands on the next call; ticking `ingest`
lands when the client reconnects, and the UI has to say so or the difference reads as a
defect. `ListToolsResult` carries `CacheScope` and `TimeToLive` in SDK 2.2.0, which would
narrow that window if clients honour them, untested here.

The password uses the same PBKDF2-HMAC-SHA256 at 600k iterations that tokens use, and is
exchanged for a short-lived admin-scoped bearer held in `sessionStorage`, so the SPA keeps
the bearer model it has and gains no cookie. [07](07-tenancy-auth.md) stops being about
tenancy. The three guards are unchanged, because enforcement is still the `corpus_id` filter,
the refusal to query on an empty scope, and `hnsw m=0`; they defend a smaller promise.
`TenantIsolationTests` keeps the unknown-corpus error, the empty-scope throw and the
unfiltered-query refusal, and loses its sharing cases.

**Revisit if.** Keys start being kept in sync by hand, which is when a named mapping earns
its entity. Or someone shares an endpoint across a team and wants a corpus only its owner can
reindex, which is the protection this gives up.

**Supersedes** [D-10](#d-10-static-tokens-and-a-tenant-header) in part: the credential format
and its storage stand, the tenant binding and `X-Dexicon-Tenant` do not. **Amends**
[D-04](#d-04-corpus-as-the-qdrant-tenant-key): `corpus_id` remains the `is_tenant` key, and
the `tenant_id` payload field goes. **Amends** [D-11](#d-11-five-mcp-tools): five tools, four
of which every key sees.

---

## Open questions

| # | Question | Needed by | Current lean |
|---|---|---|---|
| ~~Q1~~ | ~~Licence — Apache-2.0 or MIT?~~ | — | **Resolved** — Apache-2.0, see [D-14](#d-14-licence) |
| ~~Q2~~ | ~~Repository name and GHCR namespace~~ | — | **Resolved** — see [D-17](#d-17-name) |
| ~~Q3~~ | ~~Default embedding model — `nomic-embed-text` or `embeddinggemma`?~~ | — | **Resolved** — `embeddinggemma`, which won both sweeps. See [benchmarks](benchmarks.md) |
| ~~Q4~~ | ~~Should `index_refresh` require the `ingest` scope, or be admin-only?~~ | — | **Resolved** — `ingest`, granted per key and off by default, with the tool hidden from keys that lack it. See [D-28](#d-28-an-admin-password-and-scoped-api-keys) |
| Q5 | Git history indexing in v1? | M2 scope freeze | No. M5, and only on request |
