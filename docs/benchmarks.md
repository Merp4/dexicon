# Benchmarks — are the defaults earned?

Run 2026-09-17 by `scripts/bench/sweep.py` against the `docs/` corpus of this repository:
14 markdown files, 55 (query, expected file) pairs in `scripts/bench/queries-docs.json`.

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

**`language-aware` is not earned as the default for prose.** It is last of the three
boundary modes here, and this corpus is entirely markdown — a format whose real boundaries
are blank lines, which is exactly what `blank-line` splits on. This says nothing about
code, which is what `language-aware` was written for, and the sweep has not been run over
a code corpus. Do not change the default on this evidence; do change it for document
corpora.

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

**embeddinggemma edges nomic-embed-text by 0.025 mean MRR.** With 55 queries that is
suggestive and not decisive. It is consistent with the two earlier hand-run comparisons,
which also favoured gemma. It is not enough on its own to move a default that costs a
full reindex to change.

## What this is not

One corpus, one content type, 55 queries written by someone who already knew the corpus.
That makes it a fair comparison **between configurations** and a poor estimate of absolute
quality. `docs/11-roadmap.md` asks for a code corpus as well, and until that exists the
only default this justifies changing is one that is wrong for documents specifically.

Reproduce with:

```bash
python scripts/bench/sweep.py --dry-run   # the plan and the cost
python scripts/bench/sweep.py             # ~12 minutes on a GPU
```

It builds its own corpus and deletes it afterwards. Raw per-configuration numbers are in
`scripts/bench/results.json`.
