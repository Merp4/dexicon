#!/usr/bin/env python3
"""
Abstention study: can any retrieval signal tell a prompt the corpus can answer from one it cannot?

A relevance gate would drop the hits for a prompt whose signal is below a threshold. It is only
worth building if one signal keeps almost every answerable query and passes few of the ones
the corpus has nothing for. This script measures that and applies a decision rule that is fixed
here, before any run. See docs/benchmarks.md ("Abstention study") for the result.

Signals, per query and chunk set (higher means more likely answerable):

    top1     semantic top-1 cosine
    gap      semantic top-1 minus top-5 cosine
    overlap  chunks that the semantic and the keyword top-5 share (same file and start line)
    kwtop    keyword top-1 raw score (sparse IDF dot product)
    gap|overlap  passes when gap >= tg OR overlap >= to

Groups:

    on-corpus   the committed queries of scripts/bench/queries-*.json, each with one answering file
    identifier  bare identifiers an agent greps for (file names, setting names, tool names)
    off-corpus  prompts from other projects' Claude Code transcripts, labelled by hand as "the
                corpus holds no answer" ("off") or "it does" ("answerable", left out of the negatives)

Privacy. The prompts are private text. `candidates` and `sheet` write them only to the paths
you give, which belong outside the repository. `collect` and `report` write numbers and
opaque ids, never a prompt.

Method. Halves are fixed by a hash of the id, not by outcome. A threshold is chosen on half A as the
largest value that keeps at least RETENTION of the on-corpus and identifier queries of A, and is
then scored on half B, which is what the report states. One threshold per chunk set, because a
floor belongs to the model and the framing that produced it.

Decision rule (fixed before the first run, never edited after): a gate is worth building only if
ONE signal meets both limits on half B for EVERY chunk set measured, both corpora and both models:

    retention of on-corpus + identifier queries   >= RETENTION  (0.95)
    share of off-corpus prompts that still pass   <= OFF_PASS   (0.30)

Otherwise the numbers are recorded as the trigger that was not met, and no gate is built.

Usage (the key comes from the environment, as in retrieval-bench.py):

    DEXICON_TOKEN=dex_... python scripts/bench/abstention-probe.py candidates --out C:/study/candidates.jsonl --exclude dexicon
    DEXICON_TOKEN=dex_... python scripts/bench/abstention-probe.py collect --set docs:gemma --queries scripts/bench/queries-docs.json \\
        --identifiers C:/study/identifiers-docs.json --candidates C:/study/candidates.jsonl --out C:/study/signals-docs-gemma.json
    python scripts/bench/abstention-probe.py report --signals C:/study/signals-docs-gemma.json ... --labels C:/study/labels.json --corpus docs \\
        --out scripts/bench/results-abstention.json
    python scripts/bench/abstention-probe.py selftest
"""

import argparse
import glob
import hashlib
import json
import math
import os
import sys
import urllib.request

BASE = os.environ.get("DEXICON_URL", "http://127.0.0.1:8477").rstrip("/")
TOKEN = os.environ.get("DEXICON_TOKEN", "")

RETENTION = 0.95
OFF_PASS = 0.30
SIGNALS = ("top1", "gap", "overlap", "kwtop")
OR_RULE = "gap|overlap"


def qid(text):
    """An opaque id for a text: not a way back to it for anything longer than a guess."""
    return hashlib.sha1(text.encode("utf-8")).hexdigest()[:10]


def half(identifier):
    """A or B, from the id alone, so the split cannot depend on what a query scored."""
    return "A" if int(hashlib.sha1(identifier.encode("utf-8")).hexdigest()[:2], 16) % 2 == 0 else "B"


# ------------------------------------------------------------------------------------------ candidates

def candidates(args):
    seen, rows = set(), []
    excluded = [e.lower() for e in args.exclude]
    for path in glob.glob(os.path.expanduser("~/.claude/projects/*/*.jsonl")):
        project = os.path.basename(os.path.dirname(path))
        if any(e in project.lower() for e in excluded):
            continue
        try:
            handle = open(path, encoding="utf-8")
        except OSError:
            continue
        with handle:
            for line in handle:
                try:
                    o = json.loads(line)
                except ValueError:
                    continue
                c = (o.get("message") or {}).get("content") if o.get("type") == "user" else None
                if not isinstance(c, str):
                    continue
                c = " ".join(c.split())
                if not (args.min_chars <= len(c) <= args.max_chars) or c.startswith(("/", "<")) or "system-reminder" in c:
                    continue
                if c in seen:
                    continue
                seen.add(c)
                rows.append({"id": qid(c), "text": c, "project": project})
    rows.sort(key=lambda r: r["id"])
    rows = rows[: args.count]
    with open(args.out, "w", encoding="utf-8") as out:
        for r in rows:
            out.write(json.dumps(r, ensure_ascii=False) + "\n")
    print("wrote %d candidates from %d distinct prompts" % (len(rows), len(seen)))


# ----------------------------------------------------------------------------------------------- search

def search(corpus_set, query, mode, limit=5):
    body = {"query": query, "corpus": [corpus_set], "mode": mode, "limit": limit, "distinctTitles": False, "maxCharsPerHit": 160}
    req = urllib.request.Request(
        BASE + "/api/search", data=json.dumps(body).encode(),
        headers={"Content-Type": "application/json", "Authorization": "Bearer " + TOKEN})
    with urllib.request.urlopen(req, timeout=120) as r:
        d = json.load(r)
    return d.get("hits") or d.get("Hits") or []


def field(hit, name):
    return hit.get(name, hit.get(name[0].upper() + name[1:]))


def signals(corpus_set, text):
    sem = search(corpus_set, text, "semantic")
    kw = search(corpus_set, text, "keyword")
    if len(sem) < 5:
        return None
    key = lambda h: (field(h, "filePath"), field(h, "startLine"))  # noqa: E731
    shared = {key(h) for h in sem} & {key(h) for h in kw}
    return {
        "top1": field(sem[0], "score"),
        "gap": field(sem[0], "score") - field(sem[4], "score"),
        "overlap": float(len(shared)),
        "kwtop": float(field(kw[0], "score")) if kw else 0.0,
    }


def collect(args):
    if not TOKEN:
        sys.exit("DEXICON_TOKEN is not set")
    groups = []
    for q in json.load(open(args.queries, encoding="utf-8"))["queries"]:
        groups.append(("on", q["query"]))
    if args.identifiers:
        for q in json.load(open(args.identifiers, encoding="utf-8")):
            groups.append(("ident", q))
    wanted = None
    if args.labels:
        labels = json.load(open(args.labels, encoding="utf-8"))["labels"]
        wanted = {i for i, v in labels.items() if args.corpus in v}
    for line in open(args.candidates, encoding="utf-8"):
        r = json.loads(line)
        if wanted is None or r["id"] in wanted:
            groups.append(("cand", r["text"], r["id"]))

    records, short = [], 0
    for g in groups:
        text = g[1]
        s = signals(args.set, text)
        if s is None:
            short += 1
            continue
        s.update({"id": g[2] if len(g) > 2 else qid(text), "group": g[0]})
        records.append(s)
    json.dump({"set": args.set, "records": records, "skipped_fewer_than_5_hits": short}, open(args.out, "w"), indent=1)
    print("%s: %d records, %d skipped for fewer than 5 semantic hits" % (args.set, len(records), short))


def sheet(args):
    """A local file for the person labelling: each prompt with what the corpus returns for it."""
    out = open(args.out, "w", encoding="utf-8")
    for line in open(args.candidates, encoding="utf-8"):
        r = json.loads(line)
        out.write("## %s\n%s\n" % (r["id"], r["text"][: args.chars]))
        for h in search(args.set, r["text"], "hybrid", 3):
            out.write("- %s :: %s\n" % (field(h, "filePath") or field(h, "title"), (field(h, "text") or "")[:150].replace("\n", " ")))
        out.write("\n")
    out.close()
    print("wrote", args.out)


# -------------------------------------------------------------------------------------------- statistics

def auc(pos, neg):
    if not pos or not neg:
        return float("nan")
    wins = sum((p > n) + 0.5 * (p == n) for p in pos for n in neg)
    return wins / (len(pos) * len(neg))


def wilson(k, n, z=1.96):
    if n == 0:
        return (float("nan"), float("nan"))
    p = k / n
    d = 1 + z * z / n
    c = (p + z * z / (2 * n)) / d
    w = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / d
    return (c - w, c + w)


def passes(rec, rule):
    if rule[0] == "or":
        return rec["gap"] >= rule[1] or rec["overlap"] >= rule[2]
    return rec[rule[0]] >= rule[1]


def rate(recs, rule):
    return sum(passes(r, rule) for r in recs) / len(recs) if recs else float("nan")


def threshold_single(pos_a, name):
    """The largest t keeping at least RETENTION of half A's positives."""
    values = sorted(r[name] for r in pos_a)
    misses = int(math.floor(len(values) * (1 - RETENTION) + 1e-9))
    return values[misses]


def threshold_or(pos_a, neg_a):
    """The (tg, to) that keeps RETENTION of A's positives and lets the fewest of A's negatives through."""
    best = None
    for tg in sorted({r["gap"] for r in pos_a}) + [float("inf")]:
        for to in range(0, 7):
            rule = ("or", tg, float(to))
            if sum(passes(r, rule) for r in pos_a) / len(pos_a) < RETENTION:
                continue
            off = rate(neg_a, rule)
            key = (off, -tg if tg != float("inf") else -1e9, -to)
            if best is None or key < best[0]:
                best = (key, rule)
    return best[1] if best else None


def evaluate(records, labels, corpus):
    pos = [r for r in records if r["group"] in ("on", "ident")]
    neg, answerable, unsure = [], [], 0
    for r in records:
        if r["group"] != "cand":
            continue
        v = labels.get(r["id"], {}).get(corpus)
        if v == "off":
            neg.append(r)
        elif v == "answerable":
            answerable.append(r)
        else:
            unsure += 1
    A = lambda rs: [r for r in rs if half(r["id"]) == "A"]  # noqa: E731
    B = lambda rs: [r for r in rs if half(r["id"]) == "B"]  # noqa: E731
    out = {"n": {"positives": len(pos), "off": len(neg), "answerable": len(answerable), "unlabelled_or_unsure": unsure,
                 "A_pos": len(A(pos)), "A_off": len(A(neg)), "B_pos": len(B(pos)), "B_off": len(B(neg))},
           "signals": {}}
    for name in SIGNALS + (OR_RULE,):
        entry = {}
        if name == OR_RULE:
            rule = threshold_or(A(pos), A(neg)) if A(pos) else None
            entry["threshold"] = None if rule is None else {"gap": rule[1] if rule[1] != float("inf") else None, "overlap": rule[2]}
            use = rule
            entry["auc"] = None
        else:
            use = (name, threshold_single(A(pos), name)) if A(pos) else None
            entry["threshold"] = None if use is None else use[1]
            entry["auc"] = {"all": round(auc([r[name] for r in pos], [r[name] for r in neg]), 3),
                            "A": round(auc([r[name] for r in A(pos)], [r[name] for r in A(neg)]), 3),
                            "B": round(auc([r[name] for r in B(pos)], [r[name] for r in B(neg)]), 3)}
        if use is not None and B(pos) and B(neg):
            keep_b, off_b = rate(B(pos), use), rate(B(neg), use)
            kb = sum(passes(r, use) for r in B(pos))
            ob = sum(passes(r, use) for r in B(neg))
            entry["A"] = {"retention": round(rate(A(pos), use), 3), "off_pass": round(rate(A(neg), use), 3)}
            entry["B"] = {"retention": round(keep_b, 3), "retention_ci": [round(x, 3) for x in wilson(kb, len(B(pos)))],
                          "off_pass": round(off_b, 3), "off_pass_ci": [round(x, 3) for x in wilson(ob, len(B(neg)))]}
            entry["rule_met"] = bool(keep_b >= RETENTION and off_b <= OFF_PASS)
        else:
            entry["rule_met"] = False
        out["signals"][name] = entry
    return out


def report(args):
    labels_doc = json.load(open(args.labels, encoding="utf-8"))
    labels = labels_doc["labels"]
    per_set = {}
    for path in args.signals:
        d = json.load(open(path))
        per_set[d["set"]] = evaluate(d["records"], labels, args.corpus)
    verdict = {}
    for name in SIGNALS + (OR_RULE,):
        verdict[name] = bool(per_set) and all(v["signals"][name]["rule_met"] for v in per_set.values())
    result = {"corpus": args.corpus, "labeller": labels_doc.get("labeller"), "reviewed_by_owner": labels_doc.get("reviewed", {}).get(args.corpus),
              "rule": {"retention": RETENTION, "off_pass_max": OFF_PASS}, "sets": per_set, "rule_met_on_every_set": verdict}
    if args.out:
        json.dump(result, open(args.out, "w"), indent=1)
    for s, v in per_set.items():
        print("%s  n=%s" % (s, v["n"]))
        for name, e in v["signals"].items():
            b = e.get("B")
            print("  %-12s threshold %-28s auc %-34s B: %s  rule met: %s" % (
                name, e["threshold"], e["auc"], "retention %.2f off-pass %.2f" % (b["retention"], b["off_pass"]) if b else "n/a", e["rule_met"]))
    print("rule met on every set, by signal:", verdict)


def selftest(_args):
    assert auc([3, 4], [1, 2]) == 1.0 and auc([1], [1]) == 0.5
    lo, hi = wilson(95, 100)
    assert 0.88 < lo < 0.92 and 0.97 < hi < 0.99, (lo, hi)
    pos = [{"id": str(i), "group": "on", "top1": 0.5 + i / 100, "gap": 0.1, "overlap": 3.0, "kwtop": 1.0} for i in range(40)]
    assert threshold_single(pos, "top1") == 0.52  # two of forty may be lost: 0.50 and 0.51
    neg = [{"id": "n%d" % i, "group": "cand", "top1": 0.4, "gap": 0.0, "overlap": 0.0, "kwtop": 0.0} for i in range(10)]
    rule = threshold_or(pos, neg)
    assert rule is not None and rate(pos, rule) >= RETENTION and rate(neg, rule) == 0.0, rule
    assert half("abc") in ("A", "B") and half("abc") == half("abc")
    print("selftest ok")


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    c = sub.add_parser("candidates")
    c.add_argument("--out", required=True)
    c.add_argument("--exclude", action="append", default=[], help="skip transcript folders whose name contains this")
    c.add_argument("--count", type=int, default=400)
    c.add_argument("--min-chars", type=int, default=25)
    c.add_argument("--max-chars", type=int, default=600)
    c.set_defaults(fn=candidates)
    k = sub.add_parser("collect")
    k.add_argument("--set", required=True, help="corpus:chunk-set, for example docs:gemma")
    k.add_argument("--queries", required=True)
    k.add_argument("--identifiers")
    k.add_argument("--candidates", required=True)
    k.add_argument("--labels", help="only the candidates this file has a label for")
    k.add_argument("--corpus", help="the label key to require, with --labels")
    k.add_argument("--out", required=True)
    k.set_defaults(fn=collect)
    s = sub.add_parser("sheet")
    s.add_argument("--set", required=True)
    s.add_argument("--candidates", required=True)
    s.add_argument("--out", required=True)
    s.add_argument("--chars", type=int, default=320)
    s.set_defaults(fn=sheet)
    r = sub.add_parser("report")
    r.add_argument("--signals", nargs="+", required=True)
    r.add_argument("--labels", required=True)
    r.add_argument("--corpus", required=True)
    r.add_argument("--out")
    r.set_defaults(fn=report)
    t = sub.add_parser("selftest")
    t.set_defaults(fn=selftest)
    args = p.parse_args()
    args.fn(args)


if __name__ == "__main__":
    main()
