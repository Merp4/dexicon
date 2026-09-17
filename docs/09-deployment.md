# 09 — Deployment

## Getting started

```bash
git clone https://github.com/Merp4/dexicon && cd dexicon
cp .env.example .env          # edit WORKSPACE_ROOT to point at your code
docker compose up -d
docker compose logs dexicon | grep "bootstrap token"
```

Open http://localhost:8477, paste the bootstrap token, add a corpus pointing at a folder
under `/workspaces`, wait for the first index, then wire up your agent:

```bash
claude mcp add --transport http dexicon http://localhost:8477/mcp \
  --header "Authorization: Bearer dex_..."
```

Nothing else. If a first run needs more steps than that, the first run is the bug.

## Compose

`docker-compose.yml` — the whole deployment.

```yaml
name: dexicon          # sets the network (dexicon_default), volume, and container prefixes

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
      DEXICON__QDRANT__ENDPOINT:   http://dexicon-qdrant:6334
      DEXICON__OLLAMA__ENDPOINT:   http://dexicon-ollama:11434
      DEXICON__EMBEDDING__MODEL:   ${DEXICON_EMBEDDING_MODEL:-embeddinggemma}
      DEXICON__BOOTSTRAP__TENANT:  ${DEXICON_BOOTSTRAP_TENANT:-default}
      DEXICON__BOOTSTRAP__TOKEN:   ${DEXICON_BOOTSTRAP_TOKEN:-}   # blank = generate and log once
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
    tmpfs: [ /tmp ]
    security_opt: [ "no-new-privileges:true" ]
    cap_drop: [ ALL ]

  dexicon-qdrant:
    image: qdrant/qdrant:${QDRANT_TAG:-v1.16.1}     # pinned, not :latest
    restart: unless-stopped
    volumes:
      - qdrant_data:/qdrant/storage
    environment:
      QDRANT__SERVICE__API_KEY: ${QDRANT_API_KEY:-}
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
      DEXICON_EMBEDDING_MODEL: ${DEXICON_EMBEDDING_MODEL:-nomic-embed-text}
    healthcheck:
      # Readiness means the MODEL is present, not merely that the daemon answers.
      # Without this, Dexicon starts indexing against a model still downloading and
      # spends its first minutes in embedding backoff, which reads as a bug.
      test: ["CMD-SHELL", "ollama list | grep -q \"$${DEXICON_EMBEDDING_MODEL%%:*}\" || exit 1"]
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
| `docker-compose.external.yml` | Drops both dependency services and points the endpoints at instances you already run. |

The base file stays boring and complete. Overlays carry everything that is a choice.

## Exposure

**`dexicon` is the only service that publishes a port, and it binds to `127.0.0.1` by
default.** Qdrant and Ollama have no `ports:` mapping at all — they are reachable only on
the project's own bridge network.

This is not fastidiousness. Qdrant's stock configuration has **no authentication**, so a
published 6333 is an open read/write door to every tenant's content, and no amount of
application-layer tenancy ([07](07-tenancy-auth.md)) survives it. `QDRANT_API_KEY` is
supported and recommended for anything beyond one trusted machine.

It also avoids a collision that will otherwise happen on any developer machine: 6333, 6334
and 11434 are the standard ports for Qdrant and Ollama, and anyone likely to want Dexicon
is likely to already be running one of them. Two stacks both claiming 11434 fail at
`docker compose up` with a port-in-use error; worse, if the other stack started first, the
port silently belongs to it. Publishing nothing makes the question moot. The debug overlay
uses non-standard host ports for the same reason.

The default host port is **8477** rather than 8080, on the same principle — 8080 is the
most contended port on any development machine, and the failure mode is a confusing one.

### The Qdrant API key must never be blank

Found in the M0 spike, and it would have broken every first run.

**Qdrant enables authentication on the *presence* of `QDRANT__SERVICE__API_KEY`, not on it
having a value.** Setting it to an empty string — the natural result of
`${QDRANT_API_KEY:-}` with a blank `.env` — turns auth **on** with a key nothing can
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
same reason. Override it with a real key — `openssl rand -base64 32` — for anything beyond
one trusted machine. The default is not a secret and is not treated as one; it is defence
in depth behind a port that is never published.

## Routing — making sure the endpoint hits *our* container

The dependency services are named `dexicon-qdrant` and `dexicon-ollama`, not `qdrant` and
`ollama`, and the endpoints use those names.

Within a single Compose project this is belt-and-braces: service DNS is scoped to the
project's own network, so bare `qdrant` would resolve correctly. It stops being
belt-and-braces the moment the stack touches a shared network — joining an `external:`
network to reuse a GPU Ollama is the obvious reason, and it is a thing people do. On a
shared network a generic service name can resolve to somebody else's container, and the
failure is quiet: embeddings succeed, come from a different model, and land in a collection
whose dimensions no longer mean what the catalogue says they mean.

Three properties keep that from mattering:

1. **Namespaced service names.** `dexicon-ollama` is unambiguous on any network.
2. **Namespaced Qdrant collections.** Everything Dexicon creates is prefixed `dexicon__`
   ([03](03-data-model.md)), so even pointing at a Qdrant shared with another product
   cannot collide — McpToolbox's `mcp_workspace_*` collections and Dexicon's sit side by
   side untouched.
3. **Startup assertion.** On boot Dexicon calls both endpoints and logs what answered:
   Qdrant version and collection count, Ollama version and resident models. If the
   embedding model reported by Ollama is not the one configured, it refuses to start rather
   than indexing against the wrong model. An endpoint that resolves is not the same as an
   endpoint that resolves to the right thing.

### Reusing an Ollama you already run

`docker-compose.external.yml` drops `dexicon-ollama` and points at an existing instance —
worth it when you already have models pulled and a GPU configured, since the in-stack
Ollama otherwise re-downloads them into its own volume.

```yaml
services:
  dexicon:
    environment:
      DEXICON__OLLAMA__ENDPOINT: ${DEXICON_OLLAMA_ENDPOINT:-http://host.docker.internal:11434}
    extra_hosts:
      - "host.docker.internal:host-gateway"   # required on Linux; a no-op elsewhere
    depends_on: !reset []                      # nothing local to wait for
```

Pointing at another Compose stack's Ollama instead — `http://mcptoolbox-infra-ollama:11434`
— additionally needs that stack's network declared `external: true` here. Use the
container's real name, never a bare service alias, for the reason above.

## Indexing a tree outside the workspace root

`WORKSPACE_ROOT` is the only thing the indexer can see, and it is one directory. To index
a second tree — a folder of books, another checkout, a documentation site — mount it
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
machine. The overlays that ARE committed — `docker-compose.gpu.yml`,
`docker-compose.debug.yml` — are opt-in by name for the same reason.

**The automatic override stops being automatic the moment you pass `-f`.** Compose loads
it only when you name no files at all, so combining it with the GPU overlay means naming
all three:

```bash
docker compose -f docker-compose.yml -f docker-compose.gpu.yml -f docker-compose.override.yml up -d
```

Get this wrong and the container starts happily with the mount silently missing — the
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
| `QDRANT_API_KEY` | *(empty)* | Set for anything not on a single trusted machine. |
| `DEXICON__OLLAMA__ENDPOINT` | `http://dexicon-ollama:11434` | Namespaced service name — see "Routing". |
| `DEXICON__EMBEDDING__MODEL` | `embeddinggemma` | Default for new corpora. Pinned per chunk set at creation, so changing it migrates nothing. |
| `DEXICON__EMBEDDING__MAXCONCURRENCY` | `4` | Parallel embedding requests. |
| `DEXICON__INDEXING__MAXFILEBYTES` | `262144` | Default per-source size cap, for text and code. |
| `DEXICON__INDEXING__DOCUMENTMAXBYTES` | `536870912` | Size cap for extracted formats (PDF, EPUB, DOCX, PPTX). 512 MB. A memory decision — extraction holds the document's text. |
| `DEXICON__INDEXING__REFRESHMINUTES` | `0` | Automatic refresh interval in minutes. `0` = manual only. |
| `DEXICON__UPLOAD__MAXFILEBYTES` | `209715200` | 200 MB. |
| `DEXICON__BOOTSTRAP__TENANT` | `default` | Created on first run. |
| `DEXICON__BOOTSTRAP__TOKEN` | *(empty)* | Blank generates one and logs it once. |
| `DEXICON__LOG__LEVEL` | `Information` | |

No secret has a default value, and no secret is ever read from `appsettings.json`.
See [10](10-security-secrets.md).

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
  docker buildx imagetools inspect ghcr.io/<owner>/dexicon:0.2.0 --format '{{ json .SBOM }}'
  ```
- Published multi-arch (`linux/amd64`, `linux/arm64`) so it runs on Apple silicon. Both
  builder stages run on the build platform and emit architecture-independent IL
  (`UseAppHost=false`), so the arm64 image costs one small runtime layer rather than a
  full emulated SDK build.

## Image tags

`.github/workflows/release.yml` publishes to `ghcr.io/<owner>/dexicon`.

| Tag | Means | Use it for |
|---|---|---|
| `0.2.0` | That release, forever | Deployments |
| `0.1` | Newest patch of that minor | Deployments that accept patches |
| `latest` | Newest release | Trying it out |
| `edge` | Tip of `main` | Following development |
| `sha-abc1234` | One commit | Reproducing a report |

No bare major tag. Before 1.0 the minor IS the breaking change, so a `0` tag would
promise a compatibility that does not exist.

`latest` and `edge` move. A deployment pins `DEXICON_TAG` to a version, or to a digest
if it should not change even for a re-push:

```bash
DEXICON_TAG=0.2.0 docker compose up -d
```

**Cutting a release, in order.** The generated OpenAPI document carries the release's
`major.minor`, and CI checks that the committed copy matches the code. MinVer takes the
version from the tag, so tagging first produces a tag whose own release build fails on a
document that still says the previous version. Build, commit the regenerated
`clients/web-ui/Dexicon.json`, then tag. It bites once per minor version — every `0.2.x`
after the first produces the same `0.2` and nothing moves.

The version in the tag is the version in the image, because the tag is where the version
comes from at all (D-26). MinVer derives it from the nearest `v*` tag; nothing is written
down, so nothing can be stale. The image build is the one place that cannot do this —
`.dockerignore` excludes `.git` — so the workflow passes the tag in and then checks what
the built image actually contains, and refuses to publish a mismatch. An image built by
hand with no argument reports `0.0.0-dev` rather than impersonating a release. Each image carries a provenance attestation
recording the commit and the workflow that produced it:

```bash
gh attestation verify oci://ghcr.io/<owner>/dexicon:0.2.0 --owner <owner>
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

`verify` is the rehearsal, and it is destructive on purpose — a backup procedure that has
never been restored is a hypothesis. It has been run: volumes destroyed, restored from the
tarballs, catalogue intact and **search returning results** afterwards. Liveness alone
would not have proved it; a restored catalogue with no vectors comes up perfectly healthy
and answers every query with nothing.

### The migration warning on first run

A first start logs a warning naming a migration and `PRAGMA foreign_keys = 0`:

```
The migration operation 'PRAGMA foreign_keys = 0;' cannot be executed in a transaction.
```

It is expected, and it is not suppressed. SQLite cannot drop a column in place, so EF
rebuilds the table, and the rebuild has to disable foreign keys outside the transaction.
EF's own advice — put that operation in its own migration — does not apply, because the
PRAGMA is generated by the provider rather than written in the migration.

What it actually means: if the process is killed *during* a schema migration, the
catalogue can be left part-migrated and needs manual repair. Two things make that cheap
rather than frightening:

- on a **fresh install** there is nothing to lose — remove the `dexicon_data` volume and
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
