#!/usr/bin/env python3
# dexicon-hook-version: 1
"""Dexicon UserPromptSubmit hook: retrieve one cited passage for the prompt.

`POST /api/context` exists for a caller with no agent loop (D-29): it searches, takes whole
chunks, joins the ones from the same file and stops at a character budget, returning the
passage and a citation per block of it.

Every path exits 0, and that matters more here than anywhere else in Dexicon: on
UserPromptSubmit, exit code 2 blocks the prompt AND ERASES IT. A hook that failed honestly
because the index was unreachable would delete what the user had just typed.

Claude Code adds plain stdout as context on this event, so this prints text.
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

try:
    from dexicon_hook_lib import load_config, post, warn
except Exception:  # noqa: BLE001 - never take the prompt down with us
    sys.exit(0)

# "ok", "yes", "carry on". Retrieving over an acknowledgement returns whatever is nearest in
# vector space, which is noise wearing citations.
DEFAULT_MIN_PROMPT_CHARS = 25

# The one field where the server's default is the wrong default here.
#
# `POST /api/context` defaults to 8,000 characters, which is right for a deliberate call and
# far too much to paste into every prompt: measured against this project's own index, the
# default returned 6,963 characters for one query.
#
# 4,000 was chosen when a budget under the smallest matching chunk returned nothing at all,
# so the floor had to clear one whole chunk. The server now cuts its last block to fit, so
# the floor is gone and the only question left is how much to put in front of every prompt.
# 2,000 is about five hundred tokens, and what does not fit arrives as the opening of the
# next result with the rest disclosed.
DEFAULT_MAX_CHARS = 2000

# Each of these is named after the POST /api/context field it sets. An unset one is left out
# of the request, so the server's default applies and the hook carries no copy to drift.
INT_FIELDS = {
    "DEXICON_CONTEXT_LIMIT": "limit",
    "DEXICON_CONTEXT_NEIGHBOURS": "neighbours",
}


def _int(cfg: dict, key: str, fallback: int) -> int:
    raw = cfg.get(key)
    if not raw:
        return fallback
    try:
        return int(raw)
    except (TypeError, ValueError):
        warn("%s=%r is not a number; using %d" % (key, raw, fallback))
        return fallback


def build_request(cfg: dict, prompt: str) -> dict:
    request = {"query": prompt, "maxChars": _int(cfg, "DEXICON_CONTEXT_MAX_CHARS", DEFAULT_MAX_CHARS)}

    mode = cfg.get("DEXICON_CONTEXT_MODE")
    if mode:
        request["mode"] = mode

    corpus = cfg.get("DEXICON_CONTEXT_CORPUS")
    if corpus:
        names = [n.strip() for n in corpus.split(",") if n.strip()]
        if names:
            request["corpus"] = names

    for key, field in INT_FIELDS.items():
        if not cfg.get(key):
            continue
        value = _int(cfg, key, 0)
        if value > 0:
            request[field] = value

    return request


def main() -> int:
    cfg = load_config()
    if not cfg.get("DEXICON_TOKEN"):
        return 0

    try:
        payload = json.load(sys.stdin)
    except Exception:  # noqa: BLE001
        return 0

    prompt = (payload.get("prompt") or "").strip() if isinstance(payload, dict) else ""
    if not prompt:
        return 0

    if len(prompt) < _int(cfg, "DEXICON_CONTEXT_MIN_PROMPT_CHARS", DEFAULT_MIN_PROMPT_CHARS):
        return 0

    # A slash command is an instruction to the client, not a question about the corpus.
    if prompt.startswith("/"):
        return 0

    request = build_request(cfg, prompt)
    result = post(cfg, "/api/context", request)
    if not isinstance(result, dict):
        return 0

    # The server explains itself, and does it better than this hook could: the note names
    # the smallest chunk it had to reject, so "raise the budget" comes with the figure.
    # Nothing is truncated to fit, by design -- the unit is a whole chunk, because half a
    # passage under a citation claiming to be the passage reads as complete and is not.
    note = (result.get("note") or "").strip()

    context = (result.get("context") or "").strip()
    if not context:
        if note:
            warn(note)
        return 0

    # Degraded means embeddings were unavailable and this is keyword-only. D-29 put the flag
    # in the response so a caller pasting the text into a prompt can tell what it holds.
    qualifier = " (degraded: keyword-only, embeddings were unavailable)" if result.get("degraded") else ""

    print(
        "Retrieved from the Dexicon index%s. It was selected by similarity to the prompt, "
        "not by understanding it, so read it before relying on it:" % qualifier
    )
    # A note alongside a passage is about what the passage is missing, which the model
    # reading it needs; a note instead of a passage is for whoever set the budget.
    if note:
        print("Note: %s" % note)
    print()
    print(context)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:  # noqa: BLE001
        sys.exit(0)
