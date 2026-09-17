# Security

## Reporting a vulnerability

Report privately through GitHub's **[Report a vulnerability](https://github.com/Merp4/dexicon/security/advisories/new)**
form. Please do not open a public issue for a security problem.

Include what you did, what happened, and what you expected. A proof of concept helps but is
not required — a clear description of the mechanism is worth more than a working exploit.

**What to expect.** An acknowledgement within 3 working days and an assessment within 10.
If it is a real issue you will get a fix or a dated plan, and credit in the advisory unless
you would rather not be named. If it is not, you will get the reasoning rather than
silence.

This is a small project maintained by one person. Those times are a genuine commitment, not
an SLA backed by a rota.

## Supported versions

The latest released tag, and `main`. There are no long-term support branches; the project
is young enough that "upgrade" is a realistic answer.

## What Dexicon defends against

Stated plainly, because a security policy that implies more than it delivers is worse than
one that admits its limits. The full version is in
[docs/10-security-secrets.md](docs/10-security-secrets.md#threat-model).

**Defended.** One tenant reading another's content through the API, MCP, UI, or a guessed
identifier. Secrets reaching the repository, the logs, or an error response. Dexicon writing
to your source tree — workspace mounts are read-only. A malformed document taking the
service down.

**Not defended.** Anyone with access to the Docker socket, the data volume, or a published
Qdrant port. A malicious tenant holding a valid `admin` token. Side channels — timing,
per-corpus chunk counts — that might reveal that content exists without revealing what it
is.

**Dexicon is a local developer tool with tenant separation. It is not a multi-tenant SaaS
boundary**, and describing it as one would invite uses it cannot carry. If you are
considering exposing it to untrusted users, that is the sentence to read twice.

## Deployment expectations

Dexicon assumes it is reachable only by people you trust:

- The app binds to `127.0.0.1` by default. Qdrant and Ollama publish **no ports at all** —
  Qdrant's stock configuration has no authentication, so a published `6333` is an open
  read/write door to every tenant's content regardless of what the application enforces.
- API tokens are stored as PBKDF2-HMAC-SHA256 with a per-token salt. The secret is shown
  once at creation and has no retrieval path. A lost token is replaced, not recovered — or
  recovered through `DEXICON_BOOTSTRAP_TOKEN`, which is an escape hatch documented in
  [docs/09-deployment.md](docs/09-deployment.md).
- Embedding provider API keys are read from the environment, never from the catalogue.
  Configuration names the variable; the value stays outside the file.

If you put Dexicon behind a reverse proxy on a shared network, the tenant header
(`X-Dexicon-Tenant`) becomes something the proxy must control. Dexicon trusts the token
first — a header asking for a tenant the token does not own is refused — but the
deployment is yours to reason about.

## Things that are not vulnerabilities

So that a report is not wasted work:

- **The default Qdrant API key in `docker-compose.yml`.** It is not a secret, is documented
  as not being one, and exists because an *empty* `QDRANT__SERVICE__API_KEY` turns
  authentication on with a key nothing can present. The port is never published.
- **Search results revealing that content exists.** Within a tenant, that is the product.
- **An `admin` token doing administrative things.** Scopes are a capability boundary, not a
  defence against the holder.
