#!/bin/sh
# Dexicon — Ollama entrypoint.
#
# Starts the Ollama server, pulls the configured embedding model if it is not
# already present, then hands the foreground back to the server.
#
# The pull happens here rather than in a separate init container so the model is
# a precondition of the service being healthy. docker-compose.yml gates Dexicon's
# start on `ollama list` reporting the model — without that, Dexicon would come up
# against a model still downloading and spend its first minutes in embedding
# backoff, which reads as a bug rather than as a first run.

set -e

MODEL="${DEXICON_EMBEDDING_MODEL:-nomic-embed-text}"

echo "dexicon-ollama: starting server"
/bin/ollama serve &
SERVER_PID=$!

# Wait for the server to answer before asking it to pull anything.
i=0
until /bin/ollama list >/dev/null 2>&1; do
  i=$((i + 1))
  if [ "$i" -gt 60 ]; then
    echo "dexicon-ollama: server did not become ready within 60s" >&2
    exit 1
  fi
  sleep 1
done
echo "dexicon-ollama: server ready after ${i}s"

if /bin/ollama list | grep -q "$MODEL"; then
  echo "dexicon-ollama: model '$MODEL' already present, skipping pull"
else
  echo "dexicon-ollama: pulling '$MODEL' (first run — this can take several minutes)"
  /bin/ollama pull "$MODEL"
  echo "dexicon-ollama: pull complete"
fi

echo "dexicon-ollama: ready, serving '$MODEL'"
wait "$SERVER_PID"
