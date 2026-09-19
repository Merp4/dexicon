#!/usr/bin/env python3
# dexicon-hook-version: 1
"""Dexicon SessionStart hook: say what is indexed.

An agent that does not search because it does not know anything is indexed looks exactly
like one that cannot. This answers that once per session with a catalogue read: no
embedding, no search, no vector store touched.

Claude Code adds plain stdout as context on SessionStart, so this prints text rather than
building a hookSpecificOutput envelope.

Python rather than shell and `jq`: `jq` is not installed on a stock Windows machine, and a
hook that exits 0 because a dependency is missing is indistinguishable from one that ran
and found nothing. The standard library does HTTP and JSON with nothing to install.

Every path exits 0. A session must start whether or not Dexicon is running.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

try:
    from dexicon_hook_lib import get, load_config, warn
except Exception:  # noqa: BLE001 - the hook must not be why a session fails to start
    sys.exit(0)


# A description is written to tell an agent what a corpus is for, so it earns its place
# here. It is also unbounded: this project's own `books` description is 350 characters, and
# this runs on every session for every corpus. Enough of the lead to be useful, capped so
# that adding corpora does not quietly grow what every session pays.
DEFAULT_DESCRIPTION_CHARS = 180


def shorten(text: str, limit: int) -> str:
    """Cut at a word boundary, and say that it was cut."""
    if limit <= 0 or len(text) <= limit:
        return text
    cut = text[:limit].rsplit(" ", 1)[0].rstrip(" ,.;:-")
    return (cut or text[:limit]) + "..."


def main() -> int:
    cfg = load_config()
    if not cfg.get("DEXICON_TOKEN"):
        warn("no DEXICON_TOKEN in the config file; not announcing corpora")
        return 0

    try:
        description_chars = int(cfg.get("DEXICON_CORPORA_DESCRIPTION_CHARS")
                                or DEFAULT_DESCRIPTION_CHARS)
    except (TypeError, ValueError):
        description_chars = DEFAULT_DESCRIPTION_CHARS

    corpora = get(cfg, "/api/corpora")
    if corpora is None:
        return 0

    if not isinstance(corpora, list) or not corpora:
        return 0

    # Nothing useful to announce until at least one corpus can actually answer a query.
    if not any(c.get("state") == "ready" for c in corpora):
        return 0

    lines = []
    for c in corpora:
        try:
            line = "  %s: %s files, %s chunks, %s" % (
                c["name"],
                format(int(c.get("fileCount") or 0), ","),
                format(int(c.get("chunkCount") or 0), ","),
                c.get("state", "unknown"),
            )
        except (KeyError, TypeError, ValueError):
            continue
        description = " ".join((c.get("description") or "").split())
        if description:
            line += " - " + shorten(description, description_chars)
        lines.append(line)

    if not lines:
        return 0

    print("Dexicon is connected and holds these corpora, searchable with search_index:")
    print("\n".join(lines))
    print("Pass one of those names as the corpus argument; omitting it searches all of them.")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:  # noqa: BLE001
        sys.exit(0)
