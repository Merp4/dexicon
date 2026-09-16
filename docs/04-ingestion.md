# 04 — Ingestion

## Sources

Two kinds, same pipeline after discovery.

### `workspace` — recursive folder index

A path under `/workspaces`, bind-mounted read-only from the host. This is the code-search
case: point Dexicon at a repository and it indexes the tree.

Discovery walks the tree and applies, **in order**:

1. **Always-exclude** — binaries, media, archives, build output, VCS internals. Hard-coded,
   not configurable, because nothing good comes of embedding a `.dll`:
   `.git/`, `**/node_modules/**`, `**/bin/**`, `**/obj/**`, `**/.vs/**`, `**/.idea/**`,
   `**/target/**`, `**/dist/**`, `**/__pycache__/**`, and by extension:
   `exe dll pdb so dylib o obj a lib zip tar gz 7z rar jar woff woff2 ttf eot
   ico png jpg jpeg gif bmp webp svg mp3 mp4 avi mov wav db sqlite sqlite3
   safetensors gguf bin pt pth pkl npy npz`
2. **`.gitignore`** — honoured by default, full gitignore glob semantics, nested files
   respected. Disable per source with `use_gitignore: false`.
3. **`.dexiconignore`** — same syntax, for things that are checked in but not worth
   indexing (lock files, generated clients, vendored trees). Separate from `.gitignore` so
   you never have to change VCS behaviour to change index behaviour.
4. **Per-source `exclude_globs`**, then **`include_globs`** as an override.
5. **Size cap** — `max_file_bytes`, default 256 KB. A file over the cap is recorded as
   `skipped` with the reason, never silently dropped.
6. **Binary sniff** — a NUL byte in the first 8 KB means binary, regardless of extension.

Text extraction on a workspace source is `File.ReadAllText` with encoding detection (BOM,
then UTF-8, then Latin-1 fallback). Document formats (PDF, DOCX, …) found inside a
workspace tree **are** extracted with their loaders — a repo with reference PDFs in `docs/`
gets them indexed.

### `upload` — files pushed through the UI or API

Bytes are content-addressed into `/data/blobs/<sha256[0:2]>/<sha256>` and recorded in
`blobs`. Two uploads of the same file store one blob.

**Extraction is cached against the blob hash, and chunking is not.** That split is the
whole design, and it is what makes the same document cheap to hold several ways:

```
  bytes         content-addressed          uploaded twice -> one blob
  extraction    cached per blob hash       deterministic, expensive, done once ever
  chunking      a property of the CORPUS   cheap, and the thing people vary
```

So:

- Uploading the same PDF twice stores one copy and reuses the extraction. Measured on a
  1.5 MB, ~400-page PDF: **1.823 s** the first time, **0.119 s** the second.
- **Attaching one document to two corpora with different chunk settings produces two
  independent chunk sets**, without re-uploading or re-opening the file. A 12,238-character
  document at `768/100` gives 5 chunks; the same document at `256/40` gives 19.
- Changing a corpus's chunk settings re-chunks and re-embeds from cached text. Only
  embedding is repeated, and embedding is the slow part.

A corpus gets at most one upload source, created on first attachment. Detaching removes
that corpus's chunks only — the blob survives, because another corpus may still hold it.

Limits: 200 MB per file (`DEXICON__UPLOAD__MAXFILEBYTES`). Uploads are buffered to a temp
file rather than memory, because the hash is only known once the whole stream is read and a
200 MB upload should not be a 200 MB allocation.

**Staleness is a chunking fingerprint**, not a content hash: `sha256(blob | chunkSize |
chunkOverlap | boundaryMode | model)`. With a bare content hash, changing a corpus's chunk
size left every file looking unchanged, so a refresh re-chunked nothing and the new setting
silently did not apply. The fingerprint makes exactly the right set of files look stale, and
no others.

## Extraction

| Format | Extensions | Library | Licence | Provenance unit | Notes |
|---|---|---|---|---|---|
| Plain text | `.txt`, `.log`, code | — | — | line | Encoding-detected |
| Markdown | `.md`, `.markdown` | Markdig | BSD-2 | line + heading | Headings become `section` |
| HTML | `.html`, `.htm` | AngleSharp | MIT | line | One line per block element; `script`/`style` skipped; `<title>` kept |
| PDF | `.pdf` | PdfPig | Apache-2.0 | **page** | Text layer only — no OCR |
| DOCX | `.docx` | DocumentFormat.OpenXml | MIT | paragraph | Headings become `section` |
| PPTX | `.pptx` | DocumentFormat.OpenXml | MIT | **slide** | Slide notes included |
| EPUB | `.epub` | VersOne.Epub | MIT | **chapter** | Reading order from `content.opf`; chapter HTML walked block by block |
| JSON/YAML/TOML | `.json`, `.yaml`, `.yml`, `.toml` | — | — | line | Treated as text; structure-aware chunking is not attempted |

Every loader returns `(text, metadata, unitMarkers)`. Failures are per-file and recorded:

- **PDF with no text layer** → `status: empty`, `status_detail: "no text layer — scanned
  PDF, OCR not supported"`, and the file is visible in the UI as ingested-but-empty. It is
  not reported as a success and not silently missing.
- **Encrypted / DRM** → `status: failed` with the reason.
- **Malformed archive (EPUB/OOXML)** → `status: failed` with the reason.

### Block structure is content

HTML-derived formats (HTML, EPUB) are walked block by block, emitting one line per `<p>`,
heading, list item or table cell. The obvious implementation — AngleSharp's `TextContent` —
concatenates every descendant text node with no separators, and it was what Dexicon shipped
first.

It failed in a way nothing could detect. The chunker splits on line boundaries, so a chapter
on one line cannot be split: a 578,000-character EPUB became 18 chunks averaging 32,000
characters each, every one of them far past the embedding model's context window. Ollama
truncates silently, so roughly 95% of that book existed in no index anywhere — while the
file, the job and the corpus all reported success.

Newlines here are not cosmetic. They are what makes text chunkable, and what makes a line
number in a search result mean anything.

### Extraction is cached, and the cache is versioned

Extraction is cached per blob in `blob_texts`: a 437-page PDF costs ~1.8 s to extract and
its bytes never change, so re-extracting on every reindex would be waste.

But the *code* changes. `ExtractorVersions.Current` is stamped on every cached extraction
and bumped whenever extraction output changes; text from an older version is re-extracted
the next time it is indexed. The version is also part of the chunking fingerprint, so the
re-extracted text is actually re-chunked rather than skipped as unchanged.

Without this the cache is permanent: a library ingested before a fix keeps the broken text
forever, and no reindex repairs it, because reindexing re-chunks the *cached text* rather
than re-reading the file.

| Extractor version | Change |
|---|---|
| 1 | Initial extractors |
| 2 | HTML and EPUB keep block structure — one block per line |

The chunker carries its own version for the same reason, one stage later: without it the
fingerprint says "same bytes, same settings, nothing to do" and a corpus keeps chunks from
an algorithm that no longer exists — indefinitely, because skipping unchanged files is
exactly what an incremental refresh is for.

| Chunker version | Change |
|---|---|
| 1 | Initial chunker |
| 2 | Size decides *when* to split; a line over the whole budget is split |
| 3 | Heading context, unit-aware boundaries, sentence-aware splitting |

### A note on OCR

Out of scope ([01](01-overview.md)). The seam is an `ITextExtractor` per media type, so an
OCR extractor can be registered later without touching the pipeline. Nothing about the
schema assumes text came from a text layer.

## Chunking

Two strategies, selected by content kind, both producing chunks with provenance.

### Code and plain text — line-accumulating, language-aware

Accumulates whole lines up to `chunk_size` tokens with `chunk_overlap` carried into the
next chunk, so every chunk has an exact `start_line`/`end_line`. Never splits a line.

With `boundary_mode: language-aware`, the file is first split at member boundaries by
language, then each segment is size-chunked. This keeps a method with its signature instead
of slicing it at an arbitrary token count. Patterns per language (carried from McpToolbox,
which learned them the hard way):

| Language | Boundary |
|---|---|
| C# | `^\s*(public|private|protected|internal|static|abstract|sealed|override|virtual|async)\s` |
| TypeScript / JavaScript | `^(export )?(default )?(async )?(function|class|const|let|var|interface|type|enum)\b` |
| Python | `^(async def |def |class )` |
| Go | `^(func |type |var |const )` |
| Rust | `^(pub )?(fn|struct|impl|trait|enum|mod|type)\b` |
| Java / Kotlin | access-modifier anchor at line start |
| SQL | `^(CREATE|ALTER|DROP|SELECT|INSERT|UPDATE|DELETE|MERGE|WITH)\b` |
| CSS / SCSS / LESS | rule opener `^[.#\[\w@:][^{]*\{` |
| Markdown | ATX headings |
| HTML, Razor, Vue, Svelte | **blank line** — these are template *source*, not documents; splitting them on `<h1>` produces nonsense |
| everything else | blank line |

Boundary modes: `none` | `blank-line` | `language-aware` | `custom` (operator regex, compiled
with a 500 ms timeout; an invalid or timing-out regex fails the job with a clear error and
never silently falls back).

### No chunk exceeds the budget, ever

The chunker prefers not to split within a line, so that every chunk carries exact
`start_line`/`end_line` and a hit is directly openable in an editor. One case overrides
that: a single line longer than the whole budget is split at word boundaries, with each
piece keeping that line's number.

This is a hard guarantee rather than a convention, because the failure mode is invisible —
an over-budget chunk is not rejected by the embedding model, it is silently truncated, and
the missing text is reported as indexed. A property test asserts the bound across chunk
sizes, including on input with no spaces at all (a minified bundle, a base64 blob).

### Chunk sets — a corpus can be cut several ways at once

Chunk size, overlap, boundary mode and the embedding model belong to a **chunk set**, not
to the corpus. A corpus owns content, sources and visibility; a set owns a vector space and
a strategy, and a corpus can carry several over exactly the same documents.

```
corpus "library"          content, sources, who can read it
  ├── set "default"       nomic-embed-text, 768/100, language-aware   ← search lands here
  └── set "fine"          nomic-embed-text, 256/40, heading context
```

Sets are addressed as `corpus:set`. An unqualified name means the default set, which is
what an agent that has never heard of sets will send.

This is what makes changing the embedding model safe. A collection's name encodes the model
and its dimensionality, so a different model is a different vector space — and re-embedding
a three-book corpus was measured at roughly twenty minutes on CPU Ollama. Editing in place
would mean twenty minutes of half-populated results, so instead:

1. add a set on the new model — it backfills in the background
2. the live set keeps serving search throughout
3. promote when it is complete; promotion is one `UPDATE` and the only moment search changes
4. drop the old set

Promoting a set that still has pending files is refused, with a count of what is left.
Promoting a half-built set is precisely the outage that building it separately prevents.

It is also the honest home for "the same document, chunked two ways". That worked before
only by duplicating the corpus, which duplicated its grants and its sources along with it.

### Embedding providers

A chunk set names a **provider** and a **model**. Ollama is configured by default and
needs nothing; OpenAI and Azure OpenAI are opt-in:

```
DEXICON__EMBEDDING__PROVIDERS__openai__KIND=OpenAI
DEXICON__EMBEDDING__PROVIDERS__openai__APIKEYENVVAR=OPENAI_API_KEY
DEXICON__EMBEDDING__PROVIDERS__openai__MODELS__0=text-embedding-3-small
OPENAI_API_KEY=…
```

The configuration names **the environment variable** holding the key, not the key.
Configuration files get committed; environment variables do not. The catalogue records
only which provider a set uses — a database row that carries an API key is a row you
cannot back up casually, and Dexicon's backup instructions say to copy the catalogue.

A provider that is configured but missing its credential is reported as such in the
Models screen and in `/api/embedding-providers`, rather than looking identical to a
working one until someone picks it and an index fails an hour later.

The model travels as a **per-call argument**, never bound into a client at startup. Chunk
sets choose models at runtime, so anything resolved from configuration at boot — keyed DI
included — cannot see a set created five minutes ago. See
[D-22](decisions.md#d-22-the-embedding-model-is-a-per-call-argument).

The collection name carries the provider too — `dexicon__ollama__nomic-embed-text__768` —
because two providers can serve a model of the same name and those are not the same
vectors.

Listing, pulling and deleting models is separate from embedding, and only local providers
support it. You cannot pull a model into OpenAI; its catalogue is a fixed list, configured
rather than discovered.

### Knowing a model's limits, without indexing anything

`POST /api/embedding-models/probe` measures what a model will actually accept:

```json
{ "dimensions": 768, "maxInputChars": 11776, "truncatesSilently": true,
  "recommendedChunkTokens": 1962, "embedCalls": 24 }
```

This exists because of a failure nothing could detect. An EPUB produced chunks averaging
32,000 characters; the model silently truncated every one of them, roughly 95% of the book
was in no index anywhere, and the file, the job and the corpus all reported success. A
truncating model returns a perfectly good vector for the part it read.

Truncation is silent but **empirically visible**: embed a text, then embed the same text
with distinctive content appended. If the tail was read, the vector moves. If it did not,
the vector is unchanged. Bisecting on that finds the real limit in about two dozen short
calls, with no documentation to trust and nothing indexed.

Measured on this stack, both `nomic-embed-text` and `embeddinggemma` accept about 11,776
characters of English prose — 2,048 tokens — and **truncate silently** beyond it. Neither
errors. The recommendation is two thirds of the measured figure, because the measurement
is in characters and the model counts tokens: code, minified output and CJK reach the same
token limit in far fewer characters.

### Meaning, not just budget

Chunk size exists because of the embedding model's context window. Everything else here
exists because a chunk that ends mid-thought retrieves badly regardless of how well it
fits. Each is per-set and off by default.

**Heading context.** The heading trail — `Data model > Point payload > Storage budget` — is
prepended to the text that gets EMBEDDED, so a chunk's vector carries the section it came
from. The stored text stays verbatim, because that is what search returns, what
`get_context` stitches, and what a `dexicon://` resource read reconstructs a file from;
prepending to it would insert lines the file never had and break a reconstruction that is
byte-identical today.

The trail is resolved per line, up front. Reading a running cursor at the moment a chunk is
emitted looks equivalent and is not: the accumulator fills *past* a boundary before backing
up to it, so the cursor is always ahead of the chunk being flushed. Chunks came out labelled
with a heading from further down the file — confidently, and wrongly, which is worse than no
label because retrieval then files them under a section they are not in.

**Unit-aware boundaries.** Page for PDF, chapter for EPUB, slide for PPTX. These are the one
kind of boundary that *forces* a split rather than offering a place for one — everywhere
else the rule is "size decides when, a boundary decides where", and that rule is useless
here: a chapter shorter than the budget would simply be swallowed into the next one. The
cost is the caller's choice: a document of very short pages yields short chunks, because
that is what page-aligned chunking means.

**Sentence-aware splitting.** When a split lands inside a line, cut at a sentence rather
than a word. A terminator counts only when followed by a space, so `e.g.` and `3.14` are not
read as the end of a thought. The backup window is half the budget rather than the eighth a
word search uses — with the narrow window it never fired, and a setting that costs something
and does nothing is worse than no setting.

**Custom boundaries.** The `custom` mode with your own regex, compiled at the request that
sets it rather than part-way through a job an hour later.

> **Not implemented: LLM-driven chunking.** Asking a model to decide where the meaningful
> seams are is the obvious next step and is deliberately not here. It needs a decision about
> which model does the curating and what it costs per document — a 400-page book is hundreds
> of calls — and that is a product question, not a missing function. The seam is
> `ChunkOptions`: a strategy that needs a model is a new flag and a new branch, not a rewrite.

### Size decides *when* to split; a boundary decides *where*

This is worth stating precisely, because the first implementation got it backwards and the
bug was invisible.

The chunker fills to the size budget, then **backs up to the most recent boundary inside
the buffer**. A chunk therefore holds as many whole members or paragraphs as fit, and still
never ends mid-thought. If the buffer contains no boundary at all, it splits where it is.

The original version split at *every* boundary. That made `chunk_size` dead configuration
in every mode but `none`: blank-line mode on prose emitted one chunk per paragraph —
measured at a **252-character mean against a 3,072-character budget** — and two corpora
configured 768 and 256 tokens produced byte-identical output. Chunks that small retrieve
badly; there is not enough context in a paragraph to embed usefully.

Five regression tests pin the corrected behaviour, the load-bearing one being that a
smaller `chunk_size` must produce more chunks.

### Documents — unit-aware overlapping

Splits at the format's natural unit first (page, slide, chapter, heading), then
size-chunks within the unit. `page` / `section` are carried into the payload so a citation
can say *"p. 34"* rather than *"chunk 87"*.

### Defaults

| Setting | Default | Reasoning |
|---|---|---|
| `chunk_size` | 768 tokens | Fits comfortably in every candidate embedding model's window; big enough to hold a method with context. |
| `chunk_overlap` | 100 tokens | ~13%. Enough to survive a boundary landing mid-thought. |
| `boundary_mode` | `language-aware` | The reason to run this over grep is chunks that mean something. |

Token counts are approximated at 4 characters per token. Exact tokenization would mean
shipping the model's tokenizer per model; the approximation costs a few percent of window
and removes a whole dependency. Chunk size is a target, not a contract.

### Symbol extraction

A lightweight per-language regex pass pulls declared names (types, functions, methods) in
the chunk into `symbols[]`, indexed as a keyword. This makes `symbol:TokenService` filtering
cheap. It is **not** a parser and makes no claim to be one — it will miss things, and the
docs say so rather than implying call-graph fidelity.

## Embedding

- Provider: Ollama, over `Microsoft.Extensions.AI` abstractions (`IEmbeddingGenerator`), so
  another provider is a registration, not a rewrite.
- Batched: up to 32 chunks per request, bounded by `MaxConcurrentEmbeddings` (default 4).
- Per-request timeout 2 minutes; 2 retries with jitter.
- **Capped exponential backoff** on repeated failure: 5s → 10s → 20s … → 320s cap. While
  backed off, the job reports `degraded` with the failure count and next retry time.
- A single chunk that fails after retries skips its **file** (not the scan), records the
  reason, and flags the job degraded. The file's hash is deliberately not written, so the
  next scan retries it. One oversized chunk must never be able to starve the rest of a
  repository — this is a bug that actually happened upstream and cost ten hours of a stuck
  index.

### Model choice

Pinned per corpus at creation. Candidates, all available through Ollama:

| Model | Dims | Size | Use for |
|---|---|---|---|
| `nomic-embed-text` | 768 | ~300 MB | Default. Fast, small, good general text. |
| `embeddinggemma` | 768 | ~620 MB | Best small-model code retrieval measured to date. Preferred for code corpora once verified locally. |
| `qwen3-embedding:0.6b` | 1024 | ~1.5 GB | Strongest general quality per VRAM; 32k context; multilingual. |
| `bge-m3` | 1024 | ~2.2 GB | Long documents (8k context). |

The default ships as `nomic-embed-text` because it is the smallest thing that works on any
machine. The UI surfaces the trade-off at corpus creation, and M3 of the
[roadmap](11-roadmap.md) benchmarks them on a real repository rather than trusting the
table above.

## Sparse encoding

Computed in-process, no model: lowercase, split on non-alphanumerics, additionally split
`camelCase` / `PascalCase` / `snake_case` / `kebab-case` into their parts **while keeping
the original token**, drop stopwords, then emit `{ term_hash: term_frequency }`.

The identifier splitting matters for code: a query for "token refresh" should reach
`TokenService.RefreshAsync`. Qdrant applies the IDF component itself because the sparse
vector index is declared `modifier: idf`, so Dexicon never has to maintain corpus
statistics. Term hashing is a stable 32-bit hash of the term; collisions are rare enough to
be noise and the alternative is a vocabulary to version.

## Incremental refresh

```
for each discovered file:
    hash = sha256(content)
    if catalog.hash == hash      -> skip          (unchanged)
    if catalog.hash != hash      -> delete chunks by (corpus_id, file_path), re-chunk, re-embed
    if not in catalog            -> chunk, embed
after the walk:
    for each catalog file not seen -> delete its chunks and its row
    write hashes only for files that fully succeeded
```

Two properties this buys:

- A refresh over an unchanged tree makes **zero** embedding calls.
- A failed file is retried next time, because its hash was never recorded.

The hash above is the **chunking fingerprint**, not the content hash alone:

```
sha256(blob | chunk_size | chunk_overlap | boundary_mode | custom_pattern
       | unit_aware | sentence_aware | heading_context
       | embedding_model | extractor_version | chunker_version)
```

Everything that determines what ends up in Qdrant is in it, and it is computed **per chunk
set**. Two sets over the same blob get different fingerprints and independent vectors, which
is what makes several chunkings of one document work at all — including two sets on
different models, mid-migration, in two different collections.

Scheduling: on demand (UI button, `index_refresh` MCP tool), plus an optional interval per
corpus, default off. There is no filesystem watcher — polling with content hashes is more
reliable over bind mounts, particularly on Windows hosts and WSL2, and is not an area where
cleverness pays.

## Job semantics

One job runs at a time per instance, in a bounded in-process queue. Queuing a refresh for a
corpus that already has one queued is a no-op returning the existing job id, not a second
job.

Job kinds: `full` (everything, ignoring hashes), `refresh` (incremental, the default),
`rebuild` (new embedding model — writes into the new collection, drops the old points only
on success), `delete`.

Progress events are emitted per file and coalesced to at most 4/second onto
`GET /api/events` (SSE). The UI shows phase, counts, current file, and — because it is the
question people actually have — the estimate of remaining time derived from the trailing
rate.
