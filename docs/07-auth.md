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

- Seeded from `DEXICON__ADMIN__PASSWORD`, which is `DEXICON_ADMIN_PASSWORD` in `.env`,
  or generated and printed once to the container log on first start. The log shows it in
  double quotes, two lines below a box headed `Dexicon admin password`:
  `docker compose logs dexicon | grep -A 2 "admin password"`. A set value is applied on
  every start, which is also the way back in after a forgotten one. Nothing in the UI or
  the API changes the password.
- Stored as **PBKDF2-HMAC-SHA256, 600 000 iterations, 32-byte salt** and verified in
  constant time, in the catalogue; the environment is read at start-up, not per request.
- Posted to `POST /api/session`, which returns a `dexs_…` bearer valid for 12 hours, held
  in memory and nowhere else. A restart signs you out.

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
  secret scanners; the id lets the UI show and revoke it without storing the secret. A key
  adopted from `DEXICON__BOOTSTRAP__TOKEN` keeps the id it was given.
- Stored with the same PBKDF2 parameters as the password. Presented once, at creation, with
  a copy button. There is no "show key" anywhere, because there is nothing to show.
- Scopes: `search` reads and queries; `ingest` additionally permits a reindex of the corpora
  the key is mapped to, and uploading and attaching documents
  ([D-28](decisions.md#d-28-an-admin-password-and-scoped-api-keys)); `configure` permits creating corpora and adding or changing their
  sources and filters over MCP, and nothing that removes or deletes
  ([D-36](decisions.md#d-36-a-configure-scope-agents-set-up-what-is-indexed)); `propose`
  permits asking for a source, chunk set, document or corpus to be removed, which a person
  then approves or rejects, and removes nothing itself
  ([D-39](decisions.md#d-39-agents-ask-for-removals-and-a-person-decides)); `destroy` permits
  detaching an uploaded document from a corpus over the API, the one removal a key makes with no
  one deciding ([D-40](decisions.md#d-40-detaching-a-document-has-a-scope-of-its-own)). The scopes
  are independent, and a key holds the ones it is given: `propose` is neither implied by
  `configure` nor required for it, and `ingest` does not carry `destroy`. A key adopted from the
  environment starts with `search` and `ingest` only, and the others can be added afterwards.
  The migration `GrantDestroyToIngestKeys` gave `destroy` once to every key that held `ingest`
  at the 0.6.7 upgrade, so none of them lost the ability to detach a document; a key issued
  afterwards holds what is ticked when it is created, and the Access page ticks `search`
  alone by default. **`admin` is not issuable to a key.** `POST /api/tokens` and
  `PUT /api/tokens/{id}/scopes` answer 400 `Unknown scope` when it is requested, and the
  token service drops it from any key that reaches it another way.
- Scopes can be changed after issue, on the Access page or with
  `PUT /api/tokens/{id}/scopes`, under the same rules. The change evicts cached principals,
  so a scope removed is refused from the agent's next call.
- Optional expiry, set with `expiresInDays` on `POST /api/tokens`; the Access page does not
  offer it. Optional revocation, effective immediately: the principal cache holds
  entries for 60 s and revocation evicts rather than waiting. Each entry is filed under the
  cache's generation, which every eviction moves, so a request already verifying the key
  when it is revoked, or its scopes changed, is served on what it read and whatever it
  caches is never read by a later request.

### What a key reaches

Rows in `token_corpora`, edited in the UI under **Access**.

| Mapping | Reaches |
|---|---|
| no rows | every corpus, including ones created later |
| one or more | exactly those |

Empty means everything rather than nothing, which is what keeps a single-user install from
having to configure anything. A key that should reach nothing is **revoked**, not emptied.

The mapping does not limit `configure`. A key holding it can add any mounted folder to a
corpus it reaches, or create a corpus over that folder, and then search it, so it reaches the
whole workspace; the Access page says so when the scope is ticked. A corpus the key creates
is added to its mapping when it has one, so that it can reach what it made.

The mapping is read from the catalogue on every request and is deliberately not carried on
the cached principal: that cache has a 60-second TTL, and an operator who ticks a corpus and
watches an agent keep missing it for a minute concludes the feature is broken. Ticking a
corpus therefore lands on the agent's next call, with no restart and no change to its
configuration. That asymmetry has one exception, below.

### The MCP tool list is not live

A key without `ingest` is not shown `index_refresh` at all, one without `configure` is
not shown `list_folders`, `configure_corpus` or `configure_source`, one without `propose`
is not shown `propose_removal` or `removal_status`, and one without `search`
is not shown the four tools that read the index, rather than being refused when it calls
one: an agent that can see a tool will call it, spend a turn on the error, and
sometimes retry. `McpRequestFilters.ListToolsFilters` removes them from `tools/list`, which
carries the bearer like every other request. Each tool checks its scope when called as well.

But the transport is stateless ([D-12](decisions.md#d-12-stateless-streamable-http-mcp-2026-07-28)),
so there is no `notifications/tools/list_changed` to send, and a client lists on connect and
caches. **Granting a scope reaches an agent's tool list when its client reconnects; mapping
a corpus reaches it on the next call.** The UI says so next to the tick.

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
decides whether a caller may reindex at all, `configure` whether it may change a corpus or
its sources, `propose` whether it may ask for a removal, `destroy` whether it may detach a
document, and `ResolveWritableAsync` decides
which corpus they meant, within what they can reach. None substitutes for another. Deciding a
request is administration: the endpoints that approve or reject it require `admin`, which no
key can hold, so a key that asked cannot also approve.

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
never its value, plus method, path and status. A rejected credential (401) logs at Warning
with the reason, and a failed sign-in logs the consecutive count and the delay the next
attempt will wait. A 403 for a missing scope appears only in the request line. The bodies
of the 401 and 403 answers are in [13](13-integration.md#errors).

A rejected credential has no id or name to log, so the line carries the remote address,
the caller's user agent and eight hex characters of a SHA-256 of what was presented. That
is what separates a browser tab left open on an expired session from someone working
through a list, which would otherwise write the same line and fill a page with it. The digest is 32 bits on purpose — enough to recognise one caller
repeating, and too narrow to confirm a guess for anyone who can read the logs. The user
agent is the caller's own text, so it is capped, and every control character in it is
replaced with U+FFFD before it is logged, as they are in the request path and in a key's
name and id. The console template also escapes properties: that covers the sink it is
configured on, and the replacement covers the value wherever it is written.

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
