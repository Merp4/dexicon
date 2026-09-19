# 10 — Security and secrets

The project is intended to be published. Secret hygiene that is retrofitted before a first
public push has already failed, because the history is what gets scanned. These rules apply from
commit one.

## The secrets model

**One tracked template, no tracked values.**

| File | Tracked | Contains |
|---|---|---|
| `.env.example` | **yes** | Every variable, documented, all secret values blank |
| `.env` | no — gitignored | Real values. Read by Compose, substituted into the container environment. |
| `secrets/` | no — gitignored | Key material for anyone who needs file-based secrets |
| `appsettings.json` | yes | Non-secret defaults **only** |
| `appsettings.Development.json` | no — gitignored | Local overrides |

Configuration precedence, highest first:

1. `/run/secrets/*` — Docker/Swarm/K8s secret files, read as `DEXICON__SECTION__KEY`
2. Environment variables
3. `appsettings.{Environment}.json`
4. `appsettings.json`

A secret that appears in the last two is a bug, and there is a test that says so (below).

### `.gitignore`, from the first commit

```gitignore
.env
.env.*
!.env.example
secrets/
*.local.json
appsettings.Development.json
appsettings.*.local.json
*.pfx
*.p12
*.key
*.pem
.dexicon/
data/
```

## Handling of specific secrets

| Secret | Storage | Notes |
|---|---|---|
| Dexicon API tokens | PBKDF2-HMAC-SHA256, 600 000 iterations, 32-byte per-token salt, in SQLite | Shown once at creation. No retrieval path exists. Constant-time comparison. |
| Bootstrap token | Generated if unset; printed to the container log **once**, on first run only | A log line is an acceptable delivery channel for a value that is about to be rotated; a config file is not. |
| `QDRANT_API_KEY` | Environment / `/run/secrets` | Never persisted by Dexicon. |
| Qdrant / Ollama endpoints | Environment | Not secret, but shown read-only in the UI so nobody is tempted to make them editable-and-therefore-stored. |

### Redaction

A single `Redact` helper is applied to every log sink and every error response. It masks:

- anything matching `dex_[A-Za-z0-9_-]{20,}`
- `Authorization` and `X-Api-Key` header values
- query strings on outbound URLs
- connection strings

And a rule with teeth: **exception messages from the Qdrant and Ollama clients are not
returned verbatim to unauthenticated callers.** They can carry endpoint and credential
detail. Authenticated callers get the real message, because a tool that hides its errors
from its operator is unusable.

## Guards

Prose does not hold a line. Each of these is a test or a hook, landing in the same commit
as the rule it protects.

| Guard | Mechanism |
|---|---|
| No secret in tracked config | Test scans `appsettings*.json` (excluding `.local`) for keys matching `password|secret|token|apikey|key` with a non-empty value. Fails the build. |
| No secret committed, ever | `gitleaks` as a pre-commit hook **and** a CI job over full history, with a custom rule for the `dex_` prefix. |
| `.env.example` stays complete | Test asserts every `DEXICON__*` key read by the config binder appears in `.env.example`. A new setting that is undocumented fails CI. |
| Tokens never logged | Test writes a token through the logging pipeline and asserts the sink received the mask. |
| No unfiltered vector query | Test calls every public repository read method with an empty scope and asserts each throws. |
| Key scoping | `KeyScopingTests` and `AdminPasswordTests` — see [07](07-auth.md). |

Each guard is verified by **breaking the value and watching it go red**, then restoring it.
A guard that has never failed has not been shown to work. The failure mode is a check
that matches the wrong thing and is mistaken for coverage.

## Signing in without handling the password (development only)

Testing the UI end to end means being signed in, and the two obvious ways there are both
bad. A person types the password into the form every session; or whatever is driving the
browser reads it out of `.env` and types it, which puts a live credential into a
transcript, a shell history and a log.

`scripts/dev-token.py` is the third way. It reads `DEXICON_ADMIN_PASSWORD` from `.env`,
exchanges it for a session at `/api/session` **inside the script**, and hands only that
session to the page. The password never leaves the process, and what does reach the browser
expires on its own and can be revoked. Nothing in between renders either value; the
operator sees a nonce and a byte count.

```bash
python scripts/dev-token.py
```

It proves the session carries `admin` before handing it over, by calling an admin-only
endpoint. That check is the point: an API key authenticates but can never hold `admin`
([D-28](decisions.md#d-28-an-admin-password-and-scoped-api-keys)), so a browser holding one
loads the shell and then 403s on every screen worth testing, which reads as a bug in the
app rather than a wrong credential. It then prints a one-line snippet to run in the console
of a Dexicon tab, and the page stores the session exactly where the sign-in form would:
`sessionStorage['dexicon.token']`.

A wrong password is answered slowly, because the endpoint throttles by a doubling delay:
up to 30 seconds is the guard working, not the script hanging.

What constrains it:

| Property | Why |
|---|---|
| Binds `127.0.0.1` | Nothing off the machine can reach it. `--bind` widens it, for the case where the browser is in another network namespace (WSL, a container) and cannot reach loopback on this host — it says so on stderr when you do, because the loopback bind is most of what makes this safe. |
| Single-use nonce in the path, compared in constant time | A page that guesses the port gets a 404, and a replay gets a 404. |
| `Access-Control-Allow-Origin` names one origin | A wildcard would let every page the browser has open read the token while it listens. |
| Serves once, then exits; `--timeout` bounds the wait | The window is seconds, not a session. |
| Refuses to share a port | `http.server` sets `SO_REUSEADDR`, and on Windows that lets a second instance bind the same port — two nonces, one socket, and a handover that silently lands nowhere. |

**This is a development tool and must not become a login mechanism.** It is not in the
image, it reads a file that only exists on a developer's machine, and production signs in
through the form on purpose.

## What an error is allowed to say

An MCP tool result is read by a model and often shown to a person, and a model may repeat
it. That makes it an API boundary like any other: a stack trace, an internal hostname or a
connection string crossing it is a disclosure, and one that can end up pasted into a
conversation elsewhere.

Audited 2026-09-17 by provoking each failure against the running stack.

| Probe | What comes back |
|---|---|
| Unknown corpus | `Unknown corpus 'x'. Corpora this key can reach: books, docs.` — only what this key reaches |
| Unknown file, `get_context` | The path and the corpus, nothing else |
| Line outside the file | `…has no content around line -9999; it spans lines 1-197.` |
| Unknown source filter | `No source at '../../etc' in the corpora searched.` |
| **Vector store down** (`search_index`) | `An error occurred invoking 'search_index'.` — no message, no type, no host |
| **Vector store down** (REST) | RFC 9457 problem: `An error occurred while processing your request.` + a traceId |
| No `Authorization` header | 401 `Missing credentials` |
| Malformed token | 401 `Invalid credentials` |
| Well-formed unknown token | 401 `Invalid credentials` — **identical**, so nothing says whether a token exists, is revoked or has expired |
| `POST /mcp` unauthenticated | 401 before any tool listing; the tool surface is not enumerable |

Two properties this depends on, both of which should be re-checked:

- **Unhandled exceptions are redacted by the MCP SDK.** Only `McpException` has its message
  surfaced; anything else becomes `An error occurred invoking '<tool>'.` Every message a
  caller can read is therefore one this repository wrote deliberately. That is the SDK's
  behaviour rather than a setting here, so **re-run the probes after upgrading
  `ModelContextProtocol`**, because the day it starts forwarding `ex.Message` is the day a
  Qdrant connection failure starts naming `dexicon-qdrant:6334` to a model.
- **The environment is Production.** `ASPNETCORE_ENVIRONMENT` is unset in the image and in
  compose, so the developer exception page is never added. Setting it to `Development` to
  debug something also turns stack traces on for every REST caller.

The detailed health endpoint (internal endpoints, model, dimensions, corpus count and the
running job) is behind auth. The anonymous ones answer `{"status":"ok"}` and
`{"status":"ready","qdrant":true,"catalogue":true}`: enough for a container healthcheck and
a load balancer, and nothing about what is inside.

Errors that name a real internal address, such as `Could not list models from provider
'ollama': …`, are reachable only from authenticated admin endpoints that the same caller
can read `/healthz` from anyway. That is where the line sits on purpose: an operator
debugging a provider needs the endpoint in the message.

## Container and network posture

Covered operationally in [09](09-deployment.md); the security-relevant points:

- Non-root (UID 10001), read-only rootfs, `cap_drop: ALL`, `no-new-privileges`.
- `/workspaces` mounted **read-only**, so Dexicon cannot write to source trees.
- No Docker socket. Ever. There is no feature that needs it.
- Qdrant and Ollama are not published to the host in the default compose file. Qdrant with
  no API key on an exposed port bypasses the entire tenancy model.
- No outbound network calls at runtime other than Qdrant and Ollama. No telemetry, no
  update check, no model download at request time.

## Input handling

| Risk | Mitigation |
|---|---|
| Path traversal via a workspace source path | Canonicalise, then assert the result is under `/workspaces`. Symlinks that escape the root are not followed, and are recorded as `skipped` with the reason. |
| Zip-bomb EPUB / OOXML | Bounded decompressed size and entry count; exceed either and the file fails with a clear reason. |
| Malicious PDF | PdfPig is managed code; extraction runs with a wall-clock timeout per file. |
| Oversized upload | Enforced at the request-size limit, before buffering. |
| Regex denial of service (custom boundary patterns) | Compiled with a 500 ms `matchTimeout`; timeout fails the job explicitly rather than falling back. |
| Stored content echoed into the UI | React escapes by default; syntax highlighting operates on text nodes, never `dangerouslySetInnerHTML`. |
| SSRF via configured endpoints | Endpoints come from the environment only — never from a request body or the UI. |

## Supply chain

- Central package management (`Directory.Packages.props`); one version per dependency.
- Dependabot for NuGet, npm, Docker base images, and GitHub Actions.
- CI: `dotnet list package --vulnerable --include-transitive` and `npm audit`, both failing
  the build on high severity.
- CodeQL for C# and TypeScript on pull requests, on `main`, and weekly.
- Release builds publish an SBOM (CycloneDX) and pin base images by digest.
- Every third-party extraction library is permissively licensed and listed with its licence
  in [04](04-ingestion.md#extraction).

### Licence review of the dependency tree

Reviewed 2026-09-17 over the whole **transitive** graph, from each package's own metadata
(`.nuspec` for NuGet, `package.json` for npm) rather than from the direct dependency
list.

| | Packages | Licences |
|---|---|---|
| NuGet (all projects, incl. tests) | 122 | MIT 88, Apache-2.0 29, BSD-3-Clause 2, BSD-2-Clause 1, Unlicense 1, xunit.abstractions (Apache-2.0, by URL) 1 |
| npm (installed) | 307 | MIT 269, ISC 14, Apache-2.0 7, BSD-3-Clause 5, BSD-2-Clause 3, and the nine below |
| npm (production, transitive) | 85 | MIT, ISC, BSD, Apache-2.0, and `tslib` under 0BSD |

**No GPL, LGPL, AGPL, SSPL or Commons Clause anywhere in either graph.** Everything is
compatible with distributing this under Apache-2.0.

Two that needed reading rather than parsing:

- `OllamaSharp` declares a licence *file* rather than an SPDX expression. The file is MIT.
- `xunit.abstractions` predates SPDX metadata and carries a `licenseUrl`. It is Apache-2.0,
  and it is a test dependency that does not ship.

**The nine npm licences that are not plain MIT/ISC/BSD/Apache are build-time only**, bar
one. This was checked against `npm ls --omit=dev` rather than inferred from position in
the tree:

| Package | Licence | Ships? |
|---|---|---|
| `lightningcss`, `lightningcss-win32-x64-msvc` | MPL-2.0 (weak copyleft) | No — CSS transform at build |
| `caniuse-lite` | CC-BY-4.0 (attribution) | No — browser targets at build |
| `argparse` | Python-2.0 | No |
| `mdn-data` | CC0-1.0 | No |
| `lru-cache` | BlueOak-1.0.0 | No |
| `@csstools/color-helpers`, `@csstools/css-syntax-patches-for-csstree` | MIT-0 | No |
| `tslib` | 0BSD | **Yes**, in the browser bundle — 0BSD requires no attribution |

MPL-2.0 is file-level copyleft: using `lightningcss` as a build tool creates no obligation,
and modifying its sources would. Nothing here modifies it. CC-BY-4.0 on `caniuse-lite`
requires attribution when the *data* is redistributed; the build consumes it and ships
none of it.

Re-run it after any dependency change that adds a package rather than bumps one. The two
checks are the same two: whether anything new is copyleft, and whether
anything non-permissive has moved from build-time into the shipped bundle.

## Repository files, present at first push

`SECURITY.md` (how to report, expected response time, supported versions) ·
`LICENSE` ([D-14](decisions.md#d-14-licence)) · `CONTRIBUTING.md` ·
`CODE_OF_CONDUCT.md` · `.github/workflows/ci.yml` · `.github/dependabot.yml` ·
`.gitleaks.toml` · `.githooks/pre-commit`.

## Threat model

Dexicon is a local developer tool with per-key scoping. It is not a multi-tenant SaaS
boundary, and describing it as one would invite uses it cannot carry.

What is and is not defended is listed in
[SECURITY.md](../SECURITY.md#what-dexicon-defends-against), next to how to report a
problem. Everything above is how those lines are enforced.
