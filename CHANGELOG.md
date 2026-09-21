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

### Added

- **Discovery is its own pass, on its own lane.** A corpus added while another was indexing
  read as empty: its job sat behind a reindex of ~1,800 PDFs on a queue that ran one job
  at a time, and nothing said so. A sweep now walks a corpus, applies its filters and
  shadowing, and records what is there, without extracting, chunking or embedding any of
  it. Statting that library takes about two seconds against 773ms to extract one ordinary
  PDF from it, so the cheap half no longer waits for the expensive one.

  It runs when a source is added, and on demand at `POST /api/corpora/{name}/sweep`. A
  corpus reports what it found as `pendingCount`, beside the file count rather than folded
  into it, since that one counts what is searchable.

  The sweep only ever adds. Removing a vanished file means removing its vectors from every
  set's collection, and the shared row only once the last set has let go, which a sweep
  cannot do; indexing remains the only pass that removes anything. There is no exemption
  for rows that look empty either, because `Pending` is not evidence that a file has no
  vectors: the upsert runs before the status is written. See [D-32](docs/decisions.md).

- **A corpus lease, so the two lanes exclude each other.** Taken as one conditional update
  whose row count is the answer, because reading the corpus state cannot exclude anything:
  it is set inside the indexer once a job is already running, leaving a gap for the other
  pass to start in. The expiry is renewed while the holder works rather than set to a guess
  at how long the work takes, so nothing has to predict that indexing a library runs for
  hours, while a holder that dies stops renewing and the corpus falls free.

- **Extracted text for files on a mount is cached, as it already was for uploads.** A
  refresh over a tree nothing had touched still re-opened and re-parsed every PDF in it,
  and threw the text away again after chunking. The staleness check could not prevent it:
  the fingerprint it compares is a hash of the *extracted* text, so deciding a file was
  unchanged meant extracting it first. `file_texts` is keyed on a hash of the file's own
  bytes instead, which is computable without the work it exists to avoid.

  Uploads have had this since `blob_texts`, and the new table follows it: same columns,
  an `ExtractorVersions.Current` gate so an extractor fix reaches files indexed before it,
  and "produced no text" cached with its reason so a scanned PDF is not re-parsed on every
  pass forever. Two corpora indexing the same file share one row. Plain text and code are
  not cached, since reading the file is the extraction.

  Keyed on the extractor as well as the bytes, because which extractor runs is decided by
  extension and DOCX, PPTX and EPUB are all zip containers a rename moves between. Both
  come from one open, so the bytes hashed are the bytes parsed.

  This is also the first place a workspace document exists whole. Chunks carry their own
  text and nothing else did, so the content survived only as pieces.

- **Reading one file returns the document, not its chunks glued back together.** The file
  viewer, `GET /api/corpora/{name}/file` and the `dexicon://corpus/{name}/file/{path}` MCP
  resource all reconstructed a file by stitching its stored chunk payloads and marking the
  lines they could not account for, because those payloads were the only copy of the text.
  They now read `blob_texts` or `file_texts`, which cannot have holes and does not vary by
  which chunk set is being looked at. The response says which of the three it used.

  The stitch remains for a code file on a mount, where no text is stored because reading
  the file is the extraction.

  Reaching the document needs the hash recorded: `file_chunk_states.source_sha256` holds
  the bytes each set last read a file from, so `file_texts` can be looked up by path.
  Without it the cache saves the indexer work and gives a reader nothing. Per set, not per
  file, because a job can target one set while the others keep serving, and each set
  should be handed the text its own chunks were cut from. Written by the next index pass
  over the file, including the pass that skips it as unchanged; until then that file
  reports no document and falls back to the stitch.

  A file with no chunks now says which of the two it is. "Indexed, and the format yielded
  nothing" was reported as "no indexed file at that path", which sends the reader to check
  a path that is right.

- **`get_context` windows the document, not the chunks around the line.** It selected the
  chunks overlapping the requested range and stitched them, so the passage was bounded by
  chunk edges and could carry a gap marker where the index was missing lines. It now takes
  the range out of the extracted text, which has neither. A path two sources share still
  goes the old way, with the warning that says which file it chose.

### Changed

- **The file list pages, filters and sorts on the server.** Disclosing that a list was
  truncated was the first fix and the wrong one: a client can only filter and order the
  rows it fetched, so on a corpus larger than one page a name that IS in the corpus still
  came back as no match.

  `GET /api/corpora/{name}/files` gains `name` and `sort` beside the `status`, `limit` and
  `offset` it already had, and the page steps through with Previous and Next rather than
  stopping at a cap. `sort` takes `path`, `size`, `chunks` or `status`, each on a second
  key, because every one of those has ties in a real corpus and paging an unstable order
  repeats one row and skips another.

  The name is a plain substring matched without case, with `%` and `_` escaped: they are
  characters a caller typed, and unescaped the first matches every file and reads as the
  filter doing nothing.

- **The Files list shows every file, and says so when it cannot.** A 190-file corpus
  showed 100 rows with nothing indicating the rest existed, so a file added that morning
  was indexed, searchable, returned by the API, and absent from the screen.

  Three limits disagreed. `GET /api/corpora/{name}/files` pages at 100 when asked for no
  limit, and the web client asked for none. The list renders at most 300 of what it
  holds. The notice fired above 300 of the FETCHED rows, which could not happen, because
  only 100 ever arrived — a guard that cannot fire is the same as no guard.

  The page now asks for the endpoint's maximum and states what it is showing against the
  `total` the response has carried all along, including that the name filter only
  searches the rows it loaded.

- **Several corpora index at once.** The queue had one reader for the life of the
  process, so a corpus that takes hours owned the machine: measured on a 1,834-file PDF
  library, 6 files and 6,113 chunks in five minutes, which is 13.7 hours for the rest of
  it, and a sixteen-file corpus queued behind that waited all of them. There is one
  worker per `DEXICON_INDEXING_MAXCONCURRENTCORPORA` (default 4), each with its own
  catalogue connection.

  Two jobs on ONE corpus are still excluded, by the lease rather than by the queue, which
  is where that exclusion belongs. The scheduler below does not hand out work for a corpus
  that already has some running, so a worker never waits on another worker.

- **One queue for every kind of background work, with a concurrency per kind.** Sweeps ran
  on their own queue with their own reader, so the two sets of limits could not see each
  other, and a job whose corpus was busy was taken, refused by the lease and put back on a
  fifteen-second timer. Retrying a scheduling decision is how a worker comes to hold a slot
  for work that cannot run.

  There is now one queue and one pool. An item carries its type, its corpus and the key its
  handler needs, and dispatch has one rule: take the first pending item whose type has a
  free slot and whose corpus has nothing running. A corpus busy in this process is skipped
  rather than attempted, and an item that is not eligible costs nothing to leave where it
  is. A corpus held by ANOTHER process stays on the timer: the lease is the only thing that
  can see that hold, so the only way to learn of it is to be refused.

  Each type has its own limit, because the work is not comparable: `MAXCONCURRENTSWEEPS`
  (2) for walking a tree, `MAXCONCURRENTCORPORA` (4) for an incremental pass where most
  files are unchanged, and `MAXCONCURRENTREBUILDS` (1) for a pass that re-embeds everything
  it walks. Adding a kind of work is an enum value and a limit beside it.

  Fairness comes out of the corpus rule rather than a rotation: a corpus with five queued
  jobs runs one, its second is ineligible while the first holds the corpus, and the next
  corpus is taken instead. Measured with all four corpora queued at once against 7 slots,
  all four finished, the peak was 2 running together and none was starved.

  **A source added while a job was queued is walked by that job.** Queuing a refresh for
  a corpus that already has one pending returns the pending job, which is a promise that
  it covers what the caller asked for. The pass read its source list before it took the
  lease and before the row said `Running`, so a source added in that window was never
  walked and the request that added it was reported as covered by a pass that could not
  have seen it. The sources are read after the claim, where nothing can coalesce onto the
  job any more.

  A sweep refused the lease comes back the same way a deferred index job does. It did
  not: the pool discarded the sweep's result and only ever re-queued jobs, so a sweep lost
  to another process's hold was lost outright. A sweep for a corpus that no longer exists
  is terminal instead of retried, which is why the two are separate outcomes rather than
  one "skipped" flag.

  A slot is released by ticket. `TryTake` hands out a lease and `Completed` takes it back,
  so a release that arrives late finds nothing to free: the same key may legitimately be
  queued again while the first is running, and keying the release on it would free the
  replacement's slot. See [D-33](docs/decisions.md).

- **The embedding concurrency limit describes the endpoint, not the caller.** It was a
  semaphore constructed inside each call, so it bounded one embed and nothing else: with
  two corpora indexing at once, `DEXICON_EMBEDDING_MAXCONCURRENCY=4` would have sent
  eight. It is now counted per provider across every job, so raising the number of
  concurrent corpora does not multiply the load on Ollama, and a corpus indexing alone
  still gets all of it.

  Per provider rather than globally, because the number describes an endpoint: a local
  Ollama admitting four sequences says nothing about what a hosted deployment will take.

- **One corpus indexing alone uses the whole extraction budget.** A job read one file at
  a time, so the shared parsing limit only ever did anything when several corpora were
  indexing together. A source's files are now read concurrently and recorded one at a
  time: reading is the slow half and needs nothing shared, recording touches the pass's
  catalogue connection, its dictionaries and its counters, and is not what makes indexing
  slow.

  Order is no longer the walk's order. Nothing depended on it — every file's outcome is
  its own row — but a job's log now interleaves files.

- **Extraction is bounded across jobs.** `DEXICON_INDEXING_MAXCONCURRENTEXTRACTIONS`
  (default 4) caps how many files are being parsed at once. Parsing is CPU-bound, so the
  limit is the machine's rather than a corpus's; the permit is taken after the extracted
  text cache has been consulted, so a cache hit is not queued behind other corpora's
  parsing.

- **A new corpus is chunked at 256 tokens rather than 768.** Measured, not chosen:
  scored on whether the text handed back contains the answer rather than on which file
  ranked first, 256-token chunks answered 0.527 of 55 questions against 0.291 for 768 at a
  1,500-character budget, converging at 6,000 (0.600 against 0.582). The retrieval sweep
  found this twice before and set it aside, because a file split finer has more chances to
  land one chunk in the top ten and that flatters a file-rank metric. It is only flattery
  if the chunk is what the caller receives.

  **This costs no reindex.** An existing chunk set stores its own size, so the default
  applies to newly created corpora only; add a set on the new size, or rebuild, to apply it
  to content already indexed. Overlap moves with it to 32, the same eighth of the chunk the
  old default was, so the size changed and the ratio did not. Both are now settable:
  `DEXICON_INDEXING_CHUNKSIZE` and `DEXICON_INDEXING_CHUNKOVERLAP` in `.env`, which
  compose maps onto the `DEXICON__INDEXING__CHUNKSIZE` and
  `DEXICON__INDEXING__CHUNKOVERLAP` the app binds. A deployment not using compose sets
  the double-underscore form directly.

  See [D-31](docs/decisions.md#d-31-a-chunk-is-an-index-entry-and-the-model-decides-how-big-it-can-be)
  and its amendment.

- **Adding a chunk set honours the configured defaults.** Size, overlap and boundary mode
  fell back to a literal `768`/`100`/`language-aware` when the corpus had no set to inherit
  from, so `DEXICON__INDEXING__CHUNKSIZE` decided the shape of a new corpus and nothing
  about a set added to one.

- **A chunk too long for the model is split, not truncated.** The provider is asked not to
  truncate, so it refuses an over-long input. That refusal used to be answered by embedding
  the chunk truncated: a vector for the opening of the chunk, stored under the whole
  chunk's id, so its tail was unreachable by meaning and nothing downstream could tell. One
  index run produced 212 of those across 74 files.

  The refusal is now reported to the indexer, which halves the batch to find the chunk
  responsible and splits that chunk in two, repeating until the model accepts what it is
  given. A line boundary is preferred; a word boundary and then the midpoint are used where
  there is no line to cut on, which is what a minified file or a PDF page extracted as one
  line looks like. No vector is stored for less text than its chunk claims.

- **Per-file token density measurement is gone.** `TextDensity` embedded three
  3,000-character windows per file to estimate a characters-per-token ratio, about six
  seconds a file, to predict what the refusal states exactly for about 350 ms. The refusal
  is flat in input size, so rejecting a whole book costs less than embedding one chunk of
  it. Chunk sizing now uses the set's configured size, and the refusal corrects it.

  See [D-31](docs/decisions.md#d-31-a-chunk-is-an-index-entry-and-the-model-decides-how-big-it-can-be).

- **The chunking version goes to 8, so every corpus re-chunks once.** The per-file ratio
  narrowed a file's cut but was computed after the staleness key and never entered it, so
  removing it changed what a file produces while leaving the key identical. Files measured
  denser than their model's average, which was most of them, would otherwise have been
  skipped as unchanged and kept chunks no code path can produce.

- **The catalogue's journal mode and busy timeout are set by Dexicon, not inherited from
  whatever the database file carries.** Neither was established anywhere: a catalogue
  created by the connection string reports `journal_mode=delete` and `busy_timeout=0`,
  while a long-lived one is in WAL because journal mode is persistent in the file. A fresh
  install and an existing one therefore behaved differently under concurrent access, with
  nothing in the code to say which you had. WAL is now asked for explicitly and checked; if
  the filesystem refuses it, which happens on network shares, that is logged rather than
  passing as success.

  `Cache=Shared` is gone with it. It arrived with the first walking skeleton and nothing
  depended on it, and it decides how a blocked write fails: measured against a lock held
  longer than the caller would wait, a shared-cache connection fails with `SQLITE_LOCKED`,
  which no busy timeout can serve, where a private-cache one fails with `SQLITE_BUSY`,
  which one can. Ordinary contention is unaffected either way, since a lock held for 500ms
  is waited out in about 600ms.

  Prerequisite for [D-32](docs/decisions.md). `DEXICON__STORAGE__BUSYTIMEOUTSECONDS`,
  default 30, matching the provider's own command timeout so neither gives up first.
- **Ollama is told how many requests to admit at once.** `OLLAMA_NUM_PARALLEL` was not
  settable anywhere: not in `docker-compose.yml`, not in `.env.example`. Unset, Ollama
  admits one request and the embedder waits between them. Measured A/B/A/B against a
  running index, from the runner's own slot log: 49.8% and 50.4% busy unset, against
  78.0% and 80.3% at 4 — about 63% more embed calls in the same window.

  Not parallel decoding. The runner reports `n_seq_max = 1` either way, because Ollama
  pins an embedding model to one sequence, so this costs no VRAM and leaves the
  2,048-token context per request alone. It removes the gap between requests, which
  matters because one embed averages 25-27 ms. Defaults to 4, matching
  `DEXICON_EMBEDDING_MAXCONCURRENCY`, since sending more than Ollama admits only queues
  the difference.

- **The walk skips excluded directories instead of reading them.** It descended into every
  directory and filtered the files afterwards, so `.git`, `node_modules` and a database's
  data directory were enumerated in full and then discarded. On the repository that
  prompted this, 240,704 files were stat'd to keep 27,001; the walk now takes 13.9s where
  it took 99.4s, for an identical result on both counts. Indexing gains the same, because
  it shares the walk with discovery.

  Conditional on negation, and deliberately conservative about it. A rule set that
  re-includes something beneath an excluded directory makes skipping that directory a
  silent loss rather than an optimisation: the same repository keeps `!.vscode/launch.json`
  under an always-excluded `.vscode/`. A directory is skipped only where no negation can
  reach into it, so one narrow exception does not disable pruning for its siblings.

- **The catalogue's journal mode and busy timeout are set by Dexicon, not inherited from
  whatever the database file carries.** Neither was established anywhere: a catalogue
  created by the connection string reports `journal_mode=delete` and `busy_timeout=0`,
  while a long-lived one is in WAL because journal mode is persistent in the file. A fresh
  install and an existing one therefore behaved differently under concurrent access, with
  nothing in the code to say which you had. WAL is now asked for explicitly and checked; if
  the filesystem refuses it, which happens on network shares, that is logged rather than
  passing as success.

  `Cache=Shared` is gone with it. It arrived with the first walking skeleton and nothing
  depended on it, and it decides how a blocked write fails: measured against a lock held
  longer than the caller would wait, a shared-cache connection fails with `SQLITE_LOCKED`,
  which no busy timeout can serve, where a private-cache one fails with `SQLITE_BUSY`,
  which one can. Ordinary contention is unaffected either way, since a lock held for 500ms
  is waited out in about 600ms.

  Prerequisite for [D-32](docs/decisions.md). `DEXICON__STORAGE__BUSYTIMEOUTSECONDS`,
  default 30, matching the provider's own command timeout so neither gives up first.

- **A rejected credential says which caller, not just which path.** The line was the path
  and nothing else, so a browser tab left open on an expired session and someone working
  through a list of guesses wrote the identical warning, and a log full of them answered
  neither how many callers there were nor whether it was always the same one. Found on a
  live instance: two clients polling `/api/jobs` about a minute apart, both refused, with
  no way to tell what either of them was.

  It now carries the remote address, the caller's user agent and eight hex characters of a
  SHA-256 of what was presented. The digest is 32 bits deliberately: wide enough to
  recognise one caller repeating, narrow enough that it confirms no guess for anyone
  reading the logs. The user agent is the one field an unauthenticated caller writes, so
  it is capped at 120 characters and goes in as a property, which the console template
  escapes as it already does the path.

### Fixed

- **Turning a page of the file list no longer loses it to the filter's timer.** The name
  filter waits 250ms before it queries, and the pause was armed by the first render and by
  any keystroke, then reset the offset when it fired whether or not the query had changed.
  A page turned inside that window went back to the first one, with nothing on screen
  saying why, and a trailing space in the filter box was enough to do it.

  The pause is armed only when the typed value differs from the one in force. This is also
  what made the paging test fail on a slow runner: the click landed before the timer armed
  at mount, and the reset arrived between the click and the assertion.

- **A file the catalogue records with no chunks no longer keeps the vectors it had.** Four
  early exits wrote a zero-chunk row and returned before the success path's delete:
  extraction producing nothing, the chunker producing nothing, the walk excluding the file,
  and the same empty case for an uploaded document. The reconcile pass only removes files a
  walk stopped seeing, and all four leave the file in that walk, so nothing collected them.

  The stale vectors stayed searchable while the row reported zero chunks, so a hit pointed
  at text the file no longer contains and the Files list gave no way to tell. Measured on
  the index this was found in: 8 files holding 1,243 points between them, one of them 792.

  The delete is unconditional rather than conditional on the recorded count, which is what
  lets an existing deployment repair itself — where the two disagree, the count is the
  thing that is wrong. A delete that fails no longer writes a settled outcome over live
  chunks: the empty branches record `Failed`, and an excluded file keeps its chunk count as
  the only remaining record that those chunks exist. Both are retried next refresh.

- **A job's counters add up to its total.** `FilesTotal` counted only the files a source
  owns, while `FilesSkipped` also counts everything the walk excluded — binaries, oversize
  files, zero-byte files, unreadable ones — which never entered the total. A completed
  refresh reported 27,011 total against 27,031 skipped, and any progress reading
  `(done + skipped + failed) / total` went past 1.0.

  `FilesTotal` now means every file the pass will record, which is also the number the
  Files list shows for the corpus, so the two agree. Shadowing applies to the owned term
  only, so a file an exclusion catches under a nested source is counted by every source
  above it — in both terms, which is what keeps the invariant.

- **A file cannot be recorded as indexed while its vectors are missing.** The success path
  deletes a file's vectors before embedding the replacements and writes its row only after.
  A pass that died in between — a restart, a lost lease, a cancel — left a row reading
  `Indexed`, with a hash and a chunk count, over vectors that were already gone. The hash
  still matched, so the staleness check short-circuited the file on every later refresh: it
  was unsearchable, reported healthy, and no refresh would ever repair it. Found on a
  1,834-file corpus as 3 files short by 13,016 points, two of them reporting thousands of
  chunks while holding none.

  The row is now claimed before the delete and the claim made durable, so the same
  interruption leaves the file looking stale and the next pass indexes it. The two records
  are also compared: at the start of each source's pass, per-file point counts come back in
  one call and any file whose recorded count is not backed has its hash cleared, which the
  existing staleness check acts on. Both apply to workspace sources and to uploads.

  The comparison only ever clears a hash — it deletes nothing and writes no count — and is
  skipped entirely when the count cannot be read or comes back at its cap, because absent
  and zero are the same shape in that answer. `chunk_set_id` gains the payload index that
  every per-file filter already needed.

- **A cut context block kept the chunk before the hit, not the hit.** A block too large
  for the remaining budget is reduced to one piece; it took the lowest chunk index, which
  with `neighbours` is the chunk furthest before the match. A top hit at lines 122-165 came
  back as lines 1-30. Measured over 55 queries at a 1,500-character budget, the matched
  line was absent from 43 at two neighbours and from none at zero; afterwards it is absent
  from none at any width, and neighbours score what no neighbours scores.

  Nothing failed and the budget was full, which is why it lasted. It is the failure
  [05](docs/05-search.md) records for the search window — head truncation loses the answer
  — one layer up. Selection and rendering each chose a piece independently, so both were
  corrected: otherwise a block admitted on one chunk's behaviour could be cut on another's
  and vanish after the budget had been spent on it.

- **Context expansion reads the chunk set the caller asked for.** `get_context` and
  `POST /api/context` re-resolve the scope to find the collection a file's chunks live in,
  and they resolved the hits' corpus ids, which drops the `corpus:set` qualification. A
  bare corpus resolves to its default set, so asking for `books:fine` with `neighbours`
  above zero read its neighbours out of `books` instead.

  Nothing failed, which is why it went unnoticed: a chunk index means different things in
  two chunkings, so the window either pulled unrelated text or missed the hit's own index
  and fell back to the hit alone, which reads as expansion doing nothing. It bites in the
  case chunk sets exist for ([D-21](docs/decisions.md)) — a replacement backfilling on a
  new model while the live set keeps serving — where context for the set being evaluated
  came from the other one.

- **A split chunk is numbered in file order.** `ChunkIndex` is the ordering and neighbour
  key, not only part of the point identity: `ContextService` selects neighbours by the
  distance between indices, and four sites order by it. A split took the next index above
  every chunk in the file, which sorted the tail to the end of its own document and put it
  outside its own neighbourhood. Chunks are now numbered as they are written.

- **A split on a line no longer loses that line.** The head claimed the line the tail
  opens, and `Passage.Stitch` drops the lines a chunk shares with the one before it, so
  the tail's first line was missing from every assembled passage. A cut inside a line
  still leaves both halves on it, because that is where they are.

- **Each half keeps only the symbols it holds.** `symbols` is an exact filter, so copying
  the parent's list made a search for a symbol declared at the top of a chunk return its
  bottom as well.

- **A file's chunk count is what was stored.** It was the pre-split list, so a file that
  split reported fewer chunks in the UI than Qdrant held.

- **Heading context reaches a stored vector.** `EmbedText` was never copied out of the
  chunker into an indexed chunk, so a set with heading context embedded plain content and
  the setting did nothing. Longstanding, found reviewing the split path, which re-applies
  a prefix that was always empty.

- **A refusal reaches the code that already handled an embed not happening.**
  `EmbeddingInputTooLongException` was a sibling of `EmbeddingUnavailableException`, and
  three callers written against the base type silently stopped covering the refusal:
  search returned 500 on an over-long query instead of falling back to keyword, the model
  probe aborted on exactly the models that refuse rather than truncate, and the indexer
  failed the whole job instead of skipping a file whose chunk could not be divided. It is
  a subtype now, caught ahead of the base by anyone who can act on the difference.

- **A split on a line no longer inserts a blank one.** A chunk's content holds its lines
  newline-separated and never newline-terminated: measured over the chunker, no piece
  begins or ends with one, and `Passage.Stitch` supplies the terminator. Keeping the
  separator on the head made that chunk the only one carrying its own, which stitched as a
  blank line numbered the same as the tail's first real line. The separator now belongs to
  neither half, and a round-trip through `Stitch` holds it.

- **A truncated PDF is refused instead of being searched byte by byte.** A conforming PDF
  ends with `%%EOF`. One cut short by an interrupted download does not, and has no
  cross-reference table, so PdfPig rebuilds one by scanning the file backwards for object
  markers, re-reading a 4 KB block to advance a single byte. Over a bind mount each of
  those is a round trip: measured at about 6,000 a second against a 68 MiB file, which is
  3.3 hours for that file alone with the index job and its queue stopped behind it.

  The last 4 KB is now checked for the trailer, `startxref` and `%%EOF`, before the file
  is opened. The same file is refused in 2 ms and recorded as failed with its size. Both
  keywords rather than the marker alone, because those five bytes can appear inside a
  stream or a comment: run over the 1,983 PDFs to hand, the check rejects exactly the two
  truncated downloads and not one other file carries `%%EOF` without `startxref`. An
  84 MB PDF that is intact still extracts, in 13 s.

- **Extraction has a time budget, so one file can no longer hold a corpus.** `Extract` is
  synchronous and the libraries beneath it take no cancellation token, so an index job's
  own token could not interrupt one. A single file held a corpus for over two hours with
  two refresh jobs queued behind it, and only restarting the container ended it.

  Reads now pass through a deadline and throw once it is reached, so the stack unwinds and
  the thread is returned rather than left running until the process ends. The file is
  recorded as failed with no content hash, so a later refresh retries it. Set by
  `DEXICON_INDEXING_EXTRACTIONTIMEOUTSECONDS`, default 300, 0 to disable.

  The clock is read between operations on the file, so this bounds a file that keeps
  reading rather than wall-clock time in extraction. A single read that never returns, or
  a long stretch of computation inside the library, passes unchecked; bounding those needs
  process isolation, and the option says so where it is declared.

---

## 0.5.1 — 2026-09-19

### Fixed

- **The hooks' default timeout was below the cost of the query they exist to run.** The
  installed configuration set `DEXICON_TIMEOUT=5`, and a query the embedder has not seen
  was measured at about 5.4 seconds on a 15,213-chunk index, so the `UserPromptSubmit`
  hook timed out on exactly the case it is for. The default is now 10 seconds.

  A server that is down is still caught by the two-second connect timeout, so the longer
  value is only paid when one is answering slowly. The figure was already in
  `hooks/claude/README.md` as the reason the per-prompt hook ships switched off; it had
  not been carried into the timeout beside it.

  An existing installation keeps the file it has. Change `DEXICON_TIMEOUT` in
  `~/.claude/dexicon-hooks.env`, or delete the file and re-run
  `install-mcp.ps1 -What hooks` to regenerate it.

---

## 0.5.0 — 2026-09-19

### Added

- **The skill and two hooks install with the client.** `scripts/install-mcp.ps1` gains
  `-What skill|hooks|mcp|all` and `-Uninstall`, so the agent skill is no longer a file to
  copy by hand. It merges into `settings.json` rather than replacing it, backs it up first,
  and removes only entries it can name.

  `dexicon-corpora.py` runs on `SessionStart` and says what is indexed, which answers the
  failure where an agent does not search because it does not know anything is there. It
  costs one catalogue read.

  `dexicon-context.py` runs on `UserPromptSubmit` and retrieves one cited passage through
  `POST /api/context`. It is installed but **not** registered unless `-WithContextHook` is
  passed: it searches on every message, and measured against 15,213 chunks, a query the
  embedder has not seen costs about 5.4 seconds against 300 to 470 milliseconds for a
  repeat.

  Both are configured from one file, `~/.claude/dexicon-hooks.env`, which also holds the
  key. Its keys are named after the `POST /api/context` fields they set, and an unset one is
  not sent, so the server's default applies rather than the hook carrying a copy of it.

  Both exit 0 on every path and report faults on stderr. On `UserPromptSubmit` an exit code
  of 2 blocks the prompt and erases it, so a hook failing because the index was unreachable
  would delete what had just been typed. A missing dependency and a genuine empty result are
  reported differently, because a hook silent for both is one nobody can debug.

  See [D-30](docs/decisions.md#d-30-skills-and-hooks-install-with-the-client-under-a-dexicon-prefix).

### Changed
- **`POST /api/context` cuts its last block to fit rather than returning nothing.** Whole
  chunks only meant a budget below the smallest matching chunk came back empty with hits
  behind it, which reads as "nothing matched". Measured on a book corpus, one query's
  smallest chunk was 2,109 characters, so budgets of 1,500 and 2,000 both returned nothing.
  It also left the tail of every budget unspent, when the opening of the next result is the
  cheapest way to see that a variant exists.

  At most one block is cut, always the last, and only when at least 300 characters of it
  would show. The passage says `… N characters of this chunk not shown …`, and the
  citation reports the lines actually present rather than the chunk's full span, so a
  citation is never a claim about text the caller was not given.

  Where the cut falls depends on whether line numbers were asked for. With them it is a
  line boundary, because half a line carries the wrong number. Without them it is a word
  boundary when a line boundary would waste more than half the budget: a chunk of a book
  is often one paragraph, and on a real index that kept 243 characters of a 1,500
  character budget. The budget is measured against the header actually written rather
  than an estimate of it, so a request is answered within what it asked for.

  `partialBlocks` on the response, and `partial` with `omittedChars` on each citation, carry
  it as data. They are separate from `truncated`, which goes on meaning hits were dropped: a
  budget that lost results and one that shortened them are different things to know. A chunk
  with no line break inside the budget cannot be cut, so it is dropped as before and the note
  names the figure that would have fitted.

  The `UserPromptSubmit` hook's default budget drops from 4,000 to 2,000, since it no longer
  has to clear a whole chunk. See
  [D-29](docs/decisions.md#d-29-an-integration-document-and-retrieval-in-one-call).

- **The skill is `dexicon-search`, and answers a direct invocation.** It moves to
  `skills/dexicon-search/`, so the slash command moves from `/dexicon` to
  `/dexicon-search`. `~/.claude/skills/` is a namespace the user owns, and a bare `dexicon`
  is unambiguous only until a second tool wants the same name.

  Invoked directly it used to load guidance written for the model and stop. It now takes the
  text after the command as a query and searches, and lists the corpora when given nothing.

---

## 0.4.0 — 2026-09-19

### Added

- **An HTTP API for scripts, and `POST /api/context`.** The REST surface already took the
  same keys and the same per-corpus scoping as MCP; what it lacked was a shape for a
  caller with no agent loop. Search returns ranked hits with a preview of each, so
  assembling something worth putting in a prompt meant a call per hit and joining the
  overlapping chunks afterwards. `POST /api/context` runs the search, takes whole chunks,
  joins the ones belonging to the same file, and stops at a stated budget, returning the
  passage and a citation per block of it.

  The budget is characters. No tokenizer ships, and the model reading the passage is not
  the model that embedded it, so a token figure would be an estimate presented as a
  budget. `truncated`, `droppedHits` and `degraded` say what the passage is missing and
  why, which matters when nothing reads it before it reaches a prompt.

  The endpoints meant for other software are now described by their own OpenAPI document,
  `clients/web-ui/Dexicon_integration.json`, generated beside the full one. The full
  document also describes the workspace browser, the model probe and the sign-in
  endpoint, so publishing it whole as an integration contract would have committed the
  project to the shape of the UI. See [docs/13](docs/13-integration.md) and D-29.

  A running instance serves that document at `/openapi/integration.json`, behind the same
  bearer as the endpoints it describes, so a client can be generated against the version
  actually answering rather than against a checkout that may be ahead of it. No other
  document name is served.

  There is no outbound webhook, deliberately.

- **A search result marks the terms that matched it.** A hit is up to 1,500 characters and
  nothing on the card said which part answered the query. Terms of three characters or more
  are marked, minus a stopword list: `chunking strategy and overlap size` over a shelf of
  books produced 133 marks, 47 of them the word "and". Amber rather than the accent blue,
  which already means "default" and "in use" elsewhere in this UI.

- **The file list of a corpus can be filtered by name.** It was 96 alphabetical titles, each
  present twice as PDF and EPUB, and the status tabs do not narrow that when every file is
  indexed. Filtering is client-side over the list already on the page, which is capped at
  300 rows; the cap now counts the filtered list rather than the whole one.

- **A skeleton while a search runs.** A search over roughly 15,000 chunks measured 5,028 ms
  with no sign of progress but a spinner inside the button, which at that length reads as a
  hung page.

### Changed

- **Nothing is minted on first run, and the password is what gets printed.** The
  generated bootstrap key existed because a token was the only credential: it was how you
  reached the UI, so one had to exist before you could do anything. The admin password is
  that now, and printing a second secret beside it invited pasting the wrong one into the
  sign-in form.

  Creating the key in the UI is also the only way to choose what it reaches. At first run
  there is no corpus to choose, so a minted key could only ever reach everything.

  `DEXICON_BOOTSTRAP_TOKEN` still works when set: a pinned key for scripted setup and CI,
  adopted with `search` and `ingest`. `scripts/dev.ps1` gains a `password` command, and
  its `token` command now says where keys come from instead of reporting nothing found.

- **Keys issued before D-28 stored a scope they could not use.** `TokenService` strips
  `admin` when it builds a principal, so such a key was already refused every admin
  endpoint, but the row kept the value and the Access page reads the raw column. Found by
  migrating a real catalogue and asking the running server: `GET /api/tokens` returned 403
  "This token has [search, ingest] and needs admin" while the row said
  `search,ingest,admin`. A data-only migration strips it wherever it appears.

- **An admin password and API keys scoped to corpora, replacing tenancy.** The tenant was
  built so several agents could share one endpoint and see different material, chosen by
  `X-Dexicon-Tenant`. It never could: a token was bound to exactly one tenant, so the header
  could only agree with it or return 400. `Tenant`, `CorpusVisibility`, `CorpusGrant`, the
  header and the write-only `tenant_id` Qdrant payload field are all gone.

  Administration is now one password, the only route to the `admin` scope, exchanged at
  `POST /api/session` for a short-lived bearer held in memory. Nothing durable carries
  `admin`, so no credential in an agent's configuration can delete a corpus. Failed
  sign-ins are throttled by a delay that doubles and caps at 30 seconds, counted globally
  because there is one password and one thing to guess; a delay rather than a lockout,
  because with a shared credential a lockout is a denial of service anyone able to reach
  the port could inflict on the owner.

  An agent's key maps to corpora in the UI, read per request rather than cached on the
  principal, so ticking a corpus reaches the agent on its next call rather than after the
  60-second principal TTL or a client restart. No rows means every corpus; a key that
  should reach nothing is revoked. Corpus names are now unique across the install, which
  is what an agent passing `corpus: ["books"]` already assumed: previously a tenant that
  owned `books` and was also granted someone else's `books` reached only its own, and the
  other had no name that addressed it.

  `index_refresh` is filtered out of `tools/list` for a key without `ingest` rather than
  refused when called, because an agent that can see a tool will call it and spend a turn
  on the error. Document upload, attach and detach move from `ingest` to `admin`, leaving
  `ingest` meaning reindexing alone, which closes Q4.

  Migrating an existing catalogue renames colliding corpus names, keeping the oldest and
  suffixing the rest. See [D-28](docs/decisions.md#d-28-an-admin-password-and-scoped-api-keys)
  for what was rejected, including a corpus-selecting header, an identity header the UI
  maps, and a named mapping several keys share.

---

- **Prose is set in the body face, and only code keeps the monospace.** Every passage
  rendered in 12px monospace, including book text, which is slower to read than the same
  page set anywhere else. A chunk whose language is a programming language keeps it, because
  alignment and character distinction are the point there.

  Scheduled refreshes that indexed nothing now collapse into one line naming the corpora and
  the span. `DEXICON__INDEXING__REFRESHMINUTES` runs one per corpus per interval, so an
  unchanged tree fills the Jobs screen with identical "0 indexed, 0 chunks" cards and pushes
  the run that did something below the fold. A run that indexed anything, a failed run, and
  a lone quiet run each keep their own card: nothing indexed and nothing written is also
  what a failure looks like.

  Smaller corrections in the same pass. A document title wraps as a sentence rather than
  mid-word, while a path keeps monospace and breaks anywhere. `book.pdf#page=198` no longer
  sits beside a `Page 198` badge saying the same thing, though the full citation stays in
  Copy path. "All visible corpora" reads "All corpora", visibility having gone with tenancy.
  File counts pluralise.

## 0.3.0 — 2026-09-19

### ⚠️ Upgrading

- One migration, `ModelContextTokens`, applied at startup. Widening only: one nullable
  column on the measurements table. It is empty until each model is probed again, and an
  empty value means the chunk size is not capped, which is the behaviour before this change.
  **Re-probe each embedding model from the Models screen** to get the cap.

- The chunker version moves 4 to 7 and the extractor version 5 to 6, so **every file
  re-extracts, re-chunks and re-embeds on the next index run**.

- The extractor version moves 4 to 5, so **every PDF re-extracts, re-chunks and re-embeds on
  the next index run**. Other formats are untouched and their cached text still matches.

- One migration, `SourceFilterInheritance`, applied at startup. Widening only: a source's
  filter columns become nullable and a corpus gains four default columns. Every existing
  source keeps the value it had, so it stays an explicit override and indexes exactly as
  before. Nothing re-indexes on upgrade.

### Added

- **The screen is in the URL.** `#/corpora`, `#/corpora/books`, `#/settings`. Reload,
  bookmark and the back button all work from it; before, a reload landed on Search whatever
  you were reading, and the back button did nothing.

  A fragment rather than a path, because the app is served by the same origin as the API and
  every path but `/` needs a token: a real path would 401 on exactly the reload this fixes.
  An unrecognised fragment lands on Search and rewrites itself rather than showing a blank
  page.

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

### Changed

- **A search result is a window onto the matching passage, not the whole chunk.** Measured
  over eight questions against a 95-book library, five results each, a search returned a
  mean of 40,797 characters, about 10,200 tokens: the chunk is sized for retrieval, at
  2,065 tokens, and was being handed back whole. An agent with a 200k window could afford
  twenty searches.

  The window is **centred on what matched**, not taken from the head, and that is the whole
  of the design. Head truncation was the obvious implementation and loses the answer: at
  1,500 characters, 12% of real hits had their first matching term already past the cut, and
  only 25% had all of them inside it. Matched terms sit at 0.10 of the chunk at the median
  and 0.56 at the 90th percentile. Cut on line boundaries, and an elision is marked.

  `search_index` takes `max_chars_per_hit`, default 1,500; `0` returns whole chunks. On the
  same eight queries: **40,715 → 6,532 characters per call, 10,178 → 1,633 tokens.**

- **One result per document, where a library holds the same title twice.** The measured
  corpus returned 3.0 distinct books per 5 results, because most titles are held as both
  PDF and EPUB. Collapsing them is a quality fix and is counted as one: dropping a duplicate
  saved 0.2% of the tokens, because it only promotes another chunk of the same size. It
  raised distinct books per 5 results to **4.8**, and the text not already returned earlier
  in the same response from 85% to **100%**.

  `distinct_titles`, default on. Off returns every copy, which is what comparing two
  extractions of one title needs. Collapsing can return fewer results than were asked for,
  and says so rather than being quietly short.

- **Hybrid search fuses on normalised scores rather than on rank.** Reciprocal rank fusion
  has no weight to mis-set, which reads as a virtue until the two lists differ in quality: a
  lexical match at rank 3 counts for as much as a semantic match at rank 3, however much
  worse it is. Asking when to use an event-driven architecture returned a chapter on C#
  delegates third, because the word "event" is in all of them.

  Distribution-based fusion normalises each list's scores before combining, so a weak lexical
  match contributes in proportion to how weak it is. It carries no weight either, so the
  objection that ruled out a client-side weighted merge does not apply. Measured over the
  96-book library and this repository's own documentation:

  | | RRF | DBSF | semantic only |
  |---|---|---|---|
  | conceptual question, precision@3 | 0.62 | **0.88** | 0.92 |
  | verbatim passage, found in top 5 | 0.94 | **0.97** | 1.00 |
  | exact identifier, MRR | 0.69 | **0.70** | 0.46 |

  Better on all three, and the last row is why hybrid exists at all: semantic search cannot
  find `DEXICON__INDEXING__DOCUMENTMAXBYTES` at any rank. `D-06` is revised with the
  measurement, having previously rejected this on reasoning alone.

### Fixed

- **The truncation warning named no file.** Over-long input is embedded shortened rather
  than failing the file, which is the right trade and useless to act on if the log will not
  say whose text it was. One run reported 123 of them: 123 chunks with their tails dropped
  and no way to find out which documents they came from. Every batch is one file's chunks,
  so the warning now names the file and the range within it.

- **Shortening a chunk was spent from the retry budget.** The degraded attempt ran inside
  the loop meant for a struggling embedder, so a caller with retries turned off had nowhere
  to make it and the file failed instead: the opposite of what the path exists for. It also
  waited out a backoff first, half a second in front of a call certain to be made and
  certain to differ. The attempt is now its own, and immediate. A transient failure still
  gets exactly its configured retries and no more.

- **A chunk aimed at the model's whole context, leaving its size estimate nowhere to be
  wrong.** Capping the size at the context and then measuring each file's own density took
  truncation warnings from 235 to 220 to 199 over a 96-book library. Both corrections were
  right and neither addressed this: the budget is `contextTokens * ratio`, so a full chunk
  is sized to land exactly on the ceiling, and the ratio is an estimate sampled from three
  windows. A chunk denser than its file's sample goes over, and slicing the narrowed files
  showed them still doing it at the reduced budgets their density had earned.

  A chunk now aims at 90% of the context. The figure is not a guess: on the same library a
  chunk size of 1,870 tokens retrieved indistinguishably from 2,065, so the flat region
  reaches about 91% of this model's context and the margin costs nothing measurable. Two
  thirds, the previous answer, is not free: 1,365 tokens retrieved measurably worse.

  The cap and the probe's recommendation read the same number, so taking the advice cannot
  produce a set the indexer then quietly narrows.

- **An overlap could equal the chunk size and stop the file indexing.** Found by testing the
  cap against a one-token context: the overlap was clamped to at least one, which is not
  smaller than a size of one, and the chunker rejects that rather than chunking. The floor
  is zero.

- **A file denser than its model's average had its chunks truncated.** Capping the chunk
  size at the model's context took truncation warnings from 235 to 220 over a 96-book
  library, which is most of the defect left standing. The cap fixed the budget; it did not
  fix the assumption under it, that one characters-per-token ratio describes a whole
  library. Measured, that ratio runs from 2.93 in the code-heavy chapters of a programming
  book to 5.94 in plain prose, a factor of two. The model's own measured 3.80 is an average
  over prose, code and JSON, so it sits above the dense end and those files overflowed.

  No single budget solves this. Sizing the library for its densest content means 5,997
  characters, and 5,460 retrieved measurably worse than 7,480, so it would trade a loss
  across every chunk to protect about 1.7% of them.

  The ratio is now measured per file as well as per model: three windows spread through a
  file, the densest wins, and the file is chunked at `min(model, file)`. A file at or above
  the model's ratio is untouched, so the cost falls only where it buys something. Measured
  only for a file about to be chunked, never for one the fingerprint says is unchanged, so
  an incremental refresh that finds nothing to do still costs zero embedding calls.

- **The recommended chunk size pointed below a configuration already measured as worse.**
  The probe suggested two thirds of the model's context, and the UI puts that number
  straight into the chunk size field of every new corpus. The two thirds was headroom
  against a chunker that converted tokens to characters with a flat 4 and had nothing
  checking the result. That conversion now uses the measured ratio and the size is capped at
  the context, so the headroom is enforced; recommending it as well charged for it twice.

  On this model two thirds is 1,365 tokens, which the measured ratio makes 5,187 characters.
  Measured over a 96-book library, a run at 5,460 characters retrieved much worse than one
  at 7,480, while 7,480 and 8,260 were indistinguishable. The recommendation is now the
  context itself. A provider that reports no token counts has no context to cap against, and
  its character estimate keeps the two thirds.

- **Chunks were larger than the model that had to read them.** A chunk size is set in
  tokens and enforced in characters, and two conversions between the two were wrong in the
  same direction. The size was not capped at the model's context, so this library ran 2,065
  tokens against a model that reads 2,048. The character conversion used a flat 4 where the
  probe had measured 3.8 for that model, asking for 5% more characters than the token budget
  it claimed to enforce. Together, 8,260 characters of a model whose context is nearer 7,780
  of them: Ollama returns a vector for the part it read, so the tail of every full-size chunk
  was embedded by nothing. One re-index logged 235 truncation warnings.

  The chunk size is now capped at the model's measured context, and the conversion uses the
  ratio measured for that model. A set already inside both, which includes the 768-token
  default on every model here, is unchanged. An unmeasured model keeps the configured size
  and the flat 4. The effective budget is part of the chunking fingerprint, so re-probing a
  model into a different budget re-chunks the corpus instead of leaving chunks sized for a
  number no longer in force.

  The probe measured the model's context on every run and then discarded it; it is now
  stored on the measurement, which is what the cap reads.

- **A code listing in an EPUB or HTML file was extracted as a single line.** The walker
  collapses source whitespace, which is right everywhere except inside `<pre>`, where the
  whitespace is what the author was preserving. `if (a) {` / four spaces / `b();` / `}` came
  out as `if (a) { b(); }`. The listing stayed findable, which is why it went unnoticed, but
  it was unreadable in a search result, and the chunker splits on lines, so a long listing
  was one line it could not split at all.

- **A PDF's page numbers were being indexed as text.** Counted across this corpus, PDFs
  produced 99 bare numbers per 1,000 extracted blocks against 13 from the EPUBs of the same
  titles, so roughly one extracted block in ten was a page number. Each becomes a line of
  its own, so a passage carried over a page break was embedded as "...the section ends here.
  247 Chapter 9 opens with...", a sentence in no edition of the book.

  Position decides, not the digits: only a bare arabic or lowercase-roman number whose block
  sits in the top or bottom 8% of the page is dropped. The same digits in the body are a
  table cell, a numbered list item or a line of code. The pattern is anchored, so a running
  foot carrying a chapter title and a footnote opening with a marker both survive.

  Re-measured after re-indexing the 96-book corpus: bare numbers fell from 99.0 to 5.8 per
  1,000 lines, below the 22.9 the EPUBs of the same titles carry. In paired ranking
  comparisons, where one query matches both formats of a title, EPUB ranked higher in 29 of
  42 pairs before (69%, exact binomial p = 0.0195) and 22 of 43 after (51%, p = 1.0): the
  two formats now retrieve equally well.

  A second measurement was reported here as an unexplained asymmetry: a passage taken from
  an EPUB found the PDF of the same title 88% of the time, where a passage from a PDF found
  the EPUB 64% of the time. That was the measurement, not the extraction. It drew its probes
  from whatever the queries returned without requiring the other format to exist, and 19 of
  the 55 PDF titles in the library have no EPUB at all, so those probes could not succeed
  however good the text was. The 64% sat one point under the 65% ceiling composition
  imposed.

  Restricted to the 36 titles held in both formats, the two directions are the same:
  **EPUB finds the PDF 15 of 16 times, PDF finds the EPUB 15 of 16 times, 94% either way.**
  There is no fidelity asymmetry between the formats and there was nothing left to explain.

- **Probing one model by two names took the models list to a 500.** A measurement is stored
  under the name it was requested with, and the table is keyed on (provider, model), so
  `embeddinggemma` and `embeddinggemma:latest` are two legal rows for one model. Listing them
  normalised both to a single key and then keyed a dictionary on it, which threw. Reachable
  by doing nothing stranger than probing the same model twice by its two names. The listing
  now groups instead, and the most recent measurement wins, because a re-probe is a
  correction.

- **Collapsing duplicate documents returned fewer results than were asked for.** Search
  over-fetches so that dropping a near-duplicate can promote the next distinct document, and
  two ceilings quietly threw that away. The vector store clamped every request to 50 — a
  bound that belongs on what a *caller* may ask for, and which the search endpoint and the
  MCP tool already apply — and the over-fetch itself was a fixed four times the limit,
  which is too few when a document has many chunks.

  How far it has to reach depends on the chunk size. Measured on one corpus held two ways,
  asking for ten: at 2,065 tokens it returned **6.5**, and the same books at 1,365 tokens
  returned **3.7**. With both ceilings lifted, the first returns **10 of 10**.

- **Each source's indexing summary reported the whole job's counts.** The log line took the
  job's running totals, which accumulate across every source and every chunk set, so each
  source in turn was credited with all the work done so far: `books/manuals` owns one file and
  its line read `96 indexed`. It now reports the difference either side of its own pass.

- **`docker-compose.external.yml` was documented but did not exist.** `docs/09` listed it in
  the overlays table and set out its contents in full. Written from that description, so the
  documentation and the repository agree: it drops `dexicon-ollama` and points at one you
  already run, with `--profile in-stack` to bring the local one back. The table's claim that
  it drops *both* dependencies is corrected to match what it does.

- **The chunk-size recommendation overshot the model's context.** `Test limits` measures the
  character ceiling with its own filler — four repeated words, which tokenizes about as well
  as text ever does — and measures characters-per-token separately, averaged over prose,
  code and JSON. It then divided the first by the second, which applies a density correction
  twice in opposite directions and cancels out the third it had deliberately left spare.

  Against `embeddinggemma` it recommended **2,065 tokens for a 2,048-token context**, and
  about 4% of real embeds were clamped by a number that was supposed to have headroom. The
  token figure is now counted in tokens, from the same text the ceiling was measured on:
  **1,365**, which is two thirds of the context. The character budget is unchanged.

  A chunk budget is in tokens and the limit is in tokens, so the conversion had no business
  being in the middle of it.

- **Over-long input was embedded truncated, and recorded as fully indexed.** Ollama's
  `/api/embed` shortens anything past the model's context and returns a vector, with no
  field in the response to say it happened
  ([ollama/ollama#14259](https://github.com/ollama/ollama/issues/14259)). The end of the
  chunk was then missing from its vector while the file was reported as indexed, search
  could never match it, and nothing anywhere was red. Measured on the real library: about
  4% of embed calls, because `embeddinggemma` has a 2,048-token context and the chunk set
  was cut at 2,065.

  The request now asks the provider to refuse instead. A refusal is handled rather than
  retried — the same input fails identically every time, so the backoff loop has nothing to
  offer it — by embedding once with truncation allowed and logging a warning that names the
  model, the length and the fix. A worse vector for one chunk rather than a failed file,
  and no longer silent. The chunk set form already warns about the size beforehand.

- **A model probe could spin forever with nothing to show for it.** The probe embeds two
  dozen inputs one after another, and the embedding service answers indexing first, so
  during a reindex it can run for the better part of an hour. It had no deadline and the
  button had no feedback: a spinner that never ends is indistinguishable from a hang, and
  the only way out was to reload the page.

  The endpoint now gives up after 90 seconds with an error that says why and what to do
  about it, since the probe has no partial answer and grinding on is only a slower way to
  fail. The button shows how long it has been running and can be stopped, and stopping is
  not reported back as a failure.

- **A file reachable from two sources was indexed twice.** A source covers its whole tree,
  so adding one above an existing source made everything beneath reachable from both, and
  file identity is (source, relative path), so each copy was a separate row, chunking and
  set of vectors. The corpus silently doubled, every search returned the same passage twice,
  and the second copy was paid for in embedding time. Nothing failed and no count said which
  files were affected.

  The inventory is now made distinct across sources before anything is indexed: a file
  belongs to the most specific source that covers it, and a source higher up keeps what the
  deeper ones do not claim. That is what makes "index the loose files in this folder" work
  without anybody maintaining exclusions that go stale as soon as a source is added.

- **Adding a source did not update the corpus you added it to.** Every other dialog reloaded
  the corpus; this one refreshed only the list behind it, so adding a folder looked like it
  had done nothing until the page was reloaded by hand.

- The Add source form defaults to a 20 MB size cap rather than 2 MB. It caps ordinary files
  only — PDFs, EPUBs and the other document formats are measured against
  `DEXICON__INDEXING__DOCUMENTMAXBYTES` instead — so the old default excluded nothing but
  large text.

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
  of 138 files indexed 95 of them; one book sat directly in `manuals/` while every source was
  `manuals/<topic>/`, and a keyword search on its exact title returned four other books.

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
  `''` rather than `/`, so the button above `books/manuals/Architecture` was labelled
  `booksmanuals`. Navigation was correct; only the label was wrong.

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
