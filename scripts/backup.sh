#!/usr/bin/env bash
#
# Back up and restore Dexicon's volumes.
#
#   ./scripts/backup.sh backup  [dir]     -> dir/dexicon-backup-<timestamp>/
#   ./scripts/backup.sh restore <dir>
#   ./scripts/backup.sh verify  [dir]     -> back up, destroy, restore, check
#
# docs/09-deployment.md has said this script exists since before it did, which is the kind
# of documentation debt that only surfaces when someone needs it at the worst moment.
#
# WHAT MATTERS, in order:
#
#   dexicon_data  the catalogue and uploaded blobs. The ONLY volume that is not
#                 reconstructible. Losing it loses every corpus, token and document.
#   qdrant_data   vectors. Reconstructible by reindexing — back it up to save hours, not
#                 to save data.
#   ollama_data   model weights. Re-downloadable; skipped unless --with-models.
#
# The app is stopped for the duration. SQLite in WAL mode will happily hand you a copy
# mid-write that restores into a database missing its last transactions, and a backup you
# cannot trust is worse than none because you stop taking the other kind.

set -euo pipefail

PROJECT="${COMPOSE_PROJECT_NAME:-dexicon}"
DATA_VOLUME="${PROJECT}_dexicon_data"
QDRANT_VOLUME="${PROJECT}_qdrant_data"
OLLAMA_VOLUME="${PROJECT}_ollama_data"

# Git Bash on Windows rewrites anything that looks like an absolute path INSIDE an
# argument before the process sees it, so a container path of /backup arrives as
# C:/Program Files/Git/backup and tar fails on a directory that was never meant to exist
# on the host. Found on the first real run of this script.
if [ -n "${MSYSTEM:-}" ]; then
  export MSYS_NO_PATHCONV=1
  export MSYS2_ARG_CONV_EXCL='*'
fi

# Docker needs a Windows path for a bind mount; `pwd` under MSYS gives /c/Users/... .
host_path() {
  if [ -n "${MSYSTEM:-}" ]; then (cd "$1" && pwd -W); else (cd "$1" && pwd); fi
}

red()   { printf '\033[31m%s\033[0m\n' "$*" >&2; }
green() { printf '\033[32m%s\033[0m\n' "$*"; }
info()  { printf '  %s\n' "$*"; }

usage() { sed -n '2,25p' "$0" | sed 's/^# \{0,1\}//'; exit 2; }

# Volume in, tarball out, via a throwaway container — the volume is not mounted on the
# host, and this works identically on Linux, macOS and Windows.
archive_volume() {
  local volume="$1" out="$2"
  docker run --rm \
    -v "${volume}:/source:ro" \
    -v "$(host_path "$(dirname "$out")"):/backup" \
    alpine:3.20 \
    tar czf "/backup/$(basename "$out")" -C /source .
}

extract_volume() {
  local volume="$1" archive="$2"
  docker run --rm \
    -v "${volume}:/target" \
    -v "$(host_path "$(dirname "$archive")"):/backup:ro" \
    alpine:3.20 \
    sh -c "rm -rf /target/* /target/..?* 2>/dev/null; tar xzf /backup/$(basename "$archive") -C /target"
}

volume_exists() { docker volume inspect "$1" >/dev/null 2>&1; }

cmd_backup() {
  local root="${1:-./backups}"
  local stamp; stamp="$(date -u +%Y%m%dT%H%M%SZ)"
  local dir="${root}/dexicon-backup-${stamp}"

  mkdir -p "$dir"

  # Read BEFORE the stop: `docker compose images` reports nothing for a stopped service,
  # so capturing it later produced a manifest with an empty image field — the exact gap
  # the manifest exists to close.
  # JSON, not a Go template: `docker compose images` rejects --format '{{...}}' with
  # "could not be parsed" on current Compose, and it failed silently into "unknown".
  #
  # `|| true` because set -e makes an assignment inherit the status of its command
  # substitution, so a compose invocation that fails would abort the backup before it
  # started — over a field that is only ever informational.
  local image
  image="$(docker compose images dexicon --format json 2>/dev/null | grep -o '"Repository":"[^"]*","Tag":"[^"]*"' | sed 's/"Repository":"//; s/","Tag":"/:/; s/"$//' | head -1)" || true

  info "Stopping dexicon (the app only — dependencies keep running)"
  docker compose stop dexicon >/dev/null 2>&1 || true

  info "Archiving ${DATA_VOLUME}"
  archive_volume "$DATA_VOLUME" "${dir}/dexicon_data.tar.gz"

  if volume_exists "$QDRANT_VOLUME"; then
    info "Archiving ${QDRANT_VOLUME}"
    archive_volume "$QDRANT_VOLUME" "${dir}/qdrant_data.tar.gz"
  fi

  if [ "${WITH_MODELS:-}" = "1" ] && volume_exists "$OLLAMA_VOLUME"; then
    info "Archiving ${OLLAMA_VOLUME} (gigabytes; re-downloadable)"
    archive_volume "$OLLAMA_VOLUME" "${dir}/ollama_data.tar.gz"
  fi

  # Recorded so a restore knows what it is looking at. A backup that does not say which
  # image wrote it is a backup you have to guess about under pressure.
  cat > "${dir}/manifest.txt" <<EOF
dexicon backup
taken_utc  = ${stamp}
project    = ${PROJECT}
image      = ${image:-unknown}
volumes    = $(ls "$dir" | grep '\.tar\.gz$' | tr '\n' ' ')
EOF

  docker compose start dexicon >/dev/null 2>&1 || true
  green "Backed up to ${dir}"
  info "$(du -sh "$dir" | cut -f1) total"
}

cmd_restore() {
  local dir="${1:?restore needs a backup directory}"
  [ -f "${dir}/dexicon_data.tar.gz" ] || { red "No dexicon_data.tar.gz in ${dir}"; exit 1; }

  info "Stopping the whole stack"
  docker compose down >/dev/null 2>&1 || true

  info "Restoring ${DATA_VOLUME}"
  docker volume create "$DATA_VOLUME" >/dev/null
  extract_volume "$DATA_VOLUME" "${dir}/dexicon_data.tar.gz"

  if [ -f "${dir}/qdrant_data.tar.gz" ]; then
    info "Restoring ${QDRANT_VOLUME}"
    docker volume create "$QDRANT_VOLUME" >/dev/null
    extract_volume "$QDRANT_VOLUME" "${dir}/qdrant_data.tar.gz"
  fi

  if [ -f "${dir}/ollama_data.tar.gz" ]; then
    info "Restoring ${OLLAMA_VOLUME}"
    docker volume create "$OLLAMA_VOLUME" >/dev/null
    extract_volume "$OLLAMA_VOLUME" "${dir}/ollama_data.tar.gz"
  fi

  info "Starting the stack"
  docker compose up -d >/dev/null

  green "Restored from ${dir}"
  info "If qdrant_data was not in the backup, reindex every corpus: the catalogue is"
  info "intact but the vectors are not, and search will return nothing until you do."
}

# The rehearsal. A backup procedure that has never been restored is a hypothesis, and this
# is the only command here that turns it into a fact. DESTRUCTIVE by design.
cmd_verify() {
  local root="${1:-./backups}"

  red "verify DESTROYS the current volumes and restores them from a fresh backup."
  printf 'Type the project name (%s) to continue: ' "$PROJECT"
  read -r answer
  [ "$answer" = "$PROJECT" ] || { red "Aborted."; exit 1; }

  cmd_backup "$root"
  local dir; dir="$(ls -d "${root}"/dexicon-backup-* | tail -1)"

  info "Destroying volumes"
  docker compose down -v >/dev/null 2>&1 || true

  cmd_restore "$dir"

  info "Waiting for liveness"
  for _ in $(seq 1 60); do
    if curl -fsS http://127.0.0.1:8477/healthz/live >/dev/null 2>&1; then
      green "Restored stack is live. Rehearsal passed."
      info "Check a search returns results before trusting it fully — liveness is not data."
      exit 0
    fi
    sleep 2
  done

  red "Restored stack never became live."
  docker compose logs dexicon | tail -40
  exit 1
}

case "${1:-}" in
  backup)  shift; cmd_backup  "${1:-./backups}" ;;
  restore) shift; cmd_restore "${1:-}" ;;
  verify)  shift; cmd_verify  "${1:-./backups}" ;;
  *)       usage ;;
esac
