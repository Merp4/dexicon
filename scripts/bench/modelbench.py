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
TEXT_EXT = {".md", ".cs", ".csproj", ".json", ".props", ".yml", ".yaml", ".txt"}
MAX_FILE_BYTES = 256 * 1024
SKIP_DIRS = {"bin", "obj", "node_modules", ".git"}
MIN_FREE_GB = 10

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

# Pairs worth a paired interval, when both arms ran.
PAIRS = [
    ("eg2_q8", "v1_framed"), ("eg2_nvfp4", "v1_framed"), ("eg2_nvfp4", "eg2_q8"),
    ("v1_raw", "v1_framed"), ("nomic", "v1_framed"), ("v1_framed_repeat", "v1_framed"),
    ("v1_title", "v1_framed"), ("eg2_q8_title", "eg2_q8"), ("eg2_q8_title", "v1_title"),
    ("eg2_q8_codeprompt", "eg2_q8_title"), ("eg2_q8_codeprompt", "v1_title"),
] + [(f"v1_title_placebo_s{n}", base) for n in range(1, 11) for base in ("v1_framed", "v1_title")]
AGREEMENT = [("eg2_q8", "eg2_nvfp4"), ("v1_framed", "v1_framed_repeat"), ("eg2_q8", "eg2_q8_title")]


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


def chunks_of(path):
    text = path.read_text(encoding="utf-8", errors="replace").replace("\r\n", "\n")
    out, cur = [], ""

    def flush():
        nonlocal cur
        if cur.strip():
            out.append(cur.strip())
        cur = ""

    for para in text.split("\n\n"):
        if len(para) > HARD:  # a long paragraph is split on line boundaries
            flush()
            piece = ""
            for line in para.split("\n"):
                if piece and len(piece) + len(line) + 1 > TARGET:
                    out.append(piece.strip())
                    piece = ""
                piece += line[:HARD] + "\n"
            if piece.strip():
                out.append(piece.strip())
            continue
        if cur and len(cur) + len(para) + 2 > TARGET:
            flush()
        cur += para + "\n\n"
    flush()
    return out


def load_corpus(root, sub):
    items = []
    base = root / sub
    for path in sorted(p for p in base.rglob("*") if p.is_file()):  # sorted: placebo draws depend on this order
        if path.suffix.lower() not in TEXT_EXT or path.stat().st_size > MAX_FILE_BYTES:
            continue
        rel = path.relative_to(base).as_posix()
        if SKIP_DIRS.intersection(path.relative_to(base).parts[:-1]):
            continue
        items.extend((rel, c) for c in chunks_of(path))
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


def embed_all(base, model, texts, label):
    """Batches of BATCH, WORKERS in flight. Any batch that fails twice, or returns nulls, raises."""
    batches = [texts[i:i + BATCH] for i in range(0, len(texts), BATCH)]
    secs, vecs = [0.0] * len(batches), [None] * len(batches)

    def one(i):
        t0 = time.time()
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
                    raise RuntimeError(f"{label}: batch {i} failed twice: {e}") from e

    with cf.ThreadPoolExecutor(WORKERS) as ex:
        list(ex.map(one, range(len(batches))))
    return [x for b in vecs for x in b], secs


def norm(v):
    n = math.sqrt(sum(x * x for x in v)) or 1.0
    return [x / n for x in v]


def dot(a, b):
    return sum(x * y for x, y in zip(a, b))


def evaluate(qvecs, cvecs, files, queries):
    ranks, tops = [], []
    for qv, q in zip(qvecs, queries):
        scores = sorted(((dot(qv, cv), i) for i, cv in enumerate(cvecs)), reverse=True)[:10]
        tops.append([i for _, i in scores])
        ranks.append(next((k + 1 for k, (_, i) in enumerate(scores) if files[i] == q["file"]), 0))
    rr = [1 / r if r else 0.0 for r in ranks]
    n = len(ranks)
    return {"mrr": sum(rr) / n,
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
        for k, (m, d, q) in arms.items():
            print(f"{k:<20} {m:<46} doc {d!r}  query {q!r}")
        return
    wanted = args.arms.split(",") if args.arms else list(arms)
    unknown = [a for a in wanted if a not in arms]
    if unknown:
        sys.exit(f"unknown arm(s): {', '.join(unknown)}. Known: {', '.join(arms)}")

    base, root = args.ollama.rstrip("/"), pathlib.Path(args.root)
    corpora = {}
    for name, sub, qfile in (("docs", "docs", "queries-docs.json"), ("code", "src", "queries-code.json")):
        spec = json.loads((root / "scripts/bench" / qfile).read_text(encoding="utf-8"))
        items = load_corpus(root, sub)
        files = {f for f, _ in items}
        missing = {q["file"] for q in spec["queries"]} - files
        if missing:
            sys.exit(f"{name}: query targets missing from the corpus under {root}: {sorted(missing)[:5]}")
        if len(items) < 300:
            sys.exit(f"{name}: only {len(items)} chunks loaded from {root / sub}")
        corpora[name] = {"items": items, "queries": spec["queries"]}
        median = sorted(len(c) for _, c in items)[len(items) // 2]
        print(f"{name}: {len(items)} chunks from {len(files)} files, {len(spec['queries'])} queries, "
              f"median {median} chars/chunk", flush=True)

    result = {"arms": {}, "meta": {"batch": BATCH, "workers": WORKERS, "target_chars": TARGET,
                                   "chunks": {k: len(v["items"]) for k, v in corpora.items()},
                                   "ollama": json.load(urllib.request.urlopen(base + "/api/version"))}}
    vectors = {}
    for arm in wanted:
        model, dtpl, qtpl = arms[arm]
        free = mem_available_gb()
        if free is not None and free < MIN_FREE_GB:
            sys.exit(f"ABORT before {arm}: only {free:.1f} GB available in the VM (floor {MIN_FREE_GB})")
        print(f"\n=== {arm}  model={model}" + (f"  (VM available {free:.1f} GB)" if free is not None else ""), flush=True)
        call(base, "/api/embed", {"model": model, "input": ["warm"], "truncate": False})
        ps = [m for m in json.load(urllib.request.urlopen(base + "/api/ps"))["models"] if same_model(m["name"], model)]
        result["arms"][arm] = {"model": model, "ps": ps[:1]}
        for cname, c in corpora.items():
            items, queries = c["items"], c["queries"]
            paths = placebo_paths(items, placebo_seed(arm)) if "placebo" in arm else [f for f, _ in items]
            t0 = time.time()
            cv, secs = embed_all(base, model, [dtpl.format(t=t, p=paths[i]) for i, (_, t) in enumerate(items)],
                                 f"{arm}/{cname}")
            took = time.time() - t0
            qv, _ = embed_all(base, model, [qtpl.format(t=q["query"]) for q in queries], f"{arm}/{cname}/queries")
            cv, qv = [norm(v) for v in cv], [norm(v) for v in qv]
            ev = evaluate(qv, cv, [f for f, _ in items], queries)
            ev.update({"chunks": len(items), "embed_seconds": took, "chunks_per_second": len(items) / took,
                       "median_batch_s": sorted(secs)[len(secs) // 2], "max_batch_s": max(secs), "dims": len(cv[0])})
            result["arms"][arm][cname] = ev
            vectors[(arm, cname)] = (cv, qv)
            print(f"  {cname}: MRR {ev['mrr']:.3f} hit@1 {ev['hit1']:.2f} hit@3 {ev['hit3']:.2f} "
                  f"hit@5 {ev['hit5']:.2f} miss {ev['miss']:.2f} | {len(items)} chunks in {took:.0f}s = "
                  f"{ev['chunks_per_second']:.1f}/s (median batch {ev['median_batch_s']:.1f}s, "
                  f"max {ev['max_batch_s']:.1f}s)", flush=True)
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
