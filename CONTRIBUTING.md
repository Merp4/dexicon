# Contributing

Thanks for looking. This is a small project with strong opinions about a few things and no
opinion at all about most — this page is mostly the first category, because that is the
part you cannot guess from the code.

## Getting it running

```bash
git clone https://github.com/Merp4/dexicon && cd dexicon
cp .env.example .env
docker compose up -d
docker compose logs dexicon | grep "bootstrap token"
```

For the inner loop, run the app on the host against the containerised dependencies:

```bash
./scripts/dev.ps1 up        # dependencies, build, run
./scripts/dev.ps1 test      # stop, test, restart if it was running
./scripts/dev.ps1 restart   # after a code change
```

`dev.ps1 test` exists because the test project references the host, so `dotnet test`
rebuilds it — and that fails while the detached dev instance holds its own DLLs. The script
does the stop/test/start dance so you do not have to remember it.

Enable the pre-commit hook once:

```bash
git config core.hooksPath .githooks
```

## What a good change looks like

**Say why in the code, not just what.** The codebase is dense with comments explaining
decisions that look arbitrary: why `corpus_id` is the Qdrant tenant key and not
`tenant_id`, why the embedding model is a per-call argument, why a unit boundary forces a
split when nothing else does. Every one of those was a bug or an argument once. If your
change encodes a decision, write down the alternative you rejected.

**Tests carry the reasoning.** Several tests in this repository open with a paragraph about
the defect they exist to prevent — usually one that was invisible in production, like an
embedding model silently truncating an over-long chunk and reporting success. A test named
`ShouldWork` that asserts a value teaches nothing when it fails in two years.

**Prefer a failing test to a bug report.** If you have found something, a test that goes red
is the most useful possible form of it.

## Things this project will push back on

Not to be difficult — these have each been decided, and the reasoning is in
[docs/decisions.md](docs/decisions.md):

- **New MCP tools.** There are five and the count is a budget, not an accident: every tool
  definition is context an agent pays for on every turn ([D-11](docs/decisions.md)). New
  capability usually belongs as an argument to an existing tool, the way `corpus:set` did.
- **Silent fallbacks.** If the embedding service is down, search degrades to keyword **and
  says so in the response**. A result that is quietly worse than the caller expects is the
  failure mode this project exists to avoid.
- **Configuration nothing reads.** There is a test that fails the build for it. Two real
  settings had been configured, documented and passed by compose while nothing read them.
- **A number without a measurement.** Chunk sizes, overlaps and model choices are guesses
  until `scripts/retrieval-bench.py` says otherwise, and the docs label them as guesses.
  Changing one is welcome; changing one *and* posting the numbers is much better.

## Style

- C# with `TreatWarningsAsErrors`. `dotnet format` before you commit; the hook checks.
- Comments explain *why*. The code already says what.
- Timestamps are UTC everywhere — stored, returned, logged. The browser is the only thing
  that converts, because it is the only thing that knows whose clock to use. Any property
  holding one is named `…Utc`, and a test enforces that.
- Errors are written for whoever has to act on them. `Unknown corpus 'api'. Visible
  corpora: api-repo, rfc-library.` is actionable; a stack trace is not.

## Secrets

Never commit one. `.env` is gitignored and `.env.example` is the only tracked template —
it ships every key **empty**. `gitleaks` runs as a pre-commit hook and as a CI job over
full history with rules for the `dex_` token format and provider API keys.

If you believe you have committed a secret, say so immediately rather than quietly force
pushing: a secret that reached a remote is a secret to rotate, not to hide.

## Reporting a vulnerability

Privately, through the process in [SECURITY.md](SECURITY.md) — not a public issue.
