#!/usr/bin/env python3
"""
Compare embedding models, and the framing they are given, straight against Ollama.

    python scripts/bench/modelbench.py --ollama http://127.0.0.1:21434 --arms v1_framed,eg2_q8
    python scripts/bench/modelbench.py --list

Every arm sees the same chunks and the same queries, so the only differences are the model and the
text wrapped around each chunk and query. Scoring is file-level, as in sweep.py: the rank of the first
of the top-10 chunk hits that comes from the file the query is about. The result is MRR, hit@1/3/5 and
the share of queries with no hit, with a paired bootstrap interval for the difference between arms.

WHAT THIS IS NOT. It does not go through Dexicon. There is no sparse leg, no hybrid fusion, and the
chunker is a plain blank-line splitter, so absolute numbers do not match sweep.py. It exists to read
model-to-model and framing-to-framing differences without needing an admin session or a framing profile
saved in the app. With 55 and 52 queries it cannot resolve gaps under about 0.1; read the intervals.

THE CORPUS IS THIS REPOSITORY. Results depend on the docs and src trees they were measured on, and a
change to docs/ changes the docs corpus. The committed results record the commit; to reproduce them
extract that commit and pass --root:

    mkdir -p /tmp/tree && git archive a948496 docs src scripts/bench | tar -x -C /tmp/tree
    python scripts/bench/modelbench.py --root /tmp/tree ...

CHUNKS. Blank-line paragraphs are joined up to 1,200 characters, a single paragraph is kept whole up to 1,400, a
longer one is split on line boundaries, and a single line over 1,400 characters is cut at 1,400. In the tree above
that cuts two lines of docs/10-security-secrets.md (2,249 characters) and none under src. Every arm sees the same
chunks. Files under bin, obj, node_modules and .git are not read, so a built tree gives the same corpus. Files are
taken in sorted order of their Path, which compares case-sensitively on POSIX: run it there (a container does),
because a placebo seed permutes chunk positions and another order is another draw.

Dexicon's own Ollama publishes no port. Run this from a container on its network
(docker run --rm --network dexicon_default -v <repo>:/repo python:3.12-alpine python ...) or with
docker-compose.debug.yml, which publishes it on 21434.

CHUNK SIZE AND CONTEXT. An arm may name a chunk target in characters (the fourth element of its entry); without one it
uses TARGET. The `v1_c*`, `eg2x_c*` and `eg2d_c*` arms hold the model and the framing fixed and change only how much text one
chunk carries, to ask what the larger context of the second generation is worth:

  v1_c1200 / v1_c2500 / v1_c3500     embeddinggemma:latest, whose options are num_ctx 2048 and num_batch 2048
  eg2x_c1200 ... eg2x_c16000         embeddinggemma-2:q8_0, a local alias of the ggml-org Q8_0 file with num_ctx 8192 and
                                     num_batch 8192 (ollama create from a Modelfile; the GGUF itself trains to 262,144)
  eg2d_c3500                         the ggml-org file at Ollama's default options: a control that the alias is the same
                                     weights where both can take the input

The alias must exist (ollama show embeddinggemma-2:q8_0 lists its parameters). A chunk that the model cannot take fails the
arm: truncate is off, so nothing is cut quietly.

Docs queries carry an `answer` literal; those whose literal is in their file are ANSWERABLE (47 of 55 on a948496, since the rest
were edited out of the documents). For those the output also reports ANSWER RECALL: whether the top 1 chunk, and the top 3,
contain it, with the mean characters delivered, because a bigger chunk finds the file more easily (fewer, larger buckets) and
costs the reader more text; the pair of numbers is the trade.

Writes --out (default modelbench-out.json in the current directory).
"""
import argparse
import concurrent.futures as cf
import json
import math
import os
import pathlib
import random
import sys
import time
import urllib.error
import urllib.request

BATCH, WORKERS, TARGET, HARD = 32, 4, 1200, 1400
HARD_RATIO = HARD / TARGET
TEXT_EXT = {".md", ".cs", ".csproj", ".json", ".props", ".yml", ".yaml", ".txt"}
MAX_FILE_BYTES = 256 * 1024
SKIP_DIRS = {"bin", "obj", "node_modules", ".git"}
MIN_FREE_GB = 10
# During an arm: below this the VM is close to an out-of-memory kill of Ollama's runner, which the live app shares. On 2026-10-14 a
# num_batch of 32768 made a runner hold 26.8 GB of shared memory and the kernel killed it.
ABORT_FREE_GB = 3
# Characters of text one request may carry, counted at the chunk target; a bigger chunk goes in smaller batches, and in fewer at once.
# A chunk can run to HARD_RATIO times its target, so a request can carry up to about 47,000.
REQUEST_CHARS = 40000

V1 = "embeddinggemma:latest"
GGUF = "hf.co/ggml-org/embeddinggemma-2-GGUF:Q8_0"
# The nvfp4 (MLX) build by its explicit tag: embeddinggemma-2:270m now resolves to a GGUF build. Digest 9e1df58d197e.
MLX = "embeddinggemma-2:270m-nvfp4-text"
GEMMA_DOC, GEMMA_Q = "title: none | text: {t}", "task: search result | query: {t}"
TITLED = "title: {p} | text: {t}"

# name -> (model, document template, query template). {t} is the text, {p} the file's path under the
# corpus root. `placebo` arms give each chunk the path of a different, randomly chosen chunk. The draw
# is a permutation of the chunk list in sorted file order, seeded by the arm name (`_sN` is seed N), so
# the same arm on the same tree gives the same draw. One draw is not a result: draws on code ranged
# widely (see docs/benchmarks.md), so judge a placebo by all ten seeds.
ARMS = {
    "v1_framed":         (V1, GEMMA_DOC, GEMMA_Q),
    "v1_raw":            (V1, "{t}", "{t}"),
    "v1_framed_repeat":  (V1, GEMMA_DOC, GEMMA_Q),
    "v1_title":          (V1, TITLED, GEMMA_Q),
    **{f"v1_title_placebo_s{n}": (V1, TITLED, GEMMA_Q) for n in range(1, 11)},
    "eg2_q8":            (GGUF, GEMMA_DOC, GEMMA_Q),
    "eg2_q8_title":      (GGUF, TITLED, GEMMA_Q),
    "eg2_q8_codeprompt": (GGUF, TITLED, "task: code retrieval | query: {t}"),
    "eg2_nvfp4":         (MLX, GEMMA_DOC, GEMMA_Q),
    "nomic":             ("nomic-embed-text", "search_document: {t}", "search_query: {t}"),
}
ALIAS = "embeddinggemma-2:q8_0"
for _t in (1200, 2500, 3500):
    ARMS[f"v1_c{_t}"] = (V1, GEMMA_DOC, GEMMA_Q, _t)
for _t in (1200, 2500, 3500, 8000, 16000):
    ARMS[f"eg2x_c{_t}"] = (ALIAS, GEMMA_DOC, GEMMA_Q, _t)
ARMS["eg2d_c3500"] = (GGUF, GEMMA_DOC, GEMMA_Q, 3500)

# Pairs worth a paired interval, when both arms ran.
PAIRS = [
    ("eg2_q8", "v1_framed"), ("eg2_nvfp4", "v1_framed"), ("eg2_nvfp4", "eg2_q8"),
    ("v1_raw", "v1_framed"), ("nomic", "v1_framed"), ("v1_framed_repeat", "v1_framed"),
    ("v1_title", "v1_framed"), ("eg2_q8_title", "eg2_q8"), ("eg2_q8_title", "v1_title"),
    ("eg2_q8_codeprompt", "eg2_q8_title"), ("eg2_q8_codeprompt", "v1_title"),
    ("v1_c2500", "v1_c1200"), ("v1_c3500", "v1_c1200"),
    ("eg2x_c2500", "eg2x_c1200"), ("eg2x_c3500", "eg2x_c1200"), ("eg2x_c8000", "eg2x_c1200"), ("eg2x_c16000", "eg2x_c1200"),
    ("eg2x_c8000", "eg2x_c3500"), ("eg2x_c16000", "eg2x_c3500"),
    ("eg2x_c1200", "v1_c1200"), ("eg2x_c2500", "v1_c2500"), ("eg2x_c3500", "v1_c3500"),
    ("eg2x_c8000", "v1_c3500"), ("eg2x_c16000", "v1_c3500"),
    # Against the first generation's best size on each corpus (1,200 characters on docs, 2,500 on code), and the second
    # generation's high point on code. results-context-2026-11-11.json holds the 26 intervals of the pairs above these; the
    # ten intervals of the pairs below were computed from its saved reciprocal ranks and are not in it.
    ("eg2x_c8000", "v1_c1200"), ("eg2x_c16000", "v1_c1200"), ("eg2x_c16000", "v1_c2500"),
    ("eg2x_c2500", "v1_c1200"), ("eg2x_c2500", "eg2x_c3500"),
] + [(f"v1_title_placebo_s{n}", base) for n in range(1, 11) for base in ("v1_framed", "v1_title")]
AGREEMENT = [("eg2_q8", "eg2_nvfp4"), ("v1_framed", "v1_framed_repeat"), ("eg2_q8", "eg2_q8_title"), ("eg2d_c3500", "eg2x_c3500")]


def same_model(listed, wanted):
    """Ollama lists an untagged name as name:latest. The two forms match, and nothing looser does."""
    return listed == wanted or listed == f"{wanted}:latest" or wanted == f"{listed}:latest"


def mem_available_gb():
    """The Docker VM's free memory when run in a container; None where /proc/meminfo is absent."""
    try:
        for line in open("/proc/meminfo"):
            if line.startswith("MemAvailable"):
                return int(line.split()[1]) / 1048576
    except OSError:
        return None
    return None


def call(base, path, body, timeout=900):
    req = urllib.request.Request(base + path, data=json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json"})
    return json.load(urllib.request.urlopen(req, timeout=timeout))


def chunks_of(path, target=TARGET):
    hard = int(target * HARD_RATIO)
    text = path.read_text(encoding="utf-8", errors="replace").replace("\r\n", "\n")
    out, cur = [], ""

    def flush():
        nonlocal cur
        if cur.strip():
            out.append(cur.strip())
        cur = ""

    for para in text.split("\n\n"):
        if len(para) > hard:  # a long paragraph is split on line boundaries
            flush()
            piece = ""
            for line in para.split("\n"):
                if piece and len(piece) + len(line) + 1 > target:
                    out.append(piece.strip())
                    piece = ""
                piece += line[:hard] + "\n"
            if piece.strip():
                out.append(piece.strip())
            continue
        if cur and len(cur) + len(para) + 2 > target:
            flush()
        cur += para + "\n\n"
    flush()
    return out


def load_corpus(root, sub, target=TARGET):
    items = []
    base = root / sub
    for path in sorted(p for p in base.rglob("*") if p.is_file()):  # sorted: placebo draws depend on this order
        if path.suffix.lower() not in TEXT_EXT or path.stat().st_size > MAX_FILE_BYTES:
            continue
        rel = path.relative_to(base).as_posix()
        if SKIP_DIRS.intersection(path.relative_to(base).parts[:-1]):
            continue
        items.extend((rel, c) for c in chunks_of(path, target))
    return items


def placebo_seed(arm):
    """The N of an arm named `..._sN`."""
    head, _, tail = arm.rpartition("_s")
    if not head or not tail.isdigit():
        raise ValueError(f"placebo arm {arm!r} must be named ..._sN, where N is its seed")
    return int(tail)


def placebo_paths(items, seed):
    """Each chunk gets the path of a different, randomly chosen chunk's file (a seeded permutation)."""
    paths = [f for f, _ in items]
    order = list(range(len(paths)))
    random.Random(seed).shuffle(order)
    out = [paths[j] for j in order]
    same = sum(1 for a, b in zip(paths, out) if a == b)
    assert same < len(paths) * 0.5, f"placebo kept {same} of {len(paths)} true paths"
    return out


def unload(base, model):
    """Free the model before an abort: the abort fires when memory is short, and the loaded model is what holds it."""
    try:
        call(base, "/api/embed", {"model": model, "keep_alive": 0})
    except Exception as e:  # the abort or failure that follows is the message that matters
        print(f"  could not unload {model}: {e}", flush=True)


def embed_all(base, model, texts, label, batch=BATCH, workers=WORKERS):
    """Batches of `batch`, `workers` in flight. Any batch that fails twice, or returns nulls, raises, after unloading the model."""
    batches = [texts[i:i + batch] for i in range(0, len(texts), batch)]
    secs, vecs = [0.0] * len(batches), [None] * len(batches)

    def one(i):
        t0 = time.time()
        free = mem_available_gb()
        if free is not None and free < ABORT_FREE_GB:
            unload(base, model)
            raise SystemExit(f"ABORT in {label}: {free:.1f} GB available in the VM (floor {ABORT_FREE_GB}); stopping before the kernel does")
        for attempt in (0, 1):
            try:
                d = call(base, "/api/embed", {"model": model, "input": batches[i], "truncate": False})
                v = d.get("embeddings")
                if not v or len(v) != len(batches[i]) or any(x is None or any(y is None for y in x) for x in v):
                    raise ValueError(f"bad response shape or nulls in batch {i}")
                vecs[i], secs[i] = v, time.time() - t0
                return
            except (urllib.error.HTTPError, urllib.error.URLError, ValueError) as e:
                if attempt == 1:
                    unload(base, model)
                    raise RuntimeError(f"{label}: batch {i} failed twice: {e}") from e

    with cf.ThreadPoolExecutor(workers) as ex:
        list(ex.map(one, range(len(batches))))
    return [x for b in vecs for x in b], secs


def norm(v):
    n = math.sqrt(sum(x * x for x in v)) or 1.0
    return [x / n for x in v]


def dot(a, b):
    return sum(x * y for x, y in zip(a, b))


def squash(text):
    """Case and whitespace folded, so a literal that wraps across a line still matches."""
    return " ".join(text.lower().split())


def evaluate(qvecs, cvecs, files, queries, texts=None):
    ranks, tops = [], []
    for qv, q in zip(qvecs, queries):
        scores = sorted(((dot(qv, cv), i) for i, cv in enumerate(cvecs)), reverse=True)[:10]
        tops.append([i for _, i in scores])
        ranks.append(next((k + 1 for k, (_, i) in enumerate(scores) if files[i] == q["file"]), 0))
    rr = [1 / r if r else 0.0 for r in ranks]
    n = len(ranks)
    answer = {}
    if texts is not None:
        folded = [squash(t) for t in texts]
        scored = [(q, top) for q, top in zip(queries, tops) if q.get("answerable")]
        if scored:
            lit = lambda q: squash(q["answer"])
            by_file = {}
            for f, t in zip(files, folded):
                by_file.setdefault(f, []).append(t)
            # The most any arm could score at this chunk size: a literal that a boundary splits is in no chunk.
            ceiling = [any(lit(q) in t for t in by_file.get(q["file"], [])) for q, _ in scored]
            a1 = [lit(q) in folded[top[0]] for q, top in scored]
            a3 = [any(lit(q) in folded[i] for i in top[:3]) for q, top in scored]
            answer = {"answer_queries": len(scored), "answer_ceiling": sum(ceiling) / len(scored),
                      "answer_top1": sum(a1) / len(scored), "answer_top3": sum(a3) / len(scored),
                      "chars_top1": sum(len(texts[top[0]]) for _, top in scored) / len(scored),
                      "chars_top3": sum(sum(len(texts[i]) for i in top[:3]) for _, top in scored) / len(scored)}
    return {**answer, "mrr": sum(rr) / n,
            "hit1": sum(1 for r in ranks if r == 1) / n,
            "hit3": sum(1 for r in ranks if 0 < r <= 3) / n,
            "hit5": sum(1 for r in ranks if 0 < r <= 5) / n,
            "miss": sum(1 for r in ranks if r == 0) / n,
            "rr": rr, "tops": tops}


def bootstrap(a, b):
    da = [x - y for x, y in zip(a, b)]
    rng = random.Random(5)
    means = sorted(sum(rng.choice(da) for _ in da) / len(da) for _ in range(5000))
    return sum(da) / len(da), means[125], means[4875]


def default_root():
    """The repository this script sits in (scripts/bench/modelbench.py), else the current directory."""
    parents = pathlib.Path(__file__).resolve().parents
    return parents[2] if len(parents) > 2 else pathlib.Path.cwd()


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--ollama", default=os.environ.get("OLLAMA", "http://127.0.0.1:11434"))
    ap.add_argument("--root", default=str(default_root()),
                    help="a tree holding docs/, src/ and scripts/bench/queries-*.json")
    ap.add_argument("--arms", default=None, help="comma-separated arm names; default all")
    ap.add_argument("--model", action="append", default=[], metavar="ARM=MODEL",
                    help="serve an arm with another model name, for example eg2_q8=embeddinggemma-2:270m")
    ap.add_argument("--out", default="modelbench-out.json")
    ap.add_argument("--list", action="store_true", help="print the arms and exit")
    args = ap.parse_args()

    arms = {k: list(v) for k, v in ARMS.items()}
    for item in args.model:
        arm, _, model = item.partition("=")
        if arm not in arms or not model:
            sys.exit(f"--model {item!r}: expected ARM=MODEL with ARM one of {', '.join(arms)}")
        arms[arm][0] = model
    if args.list:
        for k, v in arms.items():
            m, d, q = v[:3]
            print(f"{k:<20} {m:<46} doc {d!r}  query {q!r}" + (f"  chunks of {v[3]} characters" if len(v) > 3 else ""))
        return
    wanted = args.arms.split(",") if args.arms else list(arms)
    unknown = [a for a in wanted if a not in arms]
    if unknown:
        sys.exit(f"unknown arm(s): {', '.join(unknown)}. Known: {', '.join(arms)}")

    base, root = args.ollama.rstrip("/"), pathlib.Path(args.root)
    specs = {name: json.loads((root / "scripts/bench" / qfile).read_text(encoding="utf-8"))
             for name, qfile in (("docs", "queries-docs.json"), ("code", "queries-code.json"))}
    subs = {"docs": "docs", "code": "src"}
    # A literal that is not in its file at all (the document was edited after the question was written) cannot be found by any
    # arm, so it is left out of answer recall; what is left is the denominator at every chunk size.
    for q in specs["docs"]["queries"]:
        doc = root / "docs" / q["file"]
        if not doc.is_file():
            sys.exit(f"docs: query target {q['file']} is missing under {root}: is --root the tree the queries were written for?")
        text = doc.read_text(encoding="utf-8", errors="replace")
        q["answerable"] = bool(q.get("answer")) and squash(q["answer"]) in squash(text)
    print(f"docs: {sum(q['answerable'] for q in specs['docs']['queries'])} of {len(specs['docs']['queries'])} answer literals are in their file", flush=True)
    targets = sorted({(arms[a][3] if len(arms[a]) > 3 else TARGET) for a in wanted})
    corpora_by_target = {}
    for target in targets:
        corpora = {}
        for name in ("docs", "code"):
            spec, sub = specs[name], subs[name]
            items = load_corpus(root, sub, target)
            files = {f for f, _ in items}
            missing = {q["file"] for q in spec["queries"]} - files
            if missing:
                sys.exit(f"{name}: query targets missing from the corpus under {root}: {sorted(missing)[:5]}")
            if len(items) < (300 if target <= TARGET * 2 else 20):
                sys.exit(f"{name}: only {len(items)} chunks loaded from {root / sub} at {target} characters")
            corpora[name] = {"items": items, "queries": spec["queries"]}
            median = sorted(len(c) for _, c in items)[len(items) // 2]
            print(f"target {target}: {name}: {len(items)} chunks from {len(files)} files, {len(spec['queries'])} queries, "
                  f"median {median} chars/chunk", flush=True)
        corpora_by_target[target] = corpora
    corpora = corpora_by_target[TARGET] if TARGET in corpora_by_target else next(iter(corpora_by_target.values()))

    result = {"arms": {}, "meta": {"batch": BATCH, "workers": WORKERS, "target_chars": TARGET,
                                   "chunks": {str(t): {k: len(v["items"]) for k, v in c.items()} for t, c in corpora_by_target.items()},
                                   "ollama": json.load(urllib.request.urlopen(base + "/api/version"))}}
    vectors = {}
    if mem_available_gb() is None:
        print("memory floors are not enforced: /proc/meminfo is not readable here", flush=True)
    for arm in wanted:
        model, dtpl, qtpl = arms[arm][:3]
        target = arms[arm][3] if len(arms[arm]) > 3 else TARGET
        corpora = corpora_by_target[target]
        batch = max(1, min(BATCH, REQUEST_CHARS // target))
        workers = WORKERS if target <= 5000 else 2
        free = mem_available_gb()
        if free is not None and free < MIN_FREE_GB:
            sys.exit(f"ABORT before {arm}: only {free:.1f} GB available in the VM (floor {MIN_FREE_GB})")
        print(f"\n=== {arm}  model={model}" + (f"  (VM available {free:.1f} GB)" if free is not None else ""), flush=True)
        call(base, "/api/embed", {"model": model, "input": ["warm"], "truncate": False})
        ps = [m for m in json.load(urllib.request.urlopen(base + "/api/ps"))["models"] if same_model(m["name"], model)]
        result["arms"][arm] = {"model": model, "ps": ps[:1], "target_chars": target, "batch": batch, "workers": workers}
        for cname, c in corpora.items():
            items, queries = c["items"], c["queries"]
            paths = placebo_paths(items, placebo_seed(arm)) if "placebo" in arm else [f for f, _ in items]
            t0 = time.time()
            cv, secs = embed_all(base, model, [dtpl.format(t=t, p=paths[i]) for i, (_, t) in enumerate(items)],
                                 f"{arm}/{cname}", batch, workers)
            took = time.time() - t0
            qv, _ = embed_all(base, model, [qtpl.format(t=q["query"]) for q in queries], f"{arm}/{cname}/queries")
            cv, qv = [norm(v) for v in cv], [norm(v) for v in qv]
            ev = evaluate(qv, cv, [f for f, _ in items], queries, [t for _, t in items])
            ev.update({"chunks": len(items), "embed_seconds": took, "chunks_per_second": len(items) / took,
                       "median_batch_s": sorted(secs)[len(secs) // 2], "max_batch_s": max(secs), "dims": len(cv[0])})
            result["arms"][arm][cname] = ev
            vectors[(arm, cname)] = (cv, qv)
            print(f"  {cname}: MRR {ev['mrr']:.3f} hit@1 {ev['hit1']:.2f} hit@3 {ev['hit3']:.2f} "
                  f"hit@5 {ev['hit5']:.2f} miss {ev['miss']:.2f} | {len(items)} chunks in {took:.0f}s = "
                  f"{ev['chunks_per_second']:.1f}/s (median batch {ev['median_batch_s']:.1f}s, "
                  f"max {ev['max_batch_s']:.1f}s)", flush=True)
            if "answer_top1" in ev:
                print(f"    answer in top 1 chunk {ev['answer_top1']:.2f} ({ev['chars_top1']:.0f} chars), "
                      f"in top 3 {ev['answer_top3']:.2f} ({ev['chars_top3']:.0f} chars), of {ev['answer_queries']} answerable queries "
                      f"(ceiling {ev['answer_ceiling']:.2f}: the literal is whole in a chunk of its file; the rest are cut by a boundary)", flush=True)
        call(base, "/api/embed", {"model": model, "keep_alive": 0})

    result["paired"], result["agreement"] = {}, {}
    for a, b in PAIRS:
        if a in result["arms"] and b in result["arms"]:
            for cname in corpora:
                m, lo, hi = bootstrap(result["arms"][a][cname]["rr"], result["arms"][b][cname]["rr"])
                result["paired"][f"{a} - {b} [{cname}]"] = {"mean": m, "lo": lo, "hi": hi}
    for a, b in AGREEMENT:
        for cname in corpora:
            if (a, cname) in vectors and (b, cname) in vectors:
                cs = sorted(dot(x, y) for x, y in zip(vectors[(a, cname)][0], vectors[(b, cname)][0]))
                ta, tb = result["arms"][a][cname]["tops"], result["arms"][b][cname]["tops"]
                ov = [len(set(x) & set(y)) / 10 for x, y in zip(ta, tb)]
                result["agreement"][f"{a} vs {b} [{cname}]"] = {
                    "cos_median": cs[len(cs) // 2], "cos_p05": cs[int(len(cs) * 0.05)], "cos_min": cs[0],
                    "top10_overlap_mean": sum(ov) / len(ov)}
    for arm in result["arms"].values():
        for cname in corpora:
            if cname in arm:
                arm[cname]["tops"] = None  # kept out of the output; only the reciprocal ranks are needed
    pathlib.Path(args.out).write_text(json.dumps(result))

    print("\n=== PAIRED (mean difference in reciprocal rank, 95% bootstrap interval)")
    for k, v in result["paired"].items():
        print(f"  {k:<44} {v['mean']:+.3f}  [{v['lo']:+.3f}, {v['hi']:+.3f}]")
    print("\n=== VECTOR AGREEMENT")
    for k, v in result["agreement"].items():
        print(f"  {k:<40} cos median {v['cos_median']:.4f} p05 {v['cos_p05']:.4f} min {v['cos_min']:.4f} "
              f"| top-10 overlap {v['top10_overlap_mean']:.2f}")
    print("\nwrote", args.out)


if __name__ == "__main__":
    main()
