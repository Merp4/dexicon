#!/usr/bin/env python3
"""
Function-level retrieval: does the chunker keep a member's own text together, and does that change what a question finds?

    python scripts/bench/functionbench.py --ollama http://127.0.0.1:21434 --src <tree>/src \
        --chunks <dir holding old/ and new/> --out functionbench-out.json

The chunks are written by the production `CodeChunker` (one JSON object per line: file, 1-based first and last line, the text that is
embedded) into `<chunks>/<variant>/chunks-<size>-<overlap>.jsonl` for each variant. `old` and `new` are two builds of the chunker run
over the same tree; any two directories work. Each chunk is embedded with the model's document framing and each question with its
query framing, as in modelbench.py, and chunks are ranked by cosine, dense only.

The 52 questions are those of queries-code.json. `queries-code-members.json` says which member(s) of the expected file answer each
one; those lines belong to one tree (named in the file), and `--src` has to be that tree. Three scores, each the reciprocal rank
of the first hit in the top ten:

  strict    a chunk of the expected file that holds the signature line of a labelled member
  lenient   a chunk of the expected file that overlaps a labelled member's text, from its leading comment lines to its last line
  file      a chunk of the expected file, as in modelbench.py (all 52 questions; the other two cover the questions that have a member)

NEW minus OLD gets a paired bootstrap interval over the questions, per model and size.
"""
import argparse
import json
import operator
import os
import pathlib
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import modelbench as mb  # noqa: E402  (embedding calls, normalisation, the bootstrap, the memory guard)

MODELS = {"v1": mb.V1, "eg2": mb.ALIAS}
LEADING = re.compile(r"^\s*(//|/\*|\*|\[)")  # the C# comment and attribute lines above a member, as the chunker's LeadingLines pattern
TOP = 10


def skip_string(text, k):
    """`k` is the opening quote of a C# string on one line; returns the index after its closing quote. Handles verbatim (@) strings
    and interpolated ($) strings, whose `{...}` holes can hold strings of their own."""
    prefix = text[max(0, k - 2):k]
    verbatim, interpolated = "@" in prefix, "$" in prefix
    k += 1
    while k < len(text):
        c = text[k]
        if c == '"':
            if verbatim and text[k + 1:k + 2] == '"':
                k += 2
                continue
            return k + 1
        if c == "\\" and not verbatim:
            k += 2
            continue
        if interpolated and c == "{":
            if text[k + 1:k + 2] == "{":
                k += 2
                continue
            k = skip_hole(text, k + 1)
            continue
        k += 1
    return k


def skip_hole(text, k):
    """`k` is just after the `{` of an interpolation hole; returns the index after its matching `}`."""
    depth = 1
    while k < len(text):
        c = text[k]
        if c == '"':
            k = skip_string(text, k)
            continue
        if c == "'":
            k += 4 if text[k + 1:k + 2] == "\\" else 3
            continue
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                return k + 1
        k += 1
    return k


def member_span(lines, sig):
    """(first line of the member's text, last line), 1-based, for the C# member whose signature is on line `sig` (1-based).
    The first line takes in the unbroken run of comment and attribute lines above the signature. The last line is found by
    matching braces from the signature, outside strings and comments, or by the first `;` at depth 0 for a member with no block.
    String state is kept within one line: a verbatim or raw string that spans lines, and a `'\\u007B'` or `'\\x7B'` character
    literal, are not handled. None occurs in the labelled members."""
    i = sig - 1
    first = i
    while first > 0 and LEADING.match(lines[first - 1]):
        first -= 1
    depth_b = depth_p = 0
    seen_block = False
    in_comment = False
    expr_bodied = False
    for li in range(i, len(lines)):
        text = lines[li]
        k = 0
        while k < len(text):
            c = text[k]
            two = text[k:k + 2]
            if in_comment:
                if two == "*/":
                    in_comment = False
                    k += 1
            elif two == "//":
                break
            elif two == "/*":
                in_comment = True
                k += 1
            elif c == '"':
                k = skip_string(text, k)
                continue
            elif c == "'":
                k += 4 if text[k + 1:k + 2] == "\\" else 3
                continue
            elif c == "{":
                depth_b += 1
                seen_block = True
            elif c == "}":
                depth_b -= 1
                if depth_b == 0 and depth_p == 0 and seen_block and not expr_bodied:
                    return first + 1, li + 1
            elif c == "(":
                depth_p += 1
            elif c == ")":
                depth_p -= 1
            elif two == "=>" and depth_b == 0 and depth_p == 0 and not seen_block:
                expr_bodied = True
                k += 1
            elif c == ";" and depth_b == 0 and depth_p == 0:
                return first + 1, li + 1
            k += 1
    return first + 1, len(lines)


def load_chunks(path):
    return [json.loads(l) for l in open(path, encoding="utf-8") if l.strip()]


def source_lines(src, rel, cache):
    if rel not in cache:
        text = (pathlib.Path(src) / rel).read_text(encoding="utf-8").replace("\r\n", "\n").lstrip("﻿")
        cache[rel] = text.split("\n")
    return cache[rel]


def targets_of(queries, members, src, cache):
    """Per question: the strict targets (file, signature line) and lenient spans (file, first, last); None when it has no member."""
    if len(members["queries"]) != len(queries):
        sys.exit(f"queries-code-members.json has {len(members['queries'])} questions and queries-code.json has {len(queries)}")
    out = []
    for q, m in zip(queries, members["queries"]):
        if q["query"] != m["query"] or q["file"] != m["file"]:
            sys.exit(f"queries-code-members.json does not match queries-code.json at {q['query']!r}")
        if not m["members"]:
            out.append(None)
            continue
        lines = source_lines(src, q["file"], cache)
        for x in m["members"]:
            # The labels belong to one tree: a line that is not the labelled signature means --src is another tree.
            got = lines[x["line"] - 1].strip() if 1 <= x["line"] <= len(lines) else ""
            if not x["signature"].strip() or not got.startswith(x["signature"].strip()):
                sys.exit(f"{q['file']}:{x['line']} is {got[:70]!r}, not the labelled signature {x['signature'][:70]!r}: "
                         f"is --src the tree the labels belong to ({members.get('tree')})?")
        spans = [member_span(lines, x["line"]) for x in m["members"]]
        out.append({"strict": [(q["file"], x["line"]) for x in m["members"]], "lenient": [(q["file"], a, b) for a, b in spans],
                    "ambiguous": bool(m.get("ambiguous"))})
    return out


def check_chunks(chunks, queries, src, cache, label):
    """Every expected file has chunks, and every chunk of each expected C# file holds the lines it claims, so those chunks were cut
    from the tree the labels and --src belong to. The other files of the corpus are not checked."""
    if not chunks:
        sys.exit(f"{label}: no chunks")
    by_file = {}
    for c in chunks:
        by_file.setdefault(c["file"], []).append(c)
    for rel in sorted({q["file"] for q in queries}):
        if rel not in by_file:
            sys.exit(f"{label}: no chunk of {rel}")
        if not rel.endswith(".cs"):
            continue
        lines = source_lines(src, rel, cache)
        for c in by_file[rel]:
            if "\n".join(lines[c["start"] - 1:c["end"]]).strip() != c["text"].replace("\r", "").lstrip("﻿").strip():
                sys.exit(f"{label}: the chunk {rel}:{c['start']}-{c['end']} is not the text of those lines under --src")


def reciprocal(ranked, chunks, test):
    for r, ci in enumerate(ranked[:TOP], 1):
        if test(chunks[ci]):
            return 1.0 / r
    return 0.0


def score(chunks, qvecs, cvecs, queries, targets):
    rr = {"strict": [], "lenient": [], "file": [], "strict_unambiguous": []}
    for q, qv, t in zip(queries, qvecs, targets):
        sims = [sum(map(operator.mul, qv, cv)) for cv in cvecs]
        ranked = sorted(range(len(chunks)), key=lambda i: (-sims[i], chunks[i]["file"], chunks[i]["start"]))
        rr["file"].append(reciprocal(ranked, chunks, lambda c: c["file"] == q["file"]))
        if t is None:
            continue
        strict = reciprocal(ranked, chunks, lambda c: any(c["file"] == f and c["start"] <= s <= c["end"] for f, s in t["strict"]))
        rr["strict"].append(strict)
        rr["lenient"].append(reciprocal(ranked, chunks, lambda c: any(c["file"] == f and c["start"] <= b and c["end"] >= a for f, a, b in t["lenient"])))
        if not t["ambiguous"]:
            rr["strict_unambiguous"].append(strict)
    return rr


def summary(rr):
    out = {}
    for k, v in rr.items():
        out[k] = {"n": len(v), "mrr": sum(v) / len(v), "hit1": sum(x >= 1 for x in v) / len(v), "hit5": sum(x >= 0.2 for x in v) / len(v)}
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--ollama", default=os.environ.get("OLLAMA", "http://127.0.0.1:11434"))
    ap.add_argument("--src", required=True, help="the src folder of the tree the labels belong to")
    ap.add_argument("--chunks", required=True, help="a folder holding one subfolder per variant, each with chunks-<size>-<overlap>.jsonl")
    ap.add_argument("--queries", default=str(pathlib.Path(__file__).with_name("queries-code.json")))
    ap.add_argument("--members", default=str(pathlib.Path(__file__).with_name("queries-code-members.json")))
    ap.add_argument("--models", default="v1,eg2")
    ap.add_argument("--sizes", default="256-32,512-64,1024-128")
    ap.add_argument("--variants", default="old,new")
    ap.add_argument("--out", default="functionbench-out.json")
    args = ap.parse_args()

    base = args.ollama.rstrip("/")
    queries = json.loads(pathlib.Path(args.queries).read_text(encoding="utf-8"))["queries"]
    members = json.loads(pathlib.Path(args.members).read_text(encoding="utf-8"))
    src_cache = {}
    targets = targets_of(queries, members, args.src, src_cache)
    labelled = sum(t is not None for t in targets)
    print(f"{len(queries)} questions, {labelled} with a labelled member ({sum(t is not None and not t['ambiguous'] for t in targets)} unambiguous)", flush=True)

    variants = args.variants.split(",")
    sizes = args.sizes.split(",")
    result = {"meta": {"ollama": json.load(__import__("urllib.request").request.urlopen(base + "/api/version")), "questions": len(queries), "labelled": labelled,
                       "tree": members.get("tree")}, "arms": {}, "paired": {}}
    for mname in args.models.split(","):
        model = MODELS[mname]
        cache = {}
        qvecs = None
        for size in sizes:
            for var in variants:
                chunks = load_chunks(pathlib.Path(args.chunks) / var / f"chunks-{size}.jsonl")
                check_chunks(chunks, queries, args.src, src_cache, f"{var}/{size}")
                texts = [mb.GEMMA_DOC.format(t=c["text"]) for c in chunks]
                todo = sorted({t for t in texts if t not in cache}, key=lambda t: (len(t), t))
                longest = max((len(t) for t in todo), default=1)
                batch = max(1, min(mb.BATCH, mb.REQUEST_CHARS // longest))
                t0 = time.time()
                if todo:
                    vecs, _ = mb.embed_all(base, model, todo, f"{mname}/{size}/{var}", batch, mb.WORKERS)
                    for t, v in zip(todo, vecs):
                        cache[t] = mb.norm(v)
                if qvecs is None:
                    qv, _ = mb.embed_all(base, model, [mb.GEMMA_Q.format(t=q["query"]) for q in queries], f"{mname}/queries")
                    qvecs = [mb.norm(v) for v in qv]
                rr = score(chunks, qvecs, [cache[t] for t in texts], queries, targets)
                arm = f"{mname}/{size}/{var}"
                result["arms"][arm] = {"chunks": len(chunks), "embedded_now": len(todo), "seconds": round(time.time() - t0, 1), "rr": rr, "summary": summary(rr)}
                s = result["arms"][arm]["summary"]
                print(f"{arm:<18} {len(chunks):>5} chunks ({len(todo)} embedded, {time.time() - t0:.0f}s)  strict {s['strict']['mrr']:.3f}  lenient {s['lenient']['mrr']:.3f}  file {s['file']['mrr']:.3f}", flush=True)
        mb.call(base, "/api/embed", {"model": model, "keep_alive": 0})

    if "old" in variants and "new" in variants:
        print("\nNEW minus OLD, mean difference in reciprocal rank [95% paired bootstrap]")
        for mname in args.models.split(","):
            for size in sizes:
                a, b = result["arms"][f"{mname}/{size}/new"]["rr"], result["arms"][f"{mname}/{size}/old"]["rr"]
                row = {}
                for k in ("strict", "lenient", "file", "strict_unambiguous"):
                    d, lo, hi = mb.bootstrap(a[k], b[k])
                    row[k] = {"diff": d, "lo": lo, "hi": hi}
                result["paired"][f"{mname}/{size}"] = row
                print(f"  {mname:<4}{size:<9} strict {row['strict']['diff']:+.3f} [{row['strict']['lo']:+.3f}, {row['strict']['hi']:+.3f}]   "
                      f"lenient {row['lenient']['diff']:+.3f} [{row['lenient']['lo']:+.3f}, {row['lenient']['hi']:+.3f}]   "
                      f"file {row['file']['diff']:+.3f} [{row['file']['lo']:+.3f}, {row['file']['hi']:+.3f}]   "
                      f"unambiguous {row['strict_unambiguous']['diff']:+.3f} [{row['strict_unambiguous']['lo']:+.3f}, {row['strict_unambiguous']['hi']:+.3f}]")
    pathlib.Path(args.out).write_text(json.dumps(result), encoding="utf-8")
    print("wrote", args.out)


if __name__ == "__main__":
    main()
