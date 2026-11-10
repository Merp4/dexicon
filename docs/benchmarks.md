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

**What is measured:** the rank of the file that should answer each query. This measures
retrieval position only, not relevance or passage quality. MRR is the mean of `1/rank`, so
1.000 means every query returned the correct file first.

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

Defaults at the time of the run: `nomic-embed-text` / 768 / `language-aware` / `hybrid`,
**MRR 0.744**, 34 of 55 first. That ranks **30th of 81**. The defaults since then are
`embeddinggemma` / 256 tokens / `language-aware` / `hybrid` ([04](04-ingestion.md#defaults),
[D-31](decisions.md#d-31-a-chunk-is-an-index-entry-and-the-model-decides-how-big-it-can-be)).

## What that does and does not justify

**Hybrid is justified by the results.** It leads on mean for every model, by a larger
margin than any other choice in the table, and has the highest floor. Semantic produces
both the single best configuration and the worst, making it the higher-variance option; a
default should minimise the worst case.

| | hybrid | semantic | keyword |
|---|---|---|---|
| `embeddinggemma` | 0.797 | 0.764 | 0.676 |
| `nomic-embed-text` | 0.771 | 0.712 | 0.680 |
| `mxbai-embed-large` | 0.725 | 0.669 | 0.677 |

**`language-aware` is not earned, on either corpus.** It is last of the three boundary
modes on documents (0.705) and last again on code (0.649), the corpus it was written
for. The spread on code is 0.009 across all three modes, which is nothing: the
boundary mode is the least load-bearing setting in the sweep. It remains the default
because changing it costs a reindex and buys a rounding error, not because it won.

**768 is not obviously right, and 256 is not obviously better.** Smaller wins on mean by
0.027, which is inside the noise of 55 queries. Note also that smaller chunks flatter this
metric: a file split finer has more chances to land one chunk in the top ten.

**`mxbai-embed-large`'s numbers are not a fair reading of the model.** It accepts 2,816
characters. At the time of the run the chunker converted tokens to characters at a flat 4, so
768 tokens produced 3,072 characters and 1536 produced 6,144. Two
thirds of its rows measure truncation rather than retrieval quality, and the degradation
across sizes (0.714 → 0.687 → 0.668) reflects that. This is the clearest result in the
sweep, and it is a finding about the defaults rather than about the
model: **the default chunk size then truncated every full-size chunk on a model the UI
offers in a dropdown.**

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
0.829**, 39 of 52 first. This is `mxbai` at the one swept size its 2,816-character limit
does not compromise.

**Keyword retrieval is considerably weaker on code than on prose**, 0.579 against 0.677. An
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
touching anything else. Its mid-table position on code is accepted: the configurations
above it lead by margins within the noise of 52 queries, and several do so for reasons
that do not generalise. A 256-token size flatters the metric, because a file split finer
has more chances
to land a chunk in the top ten, and the single best code configuration is `mxbai` at the
one size its character limit does not ruin. A default should be the thing that is hardest
to be badly wrong with.

The default chunk size is no longer 768. It is 256 tokens, and a chunk set's size is capped
at 90% of what its model was measured to read, so the `mxbai` truncation described above is
not reachable from the defaults.

## What this is not

Two corpora, both this repository, ~107 queries written by someone who already knew them.
That makes it a fair comparison **between configurations** and a poor estimate of absolute
quality. Nothing here measures the usefulness of a retrieved passage, only whether
retrieval returned the correct file.

Reproduce with:

```bash
python scripts/bench/sweep.py --dry-run                     # the plan and the cost
python scripts/bench/sweep.py                               # documents, ~12 minutes on a GPU
python scripts/bench/sweep.py --queries queries-code.json   # code
```

`--queries` names a file in `scripts/bench/`. The script builds its own corpus
(`bench-sweep`) and deletes it afterwards. Raw per-configuration numbers are in
`scripts/bench/results-docs.json` and `scripts/bench/results-code.json`.

Creating and deleting a corpus need the `admin` scope, which no API key can hold, so
`sweep.py` signs in with `DEXICON_ADMIN_PASSWORD` (from the environment or `.env`), or takes
a session bearer in `DEXICON_TOKEN_ADMIN`. `scripts/bench/passages.py` does the same.


## Comparing models directly (2026-11-09)

The sweep above goes through Dexicon, builds chunk sets on a throwaway corpus and needs the admin
session. `scripts/bench/modelbench.py` asks a narrower question without them: given the same chunks
and the same queries, which model, and which text wrapped around each chunk, ranks the right file
higher? It talks to Ollama directly, splits `docs/` and `src/` on blank lines into 623 and 2,145 chunks
of about 1,000 characters, embeds with the indexer's batch size of 32 and four requests in flight, and
scores the 55 and 52 queries the sweep uses. It is dense only, with no sparse leg and no fusion, so its
absolute numbers differ from the sweep's hybrid ones and only differences between arms are read.

Measured on tree `a948496` (docs and src identical to v0.6.13) with Ollama 0.40.15, on a GPU shared with
another training workload. The predictions written before each round are in
`scripts/bench/modelbench-predictions-2026-11-09.md`; the results are in
`scripts/bench/results-models-2026-11-09.json`. Two predictions missed in ways that matter here: the
second generation was expected to gain on code (45%) and did not, and the first wrong-path title on
code was expected to give no gain (90%) and gave the largest one (+0.070). Ten seeded repeats of that
arm did not reproduce it (below).

| arm | docs MRR | code MRR |
|---|---|---|
| `embeddinggemma`, its card's framing (the default) | 0.723 | 0.674 |
| `embeddinggemma`, no framing | 0.664 | 0.689 |
| `embeddinggemma` with `title: {file path}` | 0.779 | 0.728 |
| `embeddinggemma` with another file's path as the title, mean of ten draws | 0.730 | 0.662 |
| `embeddinggemma-2` Q8_0, framed | 0.666 | 0.660 |
| `embeddinggemma-2` Q8_0 with the file path as title | 0.742 | 0.680 |
| `embeddinggemma-2` Q8_0 with the title and the code-retrieval query prompt | 0.724 | 0.705 |
| `embeddinggemma-2` nvfp4 (MLX), framed | 0.650 | 0.617 |
| `nomic-embed-text`, framed | 0.606 | 0.543 |

A second run of the first row gave identical figures and a cosine of 1.0000 for every chunk vector,
so the harness adds no noise of its own. Differences below are the first arm minus the
second, in mean reciprocal rank, with a 95% interval from a paired bootstrap over the queries:

| comparison | docs | code |
|---|---|---|
| `embeddinggemma-2` Q8_0 minus `embeddinggemma` | -0.057 [-0.165, +0.049] | -0.014 [-0.101, +0.073] |
| the same with a title on both | -0.037 [-0.125, +0.048] | -0.048 [-0.116, +0.024] |
| the same with the title and the code-retrieval prompt on `embeddinggemma-2` | -0.056 [-0.147, +0.036] | -0.023 [-0.111, +0.066] |
| nvfp4 minus Q8_0, `embeddinggemma-2` | -0.016 [-0.067, +0.031] | -0.043 [-0.082, -0.012] |
| `nomic-embed-text` minus `embeddinggemma` | -0.117 [-0.215, -0.026] | -0.131 [-0.237, -0.026] |
| `embeddinggemma`, title minus no title | +0.057 [+0.001, +0.118] | +0.054 [+0.008, +0.106] |
| `embeddinggemma`, wrong-path title minus no title, mean of ten draws (range) | +0.008 (-0.020 to +0.049) | -0.011 (-0.040 to +0.008) |
| `embeddinggemma`, true-path title minus wrong-path title, mean of ten draws (range) | +0.049 (+0.008 to +0.076) | +0.066 (+0.046 to +0.094) |
| `embeddinggemma`, no framing minus framing | -0.059 [-0.127, +0.006] | +0.015 [-0.051, +0.082] |

**`embeddinggemma-2` is not shown to be better than `embeddinggemma`.** Under equal treatment every
interval includes zero and the point estimates favour the first generation by 0.01 to 0.06. The model
card reports gains on MTEB (multilingual v2 61.36 against 61.15, code 78.68 against 68.76, at full
precision); they do not appear on file-level lookups here. Either difference could explain that: this
is Q8_0, and MTEB's code task is not a natural-language query for a file. With 55 and 52 queries a
gap under about 0.1 cannot be resolved, so this supports "no evidence of a gain", not "worse". The
nvfp4 build is measurably worse than Q8_0 on code, and `nomic-embed-text` is worse than
`embeddinggemma` on both corpora, as in the September sweep.

**A title in the document text is a larger effect than the choice of model.** `title: {path}` in place
of `title: none` lifts `embeddinggemma` by about 0.055 on both corpora, the card's own recommendation
for documents that have a title. The placebo separates two causes: the title form, and the information
in the path. Ten draws gave each chunk the path of another, randomly chosen chunk, each draw a fixed
permutation. They scored 0.703 to 0.772 on docs and 0.634 to 0.682 on code, against 0.779 and 0.728 for
the true path and 0.723 and 0.674 for no title. The true path beat every draw on both corpora, and the
draws were no better than no title on average, so the gain comes from the path text and not from the
title form. A single draw does not resolve this at 55 and 52 queries: the interval on the difference
from the true path excludes zero in 4 of the 10 draws on docs and 3 of 10 on code. One further draw,
from an earlier version of the script that read files in directory order, scored 0.731 on docs and 0.744
on code. On code that is above the true path. It kept no more chunks on their own file's path than the
seeded draws did (2.1% of 2,145 against 1.3% to 2.0%), and its cause is not known. The metric scores
which file a query is about, and the files are descriptively named, so read the figure as an upper
bound for queries that do not echo a file name. It has not been run through the app's hybrid search.
Dexicon's framing templates carry `{text}` only, so a title cannot be expressed today.

**Framing matters less than the September report suggested.** Unframed `embeddinggemma` is 0.059
lower on docs and 0.015 higher on code, against 0.632 to 0.799 on twelve queries in hybrid mode in
[decisions.md](decisions.md#q3-second-measurement--with-task-framing).

Speed was measured on the same shared GPU and varied more than the models did: `embeddinggemma` ran
at 13 to 39 chunks per second over 28 corpus runs, the identical first row and its repeat between 19 and
30, and `embeddinggemma-2` Q8_0 at 7 to 25 over six. At those speeds, re-embedding a deployment of about
258,000 chunks takes 2 to 5 hours with the first generation and 3 to 10 with the second.

Reproduce with:

```bash
git archive a948496 docs src scripts/bench | tar -x -C /tmp/tree
python scripts/bench/modelbench.py --list
python scripts/bench/modelbench.py --root /tmp/tree --ollama http://127.0.0.1:21434     --arms v1_framed,v1_title,eg2_q8 --out modelbench-out.json
```

The wrong-path arms are `v1_title_placebo_s1` to `v1_title_placebo_s10`. A seed fixes one permutation
of the chunks taken in sorted file order, so the same seed on a tree with different files is a
different draw.

`--root` matters: this corpus is the repository, so a later change to `docs/` changes the docs
numbers. Ollama's tags move. The `embeddinggemma-2:270m` tag measured as nvfp4 on 2026-10-08 now
resolves to a GGUF build, so the MLX arm needs `--model eg2_nvfp4=embeddinggemma-2:270m-nvfp4-text`.
The Q8_0 arm used the ggml-org file (`hf.co/ggml-org/embeddinggemma-2-GGUF:Q8_0`), which is not the same
file as the one Ollama now serves as `embeddinggemma-2:270m`; the official build has not been measured.
Dexicon's own Ollama publishes no port, so run the script from a container on its network or with
`docker-compose.debug.yml`.
