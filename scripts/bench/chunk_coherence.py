#!/usr/bin/env python3
"""
Does the chunker keep a C# member's own text together?

    python scripts/bench/chunk_coherence.py --src <tree>/src --chunks <dir holding old/ and new/> [--sizes 256-32,512-64,1024-128]

Reads the chunk files that scripts/bench/chunkjson writes (`<chunks>/<variant>/chunks-<size>.jsonl`) and the source they were cut from,
and reports, per variant and size, over the .cs files outside Migrations:

  members per chunk        signature lines the language-aware boundary pattern matches, per chunk
  chunks ending on ///     of the chunks that are not the last of their file, the share whose last non-blank line is a /// line,
                           which is the description of the member that starts in the next chunk
  documented members kept  of the members with a /// block above their signature (attributes may sit between), the share for which
                           one chunk holds the first /// line and the signature line together
"""
import argparse
import json
import pathlib
import re

BOUNDARY = re.compile(r"^\s*(public|private|protected|internal|static|abstract|sealed|override|virtual|async|record|class|struct|interface|enum)\s")


def documented_members(lines):
    """[(first /// line, signature line)], 1-based, for every signature that has a /// block above it."""
    out = []
    for i, line in enumerate(lines):
        if not BOUNDARY.match(line):
            continue
        j = i - 1
        while j >= 0 and lines[j].lstrip().startswith("["):
            j -= 1
        end = j
        while j >= 0 and lines[j].lstrip().startswith("///"):
            j -= 1
        if end > j:
            out.append((j + 2, i + 1))
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--src", required=True)
    ap.add_argument("--chunks", required=True)
    ap.add_argument("--sizes", default="256-32,512-64,1024-128")
    ap.add_argument("--variants", default="old,new")
    args = ap.parse_args()

    src = pathlib.Path(args.src)
    members = {}
    for path in sorted(src.rglob("*.cs")):
        rel = path.relative_to(src).as_posix()
        if "/Migrations/" in "/" + rel or "/obj/" in "/" + rel or "/bin/" in "/" + rel:
            continue
        lines = path.read_text(encoding="utf-8").replace("\r\n", "\n").split("\n")
        members[rel] = ([i + 1 for i, l in enumerate(lines) if BOUNDARY.match(l)], documented_members(lines))
    if len(members) < 50:
        raise SystemExit(f"only {len(members)} .cs files under {src}")
    n_members = sum(len(v[0]) for v in members.values())
    n_documented = sum(len(v[1]) for v in members.values())
    print(f"{len(members)} .cs files, {n_members} members, {n_documented} with a /// block above\n")

    for size in args.sizes.split(","):
        for variant in args.variants.split(","):
            by_file = {}
            for line in pathlib.Path(args.chunks, variant, f"chunks-{size}.jsonl").read_text(encoding="utf-8").splitlines():
                c = json.loads(line)
                if c["file"] in members:
                    by_file.setdefault(c["file"], []).append(c)
            chunks = non_final = ends_doc = member_sum = together = 0
            for rel, cs in by_file.items():
                cs.sort(key=lambda c: c["start"])
                sigs, docs = members[rel]
                for k, c in enumerate(cs):
                    chunks += 1
                    member_sum += sum(1 for s in sigs if c["start"] <= s <= c["end"])
                    if k < len(cs) - 1:
                        non_final += 1
                        last = next((l for l in reversed(c["text"].split("\n")) if l.strip()), "").lstrip()
                        ends_doc += last.startswith("///")
                together += sum(1 for a, b in docs if any(c["start"] <= a and c["end"] >= b for c in cs))
            print(f"{size:<9} {variant:<4} {chunks:>5} chunks, {member_sum / chunks:4.2f} members each | "
                  f"{100 * ends_doc / non_final:4.1f}% of non-final chunks end on a /// line | "
                  f"{100 * together / n_documented:5.1f}% of documented members have comment and signature in one chunk")
        print()


if __name__ == "__main__":
    main()
