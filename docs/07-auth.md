# 07 — Auth

## What this is, and what it is not

Dexicon is a local developer tool that may be shared by a small team. What the auth model
gives you is **scoping**, not **separation**. A key reaches the corpora it is mapped to, and
naming one it cannot reach is a named error rather than an empty result. But anyone who can
reach the Docker socket, the Qdrant port, or the data volume reads everything, and no amount
of application-layer work changes that. The admin password reaches every corpus by design,
because the UI has to list one in order to map a key to it.

This is stated explicitly because a model described as "isolation" invites uses it cannot
support. See the deployment notes in [09](09-deployment.md) for closing the infrastructure
gaps that do exist.

## The model

```
password ──▶ session ──▶ admin, every corpus
                                                    ┌──▶ Qdrant filter
key ──────▶ principal ──▶ mapped corpora ───────────┘   (enforcement)
             (+ scopes)
```

Two credentials, and they do different jobs.

### The admin password

One password for the install. It is the only route to the `admin` scope, so nothing that
can delete a corpus or mint a key ever sits in an agent's configuration file.

- Seeded from `DEXICON__ADMIN__PASSWORD`, or generated and printed once to the container log
  on first run. Set, it is applied on every start, which is also the way back in after a
  forgotten one.
- Stored as **PBKDF2-HMAC-SHA256, 600 000 iterations, 32-byte salt**, verified in constant
  time, in the catalogue rather than read from the environment per request, so it can be
  changed in the UI without a restart.
- Posted to `POST /api/session`, which returns a short-lived `dexs_…` bearer held in memory
  and nowhere else. A restart signs you out.

It is the first credential here a human chooses, so it is the first that can be guessed.
Failed attempts are throttled by a delay that doubles from the third attempt and caps at 30
seconds, counted **globally**: there is one password, so there is one thing to guess, and in
a single container every caller arrives from the same gateway address, which makes the
source address useless as a discriminator. A delay and not a lockout, because with one
shared credential a lockout is a denial of service that anyone able to reach the port could
inflict on the owner. The counter resets on success and lives in memory, so a restart clears
it; a restart needs host access, which already defeats this model.

### API keys

How an agent authenticates. One per agent is the intended shape, because the corpus mapping
belongs to the key.

- Format `dex_<26-char ULID>_<32-byte base64url secret>`. The prefix makes it greppable by
  secret scanners; the id lets the UI show and revoke it without storing the secret.
- Stored with the same PBKDF2 parameters as the password. Presented once, at creation, with
  a copy button. There is no "show key" anywhere, because there is nothing to show.
- Scopes: `search` reads and queries; `ingest` additionally permits a reindex of the corpora
  the key is mapped to. **`admin` is not issuable to a key** and is stripped if requested.
- Optional expiry. Optional revocation, effective immediately: the principal cache holds
  entries for 60 s and revocation evicts rather than waiting.

### What a key reaches

Rows in `token_corpora`, edited in the UI under **Access**.

| Mapping | Reaches |
|---|---|
| no rows | every corpus, including ones created later |
| one or more | exactly those |

Empty means everything rather than nothing, which is what keeps a single-user install from
having to configure anything. A key that should reach nothing is **revoked**, not emptied.

The mapping is read from the catalogue on every request and is deliberately not carried on
the cached principal: that cache has a 60-second TTL, and an operator who ticks a corpus and
watches an agent keep missing it for a minute concludes the feature is broken. Ticking a
corpus therefore lands on the agent's next call, with no restart and no change to its
configuration. That asymmetry has one exception, below.

### The MCP tool list is not live

A key without `ingest` is not shown `index_refresh` at all, rather than being refused when
it calls it: an agent that can see a tool will call it, spend a turn on the error, and
sometimes retry. `McpRequestFilters.ListToolsFilters` removes it from `tools/list`, which
carries the bearer like every other request.

But the transport is stateless ([D-12](decisions.md#d-12-stateless-streamable-http-mcp-2026-07-28)),
so there is no `notifications/tools/list_changed` to send, and a client lists on connect and
caches. **Granting `ingest` reaches an agent when its client reconnects; mapping a corpus
reaches it on the next call.** The UI says so next to the tick.

### Browser auth

The SPA holds the session bearer in `sessionStorage` and sends it as
`Authorization: Bearer`. No cookies, therefore no CSRF surface, and the bearer dies with the
tab rather than outliving the person using it.

## Enforcement

One function, one place, called by every read path:

```csharp
// The corpora this principal may read, given what they asked for.
// Throws ScopeResolutionException if the result would be empty.
Task<ResolvedScope> ResolveReadableAsync(Principal principal, IReadOnlyList<string>? requested);
```

Rules:

1. Start from: every corpus for an admin session; for a key, the corpora mapped to it, or
   every corpus when nothing is mapped.
2. If `requested` is non-empty, intersect with it. A requested corpus that is not in the
   reachable set produces a **named error** (`Unknown corpus 'x'. Corpora this key can
   reach: …`) rather than being dropped. Dropping it would return incomplete results without
   indicating so.
3. **If the result is empty, throw.** There is no code path that queries Qdrant without a
   `corpus_id` filter.

The last rule is the one that matters, so it is defended three times over:

- **Application**: `ResolveReadableAsync` throws rather than returning an empty scope.
- **Repository**: the Qdrant client wrapper raises `UnscopedQueryException` for any query
  whose filter lacks a `corpus_id` condition, naming the calling method.
- **Storage**: `hnsw_config.m = 0` ([03](03-data-model.md)) means an unfiltered query has no
  index to traverse. A leak would therefore require a brute-force scan, which is slow and
  detectable.

Writes are governed by the scope guard at the endpoint and the same resolution: `ingest`
decides whether a caller may reindex at all, and `ResolveWritableAsync` decides which corpus
they meant, within what they can reach. Neither substitutes for the other.

### The tests that prove it

`KeyScopingTests`, named so nobody deletes them by accident. A key mapped to one corpus sees
only that one; naming another, by name or by guessed id, is a `ScopeResolutionException` and
not an empty list; an unmapped key sees everything; editing a mapping changes what the same
principal resolves to without the key being reissued; an empty scope throws; and the vector
store refuses a query with no corpus filter.

`AdminPasswordTests` covers the password round trip, that changing it invalidates the old
one, that a key requesting `admin` does not get it, and that the throttle doubles, caps and
resets.

## Audit

Every authenticated request writes a structured log line: the credential's id and name,
never its value, plus method, path and status. Authorization failures log at Warning with
the reason, and a failed sign-in logs the consecutive count and the delay the next attempt
will wait.

This is a log, not a table. Persisting an audit trail to SQLite is a v2 question, and the
answer for v1 is that a local tool's container logs are the audit trail. A mapping change is
an ordinary authenticated request and lands on that line, but a log is not a diff and cannot
be replayed onto a fresh machine
([D-28](decisions.md#d-28-an-admin-password-and-scoped-api-keys)).

## What is deferred, and why

| Deferred | Reason |
|---|---|
| OIDC / SSO | A local dev tool with three users does not need an identity provider, and adding one would double the auth surface. The password and key model is a clean seam if it is ever needed. |
| More than one admin | Several passwords are user accounts, which [D-10](decisions.md#d-10-static-tokens-and-a-tenant-header) rejected and D-28 did not reinstate. |
| A named mapping several keys share | One entity and two join tables to hold a list each key can hold directly. Worth adding when keys start being kept in sync by hand. |
| Per-corpus write ownership | A corpus has no owner; `ingest` governs reindexing over whatever a key reaches. Worth revisiting if an endpoint is ever shared across a team. |
| Rate limiting per key | A key is a 32-byte secret and is not guessable, and throttling one would let anyone degrade agent traffic by presenting bad bearers. The password is throttled because it is chosen. |
| An export of the mapping | Catalogue rows are not in version control. Worth adding if a deployment has to be reproducible from configuration. |
