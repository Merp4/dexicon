# Troubleshooting

Common problems during initial setup, listed by symptom rather than by cause. Most were
encountered while developing Dexicon.

---

## "My PDF is in the corpus but nothing matches it"

**Look at the file's status in the corpus.** A PDF with no text layer (a scan, or a photo of
a page) is marked `empty`, with the reason `no text layer: this is a scanned PDF, and
OCR is not supported`. Dexicon reads text layers and does not perform OCR; the file is
reported as empty rather than indexed as blank.

If the status is `indexed` but search still misses content that is definitely in the file,
check the chunk count. A book-sized document with a handful of chunks means the text
arrived as very few very long lines, and the end of each was silently truncated by the
embedding model. Run **Test limits** on the model in the Models screen: it will tell you
the real ceiling and whether the model truncates or errors.

---

## "The agent connected but sees no corpora"

Three causes, in the order to check:

1. **The key has no `search` scope.** `list_corpora` returns
   `This key has scopes [ingest] and needs 'search'`. Issue one that does.
2. **The key is not mapped to them.** A key reaches the corpora ticked against it under
   Access, or every corpus when nothing is ticked. `list_corpora` says
   `Key 'x' can reach no corpora`. Tick one, and the next call sees it.
3. **There genuinely are none yet**, because the first index has not run. The UI shows the
   job; `index_status` reports it too.

---

## "Search returns nothing, but the corpus says it is indexed"

Check `/healthz` or the Settings → **Check connectivity** button.

If embeddings are unavailable, semantic and hybrid search degrade to **keyword only** and
report it in the response: the REST result carries `degraded: true` and a `degradedReason`,
and the MCP reply a `! DEGRADED: <reason>` line after its header. Keyword search continues
to work.

If the corpus was indexed a while ago and nothing matches at all, it may be holding vectors
in a collection nothing addresses any more. Extraction, chunking and framing are all
versioned and are part of the staleness fingerprint, so an upgrade can legitimately mark
everything `pending`. Reindex and watch the chunk counts come back.

---

## "The mount path is not in the picker"

The picker browses what is mounted at `/workspaces` inside the container, not your host
filesystem. If your code is not under `WORKSPACE_ROOT` in `.env`, the container cannot see
it and neither can the picker.

```bash
grep WORKSPACE_ROOT .env
docker compose exec dexicon ls /workspaces
```

The second command is the truth. A path that is not in that listing does not exist as far
as Dexicon is concerned, whatever the host says.

Mounts are read-only by design; Dexicon does not write to source trees.

---

## "Everything is 401"

- **No key**: `Provide a key: Authorization: Bearer dex_…, or sign in at / for the UI.`
- **Revoked or expired**: a revoked key is refused from the next request. A key that has
  expired can still be accepted for up to 60 seconds, the lifetime of the principal cache.
- **Lost a key**: there is nothing to recover. Sign in and issue another under
  **Access**, then revoke the old one. A key's secret is stored only as a PBKDF2 hash.
- **Lost the admin password**: set `DEXICON_ADMIN_PASSWORD` in `.env` and restart. A
  configured value is applied on every start, which is what makes it the way back in.
  Without that the only recovery is deleting the catalogue, which deletes every corpus
  with it.
- **Pasted a key into the sign-in form**: the UI takes the password, not a key. A key
  authenticates but can never carry `admin`, so it would load the shell and then be
  refused on every screen.
- **The password is refused, and then the form waits**: the log prints it in double quotes,
  and the quotes are not part of it. After two failed attempts each further one doubles the
  wait, up to 30 seconds. The count is shared by every caller, is forgotten after 15
  minutes without a failure, and is cleared by a correct password.

---

## "The first run sits doing nothing for ten minutes"

Ollama is downloading the embedding model, several hundred megabytes. The healthcheck
waits for the **model** to be present rather than the daemon alone; otherwise Dexicon
begins indexing against a model that is still downloading, and its first files fail to
embed, which reads as a bug.

```bash
docker compose logs -f dexicon-ollama
```

`docker compose up -d` waits for that healthcheck, which allows about ten minutes. On a slow
connection it can give up first with a dependency that failed to become healthy. The download
carries on in the container, so run `docker compose up -d` again once
`docker compose logs dexicon-ollama` shows the pull finished.

---

## "Warnings on first start"

```
[WRN] The migration operation '"PRAGMA foreign_keys = 0;\n"' from migration '"DocumentLibrary"' cannot be executed in a transaction. If the app is terminated or an unrecoverable error occurs ...
[WRN] Overriding HTTP_PORTS '"8080"' and HTTPS_PORTS '""'. Binding to values defined by URLS instead '"http://0.0.0.0:8477"'.
```

Expected, and not suppressed. SQLite cannot drop a column in place, so EF rebuilds the
table and has to disable foreign keys outside the transaction. It matters only if the
process is killed *during* a schema migration; see
[09-deployment.md](09-deployment.md#the-warnings-on-first-run) for what to do then, and for
the full list. The `HTTP_PORTS` line is ASP.NET Core noting that the base image sets a port
and Dexicon sets another.

---

## "Indexing is slow"

On CPU Ollama, `embeddinggemma` embeds roughly 0.5 to 2 chunks per second: batches of 32
chunks of about 1,000 characters took 15 to 81 seconds on a CPU-only desktop. A 400-page
book is thousands of chunks. This reflects hardware throughput rather than a stall; the job
reports a phase and a per-file count, which distinguishes a slow job from a stalled one.

To make it faster: use a GPU (`docker-compose.gpu.yml`), raise
`DEXICON_EMBEDDING_MAXCONCURRENCY` and `OLLAMA_NUM_PARALLEL` together in `.env`, or use a
smaller model. A corpus with several chunk sets walks the tree once per set, so it costs
proportionally more.

---

## "The build fails with MSB3027, file locked by Dexicon"

Only when running the app on the host. The detached dev instance holds its own DLLs, and
the test project references the host so `dotnet test` rebuilds it.

```powershell
./scripts/dev.ps1 test      # stops, tests, restarts if it was running
```

---

## "I changed a setting and nothing happened"

If it is a **chunk setting**, it belongs to a chunk set rather than the corpus; changing
it queues a re-chunk of that set only.

If it is an **embedding model**, it cannot be edited at all. A different model is a
different vector space, so you add a set on the new model, let it backfill while the live
one keeps serving, and promote it when it is complete.

If it is a setting in **`.env`**, run `docker compose up -d` to recreate the container:
configuration is bound at startup, and `docker compose restart` keeps the old environment.
`.env` takes the `DEXICON_*` names listed in `.env.example` and in
[09-deployment.md](09-deployment.md#configuration); Compose passes each to the container as
`DEXICON__SECTION__KEY`, and a `DEXICON__*` name written in `.env` is not passed at all. A
setting that has no effect is a defect: a test fails the build for configuration that
nothing reads, and has caught two such cases.

---

## "A history source's newest commit stopped moving"

The source follows a ref, and nothing moves that ref but git on the host. The row says
which case it is:

- **"52 behind origin/main"**: it follows a local branch, often through `HEAD`, and
  nobody has pulled. Edit the source and pick **Follow origin/main instead**, or pull.
- **"fetched 3d ago"**: it follows a remote-tracking branch and the host has not fetched
  since. Schedule `git fetch`, or run `git maintenance start` and follow the prefetched
  ref, as [04](04-ingestion.md#following-a-remote-without-dexicon-fetching) describes.
- **A prefetched ref**: `git maintenance` has stopped running, or cannot reach the
  credential. A prefetch records no time, so a newest commit that stops moving is the
  only sign.

Dexicon does not fetch.

---

## "The container restarts in a loop"

```
Unhandled exception. System.ArgumentException: A bootstrap token must start with 'dex_'.
```

`DEXICON_BOOTSTRAP_TOKEN` in `.env` is set to something that is not a Dexicon key. A value
there must read `dex_<id>_<secret>`; blank it, or use a key issued under **Access**, then run
`docker compose up -d`.

---

## Getting more detail

```bash
docker compose logs -f dexicon
DEXICON_LOG_LEVEL=Debug docker compose up -d dexicon
```

Set `DEXICON_LOG_LEVEL` in `.env` to keep it. Logs are UTC with a `Z`, matching the API.

If none of this covers it, open an issue with the log lines around the failure and what you
expected instead. If it is a security problem, use the process in
[SECURITY.md](../SECURITY.md) rather than the issue tracker.
