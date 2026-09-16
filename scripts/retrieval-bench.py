#!/usr/bin/env python3
"""
Retrieval quality comparison between two chunk sets.

Answers the shape of question Q3 asks — "is this embedding model better than that one
for our content?" — by running the same queries against two chunk sets that differ in
exactly one respect and scoring whether the right file came back.

The comparison is only meaningful because chunk sets can hold identical chunking over
identical documents with a different model. Before them this needed two corpora and a
promise that nothing else had drifted.

    python scripts/retrieval-bench.py docs:default docs:gemma

WHAT THIS IS NOT. The query set below is small and hand-written by someone who knows the
corpus, so it measures a signal, not a verdict, and it cannot support a claim like "model
A is 12% better". Read the per-query table, not the summary line. A real answer needs a
query set built from questions people actually asked, and enough of them to survive one
lucky retrieval.
"""

import json
import os
import sys
import urllib.request

BASE = os.environ.get("DEXICON_URL", "http://127.0.0.1:8477")

# (query, the file that should answer it). Chosen so the target is unambiguous — a query
# whose answer is genuinely spread across three files measures the query set, not the
# model.
QUERIES = [
    ("what is the Qdrant tenant key and why was it chosen", "03-data-model.md"),
    ("how does an incremental refresh decide a file is unchanged", "04-ingestion.md"),
    ("how are dense and sparse results fused into one ranking", "05-search.md"),
    ("which MCP revision removed the initialize handshake", "06-mcp-surface.md"),
    ("how does one tenant get read access to another tenant's corpus", "07-tenancy-auth.md"),
    ("what do the two dependency status dots in the header show", "08-ui.md"),
    ("why is Qdrant not published on a host port", "09-deployment.md"),
    ("how are API tokens stored and verified", "10-security-secrets.md"),
    ("which open source licence was chosen and why", "decisions.md"),
    ("what are the delivery milestones", "11-roadmap.md"),
    ("what does the container topology look like", "02-architecture.md"),
    ("what problem does this project set out to solve", "01-overview.md"),
]


def search(token, corpus, query, limit=5):
    body = json.dumps({"query": query, "corpus": [corpus], "mode": "hybrid", "limit": limit})
    req = urllib.request.Request(
        f"{BASE}/api/search",
        data=body.encode(),
        headers={"Content-Type": "application/json", "Authorization": f"Bearer {token}"},
    )
    with urllib.request.urlopen(req, timeout=120) as r:
        return json.load(r)


def score(token, corpus):
    """Rank of the first hit from the expected file, per query. None = not in top k."""
    ranks = []
    for query, expected in QUERIES:
        hits = search(token, corpus, query)["hits"]
        rank = next((i + 1 for i, h in enumerate(hits) if h["filePath"] == expected), None)
        ranks.append((query, expected, rank))
    return ranks


def summarise(ranks):
    n = len(ranks)
    at = lambda k: sum(1 for _, _, r in ranks if r is not None and r <= k)
    # Mean reciprocal rank: 1.0 only if every query put the right file first.
    mrr = sum(1 / r for _, _, r in ranks if r) / n
    return at(1), at(3), at(5), mrr, n


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(2)

    token = os.environ.get("DEXICON_TOKEN")
    if not token:
        print("Set DEXICON_TOKEN to a token with the search scope.", file=sys.stderr)
        sys.exit(2)

    left, right = sys.argv[1], sys.argv[2]
    lranks, rranks = score(token, left), score(token, right)

    fmt = lambda r: "—" if r is None else str(r)
    print(f"\nRank of the expected file, lower is better ({len(QUERIES)} queries)\n")
    print(f"  {'query':<58} {left:>16} {right:>16}")
    print(f"  {'-' * 58} {'-' * 16} {'-' * 16}")
    for (q, _, lr), (_, _, rr) in zip(lranks, rranks):
        print(f"  {q[:58]:<58} {fmt(lr):>16} {fmt(rr):>16}")

    for name, ranks in ((left, lranks), (right, rranks)):
        a1, a3, a5, mrr, n = summarise(ranks)
        print(f"\n  {name}:  hit@1 {a1}/{n}   hit@3 {a3}/{n}   hit@5 {a5}/{n}   MRR {mrr:.3f}")

    print(f"\n  {len(QUERIES)} queries is a smoke test, not a verdict. Read the table.\n")


if __name__ == "__main__":
    main()
