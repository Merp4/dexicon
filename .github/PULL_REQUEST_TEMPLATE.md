## What and why

<!-- What changes, and what problem it solves. If it encodes a decision, say what you
     rejected — the codebase is full of comments doing exactly that, and they are the
     most useful thing in it. -->

## How it was verified

<!-- Not "tests pass" — CI says that. What did you actually run, and what did you see?
     A measurement beats an assertion: this project has a benchmark harness at
     scripts/retrieval-bench.py and a model probe for exactly this reason. -->

## Checklist

- [ ] `dotnet format` is clean and the build has no warnings (they are errors here)
- [ ] Tests added for anything that could regress silently — the failures this project
      cares most about are the ones that report success
- [ ] Docs updated if behaviour changed, including `docs/decisions.md` for a load-bearing choice
- [ ] No secrets, and `.env.example` still lists every `DEXICON__*` key
- [ ] If chunking, extraction or framing changed: the relevant version bumped, so existing
      corpora re-index instead of silently keeping stale vectors
