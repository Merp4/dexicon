# 09 — Deployment

## Getting started

```bash
git clone https://github.com/Merp4/dexicon && cd dexicon
cp .env.example .env          # edit WORKSPACE_ROOT to point at your code
docker compose up -d
docker compose logs dexicon | grep "admin password"
```

Open http://localhost:8477, sign in with that password, add a corpus pointing at a folder
under `/workspaces`, and wait for the first index. Then issue a key under **Access**,
ticking the corpora it may reach. The dialog hands you the command:

```bash
claude mcp add --transport http dexicon http://localhost:8477/mcp \
  --header "Authorization: Bearer dex_..."
```

Nothing else. If a first run needs more steps than that, the first run is the bug.

## Compose

`docker-compose.yml` defines the complete deployment. Abridged below: the file also passes
every setting listed under [Configuration](#configuration), and comments most lines.

```yaml
name: dexicon          # sets the network (dexicon_default), volume, and container prefixes

# Defined once and referenced by the server and the client, so they cannot drift apart.
# Never empty: see "The Qdrant API key must never be blank" below.
x-qdrant-api-key: &qdrant-api-key ${QDRANT_API_KEY:-dexicon-local-dev-key}

services:
  dexicon:
    image: ghcr.io/${DEXICON_OWNER:-merp4}/dexicon:${DEXICON_TAG:-latest}
    build: { context: ., dockerfile: Dockerfile }
    restart: unless-stopped
    ports:
      # The ONLY published port in the stack, and loopback-bound by default.
      - "${DEXICON_BIND:-127.0.0.1}:${DEXICON_PORT:-8477}:8477"
    environment:
      # Service names are namespaced so these resolve unambiguously even if this
      # stack is ever attached to a shared network. See "Routing" below.
      DEXICON__QDRANT__ENDPOINT: http://dexicon-qdrant:6334
      DEXICON__QDRANT__APIKEY: *qdrant-api-key
      DEXICON__OLLAMA__ENDPOINT: http://dexicon-ollama:11434
      DEXICON__EMBEDDING__MODEL: ${DEXICON_EMBEDDING_MODEL:-embeddinggemma}
      # ... embedding, indexing, upload and log settings: see "Configuration"
      DEXICON__ADMIN__PASSWORD: ${DEXICON_ADMIN_PASSWORD:-}
      DEXICON__BOOTSTRAP__TOKEN: ${DEXICON_BOOTSTRAP_TOKEN:-}
    volumes:
      - dexicon_data:/data
      - ${WORKSPACE_ROOT:-./workspaces}:/workspaces:ro
    depends_on:
      dexicon-qdrant: { condition: service_healthy }
      dexicon-ollama: { condition: service_healthy }
    healthcheck:
      # busybox wget, present in the alpine runtime image. Deliberately not curl
      # (not installed) and not a custom /app/healthcheck binary (nothing builds one).
      test: ["CMD-SHELL", "wget -qO- http://127.0.0.1:8477/healthz/live || exit 1"]
      interval: 15s
      timeout: 3s
      retries: 5
      start_period: 20s
    read_only: true
    tmpfs: [/tmp]
    security_opt: ["no-new-privileges:true"]
    cap_drop: [ALL]

  dexicon-qdrant:
    image: qdrant/qdrant:${QDRANT_TAG:-v1.16.3}     # pinned, not :latest
    restart: unless-stopped
    volumes:
      - qdrant_data:/qdrant/storage
    environment:
      QDRANT__SERVICE__API_KEY: *qdrant-api-key
      QDRANT__SERVICE__GRPC_PORT: "6334"
    healthcheck:
      test: ["CMD-SHELL", "bash -c ':> /dev/tcp/127.0.0.1/6333' || exit 1"]
      interval: 10s
      timeout: 3s
      retries: 10
    # no ports: — container-network only. Never publish; see "Exposure".

  dexicon-ollama:
    image: ollama/ollama:${OLLAMA_TAG:-0.32.14}
    restart: unless-stopped
    volumes:
      - ollama_data:/root/.ollama
      - ./scripts/provision-models.sh:/provision.sh:ro
    entrypoint: ["/bin/sh", "/provision.sh"]   # pulls the embedding model, then serves
    environment:
      OLLAMA_HOST: 0.0.0.0
      DEXICON_EMBEDDING_MODEL: ${DEXICON_EMBEDDING_MODEL:-embeddinggemma}
      OLLAMA_NUM_PARALLEL: ${OLLAMA_NUM_PARALLEL:-4}   # see "Configuration"
    healthcheck:
      # Readiness means the MODEL is present, not merely that the daemon answers.
      # Without this, Dexicon starts indexing against a model still downloading and
      # spends its first minutes in embedding backoff, which reads as a bug.
      test: ["CMD-SHELL", "ollama list | grep -q \"${DEXICON_EMBEDDING_MODEL:-embeddinggemma}\" || exit 1"]
      interval: 15s
      timeout: 5s
      retries: 40          # first run pulls the model; allow ~10 minutes
      start_period: 30s
    # no ports: — container-network only.

volumes:          # left unnamed: Compose prefixes them with the project name, so two
  dexicon_data: {}   # checkouts never fight over the same volume
  qdrant_data:  {}
  ollama_data:  {}
```

### Overlays

| File | Purpose |
|---|---|
| `docker-compose.gpu.yml` | Adds `deploy.resources.reservations.devices` for NVIDIA to `dexicon-ollama`. Opt-in: `docker compose -f docker-compose.yml -f docker-compose.gpu.yml up -d`. |
| `docker-compose.debug.yml` | Publishes Qdrant on **16333** and Ollama on **21434** — deliberately *not* their standard ports, so it cannot collide with a stock Qdrant or Ollama already running on the host. Loopback-bound. Never part of the default up. |
| `docker-compose.external.yml` | Drops `dexicon-ollama` and points at an instance you already run. `DEXICON_QDRANT_ENDPOINT` redirects the vector store too, though the in-stack Qdrant is left running by default. |

The base file stays boring and complete. Overlays carry everything that is a choice.

## Exposure

**`dexicon` is the only service that publishes a port, and it binds to `127.0.0.1` by
default.** Qdrant and Ollama have no `ports:` mapping at all; they are reachable only on
the project's own bridge network.

This is not fastidiousness. Qdrant's stock configuration has **no authentication**, so a
published 6333 is an open read/write door to every corpus, and no amount of
application-layer key scoping ([07](07-auth.md)) survives it. `QDRANT_API_KEY` is
supported and recommended for anything beyond one trusted machine.

It also avoids a collision that will otherwise happen on any developer machine: 6333, 6334
and 11434 are the standard ports for Qdrant and Ollama, and anyone likely to want Dexicon
is likely to already be running one of them. Two stacks both claiming 11434 fail at
`docker compose up` with a port-in-use error; worse, if the other stack started first,
the port belongs to it without any indication. Publishing nothing avoids the question. The
debug overlay uses non-standard host ports for the same reason.

The default host port is **8477** rather than 8080, on the same principle: 8080 is the
most contended port on a development machine, and the resulting failure is hard to
diagnose.

### The Qdrant API key must never be blank

Found in the M0 spike, and it would have broken every first run.

**Qdrant enables authentication on the *presence* of `QDRANT__SERVICE__API_KEY`, not on it
having a value.** Setting it to an empty string, the natural result of
`${QDRANT_API_KEY:-}` with a blank `.env`, turns auth **on** with a key nothing can
present, and every request gets a 401, including Qdrant's own dashboard:

```
$ curl http://127.0.0.1:16333/collections
401 Unauthorized — "Must provide an API key or an Authorization bearer token"
```

`docker-compose.yml` therefore defines the key once as a YAML anchor with a non-empty
development default, referenced by both the server and Dexicon's client so the two cannot
drift:

```yaml
x-qdrant-api-key: &qdrant-api-key ${QDRANT_API_KEY:-dexicon-local-dev-key}
```

`.env.example` ships the `QDRANT_API_KEY` line **commented out** rather than blank, for the
same reason. Override it with a real key (`openssl rand -base64 32`) for anything beyond
one trusted machine. The default is not a secret and is not treated as one; it is defence
in depth behind a port that is never published.

## Routing: ensuring the endpoint reaches the intended container

The dependency services are named `dexicon-qdrant` and `dexicon-ollama`, not `qdrant` and
`ollama`, and the endpoints use those names.

Within a single Compose project this is belt-and-braces: service DNS is scoped to the
project's own network, so bare `qdrant` would resolve correctly. It stops being
belt-and-braces once the stack touches a shared network, which happens when joining an
`external:` network to reuse a GPU Ollama. On a
shared network a generic service name can resolve to somebody else's container, and the
failure is quiet: embeddings succeed, come from a different model, and land in a collection
whose dimensions no longer mean what the catalogue says they mean.

Three properties keep that from mattering:

1. **Namespaced service names.** `dexicon-ollama` is unambiguous on any network.
2. **Namespaced Qdrant collections.** Everything Dexicon creates is prefixed `dexicon__`
   ([03](03-data-model.md)), so even pointing at a Qdrant shared with another product
   cannot collide with another application's collections.
3. **Startup assertion.** On boot Dexicon calls both endpoints and logs what answered:
   Qdrant version and collection count, Ollama version and resident models. If the
   embedding model reported by Ollama is not the one configured, it refuses to start rather
   than indexing against the wrong model. An endpoint that resolves is not the same as an
   endpoint that resolves to the right thing.

### Reusing an Ollama you already run

`docker-compose.external.yml` drops `dexicon-ollama` and points at an existing instance.
This is useful when models are already pulled and a GPU is configured, since the in-stack
Ollama would otherwise re-download them into its own volume.

`--profile in-stack` brings the local Ollama back without editing anything, which is the
quickest way to tell a configuration problem from a reachability one.

```yaml
services:
  dexicon:
    environment:
      DEXICON__OLLAMA__ENDPOINT: ${DEXICON_OLLAMA_ENDPOINT:-http://host.docker.internal:11434}
    extra_hosts:
      - "host.docker.internal:host-gateway"   # required on Linux; a no-op elsewhere
    depends_on: !reset []                      # nothing local to wait for
```

Pointing at another Compose stack's Ollama (`http://other-stack-ollama:11434`)
additionally requires that stack's network to be declared `external: true` here. Use the
container's real name, never a bare service alias, for the reason above.

## Indexing a tree outside the workspace root

`WORKSPACE_ROOT` is the only thing the indexer can see, and it is one directory. To index
a second tree, such as a folder of documents or another checkout, mount it
*underneath* the root:

```yaml
# docker-compose.override.yml — NOT committed; see .gitignore
services:
  dexicon:
    volumes:
      - /path/to/books:/workspaces/books:ro
```

Compose applies `docker-compose.override.yml` automatically, with no `-f` flags. That is
what makes it convenient locally and wrong to commit: it names paths that exist on one
machine. The committed overlays, `docker-compose.gpu.yml` and
`docker-compose.debug.yml`, are opt-in by name for the same reason.

**The automatic override stops being automatic the moment you pass `-f`.** Compose loads
it only when you name no files at all, so combining it with the GPU overlay means naming
all three:

```bash
docker compose -f docker-compose.yml -f docker-compose.gpu.yml -f docker-compose.override.yml up -d
```

If this is wrong the container starts normally with the mount absent, because the
empty mountpoint directory is still there, so the path exists and lists as empty rather
than erroring.

Two things that are not obvious:

- **The mountpoint must already exist on the host.** `/workspaces` is bind-mounted
  read-only, so Docker cannot create `/workspaces/books` inside it and the container
  fails to start with `read-only file system`. Create the empty directory under
  `WORKSPACE_ROOT` first.
- **Order is by depth, not by declaration.** Docker mounts `/workspaces` before
  `/workspaces/books`, so the nested mount lands on top of the parent rather than being
  hidden by it.

Read-only is not decoration. Dexicon reads your files and is structurally incapable of
writing to them; keep the `:ro` when you add a mount.

## Configuration

Environment variables, double-underscore hierarchy (standard ASP.NET Core binding).
Everything has a working default except `WORKSPACE_ROOT`.

| Variable | Default | Meaning |
|---|---|---|
| `DEXICON_PORT` | `8477` | Host port. Deliberately not 8080. |
| `DEXICON_BIND` | `127.0.0.1` | Bind address. Set to `0.0.0.0` only to reach it from another machine. |
| `WORKSPACE_ROOT` | `./workspaces` | Host directory bind-mounted read-only at `/workspaces`. |
| `DEXICON__QDRANT__ENDPOINT` | `http://dexicon-qdrant:6334` | gRPC endpoint. Namespaced service name — see "Routing". |
| `QDRANT_API_KEY` (binds `DEXICON__QDRANT__APIKEY`) | `dexicon-local-dev-key` | Used by both Qdrant and Dexicon. Never blank: see "The Qdrant API key must never be blank". The default is published in this repository, so set your own for anything not on a single trusted machine. |
| `DEXICON__OLLAMA__ENDPOINT` | `http://dexicon-ollama:11434` | Namespaced service name — see "Routing". |
| `DEXICON__EMBEDDING__MODEL` | `embeddinggemma` | Default for new corpora. Pinned per chunk set at creation, so changing it migrates nothing. |
| `DEXICON__EMBEDDING__MAXCONCURRENCY` | `4` | Parallel embedding requests Dexicon issues, counted PER PROVIDER across every indexing job. Keep it equal to `OLLAMA_NUM_PARALLEL`: sending more than Ollama admits only queues the difference. Raising `MAXCONCURRENTCORPORA` does not multiply it, and a corpus indexing alone still gets all of it. |
| `OLLAMA_NUM_PARALLEL` | `4` | How many requests Ollama admits at once. Reaches the **in-stack Ollama container only** — with `docker-compose.external.yml` that service is not started, so set it on your own Ollama instead. Measured against a live index, 50% embedder busy unset against 79% at 4, about 63% more embed calls in the same window. It is not parallel decoding: Ollama pins an embedding model to one sequence either way, so it costs no VRAM and does not change the context each request gets. |
| `DEXICON__INDEXING__CHUNKSIZE` | `256` | Chunk size in tokens for a NEW corpus. An existing chunk set stores its own, so this migrates nothing and costs no reindex. Measured; see D-31's amendment. |
| `DEXICON__INDEXING__CHUNKOVERLAP` | `32` | Overlap in tokens, an eighth of the size. Sweeping it found nothing to gain from more. |
| `DEXICON__INDEXING__MAXFILEBYTES` | `262144` | Default per-source size cap, for text and code. |
| `DEXICON__INDEXING__DOCUMENTMAXBYTES` | `536870912` | Size cap for extracted formats (PDF, EPUB, DOCX, PPTX). 512 MB. A memory decision — extraction holds the document's text. |
| `DEXICON__INDEXING__EXTRACTIONTIMEOUTSECONDS` | `300` | How long a file may go on reading itself during extraction before it is abandoned and recorded as failed. `0` disables it. Bounds a file that keeps reading, not wall-clock time in extraction. |
| `DEXICON__INDEXING__REFRESHMINUTES` | `0` | Automatic refresh interval in minutes. `0` = manual only. |
| `DEXICON__INDEXING__MAXCONCURRENTCORPORA` | `4` | How many corpora may be indexed at once. Two jobs on ONE corpus are still excluded, by the lease. Costs a catalogue connection and the memory of the documents in flight per worker; the embedding endpoint and the parser are bounded separately, so raising this does not multiply either. |
| `DEXICON__INDEXING__MAXCONCURRENTEXTRACTIONS` | `4` | How many files may be parsed at once, across every job, and how many a single job reads ahead. Parsing is CPU-bound, so the limit is the machine's rather than a corpus's. |
| `DEXICON__INDEXING__MAXCONCURRENTSWEEPS` | `2` | How many discovery passes may run at once. A sweep has its own slots so it never waits behind indexing, which is what stops a corpus added mid-index reading as empty until that index finishes. More than a couple contend for the catalogue's single writer to finish a two-second walk marginally sooner. |
| `DEXICON__INDEXING__MAXCONCURRENTREBUILDS` | `1` | How many FULL or rebuild passes may run at once, as against incremental ones. Below `MAXCONCURRENTCORPORA` on purpose: a rebuild re-embeds every file it walks, so several together saturate the embedding endpoint and slow each other without finishing any sooner. Incremental passes keep their own slots while one runs. |
| `DEXICON__UPLOAD__MAXFILEBYTES` | `209715200` | 200 MB. |
| `DEXICON__ADMIN__PASSWORD` | _(generated)_ | The admin password. Blank generates one on first run and prints it to the log once. Set, it is applied on every start, which is the way back in after a forgotten one. |
| `DEXICON__BOOTSTRAP__TOKEN` | *(empty)* | Blank generates one and logs it once. |
| `DEXICON__LOG__LEVEL` | `Information` | |

The table lists what `docker-compose.yml` passes. Any other option binds the same way once
added to the service's `environment`, and `DexiconOptions.cs` has them all. One of them is
`DEXICON__STORAGE__BUSYTIMEOUTSECONDS` (default `30`): how long a catalogue write waits on
a lock held by another connection before it fails, matching the SQLite provider's own
command timeout so neither gives up first.

No secret has a default value except `QDRANT_API_KEY`, whose default exists so that a blank
`.env` cannot turn Qdrant's authentication on with an unusable key. No secret is ever read
from `appsettings.json`. See [10](10-security-secrets.md).

## Image

Multi-stage, two builders:

```dockerfile
FROM node:22-alpine AS ui
# npm ci && npm run build -> /ui/dist

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
# dotnet publish -c Release

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
RUN addgroup -g 10001 dexicon && adduser -u 10001 -G dexicon -s /bin/false -D dexicon
COPY --from=build /app/publish .
COPY --from=ui    /ui/dist ./wwwroot
USER dexicon:dexicon
EXPOSE 8477
ENTRYPOINT ["dotnet", "Dexicon.dll"]
```

Hardening, matching the compose file:

- Non-root UID 10001.
- `read_only: true` rootfs; `/tmp` is tmpfs; `/data` is the only writable mount.
- `cap_drop: ALL`, `no-new-privileges`.
- `/workspaces` mounted **read-only**. Dexicon reads your source; it must be structurally
  incapable of writing to it.
- Base images pinned by **digest** as well as tag. A tag moves, so two builds of one
  commit could produce different images and neither would be wrong. Dependabot raises a
  pull request when a digest changes, which is how a base image update should arrive.
- Every published image carries an **SBOM** and build provenance as registry attestations:

  ```bash
  docker buildx imagetools inspect ghcr.io/<owner>/dexicon:0.6.1 --format '{{ json .SBOM }}'
  ```
- Published multi-arch (`linux/amd64`, `linux/arm64`) so it runs on Apple silicon. Both
  builder stages run on the build platform and emit architecture-independent IL
  (`UseAppHost=false`), so the arm64 image costs one small runtime layer rather than a
  full emulated SDK build.

## Image tags

`.github/workflows/release.yml` publishes to `ghcr.io/<owner>/dexicon`.

| Tag | Means | Use it for |
|---|---|---|
| `0.6.1` | That release, forever | Deployments |
| `0.6` | Newest patch of that minor | Deployments that accept patches |
| `latest` | Newest release | Trying it out |
| `edge` | Tip of `main` | Following development |
| `sha-abc1234` | One commit | Reproducing a report |

No bare major tag. Before 1.0 the minor IS the breaking change, so a `0` tag would
promise a compatibility that does not exist.

`latest` and `edge` move. A deployment pins `DEXICON_TAG` to a version, or to a digest
if it should not change even for a re-push:

```bash
DEXICON_TAG=0.6.1 docker compose up -d
```

**Cutting a release, in order.**

1. **Write the version's `CHANGELOG.md` section.** The tag push reads it for the GitHub
   Release body, and a tag with no section fails the release before anything reaches the
   registry, which is the last point at which stopping costs nothing.
2. **Build, and commit the regenerated `clients/web-ui/Dexicon.json` and
   `clients/web-ui/Dexicon_integration.json`.** Both carry the release's `major.minor` and
   CI checks the committed copies against the code. Build last: a later `dotnet build` or
   `dotnet test` without the override writes the previous version back. MinVer takes
   the version from the tag, so tagging first produces a tag whose own release build fails
   on a document still naming the previous version. This applies once per minor version;
   every `0.6.x` after the first produces the same `0.6` and nothing changes.

   On a minor bump the build has to be told the version. MinVer auto-increments the PATCH,
   so a plain build after `v0.5.1` reports `0.5.2-alpha…` and writes the `0.5` being moved
   away from:

   ```bash
   dotnet build -p:MinVerVersionOverride=0.6.0
   ```

3. **Tag and push.** That publishes the image and creates the GitHub Release.

Between step 2 and step 3 the committed document names the new minor while an untagged
build of the same commit produces the old one. CI compares the two with `info.version`
set aside for exactly that reason, so the release commit passes its own checks; the
comparison still covers the paths and schemas the generated client is typed against. It
did not always: comparing the stamped version deadlocked `0.3.0`, because the check could
not pass until the tag existed and the tag could not exist until a merge that a required
status check refuses.

Releases on GitHub start at `0.2.3`. `0.1.0` through `0.2.2` predate the step that creates
them and exist as tags, images and `CHANGELOG.md` entries only.

The version in the tag is the version in the image, because the tag is where the version
comes from at all (D-26). MinVer derives it from the nearest `v*` tag; nothing is written
down, so nothing can be stale. The image build cannot do this, because `.dockerignore`
excludes `.git`, so the workflow passes the tag in and then checks what the built image
contains, refusing to publish a mismatch. An image built by
hand with no argument reports `0.0.0-dev` rather than impersonating a release. Each image carries a provenance attestation
recording the commit and the workflow that produced it:

```bash
gh attestation verify oci://ghcr.io/<owner>/dexicon:0.6.1 --owner <owner>
```

## Health

| Endpoint | Meaning |
|---|---|
| `/healthz/live` | The process is up. Used by the container healthcheck. |
| `/healthz/ready` | Qdrant reachable **and** the catalog is migrated. Ollama being down does **not** make the service unready — keyword search still works, and taking the whole service down because embeddings are unavailable would be a worse outage than the one being reported. |
| `/healthz` | Full detail: versions, collection count, embedding model residency, last embedding latency, active job. Requires a token. |

## Backup and recovery

- **`dexicon_data`** — the catalogue and uploaded blobs. This is the one that matters: it
  is the only thing that is not reconstructible. `docker compose stop dexicon`, copy the
  volume, restart. SQLite in WAL mode; the stop is what makes the copy consistent.
- **`qdrant_data`** — reconstructible by reindexing. Back it up to save time, not data.
- **`ollama_data`** — model weights. Re-downloadable.

`scripts/backup.sh` does the above:

```bash
./scripts/backup.sh backup   [dir]   # stops the app, archives the volumes, restarts
./scripts/backup.sh restore  <dir>
./scripts/backup.sh verify   [dir]   # backup, DESTROY, restore, check it comes back
```

The app is stopped for the duration. SQLite in WAL mode will happily hand you a copy
mid-write that restores into a database missing its last transactions, and a backup you
cannot trust is worse than none, because you stop taking the other kind.

`verify` is the rehearsal, and it is destructive by design: a backup procedure that has
never been restored is untested. It has been run, with volumes destroyed, restored from
the tarballs, the catalogue intact and **search returning results** afterwards. A liveness
check alone would not have proved this, since a restored catalogue with no vectors reports
healthy and answers every query with nothing.

### The migration warning on first run

A first start logs a warning naming a migration and `PRAGMA foreign_keys = 0`:

```
The migration operation 'PRAGMA foreign_keys = 0;' cannot be executed in a transaction.
```

It is expected, and it is not suppressed. SQLite cannot drop a column in place, so EF
rebuilds the table, and the rebuild has to disable foreign keys outside the transaction.
EF's own advice, to put that operation in its own migration, does not apply, because the
PRAGMA is generated by the provider rather than written in the migration.

The practical consequence: if the process is killed *during* a schema migration, the
catalogue can be left part-migrated and requires manual repair. Two things limit the cost:

- on a **fresh install** there is nothing to lose. Remove the `dexicon_data` volume and
  start again
- on an **existing install**, the catalogue is the one volume worth backing up, and the
  copy above takes seconds

Suppressing the warning would have been a line of configuration. It describes a real if
unlikely failure, and a log that hides those is the thing this project keeps declining to
build.

## Sizing

| Deployment | RAM | Disk | Notes |
|---|---|---|---|
| One repo, CPU-only | 4 GB | 5 GB | `nomic-embed-text` on CPU: roughly 40–80 chunks/s. |
| Several repos + docs, CPU | 8 GB | 20 GB | |
| Large monorepo, GPU | 8 GB + 4 GB VRAM | 30 GB | GPU embedding is 5–10× faster; the win is on first index, not on search. |

First index of a 50 000-file repository on CPU is tens of minutes. The UI says so, with a
running estimate, rather than appearing hung.
