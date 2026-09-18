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
4. **`exclude_globs`**, then **`include_globs`** as an override.
5. **Size cap** — `max_file_bytes`, default 256 KB. A file over the cap is recorded as
   `skipped` with the reason, never dropped without record.
6. **Binary sniff** — a NUL byte in the first 8 KB means binary, regardless of extension.

### Where steps 2, 4 and 5 get their values

`use_gitignore`, the two glob lists and `max_file_bytes` resolve through three layers,
narrowest first:

```
  the source's own value        set on one folder
  the corpus default            inherited by every source that sets none
  the configured value          DEXICON__INDEXING__*
```

Each field resolves on its own, so a source that only wants a larger cap still follows the
corpus on globs. Inheritance is **live**: changing a corpus default moves every source that
has not overridden that field, which is the point, because ten folders under one parent
used to carry ten copies of the same two globs.

**Null and empty are different, and the difference is the whole of it.** An unset field
inherits; an empty glob list is a decision, meaning "none, whatever the corpus says". A
source under a corpus that excludes `**/*.pdf` needs the second to say it wants those PDFs
after all. Over the API, an omitted field leaves a value alone and `clear` returns it to
the default:

```jsonc
// Stop excluding PDFs here, and go back to the corpus's size cap.
PATCH /api/corpora/books/sources/{id}
{ "excludeGlobs": [], "clear": ["maxFileBytes"] }
```

Clearing is named rather than inferred from a null, because JSON gives no way to tell an
absent property from an explicit null; inferring it would make every partial update reset
whatever it did not mention.

Changing a filter queues a refresh, and only when something actually moved. Narrowing one
removes the files it now excludes through the ordinary reconcile: the walk stops seeing
them, which is the path a file deleted from disk already takes. Nothing on disk is touched.

Text extraction on a workspace source is `File.ReadAllText` with encoding detection (BOM,
then UTF-8, then Latin-1 fallback). Document formats (PDF, DOCX, …) found inside a
workspace tree **are** extracted with their loaders: a repository with reference PDFs in `docs/`
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
that corpus's chunks only; the blob survives, because another corpus may still hold it.

Limits: 200 MB per file (`DEXICON__UPLOAD__MAXFILEBYTES`). Uploads are buffered to a temp
file rather than memory, because the hash is only known once the whole stream is read and a
200 MB upload should not be a 200 MB allocation.

**Staleness is a chunking fingerprint**, not a content hash: `sha256(blob | chunkSize |
chunkOverlap | boundaryMode | model)`. With a bare content hash, changing a corpus's chunk
size left every file looking unchanged, so a refresh re-chunked nothing and the new setting
had no effect. The fingerprint marks precisely the affected files as stale and no
others.

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

- **PDF with no text layer** → `status: empty`, `status_detail: "no text layer: this is a
  scanned PDF, and OCR is not supported"`, and the file is visible in the UI as
  ingested-but-empty. It is
  not reported as a success, and not absent without explanation.
- **Encrypted / DRM** → `status: failed` with the reason.
- **Malformed archive (EPUB/OOXML)** → `status: failed` with the reason.

### Block structure is content

HTML-derived formats (HTML, EPUB) are walked block by block, emitting one line per `<p>`,
heading, list item or table cell. The obvious implementation, AngleSharp's `TextContent`,
concatenates every descendant text node with no separators, and was what Dexicon shipped
first.

It failed in a way nothing could detect. The chunker splits on line boundaries, so a
chapter on one line cannot be split: a 578,000-character EPUB became 18 chunks averaging
32,000 characters each, all far beyond the embedding model's context window. Ollama
truncates without error, so roughly 95% of that book was present in no index, while the
file, the job and the corpus all reported success.

Newlines here are not cosmetic. They are what makes text chunkable, and what makes a line
number in a search result mean anything.

### Extraction is cached, and the cache is versioned

Extraction is cached per blob in `blob_texts`: a 437-page PDF costs ~1.8 s to extract and
its bytes never change, so re-extracting on every reindex would be waste.

But the *code* changes. `ExtractorVersions.Current` is stamped on every cached extraction
and bumped whenever extraction output changes; text from an older version is re-extracted
the next time it is indexed. The version is also part of the chunking fingerprint, so the
re-extracted text is re-chunked rather than skipped as unchanged.

Without this the cache is permanent: a library ingested before a fix keeps the broken text
forever, and no reindex repairs it, because reindexing re-chunks the *cached text* rather
than re-reading the file.

| Extractor version | Change |
|---|---|
| 1 | Initial extractors |
| 2 | HTML and EPUB keep block structure — one block per line |

The chunker carries its own version for the same reason, one stage later: without it the
fingerprint says "same bytes, same settings, nothing to do" and a corpus keeps chunks from
an algorithm that no longer exists, indefinitely, because skipping unchanged files is what
an incremental refresh does.

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
of slicing it at an arbitrary token count. Patterns per language:

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
never falls back without reporting it).

### No chunk exceeds the budget

The chunker avoids splitting within a line, so every chunk carries exact
`start_line`/`end_line` and a hit is openable in an editor. One case overrides that: a
single line longer than the whole budget is split at word boundaries, each piece keeping
that line's number.

The bound is a hard guarantee because breaking it is not observable: an embedding model
does not reject an over-budget chunk, it truncates, and the missing text is still reported
as indexed. A property test asserts the bound across chunk sizes, including on input with no
spaces at all (a minified bundle, a base64 blob).

### Task framing — what the model is told the text is for

Most embedding models are trained with a task instruction wrapped around the input, and it
is not decoration. `nomic-embed-text` wants `search_document:` on indexed text and
`search_query:` on queries and calls them required rather than optional; EmbeddingGemma
wants `title: none | text: …` and `task: search result | query: …`. Send raw text instead
and nothing fails; retrieval is worse. One project measured EmbeddingGemma at
recall@1 16/25 without its prefixes and 23/25 with them.

Dexicon stores this per model as a **template** rather than a prefix, because Gemma's
document form wraps the text rather than preceding it:

Every model in Ollama's embedding category has a built-in, read off its model card
(checked 2026-09-17, all twelve). `ModelProfileCoverageTests` pins the list so one cannot
be dropped unnoticed.

| model | indexed text | queries |
|---|---|---|
| `nomic-embed-text`, `nomic-embed-text-v2-moe` | `search_document: {text}` | `search_query: {text}` |
| `embeddinggemma` | `title: none \| text: {text}` | `task: search result \| query: {text}` |
| `qwen3-embedding` | `{text}` | `Instruct: …\nQuery: {text}` |
| `mxbai-embed-large`, `bge-large`, `snowflake-arctic-embed` | `{text}` | `Represent this sentence for searching relevant passages: {text}` |
| `snowflake-arctic-embed2` | `{text}` | `query: {text}` |
| `bge-m3`, `all-minilm`, `paraphrase-multilingual`, `granite-embedding` | `{text}` | `{text}` |

The last row is intentional: symmetric sentence-transformers models were
trained on sentence pairs with no task prefix, so a prefix is noise in the embedding rather
than framing. The two Arctic generations want **different** prefixes and differ by one
character in their names, so the model a set uses should be confirmed.

`bge-large` is the one judgement call. BGE v1.5 made instructions optional "for
convenience", but the same card recommends them "for a retrieval task that uses short
queries to find long related documents", which describes Dexicon's use. Save a row to
override if a corpus has the opposite shape.

Resolution, in order:

1. **a saved row** for this `(provider, model)` — always wins
2. **a built-in suggestion** for a recognised name — correct out of the box
3. **nothing** — the text goes through unchanged

Models are added at **runtime**, through the Models screen, so built-ins are a fallback
rather than the mechanism: any can be overridden by saving a row, and the UI states which of
the three applies, so "embedded raw" is visible on screen. An unrecognised model
is embedded raw and reports that. Inferring a prefix would be worse than using none: the
model would embed the literal string `search_query:` as content.

Framing is applied in one place, `IEmbeddingService.EmbedAsync`, with the caller passing
`EmbedPurpose.Document` or `EmbedPurpose.Query`. Both sides of a retrieval have to agree:
a document embedded with a prefix and a query embedded without land in a less aligned
space, and the result is not an error but a lower-quality ranking. Passing the purpose as
an argument makes it impossible to omit at either of the two places that embed text.
`ModelProbe` passes `Raw` by design: it measures what the model does with a given
number of characters, and a template would shift every measurement by the length of a
prefix.

The effective template is part of the chunking fingerprint, so editing a profile
re-indexes every chunk set on that model rather than leaving the two sides to disagree.

### "Tokens" is a character budget, and the ratio is measured

A chunk size is set in tokens and enforced in **characters**. There is no tokenizer in the
chunking path; `CodeChunker` performs one conversion, `maxChars = chunkSizeTokens * 4`, and
counts characters thereafter. `768` means 3,072 characters, for every model and every kind
of text.

The trade is cost. An exact tokenizer means a versioned vocabulary per model, for models
pulled at runtime that may not exist yet; the only tokenizer guaranteed right for an
arbitrary model is the one inside it, and asking costs a round trip per chunk. Counting
characters is free, and happens tens of thousands of times per index.

The flat 4 is wrong in a known direction. English prose is around four characters a token,
dense code nearer three, CJK one or less. A "768 token" chunk of minified JavaScript can be
two or three times the budget, and a truncating model drops the end without failing.

The probe measures the ratio once per model with the model's own tokenizer, which turns the
character budget into a real token figure without putting a tokenizer in the hot path. On
this machine `nomic-embed-text` is 2.82, not 4, so the default 768-token chunk is nearer
1,090 tokens.

The consequence that matters: `mxbai-embed-large` accepts 2,816 characters, and the default
768 tokens is 3,072. **At the default, that model truncates every full-size chunk.** The
probe exists because that happened, reached from the defaults.

### Chunk sets — a corpus can be cut several ways at once

Chunk size, overlap, boundary mode and the embedding model belong to a **chunk set**, not
to the corpus. A corpus owns content, sources and visibility; a set owns a vector space and
a strategy, and a corpus can carry several over exactly the same documents.

```
corpus "library"          content, sources, who can read it
  ├── set "default"       embeddinggemma, 2065/258, language-aware   ← search lands here
  └── set "fine"          embeddinggemma, 512/64, heading context
```

Sets are addressed as `corpus:set`. An unqualified name means the default set, which is
what an agent that has never heard of sets will send.

This is what makes changing the embedding model safe. A collection's name encodes the model
and its dimensionality, so a different model is a different vector space. Re-embedding
a three-book corpus was measured at roughly twenty minutes on CPU Ollama. Editing in place
would mean twenty minutes of half-populated results, so instead:

1. add a set on the new model, which backfills in the background
2. the live set keeps serving search throughout
3. promote when it is complete; promotion is one `UPDATE` and the only moment search changes
4. drop the old set

Promoting a set that still has pending files is refused, with a count of what is left.
Promoting a half-built set is precisely the outage that building it separately prevents.

It is also the natural home for "the same document, chunked two ways". That worked before
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
only which provider a set uses. A database row holding an API key cannot be backed up
casually, and Dexicon's backup instructions direct the operator to copy the catalogue.

A provider that is configured but missing its credential is reported as such in the
Models screen and in `/api/embedding-providers`.

The model travels as a **per-call argument**, never bound into a client at startup. Chunk
sets choose models at runtime, so anything resolved from configuration at boot, including
keyed DI, cannot see a set created after startup. See
[D-22](decisions.md#d-22-the-embedding-model-is-a-per-call-argument).

The collection name also carries the provider, as in
`dexicon__ollama__nomic-embed-text__768`, because two providers can serve a model of the
same name and those are not the same vectors.

Listing, pulling and deleting models is separate from embedding, and only local providers
support it. You cannot pull a model into OpenAI; its catalogue is a fixed list, configured
rather than discovered.

### Knowing a model's limits, without indexing anything

`POST /api/embedding-models/probe` measures what a model accepts:

```json
{ "dimensions": 768, "maxInputChars": 11776, "truncatesSilently": true,
  "charsPerToken": 2.82, "recommendedChunkTokens": 2783, "embedCalls": 27 }
```

`charsPerToken` is measured with **the model's own tokenizer**, not estimated. Ollama
returns `prompt_eval_count` on an embed call, so the probe embeds three samples (prose,
dense code, and punctuation-heavy structured text) and divides. A provider that reports
no token counts gets `null`, and callers keep the estimate rather than inventing a
measurement.

Measured on the three models here:

| model | chars/token | accepts |
|---|---|---|
| `nomic-embed-text` | 2.82 | 11,776 chars |
| `embeddinggemma` | 3.80 | 11,776 chars |
| `mxbai-embed-large` | 2.82 | **2,816 chars** |

Two of the three are well below the 4 the chunker assumes; see below.

This exists because of the EPUB failure above: a truncating model returns a perfectly good
vector for the part it read, so nothing downstream could tell that most of the book was
missing.

Truncation is silent but **empirically visible**: embed a text, then embed the same text
with distinctive content appended. If the tail was read, the vector moves. If it did not,
the vector is unchanged. Bisecting on that finds the real limit in about two dozen short
calls, with no documentation to trust and nothing indexed.

Measured on this stack, both `nomic-embed-text` and `embeddinggemma` accept about 11,776
characters of English prose, or 2,048 tokens, and **truncate without error** beyond it. The recommendation is two thirds of the measured figure, because the measurement
is in characters and the model counts tokens: code, minified output and CJK reach the same
token limit in far fewer characters.

### Meaning, not just budget

Chunk size exists because of the embedding model's context window. Everything else here
exists because a chunk that ends mid-thought retrieves badly regardless of how well it
fits. Each is per-set and off by default.

**Heading context.** The heading trail, such as `Data model > Point payload > Storage
budget`, is prepended to the text that is *embedded*, so a chunk's vector carries the section it came
from. Stored text stays verbatim: it is what search returns, what `get_context` stitches,
and what a `dexicon://` resource read reconstructs a file from, and prepending would insert
lines the file never had.

The trail is resolved per line, up front. Reading a running cursor when a chunk is emitted
looks equivalent but is not: the accumulator fills *past* a boundary before backing up to
it, so the cursor is ahead of the chunk being flushed. This labelled chunks with a heading
from further down the file, which is worse than no label, because retrieval then places
them under a section they are not in.

**Unit-aware boundaries.** Page for PDF, chapter for EPUB, slide for PPTX. These *force* a
split rather than offering a place for one: under the usual rule a chapter shorter than the
budget would be absorbed into the next. The cost falls to the caller: a document of very
short pages yields short chunks.

**Sentence-aware splitting.** When a split lands inside a line, cut at a sentence rather
than a word. A terminator counts only when followed by a space, so `e.g.` and `3.14` do not
end a sentence. The backup window is half the budget, not the eighth a word search uses:
with the narrow window it never fired.

**Custom boundaries.** The `custom` mode with your own regex, compiled at the request that
sets it rather than part-way through a job an hour later.

> **Not implemented: LLM-driven chunking.** Asking a model where the meaningful seams are is
> the obvious next step. It needs a decision about which model curates and what it costs per
> document, since a 400-page book is hundreds of calls. That is a product question rather
> than a missing function. The seam is `ChunkOptions`: a strategy that needs a model is a new flag
> and a new branch, not a rewrite.

### Size decides *when* to split; a boundary decides *where*

The chunker fills to the size budget, then **backs up to the most recent boundary inside
the buffer**. A chunk holds as many whole members or paragraphs as fit and does not end
mid-thought. With no boundary in the buffer, it splits where it is.

The first implementation split at *every* boundary, which made `chunk_size` dead
configuration in every mode but `none`: blank-line mode on prose emitted one chunk per
paragraph, a **252-character mean against a 3,072-character budget**, and corpora
configured 768 and 256 tokens produced byte-identical output. Chunks that small retrieve
badly.

Five regression tests pin the corrected behaviour, the load-bearing one being that a
smaller `chunk_size` produces more chunks.

**A boundary is only used when it leaves a chunk of usable size**, at least twice the
overlap. Backing up assumes a boundary near the fill point, and prose interleaved with code
listings breaks that: a blank line early, then eleven thousand characters of listing with
none. Backing up to the early boundary emits a fraction of a chunk, and the overlap rewind
will not go back past the previous start, so the next chunk begins **one line later** and
repeats most of it.

A PDF of prose around code listings came out as **1,051 chunks averaging 388 characters**
where about 73 were intended. The EPUB of the same book, whose extractor emits no blank
lines, produced 61: the formats agreed on content to within 4% and disagreed on chunking by
seventeen times, producing fifteen times the vectors, embedding cost and storage for
near-duplicate fragments too small to carry their own context.

The threshold is the **overlap**, not a fraction of the budget. Twice the overlap is what
guarantees the start advances after the rewind. Tying it to the budget would override an
explicit boundary, since a custom pattern or a markdown heading is a request to split at
that point. A set with no overlap has no stall to prevent, so the rule then rejects only a
zero-length chunk.

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
cheap. It is **not** a parser: it will miss declarations, and these documents state that
rather than implying call-graph fidelity.

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
  repository.

### Model choice

Pinned per corpus at creation. Candidates, all available through Ollama:

| Model | Dims | Size | Use for |
|---|---|---|---|
| `embeddinggemma` | 768 | ~620 MB | **Default.** Won both sweeps — best mean MRR on documents and on code. |
| `mxbai-embed-large` | 1024 | ~670 MB | Close behind, and took the single best code configuration. Accepts only 2,816 characters, so it needs a chunk size well under the default. |
| `nomic-embed-text` | 768 | ~300 MB | A third the download. Mid on documents, last on code by a clear margin. |
| `qwen3-embedding:0.6b` | 1024 | ~1.5 GB | Strongest general quality per VRAM; 32k context; multilingual. Not yet swept. |
| `bge-m3` | 1024 | ~2.2 GB | Long documents (8k context). Not yet swept. |

The default is `embeddinggemma` because it is the only model that led on *both* corpora
([benchmarks](benchmarks.md)), by 0.025 mean MRR on documents and 0.075 on code. Its first
download is twice the size of `nomic-embed-text`, which is the argument against it and was
judged the weaker one.

This is the default for *new* corpora only. An existing chunk set records its own model and
keeps it, so changing this reindexes nothing.

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
is what allows several chunkings of one document, including two sets on different models,
mid-migration, in two different collections.

Scheduling: on demand (UI button, `index_refresh` MCP tool), plus an optional interval per
corpus, default off. There is no filesystem watcher: polling with content hashes is more
reliable over bind mounts, particularly on Windows hosts and WSL2.

## Job semantics

One job runs at a time per instance, in a bounded in-process queue. Queuing a refresh for a
corpus that already has one queued is a no-op returning the existing job id, not a second
job.

Job kinds: `full` (everything, ignoring hashes), `refresh` (incremental, the default),
`rebuild` (new embedding model, which writes into the new collection and drops the old
points only on success), `delete`.

Progress events are emitted per file and coalesced to at most 4/second onto
`GET /api/events` (SSE). The UI shows phase, counts, current file, and an estimate of
remaining time derived from the trailing rate.
