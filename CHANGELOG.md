# Changelog

Notable changes per release. Format loosely follows [Keep a Changelog]; versions follow
[Semantic Versioning], with the caveat that this is 0.x and the minor number carries what
the major one will once it stabilises.

Every version is a git tag (`v0.2.2`) and the container image carries the same number. See
[docs/09](docs/09-deployment.md) for how versions are derived and what the image tags
mean.

From `0.2.3`, each section below is also the body of that version's GitHub Release. A tag
with no section here fails its release rather than publishing an undescribed one.

[Keep a Changelog]: https://keepachangelog.com/en/1.1.0/
[Semantic Versioning]: https://semver.org/

---

## Unreleased

### ⚠️ Upgrading

- One migration, `SourceFilterInheritance`, applied at startup. Widening only: a source's
  filter columns become nullable and a corpus gains four default columns. Every existing
  source keeps the value it had, so it stays an explicit override and indexes exactly as
  before. Nothing re-indexes on upgrade.

### Added

- **A source's filters can be changed after it was added, and a corpus can set defaults
  they inherit.** They were write-once: set when the folder was added and unreachable
  afterwards, so changing one glob meant deleting the source, which drops its files from
  every chunk set, then re-adding it and re-embedding the folder from scratch. Nobody
  iterates on a filter at that price. Ten folders under one parent also carried ten copies
  of the same two globs, set one at a time.

  Filters now resolve through three layers, narrowest first: the source's own value, the
  corpus default, then the configured value. Each field resolves on its own, and
  inheritance is live, so changing a corpus default moves every source that has not
  overridden that field.

  An unset field inherits; an empty glob list is a decision, meaning "none, whatever the
  corpus says". The difference is what lets a source under a corpus that excludes
  `**/*.pdf` say it wants those PDFs after all. `PATCH /api/corpora/{name}/sources/{id}`
  leaves omitted fields alone and takes a `clear` list to return one to the default, named
  rather than inferred from a null, because JSON cannot distinguish an absent property from
  an explicit null. `PATCH /api/corpora/{name}` takes the defaults. Both queue a refresh,
  and only when something actually moved.

### Fixed

- **A size cap that was not a round number of megabytes could not be saved.** The Add
  source form set `step` on its size input, which makes the browser reject anything off
  that grid and then refuse to submit the form without saying so. A cap is a free value,
  not one on a grid.

- **The UI reports files no source covers.** `0.2.3` gave this to agents through
  `index_status` and left the web UI silent about it, which is the wrong way round: the
  corpus page is where sources are added. A `Notice` above the sources names the directory,
  up to five of the files in it and how many more, and its button opens the source picker
  already pointed at that directory, because a path read off a warning and retyped is where
  this goes wrong.

  New endpoint `GET /api/corpora/{name}/coverage`, additive. It is its own call rather than
  a field on the corpus summary because answering it reads the filesystem, and the summary
  is drawn on every navigation. The UI reads it defensively: a coverage report that cannot
  be fetched is a missing warning, not a broken page, and an older container has no such
  endpoint at all.

## 0.2.3 — 2026-09-18

### ⚠️ Upgrading

- **Every PDF re-extracts and re-chunks itself** on the next ordinary refresh, because the
  extractor version is part of the content fingerprint. Nothing to run by hand.
- `/healthz` gains a `missingModels` array. Additive, and a client that ignores it is
  unaffected; the UI reads it defensively, because an older container returns a response
  without the field.

### Fixed

- **Files no source covered were invisible rather than reported.** A file outside every
  source root is not skipped and not failed: it has no row in any count, and a search for
  it returns other documents, which is indistinguishable from a ranking result. A library
  of 138 files indexed 95 of them; one book sat directly in `orly/` while every source was
  `orly/<topic>/`, and a keyword search on its exact title returned four other books.

  `index_status` now reports a directory when two or more of the corpus's sources share it
  as their parent, which is where the children were enumerated deliberately and a file left
  loose among them was passed over. One source under a directory says nothing about that
  directory and is not reported, so a corpus that indexes a single folder stays silent.
  Only files that would have been indexed are listed: the always-exclude list, a
  `.gitignore` in the directory, the size caps and binary sniffing all still apply.

- **PDFs were read in content-stream order, not in reading order.** A producer writes the
  content stream in whatever order it likes, which on a two-column page is often left line,
  right line, left line. Read that way the columns interleave into text that is grammatical
  nonsense but looks like prose, so nothing downstream detects it: it embeds, it chunks and
  it comes back as a search hit. A table is worse, every cell being its own column.

  The quieter half affected every PDF. Content order ends a line where the text met the
  right margin, so a paragraph arrived as ten fragments. Over a 424-page book, against the
  EPUB of the same title: 14,954 lines averaging 43 characters became 5,559 averaging 118,
  where the EPUB gives 5,727 averaging 109. Character counts barely move, so this is not
  about gaining or losing text. It costs 1.2x the extraction time, 5.8 ms a page.

  Measured and not claimed: this does **not** improve chunk parity between a title's PDF
  and its EPUB. Parity was already close and is marginally worse at the smallest chunk
  size, because chunks are budgeted in characters and the character count is what did not
  change.

- **`get_context` returned the chunks holding the lines, not the lines.** Asking for three
  lines of context returned forty: the stitcher emitted every chunk that overlapped the
  window rather than trimming to it, so a request was answered with whatever the chunk
  boundaries happened to be. ([#12](https://github.com/Merp4/dexicon/pull/12))

- **The audit trail could be forged by an unauthenticated caller.** A request path is
  decoded before it is logged, so `%0A` arrived as a real newline, and the console output
  template rendered string values literally. A request to
  `/x%0A[19:05:31Z INF] DELETE /api/corpora/books -> 200 (token bootstrap)` wrote that
  second line into the log as an entry of its own. No valid token was needed: the
  rejection is logged before the 401 is returned. `docs/07` names the container logs as
  the audit trail, so the trail itself was forgeable.

  Fixed in the output template rather than at the call sites, which cannot be forgotten
  by a later log statement: `{Message:j}` instead of `{Message:lj}`, dropping the
  "literal" flag so Serilog renders string values as JSON and escapes the newline. String
  values in the log are now quoted.

- **The secret scan covered one push, not the history it claimed.** The job was named and
  commented for full history; the action scans the commits in the push it runs on, so
  `fetch-depth: 0` fetched a history nothing then read. It also fired on
  `SecretHygieneTests.cs`, whose fabricated token is token-shaped on purpose: the test
  asserts a principal cache key never contains the credential it came from. Allowlisted as
  that exact literal rather than by path, because a path in the global allowlist is exempt
  from every rule, which would stop the file being scanned for AWS keys and private keys
  too. ([#8](https://github.com/Merp4/dexicon/pull/8))

- **A UI assertion passed only on an English runner.** `Documents.test.tsx` asserted on the
  literal `2,940 chunks` beside a `.replace(',', ',')`: a comma replaced by a comma.
  The component renders the count with `toLocaleString()`, whose thousands separator is
  `2.940` under `de-DE` and `2 940` under `fr-FR`, so the no-op left an assertion that
  fails anywhere but an English locale. It now formats the expected value the same way the
  component does. ([#11](https://github.com/Merp4/dexicon/pull/11))

- **Every button in the app drew the wrong cursor.** Tailwind v3's preflight set
  `cursor: pointer` on buttons; v4 dropped the rule without replacing it, and the component
  library does not put one back. Three call sites had hand-added it, which is how a fix ends
  up covering only the controls someone happened to look at.

- **Every badge failed text contrast, in both themes.** A token tuned for a 12% fill and a
  40% border is the wrong value for the words sitting on that fill: `--warn` as text
  measured 2.95:1 on the light surface against the 4.5:1 needed at 12px, `--ok` 3.31:1, and
  dark ran 3.63:1 to 4.24:1. The palette is now two values per meaning, one for fills and
  borders and one that can carry text. Fills and borders are byte-identical; only what is
  read moved.

- **`color-scheme` followed the operating system rather than the chosen theme**, so an OS on
  light with the app on dark gave pale scrollbars down every code block. The tokens colour
  only what this stylesheet draws; scrollbars, the caret, the reveal button in the token
  field and autofill are the browser's, and it picks those from `color-scheme`.

- **`prefers-reduced-motion` was read nowhere** while the app animated. It now collapses
  durations rather than setting `animation: none`, because a `data-[state=closed]` exit
  animation that never runs leaves the element on screen, and a dismissed dialog would
  simply stay.

- **The one line saying results could not be trusted read as decoration.** "Corpus 'books'
  is still indexing; results are incomplete." was drawn in the accent blue, the same blue
  that badges the default chunk set and the corpus a hit came from. It is a warning, and it
  is now announced as one.

- **A book title pushed the page past the viewport.** A result header's min-content is a
  whole title plus an unshrinkable section label, and a grid item will not let its track be
  narrower than its own min-content, so the citation truncated only after widening the track
  past the screen: a horizontal scrollbar under the whole page, with the search box and
  results count pushed off the right edge. At phone width the result actions sat 80px beyond
  it, and on Models the Delete button sat outside the card's `overflow-hidden`, where
  nothing could reach it.

- **The health panel's reachability badge carried the model name**, so the panel never
  plainly answered the question it exists for, and colour-coded a model by whether the
  backend was up. It now reports reachability the way Qdrant does and names the provider
  instead of assuming Ollama.

- **The workspace picker named a folder that does not exist.** It joined a parent path on
  `''` rather than `/`, so the button above `books/orly/Architecture` was labelled
  `booksorly`. Navigation was correct; only the label was wrong.

- **A deduplicated upload reported itself to `console.info`.** That is the case that looks
  most like nothing happened, and it was written where nobody is looking.

- **Progress bars announced nothing**, being a pair of divs, so an index running was
  invisible to a screen reader.

- **The file list stopped at 300 rows in silence**, so a corpus of 4,000 files looked like a
  corpus of 300.

### Added

- **`/healthz` reports a model a chunk set can no longer reach.** The failure is silent
  until someone searches: the set's vectors are still in Qdrant and its row still says
  `ready`, but the query cannot be embedded, so the set answers nothing and a re-index of
  it cannot start. Deleting a model is already refused while a set uses it, so what this
  catches is a model pulled out from under the catalogue, or a volume restored without it.
  Only providers whose models can be listed are checked: a hosted provider cannot be asked
  what it has, and a guess there would be a false alarm on a working deployment. Reported
  per `corpus:set`, because the fix is per set, and surfaced in the health panel.

- **`Notice`, one warning component.** Nine of these were hand-written: `text-[var(--warn)]`
  on a paragraph in one place, a card with a warn border in another, a `⚠` typed into the
  sentence in four more. No two matched and one was the wrong colour outright. Tones are
  Badge's five, the icon follows from the tone rather than from a character in the string,
  and `role="status"` by default, so a message that appears in response to something the
  reader just did also reaches anyone driving by keyboard and screen reader. Polite rather
  than `alert`, which interrupts; `ErrorBanner` keeps `alert`, because a failed request
  should.

- **CodeQL for C# and TypeScript**, on pull requests, on `main` and weekly. `docs/10`
  listed this among the supply-chain controls while no such workflow existed, and code
  scanning needs Advanced Security on a private repository, so the line was false for as
  long as it had been written. Advanced setup rather than GitHub's default: C# here needs
  a .NET 10 SDK the runner does not ship, which the default setup cannot install.

- **A tag now cuts a GitHub Release**, with that version's CHANGELOG section as the body,
  plus the pull command and the digest actually published. The workflow previously pushed
  an image, an SBOM and an attestation and left the Releases page empty. The notes are read
  at the top of the job, so a tag with no CHANGELOG section stops the release before
  anything reaches the registry. Releases start at `0.2.3`; the five earlier tags have
  none. Questions now route to Discussions rather than the issue tracker.

### Changed

- **`/healthz` names the endpoint of the provider actually in use.** It reported
  `Ollama.Endpoint` unconditionally, so a deployment defaulting to OpenAI showed
  `http://dexicon-ollama:11434`, an address it never calls, beside a reachability badge for
  a backend on the other side of the internet. A default naming a provider that is not
  configured now says so rather than having an address invented for it.

- **Deleting a corpus or a chunk set asks in the app's own dialog.** Both used
  `window.confirm`, which the page has no say over: it ignores the theme, places the
  destructive action wherever the browser likes, and after the second one a browser offers
  to suppress further dialogs for the session, at which point deleting a chunk set stops
  asking at all.

- **A neutral professional register across the docs and the code comments**, 102 files.
  The pass removes the authorial voice: em-dashes where a colon, comma, full stop or
  parentheses were what the sentence wanted, editorialising asides, sentences admiring the
  previous sentence, and headings that were phrases rather than labels. Quoted system
  messages, facts, figures and links are unchanged. The README drops a fifteen-row table of
  contents that restated the filenames beside it, 22 lines to 11.
  ([#9](https://github.com/Merp4/dexicon/pull/9))

- **`docs/09` documents the release order**, numbered, because each step exists to stop the
  next one failing: CHANGELOG, then the regenerated OpenAPI document, then the tag.

## 0.2.2 — 2026-09-17

Documentation, bar one word of shipped text: the `path_prefix` tool description said
`SOURCE` in capitals at a model. The API surface is otherwise identical to `0.2.1`.

- The docs no longer reference a separate, non-public project of the same author's. Two
  sections existed only to describe it and are gone; the design decisions they pointed at
  stand where they are. `NOTICE` drops an attribution that was never required.
- A prose pass across the whole set: roughly 90 lines of narrative removed with no facts
  lost. Gone are the editorialising adverbs, the passages where a document narrated its own
  edit history rather than describing the software, and the habit of defining a thing by
  what it is not, kept only where the rejected alternative is real, which was 187 of the
  200 places it appeared.
- Four corrections found while reading, none of them editorial: `decisions.md` still
  recommended `nomic-embed-text` after the benchmark sweep moved the default to
  `embeddinggemma`; M3 was marked complete while still carrying the interim note that
  superseded it; two M4 items were unticked for work that shipped in `0.2.1`; and a
  `{#custom-id}` heading attribute, which GitHub does not support, left `SECURITY.md`
  linking to an anchor that did not exist.
- Duplicated passages removed, including the same EPUB-truncation story told twice in one
  file and a threat model restated almost verbatim in two.
- Shouting caps removed from prose, one instance of which was in a shipped MCP tool
  description.

## 0.2.1 — 2026-09-17

Security fixes. Two of these are real vulnerabilities and affect every release
before this one; nobody but the author has run any of them, which is the only
reason this is a changelog entry rather than an advisory.

### Fixed

- **A cache collision returned somebody else's authenticated principal.** The
  verified-principal cache was keyed on a 32-bit `string.GetHashCode` plus the token's
  length. A collision there does not return a stale value; it returns a different caller's
  principal, with verification skipped, because the cache records the token as having
  already been checked. The key is now SHA-256 over the whole token, and the token itself
  is still not the key: cache keys turn up in dumps and diagnostics.
  *.NET randomises string hashing per process, so the pairs could not be found offline.
  That made it hard to exploit; it did not make it sound.*
- **The workspace boundary was a string prefix, not a directory.** `StartsWith(root)`
  refuses `../etc/passwd` and accepts `../workspaces-secret`, a sibling that merely begins
  with the root's name. The escape never needed to traverse anywhere. Symlink
  targets in the directory walk were checked the same way, and are now checked properly
  too. The comparison is case-insensitive only where the filesystem is.
- **The 200 MB upload limit was unreachable** behind Kestrel's 30 MB request cap, so a
  40 MB PDF died on a bare 413 with no reason given and a configured limit that was a
  fiction. The cap is lifted per request on the upload endpoint only, and the real limit
  is enforced while streaming to disk, stopping at the cap rather than buffering the whole
  request to determine its size.
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
  of what an error is allowed to say across the MCP and REST boundaries, both with the
  scripts and probes that produced them.

## 0.2.0 — 2026-09-17

The result of indexing a real collection of 95 books rather than fixtures. Six defects
surfaced, four of which produced no error and no log line, leaving a system that appeared
healthy.

### ⚠️ Upgrading

- **A corpus with more than one source needs one full reindex**
  (`POST /api/corpora/{name}/reindex?full=true`). Vector point ids now include the source,
  and an incremental refresh will not rewrite ids for files it considers unchanged.
- **Everything else re-chunks and re-extracts itself** on the next ordinary refresh. The
  chunker and extractor versions are part of the content fingerprint for exactly this
  reason.
- The default embedding model is now `embeddinggemma`. This affects **new** corpora only;
  a chunk set records the model it was built with and retains it.

### Fixed

- **Every O'Reilly EPUB was unreadable, and the error blamed DRM.** Their toolchain lists
  the cover image twice in the manifest; the strict parser refuses the book. Six of the
  first nineteen books. An unparseable manifest is now salvaged from the archive, and DRM
  is asserted from `META-INF/encryption.xml` rather than guessed from any parse failure.
- **A file path did not uniquely name a file.** `file_path` is relative to its *source*
  root, so two sources of one corpus holding the same filename are two files with one path.
  Three consequences, all fixed: deletion removed both copies; `get_context` interleaved
  them into a single passage with line numbers on it; and the vector point id, derived from
  (set, path, index), let the second source's write overwrite the first without error.
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
  acquired them and brought down Ollama, and with it the whole stack. CI now rejects any
  `*.sh` containing a carriage return.

### Added

- **Sources can be removed** via `DELETE /api/corpora/{name}/sources/{id}` and a control in
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
- **Built-in task framing for every model in Ollama's embedding category**: all twelve,
  each read from its model card and pinned by a test so that none can be dropped unnoticed.
- `scripts/dev-token.py`: hands the bootstrap token from `.env` to a browser over loopback,
  behind a single-use nonce, so the UI can be driven signed-in without the token passing
  through a transcript or a shell history.

### Changed

- **`embeddinggemma` is the default embedding model.** It led both retrieval sweeps, by
  0.025 mean MRR on documents and 0.075 on code, the widest margin any single variable
  produced.
  See [benchmarks](docs/benchmarks.md).
- **`list_corpora` leads with what a corpus is *for*.** The description used to come last,
  under the state, counts, chunk sets, dimensions and overlap. An empty corpus is now
  marked `NOT SEARCHABLE` rather than listed as a plausible place to look.
- Chunk size is measured: a model's real character ceiling and its chars-per-token ratio
  are both read from its own tokenizer.
- One-of-N choices in the UI are a segmented control: one tab stop with arrow keys, rather
  than three separate tab stops with none.
- The `docs/` corpus sweep gained a code corpus: 81 configurations over each, 162 in total.

---

## 0.1.1 — 2026-09-17

- The version is derived from the git tag by MinVer rather than written in a file.
  `AssemblyInformationalVersion` is the one to read; MinVer pins `AssemblyVersion` to
  `major.0.0.0`, which reports `0.0.0` for any 0.x project.
- Base images are pinned by digest, and the release publishes an SBOM and build provenance.
- CI type-checks and tests the UI, and generates the API client first. The previous
  pipeline compiled a tree with no client in it, so `npx tsc --noEmit` was a no-op.

## 0.1.0 — 2026-09-16

First tagged release. Indexing, hybrid search, chunk sets, the MCP surface, the web UI and
the container.
