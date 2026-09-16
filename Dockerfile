# Dexicon — one image: SPA, REST API, MCP endpoint and the indexer.
#
# Build:  docker compose build
# Run:    docker compose up -d
#
# Hardening matches docker-compose.yml: non-root UID 10001, read-only rootfs with
# /tmp on tmpfs, cap_drop ALL, no-new-privileges, and /workspaces mounted READ-ONLY.
# Dexicon reads your source; it must be structurally incapable of writing to it.

# ── UI ────────────────────────────────────────────────────────────────────────
FROM --platform=$BUILDPLATFORM node:22-alpine AS ui
WORKDIR /ui

# Dependencies first, so a source-only change does not re-run npm ci.
COPY clients/web-ui/package.json clients/web-ui/package-lock.json* ./
RUN npm ci --no-audit --no-fund

COPY clients/web-ui/ ./
RUN npm run build

# ── Server ────────────────────────────────────────────────────────────────────
# `--platform=$BUILDPLATFORM`: this stage runs natively on the builder even when the
# target is arm64. It can, because `UseAppHost=false` publishes framework-dependent IL
# with no native host — the output is the same bytes for every architecture, and only the
# runtime stage below is per-platform. Emulating an SDK to produce identical output would
# cost minutes a build for nothing.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /build

# Stamped into the assembly, and from there into the OpenAPI document and the version the
# MCP server reports.
#
# Passed in rather than derived: every other build gets this from the git tag via MinVer,
# but .dockerignore excludes .git — copying history into the context would invalidate the
# layer cache on every commit — so this stage has nothing to read. The release workflow
# passes the tag. The default is deliberately not a real version: an image built by hand
# should say so rather than impersonate a release.
ARG VERSION=0.0.0-dev

# Central package management first, again for layer caching.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Dexicon.Core/Dexicon.Core.csproj src/Dexicon.Core/
COPY src/Dexicon/Dexicon.csproj src/Dexicon/
RUN dotnet restore src/Dexicon/Dexicon.csproj

COPY src/ src/
COPY .editorconfig ./
RUN dotnet publish src/Dexicon/Dexicon.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false \
    /p:Version=${VERSION} \
    /p:MinVerSkip=true

# ── Runtime ───────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime

# Non-root. The UID is fixed so a bind-mounted /data keeps working across rebuilds.
RUN addgroup -g 10001 dexicon \
 && adduser -u 10001 -G dexicon -s /bin/false -D dexicon

WORKDIR /app
COPY --from=build /app/publish ./
COPY --from=ui /ui/dist ./wwwroot

# /data is the only writable mount: catalogue and uploaded blobs.
# /workspaces is the read-only bind mount of the source to index.
RUN mkdir -p /data /workspaces && chown -R dexicon:dexicon /data /app

USER dexicon:dexicon

ENV ASPNETCORE_URLS=http://0.0.0.0:8477 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    DOTNET_gcServer=1 \
    DEXICON__STORAGE__DATAPATH=/data \
    DEXICON__INDEXING__WORKSPACEROOT=/workspaces

EXPOSE 8477

# Healthcheck lives in docker-compose.yml so it can reference the configured port,
# and because busybox wget is the tool available here — not curl, which is absent.

ENTRYPOINT ["dotnet", "Dexicon.dll"]
