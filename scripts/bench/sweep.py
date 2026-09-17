#!/usr/bin/env python3
"""
Sweep retrieval quality across models, chunk sizes and boundary modes.

Answers Q3 — "are the defaults earned?" — by building a chunk set for every
combination over ONE corpus of ONE content set, then asking the same questions of each
and scoring whether the right file came back.

    python scripts/bench/sweep.py            # the full sweep
    python scripts/bench/sweep.py --dry-run  # print the plan and the cost, build nothing

It builds its own corpus and deletes it afterwards, so nothing you already have is
touched. Chunk sets are the reason this is possible at all: identical content, identical
chunking, one variable changed, in the same instance at the same time. Before them this
needed two corpora and a promise that nothing else had drifted.

WHAT IT MEASURES. Rank of the file that should answer each query. Not relevance, not
whether the passage is any good — only whether retrieval put the right document in front
of you. That is the question a wrong default actually costs you.

WHAT IT DOES NOT MEASURE. Anything about queries nobody asked. The set in
queries-docs.json is written by someone who knows this corpus, which makes it a fair
comparison BETWEEN configurations and a poor estimate of absolute quality.
"""

import argparse
import json
import os
import pathlib
import re
import sys
import time
import urllib.error
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[2]
BASE = os.environ.get("DEXICON_URL", "http://127.0.0.1:8477")

# The corpus this builds and then removes. Named so that a crashed run leaves something
# obviously disposable rather than something you have to think about.
BENCH_CORPUS = "bench-sweep"

CHUNK_SIZES = [256, 768, 1536]
BOUNDARY_MODES = ["language-aware", "blank-line", "none"]
SEARCH_MODES = ["hybrid", "semantic", "keyword"]

LIMIT = 10


def token():
    """The caller's token, from the environment or the local .env — never a literal here."""
    if value := os.environ.get("DEXICON_TOKEN"):
        return value

    env = ROOT / ".env"
    if env.exists():
        for line in env.read_text(encoding="utf-8").splitlines():
            if line.startswith("DEXICON_BOOTSTRAP_TOKEN="):
                value = line.split("=", 1)[1].strip()
                if value:
                    return value

    sys.exit("Set DEXICON_TOKEN to a token with the search and ingest scopes.")


TOKEN = None


def call(method, path, body=None, timeout=300):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(
        f"{BASE}{path}",
        data=data,
        method=method,
        headers={"Content-Type": "application/json", "Authorization": f"Bearer {TOKEN}"},
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            raw = r.read()
            return json.loads(raw) if raw else None
    except urllib.error.HTTPError as e:
        detail = e.read().decode(errors="replace")[:400]
        raise SystemExit(f"{method} {path} -> {e.code}\n{detail}") from e


def models():
    """The embedding models actually pulled. Sweeping one you do not have is a long wait
    for a download, so the sweep covers what is here and says what it covered."""
    listing = call("GET", "/api/embedding-models")
    return [m["name"] for m in listing["models"]]


def wait_for_chunks(label, set_name, timeout=900):
    """Block until the set actually holds chunks.

    NOT "until no job is running". That was the first version, and it returned instantly
    every time — partly a race with the job appearing, and partly because the corpus was
    never indexing at all. Every configuration then scored MRR 0.000 against an empty
    index and looked like a finding. Waiting on the THING YOU NEED rather than on a
    proxy for it cannot be fooled the same way.
    """
    started = time.time()
    while time.time() - started < timeout:
        sets = call("GET", f"/api/corpora/{BENCH_CORPUS}/chunk-sets")
        me = next((s for s in sets if s["name"] == set_name), None)
        if me and me["chunkCount"] > 0 and me["pendingCount"] == 0:
            return time.time() - started
        time.sleep(2)
    raise SystemExit(f"{label}: no chunks after {timeout}s — is the corpus indexing at all?")


def score(target, queries, mode):
    """Rank of the first hit from the expected file, per query. None = not in the top k."""
    ranks = []
    for q in queries:
        result = call("POST", "/api/search", {
            "query": q["query"], "corpus": [target], "mode": mode, "limit": LIMIT,
        })
        hits = result["hits"]
        rank = next((i + 1 for i, h in enumerate(hits) if h["filePath"] == q["file"]), None)
        ranks.append(rank)
    return ranks


def summarise(ranks):
    n = len(ranks)
    at = lambda k: sum(1 for r in ranks if r is not None and r <= k)
    return {
        "queries": n,
        "hit@1": at(1),
        "hit@3": at(3),
        "hit@5": at(5),
        "mrr": round(sum(1 / r for r in ranks if r) / n, 4),
    }


def slug(model, size, boundary):
    """A chunk set name: lower case, no colons — search addresses sets as corpus:set."""
    base = re.sub(r"[^a-z0-9]+", "-", model.split(":")[0].lower())
    return f"{base}-{size}-{boundary.replace('-', '')}"[:60]


def main():
    global TOKEN

    ap = argparse.ArgumentParser()
    ap.add_argument("--dry-run", action="store_true", help="print the plan, build nothing")
    ap.add_argument("--keep", action="store_true", help="leave the bench corpus behind")
    ap.add_argument(
        "--queries", default="queries-docs.json",
        help="query set in scripts/bench/. Its `corpus` field names the folder to index.")
    args = ap.parse_args()

    spec = json.loads((ROOT / "scripts/bench" / args.queries).read_text(encoding="utf-8"))
    queries = spec["queries"]
    workspace_path = spec["corpus"]

    TOKEN = token()
    available = models()

    plan = [(m, s, b) for m in available for s in CHUNK_SIZES for b in BOUNDARY_MODES]
    print(f"corpus      ./{workspace_path}  ({args.queries})")
    print(f"models      {', '.join(available)}")
    print(f"chunk sizes {CHUNK_SIZES}")
    print(f"boundaries  {BOUNDARY_MODES}")
    print(f"modes       {SEARCH_MODES}")
    print(f"queries     {len(queries)}")
    print(f"\n{len(plan)} chunk sets, {len(plan) * len(SEARCH_MODES)} configurations, "
          f"{len(plan) * len(SEARCH_MODES) * len(queries):,} searches\n")

    if args.dry_run:
        return

    existing = [c["name"] for c in call("GET", "/api/corpora")]
    if BENCH_CORPUS in existing:
        print(f"removing a previous {BENCH_CORPUS}")
        call("DELETE", f"/api/corpora/{BENCH_CORPUS}")

    first_model, first_size, first_boundary = plan[0]
    print(f"creating {BENCH_CORPUS} over ./{workspace_path}")
    call("POST", "/api/corpora", {
        "name": BENCH_CORPUS,
        "description": "Q3 sweep. Built and deleted by scripts/bench/sweep.py.",
        "embeddingModel": first_model,
        "chunkSize": first_size,
        "chunkOverlap": max(1, first_size // 8),
        "boundaryMode": first_boundary,
        "workspacePath": workspace_path,
    })
    wait_for_chunks("initial index", "default")

    # The corpus's own set is the first combination; rename nothing, just record it.
    sets = call("GET", f"/api/corpora/{BENCH_CORPUS}/chunk-sets")
    default_set = sets[0]["name"]
    built = [((first_model, first_size, first_boundary), default_set)]

    results = []
    try:
        for i, (model, size, boundary) in enumerate(plan[1:], start=2):
            name = slug(model, size, boundary)
            print(f"[{i}/{len(plan)}] building {name}", flush=True)
            call("POST", f"/api/corpora/{BENCH_CORPUS}/chunk-sets", {
                "name": name,
                "embeddingModel": model,
                "chunkSize": size,
                "chunkOverlap": max(1, size // 8),
                "boundaryMode": boundary,
            })
            took = wait_for_chunks(name, name)
            print(f"           indexed in {took:.0f}s", flush=True)
            built.append(((model, size, boundary), name))

        sets = {s["name"]: s for s in call("GET", f"/api/corpora/{BENCH_CORPUS}/chunk-sets")}
        for (model, size, boundary), name in built:
            chunks = sets.get(name, {}).get("chunkCount", 0)
            if chunks == 0:
                raise SystemExit(f"{name} holds no chunks; refusing to score an empty index")

            for mode in SEARCH_MODES:
                ranks = score(f"{BENCH_CORPUS}:{name}", queries, mode)
                row = {
                    "model": model, "chunkSize": size, "boundaryMode": boundary,
                    "searchMode": mode, **summarise(ranks),
                }
                results.append(row)
                print(f"  {model:<26} {size:>5} {boundary:<14} {mode:<9} "
                      f"MRR {row['mrr']:.3f}  hit@1 {row['hit@1']}/{row['queries']}", flush=True)
    finally:
        out = ROOT / "scripts/bench" / args.queries.replace("queries-", "results-")
        out.write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")
        print(f"\nwrote {out.relative_to(ROOT)}")

        if not args.keep:
            print(f"removing {BENCH_CORPUS}")
            try:
                call("DELETE", f"/api/corpora/{BENCH_CORPUS}")
            except SystemExit as e:
                print(f"  could not remove it: {e}", file=sys.stderr)


if __name__ == "__main__":
    main()
