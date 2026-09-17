# Troubleshooting

Common problems during initial setup, listed by symptom rather than by cause. Most were
encountered while developing Dexicon.

---

## "My PDF is in the corpus but nothing matches it"

**Look at the file's status in the corpus.** A PDF with no text layer (a scan, or a photo of
a page) is marked `empty`, with the reason `no text layer — this is a scanned PDF, and
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

1. **The token has no `search` scope.** `list_corpora` returns
   `This token has scopes [ingest] and needs 'search'`. Issue one that does.
2. **The corpora belong to another tenant.** A token is bound to one tenant, and a corpus
   is private to its own unless shared. `list_corpora` says
   `No corpora are visible to tenant 'x'`.
3. **There genuinely are none yet**, because the first index has not run. The UI shows the
   job; `index_status` reports it too.

---

## "Search returns nothing, but the corpus says it is indexed"

Check `/healthz` or the Settings → **Check connectivity** button.

If embeddings are unavailable, semantic and hybrid search degrade to **keyword only** and
report it in the response as `degraded: true` with a reason. Returning degraded results
without indicating so would be harder to diagnose. Keyword search continues to work.

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

- **No token**: `Provide a token: Authorization: Bearer dex_…`.
- **Revoked or expired**: revocation takes effect immediately; there is no cache to wait
  out.
- **Lost the bootstrap token**: it is printed once, on first run only. Set
  `DEXICON_BOOTSTRAP_TOKEN` in `.env` to a value of your choosing and restart. It is
  adopted with full scopes. Without that escape hatch the only recovery is deleting the
  catalogue, which deletes every corpus with it.

Note the **tenant header**: a request whose `X-Dexicon-Tenant` names a tenant the token
does not own is refused with `Tenant mismatch`, not 401. The message names both.

---

## "The first run sits doing nothing for ten minutes"

Ollama is downloading the embedding model, several hundred megabytes. The healthcheck
waits for the **model** to be present rather than the daemon alone; otherwise Dexicon
begins indexing against a model that is still downloading and spends its
first minutes in embedding backoff, which reads as a bug.

```bash
docker compose logs -f dexicon-ollama
```

---

## "A migration warning on first start"

```
The migration operation 'PRAGMA foreign_keys = 0;' cannot be executed in a transaction.
```

Expected, and not suppressed. SQLite cannot drop a column in place, so EF rebuilds the
table and has to disable foreign keys outside the transaction. It matters only if the
process is killed *during* a schema migration; see
[09-deployment.md](09-deployment.md#the-migration-warning-on-first-run) for what to do then.

---

## "Indexing is slow"

On CPU Ollama, roughly 32 chunks per 18 seconds was measured while building this. A
400-page book is thousands of chunks. This reflects hardware throughput rather than a
stall; the job reports a phase and a per-file count, which distinguishes a slow job from a
stalled one.

To make it faster: use a GPU (`docker-compose.gpu.yml`), raise
`DEXICON__EMBEDDING__MAXCONCURRENCY`, or use a smaller model. A corpus with several chunk
sets walks the tree once per set, so it costs proportionally more.

---

## "The build fails with MSB3027, file locked by Dexicon"

Only when running the app on the host. The detached dev instance holds its own DLLs, and
the test project references the host so `dotnet test` rebuilds it.

```bash
./scripts/dev.ps1 test      # stops, tests, restarts if it was running
```

---

## "I changed a setting and nothing happened"

If it is a **chunk setting**, it belongs to a chunk set rather than the corpus; changing
it queues a re-chunk of that set only.

If it is an **embedding model**, it cannot be edited at all. A different model is a
different vector space, so you add a set on the new model, let it backfill while the live
one keeps serving, and promote it when it is complete.

If it is a `DEXICON__*` **environment variable**, restart the container: configuration is
bound at startup. A setting that has no effect is a defect: a test fails the build for
configuration that nothing reads, and has caught two such cases.

---

## Getting more detail

```bash
docker compose logs -f dexicon
DEXICON__LOG__LEVEL=Debug docker compose up -d dexicon
```

Logs are UTC with a `Z`, matching the API. Secrets are redacted; tokens appear as their
public id only.

If none of this covers it, open an issue with the log lines around the failure and what you
expected instead. If it is a security problem, use the process in
[SECURITY.md](../SECURITY.md) rather than the issue tracker.
