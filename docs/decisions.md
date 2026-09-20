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

**Consequence.** Authorization is resolved in SQLite ([07](07-auth.md))
and enforced as a `corpus_id` filter. The tenant is not part of the Qdrant filter, so a bug
in scope resolution is a leak. That is why three independent guards defend it, one of which
is the storage layout itself.

**Amended by** [D-28](#d-28-an-admin-password-and-scoped-api-keys): `corpus_id` remains the
`is_tenant` key and the `tenant_id` payload field is gone. Qdrant's name for the index
outlived the concept.

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

**Superseded in part by** [D-28](#d-28-an-admin-password-and-scoped-api-keys): the
credential format and its storage stand. The tenant binding and `X-Dexicon-Tenant` are
gone, `admin` is reachable only through a password, and each key maps to corpora in the
UI.

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

**Amended by** [D-28](#d-28-an-admin-password-and-scoped-api-keys): five tools, four of
which every key sees. `index_refresh` is listed only for a key granted `ingest`.

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

**Amended by [D-31](#d-31-a-chunk-is-an-index-entry-and-the-model-decides-how-big-it-can-be).** No tokenizer still holds, for a
firmer reason: parity with the loaded model cannot be shown, and chunking by tokens couples
the index to a tokenizer. What does not hold is "a rounding error" — one index run logged
212 truncations across 74 files — nor the character conversion itself, which D-31 replaces
with the provider's own refusal.

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

**Amended by [D-31](#d-31-a-chunk-is-an-index-entry-and-the-model-decides-how-big-it-can-be).** That decision is
this one, and it went the other way: rather than chunk by the measured ratio, the ratio
stops deciding the size at all. The provider's refusal does, which costs about 350 ms
against the six seconds a file the measurement costs. The ratio remains worth reporting;
it is no longer worth chunking by.

---

### D-28 An admin password and scoped API keys

**Status.** Accepted and implemented, 2026-09-19.

**Decision.** Tenancy goes. One admin password authenticates the UI and is the only route to
the `admin` scope. API keys authenticate agents, carry `search` and optionally `ingest`, and
are mapped to corpora in the UI through a `TokenCorpus` join table where no rows means every
corpus. The mapping is read per request rather than cached into the principal, whose
60-second TTL would otherwise decide how stale a scope change could be. A key that should
reach nothing is revoked, not mapped to an empty set. `X-Dexicon-Tenant` goes, and no header
replaces it.

**Why.** The tenant did two things: it was the isolation boundary [07](07-auth.md)
describes, and it was how several agents were to share one endpoint and see different
material, chosen by header. The second never worked: `ApiToken.TenantId` is a single column,
so `X-Dexicon-Tenant` can only agree with the token or return 400. The first is a boundary
this tool does not have, since anyone reaching the Qdrant port or the data volume reads
everything regardless, which [07](07-auth.md) says in its opening paragraph.

A corpus-selecting header would have worked, and the choice between it and a server-side
mapping is about where the control lives. A header sits on the far side of the connection, in
as many copies as there are agents, on whatever machines those agents run on, and changing it
costs a reconnect on the clients [12](12-clients.md) lists, though that varies by client. A
mapping sits in one place, next to the corpora it names, and changes while everything is
running. What an agent should see changes more often than how it connects, so it belongs
where it is cheapest to change.

That trade has a real cost on the other side: a header in a client's configuration file is in
version control, and a catalogue row is not. Editing a mapping is an authenticated request
like any other and lands on the audit line [07](07-auth.md) describes, but a log is
not a diff and cannot be replayed onto a fresh machine.

A key is already a stable, authenticated, per-agent identifier that the client never has to
be told about twice, which makes it the thing to hang the mapping on. Tick a corpus in the
UI, and the next `list_corpora` reflects it.

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
restart needs host access that defeats this model regardless ([07](07-auth.md)). Key
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
surface that a bearer in `sessionStorage` does not have). No default scope at all, with every
call naming its corpus and `list_corpora` returning everything (needs no mapping and no
header, and gives an agent both a longer tool result on every turn and nothing to fall back
on when it omits the argument). Mapping keys to chunk sets rather than corpora (`corpus:set`
already names a set per call and an unqualified name already means the default set, so this
would duplicate that while forcing default-set resolution to consult the mapping too; a key
pinned to one embedding is better expressed as a flag on the mapping row).

**Assumes.** That what an agent should see changes more often than how that agent connects.
That is the whole case for a server-side mapping over a header, and it is an observation
about how the tool gets used rather than a measurement. If a key turns out to be mapped once
and never edited, a header would have been sufficient and cheaper.

It also assumes a key per agent. Two agents sharing one key share its mapping and cannot be
given different material, so the UI has to make issuing a key the obvious thing to do when
adding an agent rather than an administrative chore.

**Cost, accepted.** A password is the first credential here that a human chooses, so it is
the first that can be guessed. PBKDF2 at 600k iterations and the throttle above are what
stand in the way, and what remains is that someone able to reach the port can hold the delay
at its cap and make the owner wait that long to sign in. [07](07-auth.md) defers rate
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
the bearer model it has and gains no cookie. [07](07-auth.md) stops being about
tenancy. The three guards are unchanged, because enforcement is still the `corpus_id` filter,
the refusal to query on an empty scope, and `hnsw m=0`; they defend a smaller promise.
`TenantIsolationTests` keeps the unknown-corpus error, the empty-scope throw and the
unfiltered-query refusal, and loses its sharing cases.

**Revisit if.** Keys start being kept in sync by hand, which is when a named mapping earns
its entity. Or someone shares an endpoint across a team and wants a corpus only its owner can
reindex, which is the protection this gives up. Or a deployment has to be reproducible from
configuration, on a fresh machine or from a compose file, at which point a mapping that exists
only as catalogue rows is the wrong side of the trade above and wants an export or a
declarative form.

**Supersedes** [D-10](#d-10-static-tokens-and-a-tenant-header) in part: the credential format
and its storage stand, the tenant binding and `X-Dexicon-Tenant` do not. **Amends**
[D-04](#d-04-corpus-as-the-qdrant-tenant-key): `corpus_id` remains the `is_tenant` key, and
the `tenant_id` payload field goes. **Amends** [D-11](#d-11-five-mcp-tools): five tools, four
of which every key sees.

---

### D-29 An integration document, and retrieval in one call

**Decision.** The REST surface gains a second OpenAPI document, `integration`, describing a
named subset: `POST /api/search`, `POST /api/context`, `GET /api/corpora`,
`GET /api/corpora/{nameOrId}`, `POST /api/corpora/{nameOrId}/reindex`, `GET /api/jobs` and
`GET /api/jobs/{id}`. `Dexicon.json` goes on describing everything and goes on generating
the web client. `POST /api/context` is new: one call returning an assembled, cited passage
for a query, budgeted in characters. Outbound webhooks are rejected.

**Why a second document.** [02](02-architecture.md) describes the REST API as serving the
SPA, and that description is the only thing that makes it internal: `/api/search` already
takes the same `dex_` bearer, the same `search` scope and the same per-corpus mapping as
MCP. What the current document cannot carry is a stability promise, because it also
describes `/api/workspaces`, the model probe, pull and delete, `/api/session` and the SSE
stream, which are the admin screens' own plumbing. Publishing it whole as an integration
contract commits the project to the shape of the UI. A subset is a promise that can be
kept, and it carries its own version.

**Mechanism.** `AddOpenApi("integration")` alongside the default registration, with
`WithGroupName` on the endpoints that belong to it. Build-time generation writes
`clients/web-ui/Dexicon_integration.json` beside `Dexicon.json`: a document not named `v1`
takes the suffix. The staleness check in `ci.yml` names `Dexicon.json` by path and has to
name both, or the second document drifts without failing anything.

**Why a context endpoint.** `search_index` and `/api/search` are shaped for a caller with
an agent loop: ranked hits, a character cap per hit, and a second call to `get_context`
when a hit needs its surroundings. A hook, a CI step or a shell script has no loop.
Assembling a passage from those two today costs a round trip per hit and repeats
client-side the de-overlapping the server already does in `DexiconTools.Stitch`, including
its disclosure of gaps as `… lines N-M not indexed …`. One call returns the passage, its
citations, and what it had to leave out.

**Why characters, not tokens.** No tokenizer ships
([D-16](#d-16-approximate-token-counting),
[D-27](#d-27-a-chunk-budget-is-characters-and-the-ratio-is-measured)), and the consumer's
model is not the embedding model, so a `maxTokens` parameter would be an estimate in the
shape of a budget. The request takes `maxChars`; the response reports `usedChars`,
`truncated` and how many hits were dropped. `degraded` is carried through from the search
result, so a script about to paste keyword-fallback text into a prompt can tell that is
what it holds.

**Why not a sixth MCP tool.** [D-11](#d-11-five-mcp-tools). An agent already has
`search_index` and `get_context`, and a tool definition is context every agent pays for on
every turn, including the ones that would never call this.

**Rejected.** Outbound webhooks: an operator-supplied URL POSTed to from inside the
container is an SSRF path, and it brings signing, retry and a dead-letter story for a
question `/api/events` and `GET /api/jobs` already answer for any caller that can hold a
connection or poll. If polling proves too slow, the cheaper answer is a `wait` parameter on
reindex returning the terminal job state. A separate port or process for the integration
API ([D-01](#d-01-single-container) stands; a second listener adds a boundary that does not
exist here). `maxTokens` (above). Promoting the whole REST surface to a contract (above).

**As built.** `POST /api/context`, and `Dexicon_integration.json` generated beside
`Dexicon.json` in `clients/web-ui`. Both documents come from one build and carry the same
`major.minor`; CI checks both against the code. Assembly is a pure function over ranked
hits, so the budget arithmetic is tested without a vector store, and `Stitch` moved from
the MCP tool class into `Core/Search/Passage` where its four callers can reach it.

One detail is not obvious and cost a regression in the making: giving an endpoint a group
name removes it from every OTHER document under the stock `ShouldInclude` rule, so the
full document, which the web client is generated from, would have lost search, corpora and
jobs the moment they were selected into the integration one. The full document now takes
every endpoint explicitly, and a test asserts that it still does.

**Resolved: the running instance serves the integration document, and only that one.**
The framework's template maps `MapOpenApi` in Development alone, "to minimize the risk of
exposing sensitive information and reduce the vulnerabilities in production", and the
OpenAPI documentation's own remedy where the document is wanted anyway is to apply an
authorization check. The objection recorded here was to exposing the surface
*anonymously*, and `DexiconAuthMiddleware` denies by default, so `/openapi/integration.json`
is behind a bearer without an entry being added anywhere. Authentication and no scope: a
contract is not data, and any key at all can already reach the endpoints it describes.

The full document stays build-time only. Its consumer is a code generator reading the
committed file, and it describes the workspace browser, the model endpoints and sign-in.
`/openapi/v1.json` is a 404, explicitly rather than by omission, because the SPA fallback
would otherwise answer it with the index page and a 200.

What the served copy adds over the committed one is the version of the instance actually
answering. An operator can be running an image older than the checkout a consumer
generated from, and nothing in the file says so.

**Amended 2026-09-19: the last block may be cut.** Whole chunks only meant a budget below
the smallest matching chunk returned nothing at all. Measured on this project's book corpus,
one query's smallest chunk was 2,109 characters, so budgets of 1,500 and 2,000 both came back
empty with ten hits behind them, which reads as "nothing matched". It also left the tail of
every budget unspent, when the opening of the next result is the cheapest way to see that a
variant exists.

The last block may now be cut. At most one, always last, never in the middle, and only when
at least 300 characters of it would show, below which a citation and two lines of prose cost
more to read than they return. The cut falls on a line boundary, because a chunk may be
rendered with its line numbers and half a line has the wrong one. The passage says
`… N characters of this chunk not shown …` in the same register as `Stitch`'s gap
disclosure, and the citation reports the lines actually present rather than the chunk's full
span, so a citation is never a claim about text the caller was not given.

`partialBlocks` on the response and `partial` with `omittedChars` on each citation carry it
as data. They are separate from `truncated`, which goes on meaning that hits were dropped: a
budget that lost results and one that shortened them are different things to know, and one
flag covering both could be acted on for neither. A chunk with no line break inside the
budget cannot be cut, so it is dropped as before and the note names the figure that would
have fitted.

**Revisit if.** A pipeline needs to create corpora rather than search and refresh them.
[D-28](#d-28-an-admin-password-and-scoped-api-keys) keeps `admin` off issuable keys, so that
case means the admin password in CI. The narrower answer would be a `manage` scope covering
corpus, source and chunk-set changes while key minting, the password and model management
stay with the password. Not decided here.

---

### D-30 Skills and hooks install with the client, under a dexicon prefix

**Decision.** `scripts/install-mcp.ps1` becomes the one entry point for everything Dexicon
writes into someone else's configuration: the MCP registration it already does, the agent
skill, and two hooks. It gains `-What skill|hooks|mcp|all`, an upgrade path and an
uninstall. Every artefact it writes is named `dexicon-`: the skill directory becomes
`skills/dexicon-search/`, and the hooks are `dexicon-corpora.sh` and `dexicon-context.sh`.
The two hooks are `SessionStart`, which announces the corpora, and `UserPromptSubmit`,
which retrieves a passage for the prompt. Both read their settings from one file the
installer writes. `PostToolUse` reindexing is rejected.

**Why the installer owns them.** The skill is written and good, and nothing installs it.
[12](12-clients.md) says to copy `skills/dexicon/SKILL.md` into `.claude/skills/` by hand,
the README does not mention it at all, and nothing can tell whether the copy in a given
directory is current. Connecting an agent gives it the tools; the skill is what makes it
reach for them, so an install path that stops at MCP registration delivers the half that
does nothing on its own.

**Why the prefix.** `~/.claude/skills/` and `~/.claude/hooks/` are shared namespaces owned
by the user, not by us. A bare `dexicon` skill is unambiguous only until someone installs a
second tool with the same idea, and the prefix makes uninstall a matter of naming what we
own rather than guessing. It is the settled convention among tools that install into these
directories; [rtk](https://github.com/rtk-ai/rtk) is one worked example.

**The directory name is the slash command.** A skill is user-invocable as `/<directory>`
unless its front matter says otherwise, so the rename moves `/dexicon` to `/dexicon-search`,
and that is the visible cost of the prefix. It also exposes something the skill does not
currently do: invoked directly it loads guidance written for the model to read, and then
stops. The installed skill takes an argument, so `/dexicon-search how are refresh tokens
revoked` runs the search and reports the hits, while the guidance stays where it is for the
model to pick up on its own. Neither `disable-model-invocation` nor `user-invocable` is set,
so both routes stay open.

**The hooks talk to a server, not a local binary.** Dexicon runs in a container, so a hook
speaks HTTP to the endpoints
[D-29](#d-29-an-integration-document-and-retrieval-in-one-call) published for exactly this
caller, `POST /api/context` and `GET /api/corpora`, and needs a bearer token to do it. Three
things follow. The token is a `search`-scoped key under
[D-28](#d-28-an-admin-password-and-scoped-api-keys), never the admin password, so a hook
cannot reindex or delete anything. It does not go in the hook script, which lives in a
directory the user reads and backs up. And the call costs real time: measured against this
project's own index of 15,213 chunks, `POST /api/context` returned in 738 ms warm, while a
cold search on the same instance took 5,028 ms.

**Configuration is one file, and it is the file that holds the token.** The installer writes
`~/.claude/dexicon-hooks.env`; both hooks source it, and it is the only thing either of them
reads. `DEXICON_URL`, `DEXICON_TOKEN` and a timeout cover the connection. `POST /api/context`
already takes the knobs worth turning, so the file names them after the fields rather than
inventing a second vocabulary: `DEXICON_CONTEXT_MAX_CHARS`, `DEXICON_CONTEXT_LIMIT`,
`DEXICON_CONTEXT_MODE`, `DEXICON_CONTEXT_NEIGHBOURS`, and `DEXICON_CONTEXT_CORPUS`, which
points the per-prompt hook at one corpus while the agent's own searches still reach
everything. An unset key is not sent at all, so the server's default applies and the hook
does not carry a second copy of it to drift from. Editing that file is the whole
configuration story: nothing to change inside a script the installer overwrites on upgrade.

**Why `SessionStart` is the safe one and `UserPromptSubmit` is opt-in.** `SessionStart`
fires once and lists corpora, which is a catalogue read with no embedding in it. It answers
the failure this project keeps hitting, an agent that does not search because it does not
know what is indexed, at a cost paid once per session. `UserPromptSubmit` fires on every
prompt and runs a hybrid search, so the cold figure above lands on the user's first message
of the day. It is worth having and it is not worth having on by default.

**Fail open, and never exit 2.** On `UserPromptSubmit`, exit code 2 blocks the prompt **and
erases it**, so a hook that failed loudly would destroy what the user typed because the
search index was unreachable. Every path exits 0: no `curl`, no `jq`, no configuration file,
a refused or timed-out request, a 401, malformed JSON. A short connect timeout is part of
the contract, not a nicety, because the failure being guarded against is a server that
accepts the connection and then does not answer. Plain stdout is added as context on both
events, so the hooks emit text and do not construct `hookSpecificOutput` themselves.

**Versioned artefacts.** Each installed file carries `# dexicon-hook-version: N` on its
second line, and the skill carries the same in its front matter, so the installer can tell
an old copy from a current one, and either of those from a file the user has edited and
wants left alone. Without it, upgrading means overwriting someone's changes or asking a
question the installer has no way to answer.

**Rejected.** `PostToolUse` reindexing after `Edit` and `Write`: the server already refreshes
on a schedule, so this narrows a staleness window rather than closing a gap, and it makes
every file the agent touches queue work on a machine that may be embedding already. A hook
that rewrites or denies a tool call: a search index that silently redirected `Grep` to
`search_index` would be answering a different question from the one asked, and the skill
already says when each is right. Bundling the token into the hook script. A configuration
file per hook, or settings edited inside the scripts. A bare `dexicon` skill name (above).
Shipping hooks for every client in [12](12-clients.md): the hook APIs differ per client and
each one is a maintenance commitment, so Claude Code first, and a second client only when
someone asks.

**As built.** Python, not shell. The entry said `dexicon-corpora.sh` and
`dexicon-context.sh`, following the shape hooks usually take; the first draft of those was
written, and on this machine it exited 0 and printed nothing, because `jq` is not installed
on a stock Windows box and the hook had just been told to fail quietly. A hook silent for a
missing dependency and silent for a genuine empty result cannot be told apart by the person
running it. The standard library does HTTP and JSON with nothing to install, `scripts/`
already ships four Python programs, and the quoting hazards of assembling JSON in shell go
with it. The files are `dexicon-corpora.py`, `dexicon-context.py` and a shared
`dexicon_hook_lib.py`, and every failure path now warns on stderr as well as exiting 0.

Two figures moved a default. The server's `maxChars` default of 8,000 returned 6,963
characters for one query, which is too much to put in front of every prompt, so the hook
sets 4,000 and says why it differs rather than inheriting. The entry's rule that an unset
key is not sent still holds for every other field. And nothing is truncated to fit: a budget
under the smallest matching chunk returns an empty passage, measured at 2,109 characters for
one query on the book corpus, with 1,500 and 2,000 both returning nothing. The server
already composes a note naming the figure that would have fitted, so the hook prints that
rather than inventing a worse one: on stderr when there is no passage, and into the model's
context when there is, since "still indexing; results are incomplete" is something the
reader needs.

`Get-Interpreter` runs each candidate rather than trusting `Get-Command`. Windows ships a
zero-byte `python3.exe` in WindowsApps that opens the Microsoft Store when no Python is
installed, and `Get-Command` finds it either way, which would install a hook that cannot
start.

**Revisit if.** The `UserPromptSubmit` hook proves useful enough to default on, which needs
a relevance floor Dexicon does not currently expose: `POST /api/context` reports `usedChars`,
`truncated` and `droppedHits`, but no score, so a hook cannot yet tell a good passage from
the best of a bad set. Fused RRF rank is ordering, not magnitude, so the figure would have to
be chosen and measured the way the chunk ratio was in
[D-27](#d-27-a-chunk-budget-is-characters-and-the-ratio-is-measured). A `minScore` parameter,
or returning the fused score, is the smaller change that would make the default defensible.

---

### D-31 A chunk is an index entry, and the model decides how big it can be

**Decision.** A chunk stops being the unit a search returns and becomes the unit a search
*finds*: a locator into a document. What a caller reads is assembled from the document
around the hit, at whatever width the caller asks for, which is what `get_context` and
`POST /api/context` already do.

Sizing follows from that. Splitting starts from the whole document and subdivides at a
boundary whenever the provider refuses the input, until every piece is accepted. The model
decides the size, so nothing predicts it. A smaller target may be configured, and then the
recursion is only the guarantee behind it. `TextDensity` is deleted and `CharsPerToken`
stops being load-bearing. Amends [D-16](#d-16-approximate-token-counting) and
[D-27](#d-27-a-chunk-budget-is-characters-and-the-ratio-is-measured).

**What was actually wrong.** The chunk budget is defined in tokens and enforced in
characters, and everything built on that conversion leaks. One index run logged 212
truncation warnings across 74 files, 146 of them in `books`. A truncated embedding is not a
short vector: it is a vector for the opening of a chunk, stored as the vector for the whole
chunk, so the tail is unreachable by meaning and nothing downstream can tell.

The conversion cannot be made exact from this side. Ollama has no tokenize endpoint;
`/api/embed` returns vectors and an aggregate `prompt_eval_count` and nothing per token, so
token boundaries are not observable. A local tokenizer would be exact only if it matched the
model actually loaded, which cannot be shown: a re-pull at a different quantisation or a
provider-side template breaks it silently, and that is worse than a ratio, because a ratio
announces itself as an approximation. It also couples the index to a tokenizer, which is the
downside AI Engineering names for token-based chunking: change the tokenizer and reindex.

**The refusal is the measurement, and it is nearly free.** `EmbeddingService` already sets
`truncate: false` so the provider refuses rather than silently shortening. Measured against
this project's own instance:

| input | outcome | time |
|---|---|---|
| 2,000 chars | accepted | 1,429 ms |
| 8,000 chars | accepted | 4,540 ms |
| 20,000 chars | refused | 356 ms |
| 200,000 chars | refused | 449 ms |
| 600,000 chars | refused | 1,071 ms |

A refusal costs about 350 ms and is flat in input size, because the length is checked before
any work is done. Rejecting a whole book costs less than embedding one chunk of it. Walking
a 400,000-character document down to accepted pieces is about six levels, so a couple of
seconds of refusals against minutes of embedding.

Set against that, `TextDensity` embeds three 3,000-character windows per file to estimate a
ratio, which is roughly six seconds a file, or about twenty minutes across the 189 files in
`books` on a full reindex. It spends that to approximate what a 350 ms refusal reports
exactly, and it still let 146 truncations through in the same corpus. The preventive
mechanism costs more than the failure it prevents.

**Why the chunk was the wrong unit to tune.** The retrieval benchmark measures "the rank of
the file that should answer each query... not relevance or passage quality". Its best single
configuration on documents was `embeddinggemma` at 256 tokens, and that result was set aside
twice: "smaller chunks flatter this metric: a file split finer has more chances to land one
chunk in the top ten."

That is only flattery if the chunk is what the caller receives, which makes file rank a
proxy for passage quality. If the chunk is a locator, landing one in the top ten is not a
proxy for the job, it is the job, and the measurement was answering the right question all
along. The same data then says small locators are better, and the reason to discount it was
the assumption this entry removes.

**What the size is for.** Locator precision, not capacity. A small chunk points at a
narrower part of a document, and nothing about it needs to approach a context limit, so the
whole class of overflow stops being reachable in normal operation rather than being guarded
against. Chunk boundaries also stop being load-bearing for what a caller reads, because the
passage is reassembled around the hit either way. They remain how the text is stored and
what a reindex is diffed against.

**The default is self-sizing.** With no configured target, indexing starts from the document
and subdivides on refusal until every piece is accepted. This needs no knowledge of the
model, no ratio, no probe and no configuration, and it is correct against a model nobody has
told it about. A configured target, expected to be small, is a preference for locator
precision; the recursion sits behind it as the guarantee that a wrong target degrades into
smaller pieces rather than into truncated vectors.

**Rejected.** Shipping a tokenizer: unprovable parity, and it couples the index to the
tokenizer. More sample windows in `TextDensity`: a smaller residual on the same class of
error, at a cost already larger than the error. A feedback loop correcting the ratio from
`prompt_eval_count`: still a prediction, still a second mechanism for a number the refusal
settles, and it cannot act until after the chunk it would have sized. A fixed fraction of
the context as a ceiling: the same prediction with a margin on it. Keeping `TextDensity`
beside the recursion: two mechanisms for one decision is how the next inconsistency arrives.

**Consequences.** Chunk boundaries move, so every corpus re-chunks and re-embeds once,
the same cost as an extractor version bump. That happens because `CodeChunker.Version` goes
to 8, not on its own: the per-file ratio narrowed the cut but was computed after the
fingerprint and never entered it, so the key for a file whose density differed from its
model's average is identical before and after. Without the bump those files, which were
most of them, would be skipped as unchanged and keep chunks no code path can produce.

Two claims here are not yet measured and should not be reported as though they were. The
benchmark scores file rank, which is the right metric for a locator, but nothing has scored
the passages assembled from a document around a small-chunk hit, and that is what the design
rests on. And there is a floor on smallness where a chunk carries too little meaning to
embed distinctively: 256 tokens is demonstrated, below that is untested.

**Revisit if.** Assembled passages measure worse than returned chunks on a retrieval
evaluation that scores passages rather than file rank. That is the experiment this entry
asks for, and the one result that would overturn it.
---

### D-32 Discovery is its own pass, and does not queue behind indexing

**Decision.** Walking a corpus and indexing it become separate pieces of work. A sweep
resolves the source's filters, walks the tree, applies source shadowing and writes the
file inventory, then stops: nothing is extracted, chunked or embedded. It runs on its own
lane, so a sweep of one corpus does not wait for another corpus to finish indexing. A file
that has been swept and not yet indexed is `Pending`, and a corpus reports that count
beside its indexed one rather than folding the two together.

**Why.** A corpus added while another was indexing read as empty. `mcptoolbox` showed
"0 files, 0 chunks, never indexed" with a valid source and 109 entries visible under it,
because its job sat behind a `tpn` reindex of roughly 1,800 PDFs on a queue that runs one
job at a time. Nothing was broken and nothing said so: the only way to learn what a corpus
contains was to wait for the expensive work to reach it.

The two costs are not comparable. Statting all 1,804 files of that library through the
container's 9p mount takes 2.08s, which is the syscall floor rather than a run of
`WorkspaceWalker`: the real walk evaluates gitignore, globs and size caps on top, in
process and against the same syscalls. Extracting one ordinary 204 KB PDF from it takes
773ms, and an intact 84 MB one takes 13.4s, before anything is embedded. Discovery is
roughly three orders of magnitude cheaper than the work it is currently queued behind,
and it is the half that answers "what is in here".

Most of the seam is already cut. `IndexedFile` is the inventory and belongs to a source;
`FileChunkState` is per file and chunk set and carries the status, a split the entity
already documents. `FileStatus.Pending` is commented "discovered, not yet chunked".
`Track` writes both rows. `WorkspaceWalker.Walk` and `SourceScope.ShadowedPrefixes` have
no indexing in them. `pendingCount` is already computed by the corpus endpoint and already
rendered by `ChunkSets.tsx`. What is missing is a caller that stops after the walk, and a
lane for it to run on.

**Rejected.** A new `JobKind` on the existing queue. `IndexJobQueue` is one channel with a
single reader, so a discovery job would wait behind precisely the work it exists to get in
front of: correct, and useless.

Redefining `fileCount` to include pending files. It counts `Indexed` today and every
display reads it that way, next to a chunk count. Widening it silently changes what the
number means on every screen, and the separate count the UI can already render says the
same thing without the ambiguity.

Sweeping a corpus while that same corpus is indexing. Both write `IndexedFile` rows for
the same source, and the reconcile phase deletes rows for files that have gone. Two
writers with deletion on one side is a race for no benefit, since the indexing pass is
walking the tree anyway.

Excluding them needs a claim, not a look. `CorpusState.Indexing` is set inside `RunAsync`,
after the job has been taken off the queue, so a sweep that reads the state and then starts
can be overtaken by an index job that starts in the gap, and both write. The exclusion is
therefore an atomic per-corpus claim: whichever pass takes it runs, the other does not, and
taking it is one conditional write rather than a read followed by a write. If that claim
carries an expiry so a crashed holder cannot block the corpus forever, the expiry has to
exceed the longest legitimate hold, which for indexing is hours on a library this size. An
expiry chosen for how long a sweep takes would release the claim under a running index
job, which is the failure it was added to prevent.

**Consequences.** Two things have to be settled before this lands rather than discovered
during it.

The catalogue is SQLite, opened as `Data Source=…;Cache=Shared`. What that produces was
measured rather than read off the connection string, because the two obvious readings of
it are both wrong.

A database created by that string reports `journal_mode=delete` and `busy_timeout=0`. This
machine's catalogue is in WAL anyway: bytes 18 and 19 of its header both read 2. Journal
mode is persistent once set, so this file carries it and nothing in the startup path
establishes it. A fresh deployment therefore gets rollback journalling, where readers block
writers as well, and behaves differently under a concurrent sweep from the machine the
feature was designed on.

`busy_timeout=0` does not mean the second writer is refused at once. Microsoft.Data.Sqlite
retries for the command timeout, 30s by default. Measured against a held write transaction,
a second connection failed after 30,108ms with `SQLite Error 5: 'database is locked'`. The
cost of contention is not a fast error but a thirty-second stall and then an error, which
is the worse outcome for a sweep whose whole justification is being the quick half.

Setting the journal mode and a busy timeout explicitly, and writing the sweep in batched
transactions, are part of this change rather than a follow-up. The timeout is sized against
the longest write the other side can hold, not chosen as a round number: it is the batch
size that makes that bound exist, so the two are picked together or neither is meaningful.

Indexing stays the only pass that removes anything, and the reason is not symmetry.
Deleting a file that has vanished means deleting its vectors from every set's collection,
and the `IndexedFile` row can only go once the last set has let go of it, which is what
`CorpusIndexer` already threads carefully. A sweep knows nothing about collections and
touches none, so a sweep that removed rows would strand the vectors those rows named. A
sweep only ever adds.

There is deliberately no exemption for rows that look empty. `Pending` is not evidence
that a file has no vectors: the upsert runs before the status is set to `Indexed`, and the
save is throttled to about a second, so a crash between them leaves a durable `Pending`
beside vectors that exist. A sweep deleting on that reading would strand exactly what the
rule above exists to protect. Absent and unobserved arrive identically here, and the
ambiguous one must not drive a delete.

The cost is that a corpus which is only ever swept keeps rows for files that have since
gone, including ones that were never indexed at all. They are removed the first time it is
indexed, which is the path every corpus is on anyway.

What remains is that a swept-but-unindexed corpus over-reports: it lists files that have
since disappeared until an indexing pass for each set has removed them. That is the
accepted cost and it is the safe direction to be wrong in, because an inventory briefly
too large is a display problem, while one too small hides files that are really there.

The inventory is one row per file and the status is one row per file and chunk set, so a
sweep of a corpus carrying two sets writes two `Pending` rows for everything it finds.
`StatesFor` backfills those during indexing today, which is how a set added to a corpus
full of documents comes to have rows at all. Whether adding a set now triggers a sweep, or
indexing keeps that job, is the same question as reconcile in a different place: one writer
or two.

**Revisit if.** The sweep grows expensive enough to need its own progress and
cancellation. A tree of a million files is a different problem from 1,804, and at that
size discovery stops being the cheap half.
---

## Open questions

| # | Question | Needed by | Current lean |
|---|---|---|---|
| ~~Q1~~ | ~~Licence — Apache-2.0 or MIT?~~ | — | **Resolved** — Apache-2.0, see [D-14](#d-14-licence) |
| ~~Q2~~ | ~~Repository name and GHCR namespace~~ | — | **Resolved** — see [D-17](#d-17-name) |
| ~~Q3~~ | ~~Default embedding model — `nomic-embed-text` or `embeddinggemma`?~~ | — | **Resolved** — `embeddinggemma`, which won both sweeps. See [benchmarks](benchmarks.md) |
| ~~Q4~~ | ~~Should `index_refresh` require the `ingest` scope, or be admin-only?~~ | — | **Resolved** — `ingest`, granted per key and off by default, with the tool hidden from keys that lack it. See [D-28](#d-28-an-admin-password-and-scoped-api-keys) |
| Q5 | Git history indexing in v1? | M2 scope freeze | No. M5, and only on request |
