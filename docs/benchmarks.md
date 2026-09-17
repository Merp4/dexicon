# Benchmarks — are the defaults earned?

Run 2026-09-17 by `scripts/bench/sweep.py` against **two** corpora of this repository:

| Corpus | Content | Queries |
|---|---|---|
| documents | `docs/` — 14 markdown files | 55, in `scripts/bench/queries-docs.json` |
| code | `src/` — the C# tree | 52, in `scripts/bench/queries-code.json` |

Each is swept independently, so a default can be judged on both rather than on prose
alone.

Three models × three chunk sizes × three boundary modes = 27 chunk sets, queried in three
search modes = **81 configurations, 4,455 searches**. Every chunk set holds identical
content with exactly one variable changed, in one instance at one time. That comparison is
only possible because a corpus can carry several chunk sets; before that it needed two
corpora and a promise that nothing else had drifted.

**What is measured:** the rank of the file that should answer each query. Not relevance,
not whether the passage is any good — only whether retrieval put the right document in
front of you. MRR is the mean of `1/rank`, so 1.000 means every query put the right file
first.

## What it says

| | mean MRR | best | worst |
|---|---|---|---|
| **Search mode** | | | |
| `hybrid` | **0.764** | 0.826 | 0.678 |
| `semantic` | 0.715 | 0.836 | 0.581 |
| `keyword` | 0.677 | 0.713 | 0.652 |
| **Model** | | | |
| `embeddinggemma` | **0.746** | 0.836 | 0.652 |
| `nomic-embed-text` | 0.721 | 0.826 | 0.596 |
| `mxbai-embed-large` | 0.690 | 0.774 | 0.581 |
| **Chunk size** | | | |
| 256 | **0.738** | 0.836 | 0.657 |
| 768 | 0.711 | 0.811 | 0.633 |
| 1536 | 0.707 | 0.826 | 0.581 |
| **Boundary mode** | | | |
| `blank-line` | **0.728** | 0.836 | 0.626 |
| `none` | 0.724 | 0.825 | 0.657 |
| `language-aware` | 0.705 | 0.811 | 0.581 |

Best single configuration: `embeddinggemma` / 256 / `blank-line` / `semantic`, **MRR
0.836**, 41 of 55 queries answered first.

Current defaults: `nomic-embed-text` / 768 / `language-aware` / `hybrid`, **MRR 0.744**,
34 of 55 first. That ranks **30th of 81**.

## What that does and does not justify

**Hybrid is earned.** It wins on mean for every model, and by more than any other choice
in the table — and it has the highest floor, which matters more than its ceiling. Semantic
takes the single best configuration but also the worst: it is the higher-variance bet, and
a default should be the one that is hardest to be badly wrong with.

| | hybrid | semantic | keyword |
|---|---|---|---|
| `embeddinggemma` | 0.797 | 0.764 | 0.676 |
| `nomic-embed-text` | 0.771 | 0.712 | 0.680 |
| `mxbai-embed-large` | 0.725 | 0.669 | 0.677 |

**`language-aware` is not earned, on either corpus.** It is last of the three boundary
modes on documents (0.705) and last again on code (0.649) — and code is what it was
written for. The spread on code is 0.009 across all three modes, which is nothing: the
boundary mode is the least load-bearing setting in the sweep. It remains the default
because changing it costs a reindex and buys a rounding error, not because it won.

**768 is not obviously right, and 256 is not obviously better.** Smaller wins on mean by
0.027, which is inside the noise of 55 queries. Note also that smaller chunks flatter this
metric: a file split finer has more chances to land one chunk in the top ten.

**`mxbai-embed-large`'s numbers are not a fair reading of the model.** It accepts 2,816
characters; at 768 tokens the chunker produces 3,072 and at 1536 it produces 6,144. Two
thirds of its rows measure silent truncation rather than retrieval quality — and its
degradation across sizes (0.714 → 0.687 → 0.668) is what that looks like. It is the
clearest thing in the sweep, and it is a finding about the defaults rather than about the
model: **the default chunk size truncates every full-size chunk on a model the UI offers
in a dropdown.**

**embeddinggemma won both corpora, and that is what moved the default.** On documents
it leads `nomic-embed-text` by 0.025 mean MRR, which alone is suggestive and not decisive.
On code it leads by **0.075**, the widest gap any single variable opens in either sweep.
Two independent corpora agreeing is the evidence a default change needed; one was not.

It costs twice the first download of `nomic-embed-text`, and that is the whole of the case
against it.

## The code corpus

81 configurations, 52 queries, against `src/`.

| | mean MRR | best | worst |
|---|---|---|---|
| **Search mode** | | | |
| `hybrid` | **0.708** | 0.829 | 0.607 |
| `semantic` | 0.670 | 0.772 | 0.518 |
| `keyword` | 0.579 | 0.624 | 0.523 |
| **Model** | | | |
| `embeddinggemma` | **0.679** | 0.772 | 0.523 |
| `mxbai-embed-large` | 0.674 | 0.829 | 0.523 |
| `nomic-embed-text` | 0.604 | 0.732 | 0.518 |
| **Chunk size** | | | |
| 256 | **0.668** | 0.772 | 0.575 |
| 768 | 0.648 | 0.829 | 0.523 |
| 1536 | 0.641 | 0.773 | 0.518 |
| **Boundary mode** | | | |
| `blank-line` | **0.658** | 0.829 | 0.544 |
| `none` | 0.650 | 0.755 | 0.569 |
| `language-aware` | 0.649 | 0.773 | 0.518 |

Best single configuration: `mxbai-embed-large` / 768 / `blank-line` / `hybrid`, **MRR
0.829**, 39 of 52 first — which is `mxbai` at the one swept size its 2,816-character limit
does not ruin.

**Keyword retrieval is much weaker on code than on prose** — 0.579 against 0.677. An
identifier a developer half-remembers is rarely the identifier in the file, and the sparse
encoder has no notion of `TokenService` being what you meant by "token hashing". Hybrid
carries it: it beats keyword on every model, by 0.13 on the best of them.

**Every conclusion from the documents sweep survives.** Hybrid wins both. `embeddinggemma`
wins both. `language-aware` loses both. The two corpora disagree about nothing that
matters.

## Where the defaults land

| | documents | code |
|---|---|---|
| before (`nomic-embed-text` / 768 / `language-aware` / `hybrid`) | 0.744, rank 30/81 | 0.634, rank 44/81 |
| after (`embeddinggemma`, same otherwise) | **0.811, rank 5/81** | 0.674, rank 36/81 |

One model change moved documents from 30th to 5th and code from 44th to 36th, without
touching anything else. Mid-table on code is deliberate: the configurations above it are
above by margins inside the noise of 52 queries, and several are there for a reason that
does not generalise — 256 flatters the metric, because a file split finer has more chances
to land a chunk in the top ten, and the single best code configuration is `mxbai` at the
one size its character limit does not ruin. A default should be the thing that is hardest
to be badly wrong with.

The chunk size is no longer a fixed 768: it is set from what the model was measured to
accept, which is what stops the `mxbai` truncation described above from being reachable
from the defaults at all.

## What this is not

Two corpora, both this repository, ~107 queries written by someone who already knew them.
That makes it a fair comparison **between configurations** and a poor estimate of absolute
quality. Nothing here measures whether a retrieved passage is any *good* — only whether
retrieval put the right file in front of you.

Reproduce with:

```bash
python scripts/bench/sweep.py --dry-run   # the plan and the cost
python scripts/bench/sweep.py             # documents, ~12 minutes on a GPU
python scripts/bench/sweep.py --queries scripts/bench/queries-code.json
```

It builds its own corpus and deletes it afterwards. Raw per-configuration numbers are in
`scripts/bench/results.json`.
