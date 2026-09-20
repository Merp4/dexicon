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

WHAT IT MEASURES. Answer recall at equal cost. Each query carries a short literal from the
document that answers it; a delivery is correct when the text returned contains that
literal. An assembled passage trivially wins on recall if it is allowed to be larger, so
the arms are matched per query on the characters actually returned:

    passage   POST /api/context at the budget, with an explicit neighbour width
    chunks    the search results' own content, cut to the length the passage arm produced

Matched on realised length rather than on the nominal budget, because the assembler
charges its block headers and disclosure text against maxChars and the chunks arm pays
neither. Matching on the budget would quietly hand the chunks arm more answer-bearing
characters and call it equal.

NEIGHBOURS IS THE POINT. The API defaults it to 0, and at 0 the assembler returns only the
chunks that ranked as hits — the same retrieval rendered differently, which cannot show
anything about assembling from a document. It is swept, with 0 kept as the control.

Reported per chunk size, because the claim is specifically that SMALL locators plus
assembly beat large chunks. A configuration wins by answering more queries with the same
number of characters.

OVERLAP. Also swept, for two reasons. Overlap exists so a passage crossing a boundary sits
wholly inside at least one chunk, and nothing here has ever measured whether that earns
its cost: at 50% it doubles the chunk count, the vectors and the embedding time. And it
decides whether a relevance profile over a document is worth building at all — counting
how many of a file's returned windows cover each line is only a signal when windows
overlap, so if overlap does not move recall here, there is nothing for that idea to stand
on.

What this does NOT measure is the split path. `CorpusIndexer.Split` divides a refused
chunk into halves that abut with no overlap, so the configured overlap is honoured by the
chunker and silently ignored by the splitter. A split only fires when the model refuses,
which this corpus is too small to provoke, so the arm below bounds how much that gap could
cost rather than measuring it.

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

# The shipped default, in tokens, whatever the chunk size. That is 39% of a 256-token
# chunk and 13% of a 768-token one: one setting meaning two quite different things, which
# is part of what this is here to show.
SHIPPED_OVERLAP = 100

# (size, overlap) pairs, written out rather than generated from fractions, because the
# fractions produced near-duplicates at one size and missed the shipped setting at the
# other. Each point below answers something:
#
#   0    the control. Does overlap earn its cost at all?
#   100  what actually ships today, scored rather than interpolated.
#   384  half of 768, which doubles the chunk count, the vectors and the embedding time.
#
# 256 gets no half-size point: 128 is so close to the shipped 100 that the pair would
# cost a full index to distinguish 39% from 50%. Its high-overlap end is already 100.
SETS = [
    (256, 0),
    (256, SHIPPED_OVERLAP),
    (768, 0),
    (768, SHIPPED_OVERLAP),
    (768, 384),
]

# Budgets in characters. 1,500 is the shipped max_chars_per_hit; 6,000 is roughly what an
# agent will tolerate for one call. The budget is what the passage arm is given; the
# chunks arm is then cut to what the passage arm actually produced, so the two are matched
# on realised length rather than on a number they spend differently.
BUDGETS = [1_500, 6_000]

# Chunks either side of a hit that the assembler pulls in. This is the dimension the
# experiment is actually about: at 0 the passage is only the chunks that ranked, which is
# the same retrieval rendered differently, and D-31's claim is that a SMALL locator plus
# the document around it beats a large chunk returned whole. 0 is kept as the control that
# shows what the expansion is worth.
NEIGHBOURS = [0, 2, 5]

LIMIT = 10
TOKEN = None


def plan():
    """The sets to build, minus any the chunker would refuse to produce."""
    # CodeChunker rejects overlap >= size, and stalls into near-duplicate fragments
    # unless a chunk is at least twice the overlap. Asserted rather than filtered: every
    # pair above is deliberate, so one that cannot be built is a mistake in the list.
    for size, overlap in SETS:
        assert overlap * 2 <= size, f"overlap {overlap} is too large for size {size}"
    return SETS


def env_value(name):
    """A variable from the environment, or from the .env beside the repo."""
    if value := os.environ.get(name):
        return value
    env = ROOT / ".env"
    if env.exists():
        for line in env.read_text(encoding="utf-8").splitlines():
            if line.startswith(f"{name}="):
                if value := line.split("=", 1)[1].strip():
                    return value
    return None


def token():
    """An admin bearer, because this builds and deletes a corpus.

    An API key cannot do that. D-28 made `admin` the password's alone and stripped it from
    keys, so a bootstrap token gets `[search, ingest]` and a 403 naming the missing scope
    on the first POST. Signing in is what the API intends for administration, and it is
    what the error message says to do.

    DEXICON_TOKEN still wins when set, for a caller that has its own bearer.
    """
    if value := os.environ.get("DEXICON_TOKEN_ADMIN"):
        return value

    if password := env_value("DEXICON_ADMIN_PASSWORD"):
        session = call("POST", "/api/session", {"password": password}, admin=False)
        if session and (bearer := session.get("token")):
            return bearer
        raise SystemExit("Signing in with DEXICON_ADMIN_PASSWORD returned no token.")

    raise SystemExit(
        "No admin credential. This builds and deletes a corpus, which needs the admin "
        "password: set DEXICON_ADMIN_PASSWORD in .env, or DEXICON_TOKEN_ADMIN to a "
        "session bearer. An API key cannot do it — see D-28.")


def call(method, path, body=None, timeout=180, admin=True):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(BASE + path, data=data, method=method)
    # Sign-in is the one call made before there is a bearer to send.
    if admin and TOKEN:
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
    print("  this proves a span DISCRIMINATES, not that it is the answer: a unique span")
    print("  can still be the wrong fact, and fifteen of these were before review")
    return True


# ── the two arms ──────────────────────────────────────────────────────────────

def passage_arm(target, query, budget, neighbours):
    """What POST /api/context assembles for the query, at the given budget and width.

    `neighbours` is explicit because the API defaults it to 0, and at 0 the assembler
    returns only the chunks that ranked as hits. That is two renderings of the same
    retrieval, not a passage taken from the document around the hit, so a sweep that left
    it at the default could not see the thing D-31 rests on however it came out.
    """
    result = call("POST", "/api/context", {
        "query": query, "corpus": [target], "mode": "hybrid",
        "limit": LIMIT, "maxChars": budget, "neighbours": neighbours,
    })
    return result.get("context") or ""


def chunks_arm(target, query, cap):
    """The search results' own text, in rank order, cut to `cap` characters.

    max_chars_per_hit=0 asks for whole chunks, so this is the "returned chunks" arm as it
    exists today. The cap is the length the passage arm actually produced for this same
    query, not the nominal budget: the assembler charges its block headers and disclosure
    text against maxChars and this arm pays neither, so matching on the budget would hand
    this arm more answer-bearing characters and call it equal cost.
    """
    if cap <= 0:
        return ""
    result = call("POST", "/api/search", {
        "query": query, "corpus": [target], "mode": "hybrid",
        "limit": LIMIT, "maxCharsPerHit": 0,
    })
    out, used = [], 0
    for hit in result["hits"]:
        text = hit.get("content") or ""
        if used + len(text) + 1 > cap:
            out.append(text[: cap - used])
            break
        out.append(text)
        used += len(text) + 1        # the newline this join adds is part of the cost
    return "\n".join(out)[:cap]


def score(target, queries, budget, neighbours):
    """Matched pairs: both arms answer the same query at the same realised length."""
    found = {"chunks": 0, "passage": 0}
    chars = {"chunks": 0, "passage": 0}

    for q in queries:
        passage = passage_arm(target, q["query"], budget, neighbours)
        chunks = chunks_arm(target, q["query"], len(passage))
        for name, text in (("passage", passage), ("chunks", chunks)):
            chars[name] += len(text)
            if q["answer"] in text:
                found[name] += 1

    return {
        name: {
            "found": found[name],
            "queries": len(queries),
            "recall": round(found[name] / len(queries), 4),
            "meanChars": round(chars[name] / len(queries)),
        }
        for name in ("chunks", "passage")
    }


# ── build ─────────────────────────────────────────────────────────────────────

def wait_for_chunks(label, set_name, timeout=1800):
    """Block until the set is fully indexed, and refuse it if any file failed.

    Nothing pending is not the same as everything indexed: a file can finish in `failed`
    while chunkCount is healthily positive. Its answer span is then unreachable and every
    query that needed it scores as a retrieval miss, so the run would report a number for
    a corpus it never fully read. This corpus is markdown and should never fail a file,
    which is the point — if one does, something is wrong and the score is not the thing to
    look at.
    """
    started = time.time()
    while time.time() - started < timeout:
        sets = call("GET", f"/api/corpora/{BENCH_CORPUS}/chunk-sets")
        me = next((s for s in sets if s["name"] == set_name), None)
        if me and me["chunkCount"] > 0 and me["pendingCount"] == 0:
            if me.get("failedCount"):
                raise SystemExit(
                    f"{label}: {me['failedCount']} file(s) failed to index; "
                    "refusing to score a corpus that is missing documents")
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

    sets_to_build = plan()

    print(f"\ncorpus      ./{spec['corpus']}")
    print(f"sets        {', '.join(f'size {s} overlap {o}' for s, o in sets_to_build)}")
    print(f"budgets     {BUDGETS} characters, given to the passage arm")
    print(f"neighbours  {NEIGHBOURS} chunks either side of a hit")
    print(f"queries     {len(queries)}")
    print(f"\n{len(sets_to_build)} sets, "
          f"{len(sets_to_build) * len(BUDGETS) * len(NEIGHBOURS) * 2 * len(queries):,} calls\n")

    if args.dry_run:
        return 0

    TOKEN = token()

    if BENCH_CORPUS in [c["name"] for c in call("GET", "/api/corpora")]:
        print(f"removing a previous {BENCH_CORPUS}")
        call("DELETE", f"/api/corpora/{BENCH_CORPUS}")

    (first_size, first_overlap), rest = sets_to_build[0], sets_to_build[1:]
    results = []

    # Everything that can create the corpus is inside the cleanup scope. Creating it and
    # then failing the first index left a partly built bench corpus behind, which the next
    # run deletes and rebuilds, so the cost of a timeout was paid twice and silently.
    try:
        print(f"creating {BENCH_CORPUS} over ./{spec['corpus']}")
        call("POST", "/api/corpora", {
            "name": BENCH_CORPUS,
            "description": "D-31 passage evaluation. Built and deleted by scripts/bench/passages.py.",
            "chunkSize": first_size,
            "chunkOverlap": first_overlap,
            "boundaryMode": "language-aware",
            "workspacePath": spec["corpus"],
        })
        wait_for_chunks("initial index", "default")
        built = [(first_size, first_overlap,
                  call("GET", f"/api/corpora/{BENCH_CORPUS}/chunk-sets")[0]["name"])]

        for size, overlap in rest:
            name = f"s{size}-o{overlap}"
            print(f"building {name}", flush=True)
            call("POST", f"/api/corpora/{BENCH_CORPUS}/chunk-sets", {
                "name": name, "chunkSize": size,
                "chunkOverlap": overlap, "boundaryMode": "language-aware",
            })
            print(f"  indexed in {wait_for_chunks(name, name):.0f}s", flush=True)
            built.append((size, overlap, name))

        sets = {s["name"]: s for s in call("GET", f"/api/corpora/{BENCH_CORPUS}/chunk-sets")}
        print()
        for size, overlap, name in built:
            held = sets.get(name, {}).get("chunkCount", 0)
            if held == 0:
                raise SystemExit(f"{name} holds no chunks; refusing to score an empty index")

            for budget in BUDGETS:
                for neighbours in NEIGHBOURS:
                    arms = score(f"{BENCH_CORPUS}:{name}", queries, budget, neighbours)
                    results.append({
                        "chunkSize": size, "overlap": overlap, "chunks": held,
                        "budget": budget, "neighbours": neighbours, **arms,
                    })
                    for arm in ("chunks", "passage"):
                        r = arms[arm]
                        print(f"  size {size:>4} ov {overlap:>3}  budget {budget:>5} "
                              f"nb {neighbours}  {arm:<8} "
                              f"recall {r['recall']:.3f}  ({r['found']}/{r['queries']})  "
                              f"mean {r['meanChars']:>6,} chars", flush=True)
                    print(flush=True)
    finally:
        # Nothing in here may raise. A DELETE against a corpus that was never created, or
        # against a server that has just gone away, would replace the indexing or scoring
        # error that brought us here with an HTTP error about the cleanup, and that is the
        # error nobody needs. Same shape as sweep.py.
        out = ROOT / "scripts/bench" / args.queries.replace("queries-", "passages-")
        try:
            out.write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")
            print(f"wrote {out.relative_to(ROOT)}")
        except OSError as e:
            print(f"could not write results: {e}", file=sys.stderr)

        if not args.keep:
            print(f"removing {BENCH_CORPUS}")
            try:
                call("DELETE", f"/api/corpora/{BENCH_CORPUS}")
            except SystemExit as e:
                print(f"  could not remove it: {e}", file=sys.stderr)

    return 0


if __name__ == "__main__":
    sys.exit(main())
