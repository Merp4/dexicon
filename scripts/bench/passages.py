#!/usr/bin/env python3
"""
Score what a caller actually READS, not which file ranked first.

    python scripts/bench/passages.py --check          # validate the ground truth only
    python scripts/bench/passages.py --dry-run        # print the plan, build nothing
    python scripts/bench/passages.py                  # the comparison

THE QUESTION. D-31 turns a chunk from the unit a search RETURNS into the unit a search
FINDS, with the passage reassembled from the document around the hit. It says so, and then
says the claim is unmeasured: "nothing has scored the passages assembled from a document
around a small-chunk hit, and that is what the design rests on". Its Revisit-if names the
experiment — assembled passages measuring worse than returned chunks — as the one result
that would overturn the entry. This is that experiment.

sweep.py cannot answer it. It scores the rank of the expected file, which is the right
metric for a locator and says nothing about whether the text handed back contains the
answer. That is the whole disagreement D-31 has with it: "smaller chunks flatter this
metric" is an objection about passage quality that file rank cannot see.

WHAT IT MEASURES. Answer recall at a fixed character budget. Each query carries a short
literal from the document that answers it; a delivery is correct when the text returned
contains that literal. Both arms get the SAME budget, which is the control that matters:
an assembled passage trivially wins on recall if it is allowed to be larger, so the
comparison is recall at equal cost, not recall.

    chunks    the search results' own content, concatenated until the budget is spent
    passage   POST /api/context with the same budget

Reported per chunk size, because the claim is specifically that SMALL locators plus
assembly beat large chunks. A configuration wins by answering more queries with the same
number of characters.

WHAT IT DOES NOT MEASURE. Whether the passage reads well, whether it is the best passage,
or anything about queries nobody asked. Containment is a floor: text holding the answer
can still be padded with noise, and this will not say so. It is a fair comparison BETWEEN
arms on identical queries, and a poor estimate of absolute quality.

WHY CONTAINMENT AND NOT A JUDGE. An LLM judge would score presentation as well as content,
needs its own calibration against something, and is not reproducible run to run. A literal
that occurs exactly once in the corpus is checkable, cheap, and cannot drift. --check
enforces that uniqueness, so a span too generic to discriminate fails the run rather than
quietly scoring everything correct.
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

BENCH_CORPUS = "bench-passages"

# The sizes the question is about: D-31's case is that small locators plus assembly beat
# a large chunk returned whole. 768 is today's default and is the thing to beat.
CHUNK_SIZES = [256, 768]

# Budgets in characters, applied identically to both arms. 1,500 is the shipped
# max_chars_per_hit; 6,000 is roughly what an agent will tolerate for one call.
BUDGETS = [1_500, 6_000]

LIMIT = 10
TOKEN = None


def token():
    if value := os.environ.get("DEXICON_TOKEN"):
        return value
    env = ROOT / ".env"
    if env.exists():
        for line in env.read_text(encoding="utf-8").splitlines():
            if line.startswith("DEXICON_BOOTSTRAP_TOKEN="):
                if value := line.split("=", 1)[1].strip():
                    return value
    raise SystemExit("No token. Set DEXICON_TOKEN or DEXICON_BOOTSTRAP_TOKEN in .env.")


def call(method, path, body=None, timeout=180):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(BASE + path, data=data, method=method)
    req.add_header("Authorization", f"Bearer {TOKEN}")
    if data:
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            raw = r.read()
            return json.loads(raw) if raw else None
    except urllib.error.HTTPError as e:
        raise SystemExit(f"{method} {path} -> {e.code}: {e.read().decode()[:400]}")


# ── ground truth ──────────────────────────────────────────────────────────────

def check(spec):
    """Every answer span must exist, in its own file, exactly once in the corpus.

    This is the guard on the part of the experiment I authored. A span that appears in
    several files does not prove retrieval found the right passage; one that appears
    nowhere scores every configuration zero and looks like a finding. Either way the
    number would be worthless, so the run refuses rather than reporting it.
    """
    corpus_dir = ROOT / spec["corpus"]
    files = {p.relative_to(corpus_dir).as_posix(): p.read_text(encoding="utf-8")
             for p in corpus_dir.rglob("*.md")}
    assert files, f"no documents under {corpus_dir}"

    problems = []
    for q in spec["queries"]:
        answer, want = q.get("answer"), q["file"]

        if not answer:
            problems.append(f"no answer span: {q['query']}")
            continue
        if want not in files:
            problems.append(f"expected file missing from the corpus: {want}")
            continue

        hits = {name: text.count(answer) for name, text in files.items() if answer in text}
        total = sum(hits.values())

        if total == 0:
            problems.append(f"span not found in the corpus: {answer!r}")
        elif total > 1:
            where = ", ".join(f"{n}x{c}" for n, c in sorted(hits.items()))
            problems.append(f"span occurs {total} times ({where}): {answer!r}")
        elif want not in hits:
            problems.append(f"span is in {list(hits)[0]}, not the expected {want}: {answer!r}")

    print(f"{len(spec['queries'])} queries, {len(files)} documents")
    if problems:
        print(f"\n{len(problems)} problem(s):")
        for p in problems:
            print("  " + p)
        return False

    print("every answer span occurs exactly once, in its expected file")
    return True


# ── the two arms ──────────────────────────────────────────────────────────────

def chunks_arm(target, query, budget):
    """The search results' own text, taken in rank order until the budget is spent.

    max_chars_per_hit=0 asks for whole chunks, so this is the "returned chunks" arm as it
    exists today, truncated to the same budget the passage arm gets.
    """
    result = call("POST", "/api/search", {
        "query": query, "corpus": [target], "mode": "hybrid",
        "limit": LIMIT, "maxCharsPerHit": 0,
    })
    out, used = [], 0
    for hit in result["hits"]:
        text = hit.get("content") or ""
        if used + len(text) > budget:
            out.append(text[: budget - used])
            break
        out.append(text)
        used += len(text)
    return "\n".join(out)


def passage_arm(target, query, budget):
    """What POST /api/context assembles for the same query and the same budget."""
    result = call("POST", "/api/context", {
        "query": query, "corpus": [target], "mode": "hybrid",
        "limit": LIMIT, "maxChars": budget,
    })
    return result.get("context") or ""


def score(target, queries, budget):
    rows = {}
    for name, arm in (("chunks", chunks_arm), ("passage", passage_arm)):
        found, chars = 0, 0
        for q in queries:
            text = arm(target, q["query"], budget)
            chars += len(text)
            if q["answer"] in text:
                found += 1
        rows[name] = {
            "found": found,
            "queries": len(queries),
            "recall": round(found / len(queries), 4),
            "meanChars": round(chars / len(queries)),
        }
    return rows


# ── build ─────────────────────────────────────────────────────────────────────

def wait_for_chunks(label, set_name, timeout=1800):
    started = time.time()
    while time.time() - started < timeout:
        sets = call("GET", f"/api/corpora/{BENCH_CORPUS}/chunk-sets")
        me = next((s for s in sets if s["name"] == set_name), None)
        if me and me["chunkCount"] > 0 and me["pendingCount"] == 0:
            return time.time() - started
        time.sleep(2)
    raise SystemExit(f"{label}: no chunks after {timeout}s")


def main():
    global TOKEN

    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="validate the ground truth and stop")
    ap.add_argument("--dry-run", action="store_true", help="print the plan, build nothing")
    ap.add_argument("--keep", action="store_true", help="leave the bench corpus behind")
    ap.add_argument("--queries", default="queries-docs.json")
    args = ap.parse_args()

    spec = json.loads((ROOT / "scripts/bench" / args.queries).read_text(encoding="utf-8"))
    queries = spec["queries"]

    if args.check:
        return 0 if check(spec) else 1

    # Always, not only under --check: a bad span makes every number meaningless, and a
    # run that reports one anyway is worse than a run that refuses.
    if not check(spec):
        raise SystemExit("ground truth is not sound; refusing to score against it")

    print(f"\ncorpus      ./{spec['corpus']}")
    print(f"chunk sizes {CHUNK_SIZES}")
    print(f"budgets     {BUDGETS} characters, applied to both arms")
    print(f"queries     {len(queries)}")
    print(f"\n{len(CHUNK_SIZES)} sets, {len(CHUNK_SIZES) * len(BUDGETS) * 2 * len(queries):,} calls\n")

    if args.dry_run:
        return 0

    TOKEN = token()

    if BENCH_CORPUS in [c["name"] for c in call("GET", "/api/corpora")]:
        print(f"removing a previous {BENCH_CORPUS}")
        call("DELETE", f"/api/corpora/{BENCH_CORPUS}")

    first, rest = CHUNK_SIZES[0], CHUNK_SIZES[1:]
    print(f"creating {BENCH_CORPUS} over ./{spec['corpus']}")
    call("POST", "/api/corpora", {
        "name": BENCH_CORPUS,
        "description": "D-31 passage evaluation. Built and deleted by scripts/bench/passages.py.",
        "chunkSize": first,
        "chunkOverlap": max(1, first // 8),
        "boundaryMode": "language-aware",
        "workspacePath": spec["corpus"],
    })
    wait_for_chunks("initial index", "default")
    built = [(first, call("GET", f"/api/corpora/{BENCH_CORPUS}/chunk-sets")[0]["name"])]

    results = []
    try:
        for size in rest:
            name = f"size-{size}"
            print(f"building {name}", flush=True)
            call("POST", f"/api/corpora/{BENCH_CORPUS}/chunk-sets", {
                "name": name, "chunkSize": size,
                "chunkOverlap": max(1, size // 8), "boundaryMode": "language-aware",
            })
            print(f"  indexed in {wait_for_chunks(name, name):.0f}s", flush=True)
            built.append((size, name))

        sets = {s["name"]: s for s in call("GET", f"/api/corpora/{BENCH_CORPUS}/chunk-sets")}
        print()
        for size, name in built:
            held = sets.get(name, {}).get("chunkCount", 0)
            if held == 0:
                raise SystemExit(f"{name} holds no chunks; refusing to score an empty index")

            for budget in BUDGETS:
                arms = score(f"{BENCH_CORPUS}:{name}", queries, budget)
                results.append({"chunkSize": size, "chunks": held, "budget": budget, **arms})
                for arm in ("chunks", "passage"):
                    r = arms[arm]
                    print(f"  size {size:>4}  budget {budget:>5}  {arm:<8} "
                          f"recall {r['recall']:.3f}  ({r['found']}/{r['queries']})  "
                          f"mean {r['meanChars']:>6,} chars", flush=True)
                print(flush=True)
    finally:
        out = ROOT / "scripts/bench" / args.queries.replace("queries-", "passages-")
        out.write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")
        print(f"wrote {out.relative_to(ROOT)}")
        if not args.keep:
            call("DELETE", f"/api/corpora/{BENCH_CORPUS}")
            print(f"removed {BENCH_CORPUS}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
