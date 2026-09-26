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

One file, `dexicon-hooks.env`, holds the connection, the key and every knob. The installer
writes it into the `.claude` directory the hooks are installed under, `~/.claude` at user
scope and a project's `.claude` at project scope, and the hooks read it from there, falling
back to `~/.claude/dexicon-hooks.env` when there is none beside them. `DEXICON_HOOKS_ENV`
names another file. The keys are named after the `POST /api/context` fields they set, and an
unset one is not sent at all, so the server's own default applies rather than the hook
carrying a copy of it.

| Key | Sets |
|---|---|
| `DEXICON_URL` | Where the server is. The REST base, not the `/mcp` endpoint |
| `DEXICON_TOKEN` | A `search`-scoped key. Never the admin password |
| `DEXICON_TIMEOUT` | Seconds, covering connect and read. Defaults to 10, above the measured cold-query cost |
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

## The budget, and what a small one gives you

The unit is a whole chunk, except for the last block, which the server cuts to fit. It
falls on a line boundary, the passage says `… N characters of this chunk not shown …`, and
the citation reports the lines actually present rather than the chunk's full span. The
response carries `partialBlocks`, and each citation carries `partial` and `omittedChars`,
separately from `truncated`, which goes on meaning hits were dropped.

So the tail of the budget shows the opening of the next result rather than going unspent.
The hook defaults to 2,000 rather than the server's 8,000: 8,000 returned 6,963 characters
for one query here, which is too much to put in front of every prompt.

A budget under about 380 characters still returns nothing, since below that a citation and
two lines of prose cost more than they return. The server then says which budget would have
fitted, and the hook prints that on stderr where it reaches whoever set it and does not
become model context.

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
