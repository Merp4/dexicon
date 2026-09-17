# Changelog

Notable changes per release. Format loosely follows [Keep a Changelog]; versions follow
[Semantic Versioning], with the caveat that this is 0.x and the minor number carries what
the major one will once it stabilises.

Every version is a git tag (`v0.2.1`), and the container image carries the same number —
see [docs/09](docs/09-deployment.md) for how versions are derived and what the image tags
mean.

[Keep a Changelog]: https://keepachangelog.com/en/1.1.0/
[Semantic Versioning]: https://semver.org/

---

## 0.2.1 — 2026-09-17

Security fixes. Two of these are real vulnerabilities and affect every release
before this one; nobody but the author has run any of them, which is the only
reason this is a changelog entry rather than an advisory.

### Fixed

- **A cache collision returned somebody else's authenticated principal.** The
  verified-principal cache was keyed on a 32-bit `string.GetHashCode` plus the token's
  length. A collision there does not return a stale value — it returns a different
  caller's principal, with verification skipped, because the cache says the token has
  already been checked. The key is now SHA-256 over the whole token, and the token itself
  is still not the key: cache keys turn up in dumps and diagnostics.
  *.NET randomises string hashing per process, so the pairs could not be found offline.
  That made it hard to exploit; it did not make it sound.*
- **The workspace boundary was a string prefix, not a directory.** `StartsWith(root)`
  refuses `../etc/passwd` and accepts `../workspaces-secret` — a sibling that merely
  begins with the root's name. The escape never needed to traverse anywhere. Symlink
  targets in the directory walk were checked the same way, and are now checked properly
  too. The comparison is case-insensitive only where the filesystem is.
- **The 200 MB upload limit was unreachable** behind Kestrel's 30 MB request cap, so a
  40 MB PDF died on a bare 413 with no reason given and a configured limit that was a
  fiction. The cap is lifted per request on the upload endpoint only, and the real limit
  is enforced while streaming to disk — it stops reading at the cap rather than buffering
  the whole request to discover how big it was.
- **The secret scanner matched code rather than secrets.** All four findings over full
  history were the bootstrap-token rule firing on scripts that name the variable and hold
  no value. A scanner that cries wolf on source is one people learn to wave through.

### Changed

- The README leads with what the tool does rather than eleven lines of prose, and its
  screenshot is of this project's own documentation. The first one was of a corpus of
  O'Reilly books and carried several hundred legible words of two of them; the script that
  takes it now names its corpus and says that overriding it means content you hold the
  rights to distribute.
- `docs/10` records a licence review of the whole transitive dependency tree, and an audit
  of what an error is allowed to say across the MCP and REST boundaries — both with the
  scripts and probes that produced them.

## 0.2.0 — 2026-09-17

The release that came from pointing the indexer at a real shelf of 95 books instead of at
fixtures. Six defects surfaced that way, four of them silent — no error, no log line, and a
system that looked healthy.

### ⚠️ Upgrading

- **A corpus with more than one source needs one full reindex**
  (`POST /api/corpora/{name}/reindex?full=true`). Vector point ids now include the source,
  and an incremental refresh will not rewrite ids for files it considers unchanged.
- **Everything else re-chunks and re-extracts itself** on the next ordinary refresh. The
  chunker and extractor versions are part of the content fingerprint for exactly this
  reason.
- The default embedding model is now `embeddinggemma`. This affects **new** corpora only —
  a chunk set records the model it was built with and keeps it.

### Fixed

- **Every O'Reilly EPUB was unreadable, and the error blamed DRM.** Their toolchain lists
  the cover image twice in the manifest; the strict parser refuses the book. Six of the
  first nineteen books. An unparseable manifest is now salvaged from the archive, and DRM
  is asserted from `META-INF/encryption.xml` rather than guessed from any parse failure.
- **A file path did not uniquely name a file.** `file_path` is relative to its *source*
  root, so two sources of one corpus holding the same filename are two files with one path.
  Three consequences, all fixed: deletion removed both copies; `get_context` interleaved
  them into a single passage with line numbers on it; and the vector point id, derived from
  (set, path, index), let the second source's write silently overwrite the first.
- **A running index job absorbed work it had already passed.** Adding sources to a corpus
  mid-index left them unindexed while the corpus reported `ready` with no job pending.
  Coalescing now happens only onto a *queued* job.
- **Chunking collapsed on prose interleaved with code.** A boundary far behind the fill
  point produced a chunk smaller than the overlap, which made the splitter advance one line
  and repeat. One book produced 1,051 chunks averaging 388 characters where 73 of ~8,000
  were intended. A boundary is now only used when it leaves a chunk of at least twice the
  overlap.
- **`snowflake-arctic-embed2` was sent unframed queries**, on the belief that Arctic trains
  without a task prefix. Its model card specifies `query_prefix = 'query: '`.
- **`:latest` created a second collection for the same model.** `embeddinggemma:latest` and
  `embeddinggemma` are one model and one vector space; slugged raw they became two
  collections, reachable from the UI's own model picker.
- **A corpus created with a workspace path never queued an index.** Found because a
  benchmark scored 0.000 everywhere.
- **The GPU overlay could never have started** — `device_ids` was a scalar where Compose
  requires a list.
- **A shell script with CRLF endings is not a shell script.** `scripts/provision-models.sh`
  acquired them and took Ollama, and with it the whole stack, down. CI now rejects any
  `*.sh` containing a carriage return.

### Added

- **Sources can be removed** — `DELETE /api/corpora/{name}/sources/{id}` and a control in
  the UI. Previously the only way to undo a mistyped path was deleting the whole corpus.
- **Search can be scoped to one source** (`source` on `search_index` and `POST /api/search`),
  and results say which source they came from when that disambiguates. `pathPrefix` cannot
  do this: it matches a path relative to a source root.
- **A whole book can be read in the UI.** The file viewer pages through with `start` /
  `nextOffset` and a "Read on" control, instead of stopping at 400,000 characters.
- **The document size cap is configurable** — `DEXICON__INDEXING__DOCUMENTMAXBYTES`,
  default 512 MB, raised from a hard-coded 64 MB. PDF extraction streams rather than
  copying the file twice, so a 128 MB book costs a fraction of what it used to.
- **The folder picker browses**, at any depth. `GET /api/workspaces` always took a path;
  nothing ever passed one, so a corpus could only be pointed at a top-level directory.
- **New corpora choose their embedding model**, sized from what that model was measured to
  accept. It is the one property of a corpus that cannot be changed afterwards.
- **Built-in task framing for every model in Ollama's embedding category** — all twelve,
  each read off its model card, pinned by a test so one cannot be dropped silently.
- `scripts/dev-token.py`: hands the bootstrap token from `.env` to a browser over loopback,
  behind a single-use nonce, so the UI can be driven signed-in without the token passing
  through a transcript or a shell history.

### Changed

- **`embeddinggemma` is the default embedding model.** It won both retrieval sweeps — by
  0.025 mean MRR on documents and 0.075 on code, the widest gap any single variable opened.
  See [benchmarks](docs/benchmarks.md).
- **`list_corpora` leads with what a corpus is *for*.** The description used to come last,
  under the state, counts, chunk sets, dimensions and overlap. An empty corpus is now
  marked `NOT SEARCHABLE` rather than listed as a plausible place to look.
- Chunk size is measured, not guessed: a model's real character ceiling and its
  chars-per-token ratio are measured with its own tokenizer.
- One-of-N choices in the UI are a segmented control — one tab stop with arrow keys, rather
  than three separate tab stops with none.
- The `docs/` corpus sweep gained a code corpus: 81 configurations over each, 162 in total.

---

## 0.1.1 — 2026-09-17

- The version is derived from the git tag by MinVer rather than written in a file.
  `AssemblyInformationalVersion` is the one to read; MinVer pins `AssemblyVersion` to
  `major.0.0.0`, which reports `0.0.0` for any 0.x project.
- Base images are pinned by digest, and the release publishes an SBOM and build provenance.
- CI type-checks and tests the UI, and generates the API client first — the previous
  pipeline compiled a tree with no client in it, and `npx tsc --noEmit` was a no-op.

## 0.1.0 — 2026-09-16

First tagged release. Indexing, hybrid search, chunk sets, the MCP surface, the web UI and
the container.
