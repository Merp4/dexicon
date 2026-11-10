# Predictions for modelbench.py, written before each round was run (2026-11-09)

ARM: dense-only retrieval quality, file-level, for embeddinggemma-2 (GGUF Q8_0 and MLX nvfp4) against
embeddinggemma v1, on this repository's docs (55 queries) and src (52 queries), straight against the
live Ollama 0.40.15. Own chunker (blank-line paragraphs, <= 1,200 chars), identical chunks and queries for
every arm, top 10 chunk hits, rank of the first hit from the expected file.

CONTROL: v1, framed with its card prompts. Sensitivity control: v1 with no framing (the September report
measured framing moving v1 from 0.632 to 0.799 MRR, so this arm must drop or the harness cannot see framing).
Apparatus noise: v1 framed, run twice.
ANCHOR: nomic-embed-text (September ordering: v1 above nomic on both corpora).

PREDICTIONS (probabilities are my confidence, recorded now):
P1 docs: eg2-Q8 MRR within +-0.04 of v1 (60%); higher by > 0.04 (20%); lower by > 0.04 (20%).
P2 code: eg2-Q8 above v1 by 0.02 to 0.08 (45%); within +-0.02 (30%); below by > 0.02 (25%).
   The card's +9.9 is MTEB-code (code retrieval tasks); these queries are natural-language-to-file lookups.
P3 nvfp4 within 0.02 MRR of Q8 on both corpora (65%); median per-chunk cosine nvfp4 vs Q8 >= 0.98 (65%).
P4 v1 unframed is below v1 framed by >= 0.05 MRR on docs (80%). If it is not, the harness is not sensitive to
   framing and nothing else in this run can be trusted.
P5 nomic below v1 on both corpora (80%).
P6 chunks per second, eg2-Q8 between 0.4x and 1.5x of v1 (60%).

WHAT OUTCOMES MEAN
- eg2 > v1 on BOTH corpora with the paired-bootstrap lower bound > 0: worth an app-level sweep (hybrid, real
  chunker) and a pilot chunk set; still not a default change on this evidence alone.
- eg2 within noise on docs, better on code: candidate for code corpora only (per-corpus model choice).
- eg2 <= v1 on both: stop spending effort on adoption; keep the framing entry (cheap, prevents a silent trap).
- Mixed or all CIs straddle 0: HOLD. n = 55 and 52 queries cannot resolve gaps under about 0.05.

NOISE: 55 and 52 queries; the September report called a 0.025 gap suggestive, not decisive. A "positive gap"
alone is met by chance about half the time, so the criterion is: same sign on both corpora AND paired-bootstrap
95% interval above zero. Under the null that passes well under 10% of the time.
NOT MATCHED TO THE APP: no hybrid (sparse) leg, own chunker, no overlap. Absolute numbers will not match the
September sweep; only the model-to-model differences are read.
COST: about 2,900 chunks x 6 arms. Shared GPU (another training workload) and the app's own refresh embeds on v1.
ABORT: VM MemAvailable < 10 GB at any arm start.

# ROUND 1 OUTCOMES (recorded 2026-11-09 after the run)
P1 MISSED  docs eg2-Q8 - v1 = -0.057 (CI -0.165..+0.049); I predicted within +-0.04 (60%).
P2 MISSED  code eg2-Q8 - v1 = -0.014 (CI -0.101..+0.073); I predicted +0.02..+0.08 (45%). No code gain.
P3 HALF    nvfp4 - Q8: docs -0.016 (ok), code -0.043 (CI -0.082..-0.012, not within 0.02). Median cos 0.988 (ok).
P4 PASSED NARROWLY  v1 raw - framed: docs -0.059 (CI -0.127..+0.006), code +0.015 (n.s.).
P5 PASSED  nomic - v1: docs -0.117, code -0.131, both CIs below 0.
P6 UNRESOLVED  eg2-Q8 7.0 and 13.2 chunks/s vs v1 22.2 and 18.5 (v1 repeat 27.1 and 29.5, raw 35.3 and 31.9):
   v1 alone varies 1.9x between identical runs on the shared GPU.
Adoption criterion (same sign on both corpora AND bootstrap lower bound > 0) FAILED for eg2-Q8 and eg2-nvfp4.

# ROUND 2, registered before running (card best practice the app cannot express today)
Card: "Documents with a real title should be formatted title: {title} | text: {content}"; for code,
"title: {title or filename}" and query prompt "task: code retrieval | query:".
T1 v1 with title = file path improves docs MRR by >= 0.03 (55%).
T2 v1 with title = file path improves code MRR by >= 0.03 (50%).
T3 eg2-Q8 with title + code-retrieval query prompt beats v1 with title on code with bootstrap lower bound > 0 (25%).
T4 the eg2 - v1 ordering does not change sign on docs when both get titles (65%).
Reading: if titles help both, the cheapest quality lever is not a model switch but a {title} placeholder in
the framing templates (Dexicon knows each chunk's path). If eg2 only wins with its code prompt, a per-corpus
query prompt is the mechanism, not a different default.
Matched on: same chunks and queries as round 1. v1_framed and eg2_q8 re-run as in-run baselines.

# ROUND 2 OUTCOMES (recorded after the run)
T1 PASSED  v1 title - v1 framed, docs +0.057 (CI +0.001..+0.118; lower bound barely above 0).
T2 PASSED  v1 title - v1 framed, code +0.054 (CI +0.008..+0.106).
T3 MISSED  eg2-Q8 title+code prompt - v1 title, code -0.023 (CI -0.111..+0.066). I gave it 25%.
T4 PASSED  eg2 - v1 on docs with titles -0.037 (CI -0.125..+0.048): same sign as without titles (-0.057).
Also: eg2-Q8 title - eg2-Q8, docs +0.077 (CI +0.018..+0.143), code +0.021 (n.s.). eg2 never beat v1 under equal
treatment; 5 of 6 point estimates favour v1 by 0.02..0.06, every interval spans 0. n = 55 and 52 cannot resolve
gaps under about 0.1.

# ROUND 3, registered before running: is the title lift information or only format?
The title carries the file path, and this benchmark scores file-level lookups, so path text can match query words.
Placebo arm: same "title: {p} | text: {t}" format on v1, but {p} is the path of a different, randomly chosen chunk.
PREDICTION: placebo MRR is below v1 framed on both corpora (60%), about equal to v1 framed within +-0.02 (30%),
above v1 framed (10%).
Reading: below or equal -> the lift is the path INFORMATION. That is real signal for these corpora but is
inflated here by file-level scoring against descriptively named files; read +0.055 as an upper-ish bound for
queries that do not echo file names. Above -> the format itself helps and the claim is stronger.

# ROUND 3 OUTCOME (first harness, one draw, seed 3, files in directory order)
Placebo - v1 framed: docs +0.009 (CI -0.021..+0.042), code +0.070 (CI +0.013..+0.131). Docs fell in "about
equal". Code fell in "above v1 framed" (registered at 10%), and above the true-path title on code (0.744 against
0.728).

# ROUNDS 4 AND 5, not registered: ten seeded draws, run because a rerun of round 3 with a cleaned script gave
# code 0.675, not 0.744
Cause of the difference, found by reproducing it: the first harness read files in directory order and the cleaned
one sorts them. A seed permutes chunk positions, so the same seed assigned different paths. Running the cleaned
script with the old order reproduced the first draw (docs 0.731, code 0.744).
Ten seeded draws, sorted order, seeds 1 to 10:
  docs  0.703..0.772, mean 0.730; minus v1 framed +0.008 (range -0.020..+0.049); minus true path -0.049 (range -0.076..-0.008)
  code  0.634..0.682, mean 0.662; minus v1 framed -0.011 (range -0.040..+0.008); minus true path -0.066 (range -0.094..-0.046)
Against the round 3 registration: the mean of the draws is within +-0.02 of v1 framed on both corpora ("about
equal", 30%), and every draw is below the true path. None of the ten reproduces the first draw's code result, and its
cause is not known. Its share of chunks left on their own file's path (2.10% of 2,145) is inside the range of the
others (1.31% to 1.96%), so leakage of the true path does not explain it.
