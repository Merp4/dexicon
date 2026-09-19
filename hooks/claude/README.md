# Claude Code hooks

Two hooks, both read-only, both optional. See
[D-30](../../docs/decisions.md#d-30-skills-and-hooks-install-with-the-client-under-a-dexicon-prefix)
for why they are shaped this way.

| Hook | Event | Default | What it does |
|---|---|---|---|
| `dexicon-corpora.py` | `SessionStart` | on | Announces the corpora, their sizes and their state, once per session |
| `dexicon-context.py` | `UserPromptSubmit` | off | Retrieves one cited passage for the prompt |

`dexicon_hook_lib.py` holds what both need: the config file, one HTTP call, and a warning
channel. It is installed beside them.

## Installing

```powershell
./scripts/install-mcp.ps1 -What hooks                    # SessionStart only
./scripts/install-mcp.ps1 -What hooks -WithContextHook   # and the per-prompt one
./scripts/install-mcp.ps1 -Uninstall                     # take it all back out
```

Add `-WhatIf` to see the plan without writing anything. The installer merges into
`settings.json` rather than replacing it, backs it up first, and names only its own entries
when removing them.

Copying the files by hand works too; the installer exists so a later version can replace
them and so `-Uninstall` knows what it owns.

## Configuration

One file, `~/.claude/dexicon-hooks.env`, holds the connection, the key and every knob. The
keys are named after the `POST /api/context` fields they set, and an unset one is not sent
at all, so the server's own default applies rather than the hook carrying a copy of it.

| Key | Sets |
|---|---|
| `DEXICON_URL` | Where the server is. The REST base, not the `/mcp` endpoint |
| `DEXICON_TOKEN` | A `search`-scoped key. Never the admin password |
| `DEXICON_TIMEOUT` | Seconds, covering connect and read |
| `DEXICON_CONTEXT_MAX_CHARS` | `maxChars` |
| `DEXICON_CONTEXT_LIMIT` | `limit` |
| `DEXICON_CONTEXT_MODE` | `mode` |
| `DEXICON_CONTEXT_NEIGHBOURS` | `neighbours` |
| `DEXICON_CONTEXT_CORPUS` | `corpus`, comma-separated |
| `DEXICON_CONTEXT_MIN_PROMPT_CHARS` | Floor below which no search runs |
| `DEXICON_CORPORA_DESCRIPTION_CHARS` | How much of each description `SessionStart` announces |

`DEXICON_CONTEXT_CORPUS` narrows the per-prompt hook without narrowing the key, so the
agent's own `search_index` calls still reach everything.

Anything already in the environment wins over the file, so a single run can be pointed
elsewhere without editing it.

## The budget, and why a small one returns nothing

Nothing is truncated to fit. The unit is a whole chunk, because half a passage under a
citation claiming to be the passage reads as complete and is not. A budget below the
smallest matching chunk therefore returns an empty passage, and the server says so:

```
No result fitted a budget of 1,500 characters; the smallest is 2,109.
Raise maxChars, or narrow the query.
```

The hook prints that on stderr, where it is visible to whoever set the budget and does not
become model context. Measured on this project's own index, `maxChars` of 1,500 and 2,000
both returned nothing and 4,000 returned a passage, which is why the hook defaults to
4,000 rather than inheriting the server's 8,000.

## Why the per-prompt hook is off by default

It runs a hybrid search on every message. Measured against 15,213 chunks: a query the
embedder has not seen takes about 5.4 seconds, a repeat 300 to 470 milliseconds. The cost
is embedding the query, so the first message of a session pays it.

## Graceful degradation

Every path exits 0, and on `UserPromptSubmit` that is not a preference: exit code 2 blocks
the prompt **and erases it**. A hook failing honestly because the index was unreachable
would delete what had just been typed.

Failures are reported on stderr, never silently:

| Situation | Result |
|---|---|
| No config file | Warning naming the path; no output |
| No `DEXICON_TOKEN` | Warning; no output |
| Server unreachable, or times out | Warning with the reason; no output |
| 401 or 403 | Warning saying which; no output |
| Response is not JSON, or is an error object | Warning; no output |
| Nothing matched | Silent. This is a real answer, not a fault |
| Prompt shorter than the floor, or a slash command | Silent, before any request |

The distinction in the last two rows is the point: a hook that stays quiet for a missing
dependency and for a genuine empty result is one nobody can debug.

## Testing

```bash
python hooks/claude/test-hooks.py
```

Runs both hooks against fakes, covering each row above. It needs no Dexicon instance. Pass
`--live` to run them against a real one using `~/.claude/dexicon-hooks.env`.
