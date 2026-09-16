# 10 — Security and secrets

The project is intended to be published. Secret hygiene that is retrofitted before a first
public push has already failed — the history is what gets scanned. These rules apply from
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
| Tenant isolation | `TenantIsolation_SecondTenantCannotRetrieveFirstTenantsContent` — see [07](07-tenancy-auth.md). |

Each guard is verified by **breaking the value and watching it go red**, then restoring it.
A guard that has never failed has not been shown to work — the failure mode is a check that
silently matches the wrong thing and reads as cover.

## Container and network posture

Covered operationally in [09](09-deployment.md); the security-relevant points:

- Non-root (UID 10001), read-only rootfs, `cap_drop: ALL`, `no-new-privileges`.
- `/workspaces` mounted **read-only** — Dexicon cannot write to your source tree.
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
- CodeQL for C# and TypeScript on PRs.
- Release builds publish an SBOM (CycloneDX) and pin base images by digest.
- Every third-party extraction library is permissively licensed and listed with its licence
  in [04](04-ingestion.md#extraction) — a table that exists to be checked, not admired.

## Repository files, present at first push

`SECURITY.md` (how to report, expected response time, supported versions) ·
`LICENSE` ([D-14](decisions.md#d-14-licence)) · `CONTRIBUTING.md` ·
`CODE_OF_CONDUCT.md` · `.github/workflows/ci.yml` · `.github/dependabot.yml` ·
`.gitleaks.toml` · `.githooks/pre-commit`.

## Threat model, stated honestly

**Defended:** one tenant reading another's content through the API, MCP, UI, or a guessed
identifier. Secrets reaching the repository, the logs, or an error response. Dexicon writing
to your source tree. A malformed document taking the service down.

**Not defended:** anyone with access to the Docker socket, the data volume, or a published
Qdrant port. A malicious tenant with a valid `admin` token. Side channels — timing,
per-corpus chunk counts — that might reveal that content exists without revealing what it
is.

Dexicon is a local developer tool with tenant separation. It is not a multi-tenant SaaS
boundary, and describing it as one would invite uses it cannot carry.
