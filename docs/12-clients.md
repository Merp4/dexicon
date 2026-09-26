# 12 — Connecting an agent

Dexicon is a **remote MCP server over streamable HTTP with bearer auth**, which is the
shape most clients now support natively:

```
url     http://localhost:8477/mcp
header  Authorization: Bearer dex_…
```

That one line is the whole integration. Everything below is the same fact written in each
client's own dialect, plus the two clients that need a bridge and the reasons why.

For a caller with no agent loop — a git hook, a CI step, a shell script — the same key
reaches an HTTP API instead, including one call that returns an assembled, cited passage
for a query. See [13](13-integration.md).

`./scripts/install-mcp.ps1` generates any of these; see [Installer](#installer).

> **Where the key comes from.** Issue it on the **Access** screen, which is also where
> you tick the corpora it may reach and copy the command below. Nothing is minted for you
> on first run: what is printed once is the admin password, which signs you in
> (`docker compose logs dexicon | grep "admin password"`).
>
> For scripted setup with no browser, set `DEXICON_BOOTSTRAP_TOKEN` in `.env` to a value
> of your own and restart; it is adopted with `search` and `ingest`, and reaches every
> corpus because nothing has narrowed it. A key can never carry `admin`, so no agent
> configuration can delete a corpus.

---

## Claude Code

No config file; the CLI writes it. Use `--scope user` so every project's session has it,
rather than only the one whose directory you happened to run this from:

```bash
claude mcp add --transport http dexicon http://localhost:8477/mcp \
  --header "Authorization: Bearer dex_…" --scope user
```

Verify with `claude mcp list`; Dexicon should report `✔ Connected`.

Claude Code's `project` and `local` scopes key the registration by the literal working
directory string, case and slash direction included, so a server added from one shell can be
invisible to a session launched from another that reports the same path differently. `user`
scope does not depend on where or how you ran the command. `--scope project` writes the
connection into `.mcp.json` in the directory the command runs from, so run it from inside
the repo it is for. That file holds the key in plain text: keep it out of version control,
since anyone who can read it can search with the key.

## Cursor

`.cursor/mcp.json` in the project, or `~/.cursor/mcp.json` for all of them.

```json
{
  "mcpServers": {
    "dexicon": {
      "url": "http://localhost:8477/mcp",
      "headers": { "Authorization": "Bearer dex_…" }
    }
  }
}
```

## VS Code (GitHub Copilot)

`.vscode/mcp.json`. Note the key is `servers`, not `mcpServers`, and the type is explicit.

```json
{
  "servers": {
    "dexicon": {
      "type": "http",
      "url": "http://localhost:8477/mcp",
      "headers": { "Authorization": "Bearer dex_…" }
    }
  }
}
```

VS Code can read the token from an input prompt instead of storing it in the file, which is
worth doing if the file is committed:

```json
{
  "inputs": [
    { "id": "dexicon-token", "type": "promptString", "description": "Dexicon API token", "password": true }
  ],
  "servers": {
    "dexicon": {
      "type": "http",
      "url": "http://localhost:8477/mcp",
      "headers": { "Authorization": "Bearer ${input:dexicon-token}" }
    }
  }
}
```

## Windsurf

`~/.codeium/windsurf/mcp_config.json`.

```json
{
  "mcpServers": {
    "dexicon": {
      "serverUrl": "http://localhost:8477/mcp",
      "headers": { "Authorization": "Bearer dex_…" }
    }
  }
}
```

The key is `serverUrl`, not `url`. Windsurf is the one client in this list that spells it
differently, and a `url` key there fails without a useful message.

## Cline / Roo

Edit through the MCP Servers panel, or the settings JSON directly:

```json
{
  "mcpServers": {
    "dexicon": {
      "type": "streamableHttp",
      "url": "http://localhost:8477/mcp",
      "headers": { "Authorization": "Bearer dex_…" }
    }
  }
}
```

---

## The two that need a bridge

### Claude Desktop

**`claude_desktop_config.json` only validates stdio servers.** A `url` field is silently
dropped, or causes the app to fail on startup, with nothing explaining why. A
configuration that appears correct and does nothing is difficult to diagnose.

Two working options.

**Custom connector (preferred).** Settings → Connectors → Add custom connector, then the URL
and header. No file editing, and it is the path Anthropic maintains.

**`mcp-remote` as a stdio bridge**, if you would rather keep it in the file:

```json
{
  "mcpServers": {
    "dexicon": {
      "command": "npx",
      "args": [
        "-y", "mcp-remote",
        "http://localhost:8477/mcp",
        "--header", "Authorization:Bearer dex_…"
      ]
    }
  }
}
```

`Authorization:Bearer dex_…` has **no space after the colon**. `mcp-remote` splits each
`--header` on the first colon, and a space ends up inside the header value, which the server
rejects as a malformed token.

Config file location:

| OS | Path |
|---|---|
| macOS | `~/Library/Application Support/Claude/claude_desktop_config.json` |
| Windows | `%APPDATA%\Claude\claude_desktop_config.json` |
| Linux | `~/.config/Claude/claude_desktop_config.json` |

### Zed

`settings.json`, under `context_servers`, and stdio only, so the same bridge applies:

```json
{
  "context_servers": {
    "dexicon": {
      "source": "custom",
      "command": "npx",
      "args": ["-y", "mcp-remote", "http://localhost:8477/mcp", "--header", "Authorization:Bearer dex_…"]
    }
  }
}
```

---

## Anything else

If a client accepts a URL and headers, it works; there is nothing Dexicon-specific to learn.
If it only speaks stdio, `mcp-remote` bridges it, as above.

Two things that trip people up with any client:

- **`localhost` from inside a container is that container.** An agent running in Docker
  needs `http://host.docker.internal:8477/mcp`, or a shared network and the service name.
- **2026-07-28 clients send no `initialize`.** That is correct and Dexicon supports it;
  older clients negotiate normally. A client reporting "no handshake" is not broken. See
  [06-mcp-surface.md](06-mcp-surface.md).

---

## Installer

```powershell
./scripts/install-mcp.ps1 -Client claude-code -Token dex_…
./scripts/install-mcp.ps1 -Client cursor -Scope user
./scripts/install-mcp.ps1 -Client vscode -Project .
./scripts/install-mcp.ps1 -List
```

It merges into an existing config rather than replacing it, backs the file up first, and
prints what it wrote, excluding the token. `-WhatIf` shows the change without making it.
`-Project` is the directory to write into for project scope, defaulting to the current one
rather than the Dexicon checkout.

`-Client claude-code` defaults to `-Scope user`, for the reason under
[Claude Code](#claude-code) above, rather than the `project` default every other client
gets. `-Scope project` writes a per-repo `.mcp.json` into `-Project` instead, holding the
key in plain text, so keep it out of version control. `-What skill`, `-What hooks` and
`-Uninstall` default to `user` the same way, since they only touch Claude Code, and so does
`-What all` unless another client is named with it.

An install made before this default, into a project's `.claude` and `.mcp.json`, is
removed with `-Uninstall -Scope project -Project <that repo>`.

It will not invent a token: pass `-Token`, or let it read the one in `.env`
(`DEXICON_BOOTSTRAP_TOKEN`) when you have set one. There is no path where the installer
puts a credential somewhere you did not ask it to.

`-List` shows every client, where its config lives, and whether Dexicon is already in it.
Two entries in that list it will not write, and says so rather than writing something
ignored: **Cline**, whose servers live in extension storage, and **VS Code at user scope**,
which shares a `settings.json` with every other preference. Both take the JSON above by hand.

Needs PowerShell 7 (`pwsh`). On Windows PowerShell 5.1 it refuses by version rather than
failing with a syntax error.

## A skill for the agent

Connecting gives an agent the tools. It does not tell it *when* to reach for them, and an
agent that never searches is indistinguishable from one that cannot.

`skills/dexicon-search/SKILL.md` covers that: what the corpora are for, when semantic search
beats grep, how `corpus:set` addresses a chunking, and when to follow a hit with
`get_context`.

```powershell
./scripts/install-mcp.ps1 -What skill
```

That copies it to `~/.claude/skills/dexicon-search/`, or into a project with `-Scope
project`. Copying the directory by hand works just as well; the installer exists so that a
later version can replace it, and so `-Uninstall` knows what to take away.

It is invocable both ways. Claude reaches for it on its own, from the `description` in its
front matter, and `/dexicon-search how are refresh tokens revoked` runs that search
directly.
