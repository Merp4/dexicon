# Contributing

This is a small project with firm conventions in a few areas and no position on most
others. This page covers the former, since those are the parts not evident from the
code.

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
rebuilds it, and that fails while the detached dev instance holds its own DLLs. The script
performs the stop, test and restart sequence.

Enable the pre-commit hook once:

```bash
git config core.hooksPath .githooks
```

## What a good change looks like

**Record why in the code, not only what.** The codebase carries comments explaining
decisions that would otherwise look arbitrary: why `corpus_id` carries Qdrant's
`is_tenant` index, why the embedding model is a per-call argument, why a unit
boundary forces a split when nothing else does. Each of those followed a defect or a
design discussion. If a change encodes a decision, record the alternative that was
rejected.

**Tests should state their reasoning.** Several tests here begin with a note on the defect
they prevent, typically one that produced no error in production, such as an embedding
model truncating an over-long chunk and reporting success. A test named `ShouldWork` that
asserts a value gives no context when it fails later.

**A failing test is more useful than a bug report.** If you have found a defect, a test
that reproduces it is the most useful form in which to report it.

## Things this project will push back on

Each of these has been decided, with the reasoning recorded in
[docs/decisions.md](docs/decisions.md):

- **New MCP tools.** There are five, and the count is a budget: every tool definition is
  context an agent pays for on every turn ([D-11](docs/decisions.md)). New
  capability usually belongs as an argument to an existing tool, as `corpus:set` does.
- **Unreported fallbacks.** If the embedding service is unavailable, search degrades to
  keyword **and reports this in the response**. A result that is worse than the caller
  expects, without saying so, is the failure mode this project is designed to avoid.
- **Configuration nothing reads.** There is a test that fails the build for it. Two real
  settings had been configured, documented and passed by compose while nothing read them.
- **A number without a measurement.** Chunk sizes, overlaps and model choices are
  estimates until `scripts/retrieval-bench.py` establishes otherwise, and the documentation
  labels them as such. Changes are welcome; changes accompanied by the measurements are
  preferred.

## Style

- C# with `TreatWarningsAsErrors`. `dotnet format` before you commit; the hook checks.
- Comments explain *why*; the code states what.
- Timestamps are UTC everywhere: stored, returned, logged. The browser is the only thing
  that converts, because it is the only component that knows which clock applies. Any
  property holding one is named `…Utc`, and a test enforces this.
- Errors are written for whoever has to act on them. `Unknown corpus 'api'. Visible
  corpora: api-repo, rfc-library.` is actionable; a stack trace is not.

## Secrets

Never commit one. `.env` is gitignored, and `.env.example` is the only tracked template:
it ships every key **empty**. `gitleaks` runs as a pre-commit hook and as a CI job over
full history with rules for the `dex_` token format and provider API keys.

If you believe you have committed a secret, report it immediately rather than force
pushing: a secret that has reached a remote must be rotated, not concealed.

## What you index is not yours to publish

Everything here is public. What Dexicon indexes usually is not: a private library, a
third-party repository, someone's source tree. None of that belongs in a commit, a
comment, a test fixture, a changelog entry or a pull request.

Measurements from a real index are worth having, and the rule is about names rather than
numbers. Keep the figure, drop what identifies the content:

- **Yes.** "a 1,834-document corpus", "a 27,000-file repository", "an intact 84 MB PDF at
  13.4s", "the matched line was absent from 43 of 55 queries".
- **No.** Document titles, author names, publishers, the name of a third-party repository
  or project, directory layouts from a real mount, the corpus names on your own machine.

Examples in comments, docs and fixtures should be invented. Naming a real book to
illustrate two sources sharing a filename works just as well with a made-up one, and a
made-up one cannot be read as an endorsement or an association.

`IndexedContentIsNotPublishedTests` fails the build when a name that has been found once
reappears. It holds each name as a hash of its words, not the name itself, so adding one
publishes nothing; its class comment says how to compute one.

This applies most easily to the place it is easiest to forget: pasting a probe's output
into a pull request, which is exactly where raw evidence is most valuable and least
reviewed. Anonymise it there before it is published, not afterwards — an edit removes a
PR body from view, not from anyone's inbox.

## Reporting a vulnerability

Privately, through the process in [SECURITY.md](SECURITY.md), not a public issue.
