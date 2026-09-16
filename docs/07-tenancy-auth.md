# 07 — Tenancy and auth

## What this is, and what it is not

Dexicon is a local developer tool that may be shared by a small team. The tenancy model
gives you **separation**, not **defence against a hostile tenant**. A tenant cannot reach
another tenant's content through the API, the MCP surface, or the UI. But a tenant who can
reach the Docker socket, the Qdrant port, or the data volume can read everything, and no
amount of application-layer work changes that.

Saying so plainly is the point. A model described as "isolation" invites uses it cannot
carry. See the deployment notes in [09](09-deployment.md) for closing the infrastructure
gaps that do exist.

## The model

```
token ──▶ principal ──▶ tenant ──▶ visible corpora ──▶ Qdrant filter
                   (+ scopes)                          (enforcement)
```

### Tenant

A slug. The isolation boundary. Created in the UI or by `DEXICON__BOOTSTRAP__TENANT` on
first run. `default` is created automatically so a single-user install never has to think
about tenancy at all.

Resolution order for a request:

1. The token's bound tenant, if it is bound to exactly one.
2. The `X-Dexicon-Tenant` header, which must be one of the token's bound tenants.
3. If the token is bound to several and no header is present — **fail with 400**, listing
   the candidates.

There is no ambient or inferred tenant. This is carried straight from McpToolbox's
ADR-005: *if the caller does not specify a valid target, the request fails fast; the system
must not infer a target from ambiguous state.* That ADR exists because four incompatible
tenant-resolution philosophies once coexisted in one codebase and wrote role assignments
into the wrong tenant's table.

### Token

The only credential. No OIDC, no user accounts, no password login.

- Format `dex_<26-char ULID>_<32-byte base64url secret>`. The prefix makes it greppable by
  secret scanners; the id lets the UI show and revoke it without storing the secret.
- Stored as **PBKDF2-HMAC-SHA256, 600 000 iterations, 32-byte per-token salt**. Verified in
  constant time. Never logged, never returned after creation.
- Presented once, at creation, with a copy button and a warning. There is no "show token"
  anywhere in the UI, because there is nothing to show.
- Scopes: `search` (read + query), `ingest` (upload, trigger reindex), `admin` (tenants,
  corpora, tokens, settings).
- Optional expiry. Optional revocation, effective immediately (the principal cache holds
  entries for 60 s; revocation punches through it rather than waiting).

Verified principals are cached in memory keyed by a hash of the presented token, with a
60-second TTL, because PBKDF2 at 600k iterations on every MCP call would be the dominant
cost of a search.

### Browser auth

The SPA authenticates with the same tokens: paste one on first load, it is kept in
`sessionStorage`, and it goes out as `Authorization: Bearer`. No cookies, therefore no CSRF
surface. A single-user install reads `DEXICON__BOOTSTRAP__TOKEN` from the environment and
prints the generated token to the container log on first run.

## Corpus visibility

| `visibility` | Grants | Who can read |
|---|---|---|
| `private` | ignored | The owning tenant only. |
| `shared` | none | Every tenant in the deployment. |
| `shared` | one or more rows | The owning tenant plus the listed tenants. |

Writes — reindex, upload, delete, settings — are **always owner-only**, regardless of
visibility. Sharing is read-only sharing, and there is no setting that changes that. A
shared reference library that another tenant can silently reindex is a support call waiting
to happen.

The UI surfaces this as a per-corpus control: *Private* / *Shared with everyone* / *Shared
with…* plus a tenant picker, and the corpus list shows a badge for anything not private.

## Enforcement

One function, one place, called by every read path:

```csharp
// Returns the corpus ids the principal may read, given what they asked for.
// Throws ScopeResolutionException if the result would be empty.
IReadOnlyList<string> ResolveReadableCorpora(Principal p, string tenantId, string[]? requested);
```

Rules:

1. Start from: corpora owned by `tenantId`, plus shared corpora granted to it, plus shared
   corpora with no grants at all.
2. If `requested` is non-empty, intersect with it. A requested corpus that is not in the
   visible set is a **named error** (`Unknown corpus 'x'. Visible: …`), not a silent drop —
   silently dropping it means the caller gets confidently incomplete results.
3. **If the result is empty, throw.** There is no code path that queries Qdrant without a
   `corpus_id` filter.

The last rule is the one that matters, so it is defended three times over:

- **Application**: `ResolveReadableCorpora` throws rather than returning an empty list.
- **Repository**: the Qdrant client wrapper rejects any query whose filter lacks a
  `corpus_id` condition, with a message naming the calling method.
- **Storage**: `hnsw_config.m = 0` ([03](03-data-model.md)) means an unfiltered query has no
  index to traverse. A leak would also be a brute-force scan — detectable and slow.

### The test that proves it

A single integration test, named so nobody deletes it by accident:

```
TenantIsolation_SecondTenantCannotRetrieveFirstTenantsContent
```

Two tenants, two corpora, a distinctive string in one. It asserts the string is
unreachable via `/api/search`, via `search_index` over MCP, via `list_corpora`, via
`dexicon://` resource reads, and via `get_context` with a guessed corpus id — and that each
attempt fails with an authorization error rather than an empty result. The failure message
carries the surface that leaked. This lands in the same commit as the enforcement code,
never as a follow-up.

## Audit

Every authenticated request writes a structured log line: token id (never the secret),
tenant, surface (`api` / `mcp` / `ui`), operation, resolved corpus ids, result count,
duration. Authorization failures log at Warning with the reason.

This is a log, not a table. Persisting an audit trail to SQLite is a v2 question, and the
honest answer for v1 is that a local tool's container logs are the audit trail.

## What is deferred, and why

| Deferred | Reason |
|---|---|
| OIDC / SSO | A local dev tool with three users does not need an identity provider, and adding one would double the auth surface. The token model is a clean seam if it is ever needed. |
| Per-corpus roles beyond read/write | Two levels cover the actual use. More would be modelling for a team structure Dexicon does not have. |
| Rate limiting per token | Single-instance local tool. Add it when someone reports a problem, not before. |
| Signed/expiring share links | Requires a second token model with its own policy envelope. Deliberately not started; see McpToolbox ADR-004 for how much that grows. |
