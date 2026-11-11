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
absolute numbers differ from the sweep's hybrid ones and only differences between arms are read. A line
over 1,400 characters is cut at 1,400: that is two lines of `docs/10-security-secrets.md` (2,249 characters)
and none under `src/`, and every arm sees the same chunks.

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

**A title in the document text lifts the score by at least as much as the choice between the two generations changes it.** `title: {path}` in place
of `title: none` lifts `embeddinggemma` by about 0.055 on both corpora, the card's own recommendation
for documents that have a title. The placebo separates two causes: the title form, and the information
in the path. Ten draws gave each chunk the path of another, randomly chosen chunk, each draw a fixed
permutation. They scored 0.703 to 0.772 on docs and 0.634 to 0.682 on code, against 0.779 and 0.728 for
the true path and 0.723 and 0.674 for no title. The true path beat every draw on both corpora, and the
draws were no better than no title on average, so the gain comes from the path text and not from the
title form. A single draw does not resolve this at 55 and 52 queries: the interval on the difference
from the true path excludes zero in 4 of the 10 draws on docs and 3 of 10 on code. One further draw,
from an earlier version of the script that read files in directory order, scored 0.731 on docs and 0.744
on code. On code that is above the true path. It left 2.1% of its 2,145 chunks on their own file's path,
just above the seeded draws' 1.3% to 2.0%, but 25 of the 892 chunks of the files the queries ask about kept
their own path, inside the seeded draws' 14 to 26, and the draws with 26 and 25 scored 0.675 and 0.663. A leak
of the true path does not account for it, and its cause is not known. The metric scores
which file a query is about, and the files are descriptively named, so read the figure as an upper
bound for queries that do not echo a file name. It has not been run through the app's hybrid search.
Dexicon's framing templates carry `{text}` only, so a title cannot be expressed today.

**Framing matters less here than in the September report.** Unframed `embeddinggemma` is 0.059
lower on docs and 0.015 higher on code (dense only, 55 and 52 queries, blank-line chunks). September's
second measurement went from 0.632 raw to 0.799 framed on twelve queries through the app's hybrid search
([decisions.md](decisions.md#q3-second-measurement--with-task-framing)). The two differ in query set,
chunking and retrieval mode, and twelve queries resolve only large gaps, so the two figures are not
directly comparable.

Speed was measured on the same shared GPU and varied more than the models did: `embeddinggemma` ran
at 13 to 39 chunks per second over 28 corpus runs, the identical first row and its repeat between 19 and
30, and `embeddinggemma-2` Q8_0 at 7 to 25 over six. At those speeds, re-embedding 258,000 chunks, the
size of one deployed instance, takes 2 to 6 hours with the first generation and 3 to 10 with the second;
scale by chunks divided by rate.

Reproduce with:

```bash
mkdir -p /tmp/tree && git archive a948496 docs src scripts/bench | tar -x -C /tmp/tree
python scripts/bench/modelbench.py --list
python scripts/bench/modelbench.py --root /tmp/tree --ollama http://127.0.0.1:21434 --arms v1_framed,v1_title,eg2_q8 --out modelbench-out.json
```

The wrong-path arms are `v1_title_placebo_s1` to `v1_title_placebo_s10`. A seed fixes one permutation
of the chunks taken in sorted file order, so the same seed on a tree with different files is a
different draw.

`--root` matters: this corpus is the repository, so a later change to `docs/` changes the docs
numbers. Ollama's tags move. The `embeddinggemma-2:270m` tag measured as nvfp4 on 2026-10-08 now
resolves to a GGUF build (digest `ff630aa67948`), so the `eg2_nvfp4` arm names the build by its explicit tag,
`embeddinggemma-2:270m-nvfp4-text`, whose digest `9e1df58d197e` is the one recorded in the results.
The Q8_0 arm used the ggml-org file (`hf.co/ggml-org/embeddinggemma-2-GGUF:Q8_0`), which is not the same
file as the one Ollama now serves as `embeddinggemma-2:270m`; the official build has not been measured.
Dexicon's own Ollama publishes no port, so run the script from a container on its network or with
`docker-compose.debug.yml`.

## Chunk size and the second generation's context (2026-11-11)

`embeddinggemma` takes about 2,000 tokens of input and `embeddinggemma-2:q8_0` about 8,000 (Test limits,
`POST /api/embedding-models/probe`, measured a context of 2,006 and 8,150 tokens and 11,520 and 46,848
characters of prose), so the second generation can read chunks the first cannot. This round asks whether larger chunks
are worth anything. The arms hold the model and the framing fixed and change the chunk target in
characters: `v1_c1200`, `v1_c2500` and `v1_c3500` for `embeddinggemma`, and `eg2x_c1200` to `eg2x_c16000`
for `embeddinggemma-2:q8_0`, a local alias of the ggml-org Q8_0 file with `num_ctx` and `num_batch` 8192.
A paragraph is kept whole up to 1.167 times the target, so the largest chunks are about 18,700 characters.
The tree (`a948496`), queries, scoring and Ollama (0.40.15) are those of the section above, on a GPU shared
with a training workload. No predictions were written for this round, so every figure here is exploratory.
The results are in `scripts/bench/results-context-2026-11-11.json`.

Two arms reproduce rows of the earlier round: `v1_c1200` gives the framed first-generation figures (0.723,
0.674) and `eg2x_c1200` the Q8_0 figures (0.666, 0.660). `eg2d_c3500`
is the ggml-org GGUF under its `hf.co` name, at Ollama's default options, on the same chunks as `eg2x_c3500`: its chunk vectors
have a cosine of 1.0000 (median, 5th percentile and minimum) to the alias's on both corpora, its top-10
overlap is 1.00, and its MRR is identical, so the alias holds the same weights and the context setting
does not change a vector.

| chunk target (characters) | chunks (docs, code) | `embeddinggemma` docs / code | `embeddinggemma-2` docs / code |
|---|---|---|---|
| 1,200 | 623, 2,145 | 0.723 / 0.674 | 0.666 / 0.660 |
| 2,500 | 277, 969 | 0.689 / 0.706 | 0.654 / 0.744 |
| 3,500 | 193, 687 | 0.662 / 0.693 | 0.696 / 0.657 |
| 8,000 | 85, 339 | not run | 0.727 / 0.656 |
| 16,000 | 43, 208 | not run | 0.779 / 0.630 |

The docs queries that carry an answer literal (47 of 55) also give how often the top chunk or the top three
contain it, and how much text that delivers to the reader:

| arm | answer in the top chunk (mean characters) | in the top three (mean characters) |
|---|---|---|
| `embeddinggemma`, 1,200 | 0.30 (860) | 0.49 (2,737) |
| `embeddinggemma`, 2,500 | 0.30 (2,121) | 0.47 (6,150) |
| `embeddinggemma`, 3,500 | 0.21 (3,128) | 0.43 (9,357) |
| `embeddinggemma-2`, 1,200 | 0.30 (849) | 0.53 (2,599) |
| `embeddinggemma-2`, 2,500 | 0.28 (1,958) | 0.57 (6,151) |
| `embeddinggemma-2`, 3,500 | 0.23 (2,916) | 0.64 (8,796) |
| `embeddinggemma-2`, 8,000 | 0.34 (6,745) | 0.66 (19,461) |
| `embeddinggemma-2`, 16,000 | 0.60 (13,349) | 0.83 (41,210) |

Differences are the first arm minus the second in mean reciprocal rank, with a 95% interval from a paired
bootstrap over the queries:

| comparison | docs | code |
|---|---|---|
| `embeddinggemma-2` minus `embeddinggemma`, 1,200 | -0.057 [-0.165, +0.049] | -0.014 [-0.101, +0.073] |
| the same at 2,500 | -0.034 [-0.127, +0.059] | +0.038 [-0.044, +0.126] |
| the same at 3,500 | +0.034 [-0.059, +0.125] | -0.036 [-0.135, +0.069] |
| `embeddinggemma-2` at 8,000 minus its own 1,200 | +0.062 [-0.023, +0.148] | -0.004 [-0.112, +0.103] |
| `embeddinggemma-2` at 16,000 minus its own 1,200 | +0.113 [+0.024, +0.200] | -0.029 [-0.147, +0.087] |
| `embeddinggemma-2` at 16,000 minus its own 3,500 | +0.083 [+0.011, +0.157] | -0.027 [-0.111, +0.060] |
| `embeddinggemma-2` at 16,000 minus `embeddinggemma` at 3,500 | +0.117 [+0.037, +0.197] | -0.062 [-0.154, +0.029] |
| `embeddinggemma-2` at 16,000 minus `embeddinggemma` at 1,200 | +0.056 [-0.048, +0.156] | -0.043 [-0.142, +0.056] |
| `embeddinggemma-2` at 16,000 minus `embeddinggemma` at 2,500 | +0.090 [+0.004, +0.179] | -0.075 [-0.175, +0.024] |

**At equal chunk size the second generation is not shown to be better.** The six differences at 1,200,
2,500 and 3,500 characters range from -0.057 to +0.034 on docs and from -0.036 to +0.038 on code, and every
interval includes zero.

**The longer context is not shown to help.** The script computes 26 intervals for the arms of this round. Ten
more (five arm pairs on both corpora) were computed from the saved per-query reciprocal ranks with the
script's `bootstrap` function and are in `PAIRS`; the results file does not hold them. Five of the 36 exclude zero. None is
against the first generation's best arm on its corpus. On docs the 16,000-character arm gains over
`embeddinggemma-2` at 1,200 and 3,500 characters and over `embeddinggemma` at 2,500 and 3,500, where the
first generation scores less; against the first generation's best docs size (1,200 characters, 0.723) the gain
is +0.056 and the interval includes zero. On code the figures for the second generation are 0.660, 0.744,
0.657, 0.656 and 0.630 from 1,200 to 16,000 characters: one high point at 2,500, which beats 3,500 by +0.087
with a lower bound of +0.002 and is the fifth interval, and no trend around it. With 36 intervals on shared
queries, a few are expected to exclude zero by chance alone.

**At 16,000 characters a docs lookup is easier and costs the reader more.** The docs corpus is 16 files,
and at 16,000 characters it is 43 chunks, about 2.7 per file. No arm separates chunk size from the number
of chunks per file, so part of the docs gain may be that fewer chunks compete with the right file.
The answer is in the top chunk for 0.60 of the answerable queries, against 0.30 at 1,200 characters, and
the top three chunks carry 41,210 characters, 16 times the 2,599 at 1,200.

**The size the Models screen suggests is outside what was measured.** The UI suggests for a new chunk set 90%
of the model's measured context as its chunk size (`CodeChunker.UsableContext`), held to 64 to 8,192 tokens.
Test limits suggested 1,805 tokens for `embeddinggemma` and 7,335 tokens (31,232 characters of prose) for
`embeddinggemma-2:q8_0`. The largest arm here is 16,000 characters, about 4,200 tokens at 3.8 characters per
token, so nothing in this round supports the size suggested for the alias, and on code the trend at larger
sizes is flat to negative.

Speed, on the shared GPU: `embeddinggemma` ran at 12 to 15 chunks per second and `embeddinggemma-2` at 8 to
9 up to 8,000 characters, and at 4.2 (docs) and 5.8 (code) at 16,000. `/api/ps` reported the alias at 1.40 GB
and `embeddinggemma` at 0.69 GB.

Reproduce with:

```bash
mkdir -p /tmp/tree && git archive a948496 docs src scripts/bench | tar -x -C /tmp/tree
python scripts/bench/modelbench.py --root /tmp/tree --ollama http://127.0.0.1:21434 \
  --arms v1_c1200,v1_c2500,v1_c3500,eg2x_c1200,eg2x_c2500,eg2x_c3500,eg2x_c8000,eg2x_c16000,eg2d_c3500 \
  --out modelbench-out.json
```

The `embeddinggemma-2:q8_0` alias must exist (`ollama show embeddinggemma-2:q8_0` lists `num_ctx` and `num_batch`
8192). The `eg2x` arms load it at that context, which needs more memory than the other arms; the script
refuses to start an arm with less than 10 GB available in the VM and stops if the figure falls below 3 GB.

## Function-level retrieval, and where the chunker breaks a member (2026-11-11)

Every score above asks which file a question is about. It cannot say whether the chunk that comes back is the function the
question is about, or whether that function arrives with its own description. This round measures both on the C# of `src/`
(tree `f239ba4`, 143 text files of which 88 are C#, chunked by the production `CodeChunker` in `language-aware` mode at 256/32,
512/64 and 1,024/128 tokens and overlap). 2,065/258, the size the docs' example uses, was dropped: `embeddinggemma:latest` refused 7 of the 60
longest such chunks of this code ("the input length exceeds the context length"). The predictions, committed before any scoring
run, are in `scripts/bench/functionbench-predictions-2026-11-11.md`; the results are in
`scripts/bench/results-functionbench-2026-11-11.json`.

**Where the chunker breaks a member.** A `language-aware` boundary is the member's signature line, so the `///` block and the
attributes above it belong to the segment before. When a chunk breaks at a boundary, which is what the chunker aims for, the next
member's description sits at the end of the chunk before it (a chunk can also end inside a long comment, and the table counts both). Over the 88 `.cs` files outside `Migrations` (1,868 members, 1,005
with a `///` block above), measured by `scripts/bench/chunk_coherence.py` on the chunks of `f239ba4`:

| chunk size / overlap | chunks | members per chunk | non-final chunks ending on a `///` line | documented members with comment and signature in one chunk |
|---|---|---|---|---|
| 256 / 32 | 1,820 | 1.13 | 45.7% | 46.8% |
| 512 / 64 | 932 | 2.23 | 48.6% | 74.4% |
| 1,024 / 128 | 486 | 4.21 | 52.5% | 91.7% |

Two things raise the figure with the chunk: fewer breaks per member, and an overlap long enough to carry a whole comment forward;
this measurement does not separate them. The `///` blocks of this repository have a median of 262 characters and a 90th percentile
of 845, and the overlap at 256/32 is 128 characters at four characters per token; 73% of the blocks are longer than that. The
figures are for this comment style.

**A prototype that attaches them.** `scripts/bench/functionbench-attach-leading-lines.patch` moves each boundary up over the
unbroken run of comment and attribute lines above it (a blank line, or another boundary, ends the run), for the 23 languages that
have a member pattern; Markdown, HTML, Razor, Vue and Svelte, whose boundaries are headings or blank lines, are left alone.
On the same chunks the share of documented members kept whole becomes 89.3%, 98.4% and 100%, and the share of non-final chunks
ending on a `///` line falls to 8.2%, 2.2% and 0.3%.

**Whether it changes what a question finds.** `queries-code-members.json` says which member of the expected file answers each of the
52 questions in `queries-code.json`: a reviewer who saw the questions and the source, and no result, chose them from what each member
does. 48 questions have a member (12 of them marked ambiguous by the reviewer) and 4 are answered by the whole file. A question is
scored by the reciprocal rank of its first hit in the top ten, dense only, with each model's own framing. A *strict* hit is a chunk of
the expected file that holds a labelled member's signature line; a *lenient* hit overlaps the member's text from its leading comment
to its last line; a *file* hit is any chunk of the expected file, as in `modelbench.py`. Each cell is the old chunker, then the
prototype:

| model, size | strict MRR | new minus old, strict [95% paired bootstrap] | lenient MRR | file MRR |
|---|---|---|---|---|
| `embeddinggemma`, 256 | 0.323, 0.337 | +0.014 [-0.077, +0.098] | 0.556, 0.548 | 0.735, 0.696 |
| `embeddinggemma`, 512 | 0.448, 0.374 | -0.074 [-0.159, +0.004] | 0.594, 0.544 | 0.716, 0.661 |
| `embeddinggemma`, 1,024 | 0.447, 0.460 | +0.013 [-0.037, +0.067] | 0.558, 0.554 | 0.646, 0.660 |
| `embeddinggemma-2`, 256 | 0.193, 0.270 | +0.077 [+0.002, +0.162] | 0.349, 0.370 | 0.674, 0.686 |
| `embeddinggemma-2`, 512 | 0.385, 0.367 | -0.018 [-0.088, +0.051] | 0.480, 0.439 | 0.700, 0.656 |
| `embeddinggemma-2`, 1,024 | 0.459, 0.425 | -0.034 [-0.115, +0.049] | 0.566, 0.552 | 0.651, 0.638 |

**The prototype does not change retrieval consistently, so it is not adopted.** One of six intervals excludes zero, and barely
(`embeddinggemma-2` at 256, whose lower bound is +0.002); the sign changes with the size and with the model. One of the six
predictions made beforehand held (an interval at 256 that excludes zero for one model). The others expected a gain of 0.03 or more at
256 for both models (`embeddinggemma` gained 0.014), a gain at 512 (both lost), a change of at most 0.02 at 1,024 (`embeddinggemma-2`
lost 0.034), a file-level change under 0.02 (up to -0.055), and the best size to be 256 or 512 (it was 1,024 for three of the four
model and chunker pairs). The rule written beforehand, a gain of at least +0.02 for both models at 256 or 512 tokens and an
interval that excludes zero for at least one of them, is not met. The chunker is unchanged. A strict hit is also a file hit, so the
strict score cannot exceed the file score; the gap here is strict 0.19 to 0.46 against file 0.64 to 0.74. For both models and the
unchanged chunker the strict score is higher at 512 and 1,024 tokens than at 256 (`embeddinggemma` 0.323, 0.448, 0.447;
`embeddinggemma-2` 0.193, 0.385, 0.459), without an interval, and a larger chunk holds more signatures, so part of that follows from
the definition.

A second full run reproduced all 2,208 per-question scores and every interval exactly. Limits: 48 questions resolve differences of
about 0.08, and the intervals are that wide. The labels are one reviewer's, and 12 are
marked ambiguous (on the 36 unambiguous ones `embeddinggemma-2` at 256 gains +0.103 [+0.025, +0.199]). The strict score does not
credit a chunk that holds only the tail of a long member, and the lenient score credits any overlap: one question is labelled with a
class that is 98% of its file, and five more with a member that covers more than half of a short file, so for those the lenient score
is close to the file score. The member's last line comes from a
brace matcher and not a parser; the script stops if a span does not contain its own signature line, and the spans of all 76 labelled
members were read for plausibility without a record in the repository. One repository and one language were measured, dense only;
the prototype's patterns for the other 22 languages were not.

Reproduce with:

```bash
git archive f239ba4 src | tar -x -C /tmp/tree
dotnet run --project scripts/bench/chunkjson -c Release -- /tmp/tree/src /tmp/chunks/old
git apply scripts/bench/functionbench-attach-leading-lines.patch
dotnet run --project scripts/bench/chunkjson -c Release -- /tmp/tree/src /tmp/chunks/new
git checkout -- src
python scripts/bench/chunk_coherence.py --src /tmp/tree/src --chunks /tmp/chunks
python scripts/bench/functionbench.py --ollama http://127.0.0.1:21434 --src /tmp/tree/src --chunks /tmp/chunks --out functionbench-out.json
```

`--src` has to be the tree the labels belong to (`f239ba4`), and the chunker under test is the one in the working tree.
