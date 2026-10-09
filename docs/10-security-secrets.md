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
| Admin password | PBKDF2-HMAC-SHA256, as for tokens, in SQLite | Set from `DEXICON__ADMIN__PASSWORD` (`DEXICON_ADMIN_PASSWORD` in `.env`) on every start. If unset, a generated password is printed to the container log **once**, on first run only. Nothing in the UI or API changes it. A log line is an acceptable delivery channel for a value shown once; a config file is not. |
| Bootstrap token | `DEXICON__BOOTSTRAP__TOKEN`, adopted as a key holding `search` and `ingest` | Never generated. A blank value creates no key. A set value must read `dex_<id>_<secret>`. It lives in `.env`. |
| `QDRANT_API_KEY` | Environment / `/run/secrets` | Never persisted by Dexicon. |
| Qdrant / Ollama endpoints | Environment | Not secret, but shown read-only in the UI so nobody is tempted to make them editable-and-therefore-stored. |

### Logs and errors

The one credential written to a log is the generated admin password, printed once on the
first start (see the table above). A presented credential is not logged. The console
template renders values as JSON (`LogOutput.ConsoleTemplate`), so a request path cannot
start a new log line. A rejected request logs a 32-bit digest of the presented credential
(`CallerDigest`), which tells one caller from several. It is not a secret: whoever can read
the logs can compute the digest of a candidate credential and compare, and a match is a
1 in 2^32 coincidence for a wrong guess, so 32 bits limits what the digest reveals and does
not make a guess impossible to test. The principal cache and the admin session store are
keyed on a SHA-256 of the credential, never on the credential.

Exception messages from the Qdrant and Ollama clients do not reach a search caller. A search
whose query embedding fails returns keyword-only results with a fixed reason. In Production
the MCP SDK replaces any exception other than `McpException` with a generic message, and the
default exception handler returns a problem response with a trace id to REST callers (see
"What an error is allowed to say"). Admin endpoints return provider errors in full, because
an operator debugging a provider needs the endpoint in the message.

## Guards

Prose does not hold a line. Each of these is a test or a hook, landing in the same commit
as the rule it protects.

| Guard | Mechanism |
|---|---|
| No secret in tracked config | `NoSecretValuesInTrackedConfiguration` scans `appsettings*.json` (excluding `.local`) for keys matching `password|secret|apikey|api_key|token|credential` with a non-empty value. Fails the build. |
| No secret committed, ever | `gitleaks` as a pre-commit hook **and** a CI job over full history, with a custom rule for the `dex_` prefix. |
| `.env.example` stays complete | `EnvExampleDocumentsEveryVariableComposeUses` asserts every variable `docker-compose.yml` references appears in `.env.example`, and `EveryDocumentedEnvironmentVariableBindsToARealOption` asserts every `DEXICON__` variable compose sets binds to an option. A variable newly forwarded by `docker-compose.yml` without an entry in `.env.example` fails CI, and so does one that binds to no option. An option never wired into compose is invisible to both tests. |
| No credential in a derived key or digest, no forged log line | `PrincipalCacheKeyNeverContainsTheToken` asserts the principal cache key does not contain the token's secret; `CallerDigestNeverContainsTheCredential` asserts the caller digest holds neither the secret nor the id and is hex; `LogForgingTests` asserts a request path carrying a newline cannot start a log line. No test scans log calls for credentials. |
| No unfiltered vector query | `VectorStoreRefusesAQueryWithNoCorpusFilter` asserts that `SearchAsync` throws on an empty scope. The other read methods take a chunk set and are reached after scope resolution. |
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
  no API key on an exposed port bypasses per-corpus scoping entirely.
- No outbound network calls at runtime other than Qdrant, Ollama, and an OpenAI or Azure
  OpenAI embedding provider where the operator has configured one
  ([04](04-ingestion.md#embedding-providers)). No telemetry and no update check. Models
  download in the Ollama container: the configured embedding model on first start
  (`scripts/provision-models.sh`), and any model an administrator pulls from the Models
  screen.

## Input handling

| Risk | Mitigation |
|---|---|
| Path traversal via a workspace source path | Canonicalise, then assert the result is under `/workspaces`. A source path through a link is refused. The walk follows no links, and records each as `skipped` with the reason ([D-35](decisions.md#d-35-links-are-not-followed)). |
| Zip-bomb EPUB / OOXML | No decompressed-size or entry-count bound. A file over `DEXICON__INDEXING__DOCUMENTMAXBYTES` (512 MB, compressed) is skipped, and a file that keeps reading is abandoned after `DEXICON__INDEXING__EXTRACTIONTIMEOUTSECONDS` (300 s). A highly compressed archive under both limits is not bounded. |
| Malicious PDF | PdfPig is managed code. Extraction is abandoned after 300 s of reading (`DeadlineStream`). The clock is checked between reads, so a read that never returns, or a long computation between reads, is not bounded ([09](09-deployment.md)). |
| Oversized upload | Partial. The upload endpoint reads the multipart body with a `MultipartReader` and streams each file to the blob store, which applies `DEXICON__UPLOAD__MAXFILEBYTES` (200 MB per file) as the bytes arrive, so nothing is buffered in `/tmp` (a tmpfs in the compose file). A 400 MB file measured an empty `/tmp` throughout, then a 400 response. The request as a whole is bounded at ten files at the cap plus 1 MiB of framing (2,098,200,576 bytes at the default): a larger body gets a `413` naming the bound, before it is read when `Content-Length` is declared and otherwise when it reaches the bound. A request is also read for at most ten file parts and one hundred multipart sections (form fields included); past either the application reads no more and answers with a request-level failure. A probe against Kestrel with the body-size limit unset, as `UploadAsync` sets it, found that the server reads and discards the unread rest of a request after the handler returns (2,500 MB accepted, above the 2,098,200,576 byte bound, and the connection was reused), and the same for a `413` answered from the declared `Content-Length` before any body byte was read (2,500 MB accepted). No cap on the discard was found up to 2,500 MB, so the bytes of such a request cross the network in full. A file refused for its size is read off the connection and discarded as well. Extraction of an uploaded file is abandoned after `DEXICON__INDEXING__EXTRACTIONTIMEOUTSECONDS` (300 s) of reading, and no document record is created for it; its bytes stay in the blob store. Each upload in flight holds one temp copy of its current file, at most the cap, under `/data/blobs`, and concurrent uploads are not limited. The caller needs `ingest` or the admin session. |
| Regex denial of service (custom boundary patterns) | Compiled with a 500 ms `matchTimeout`; timeout fails the job explicitly rather than falling back. |
| Stored content echoed into the UI | React escapes by default; syntax highlighting operates on text nodes, never `dangerouslySetInnerHTML`. |
| SSRF via configured endpoints | Endpoints come from the environment only — never from a request body or the UI. |

## Supply chain

- Central package management (`Directory.Packages.props`); one version per dependency.
- Dependabot for NuGet, npm, Docker base images, and GitHub Actions.
- CI: `dotnet list package --vulnerable --include-transitive` and `npm audit`, both failing
  the build on high severity.
- CodeQL for C# and TypeScript on pull requests, on `main`, and weekly.
- Release builds publish an SBOM (BuildKit, `sbom: true`) and pin base images by digest.
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

## What a history corpus holds

A key that reaches a history corpus reads the repository's history, not its current tree.
With `includeDiff` on, that can include the content of files committed and later removed,
a credential committed by mistake and deleted in the next commit among them: wherever the
include globs reach, and in every commit whose patch is within `maxDiffBytes` (64 KiB
unless set). With the diff off, what remains is what `includeMessage` and
`includeStat` keep, which by default is every commit message and every changed path.
`.gitignore` only ever kept out what it listed at the time, and a history source takes no
exclude globs, so its include globs are the only narrowing ([04](04-ingestion.md#git-history--a-repositorys-commits)). Scan a
repository's full history before indexing it with the diff on; this repository's CI runs
gitleaks over its own for the same reason.

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
