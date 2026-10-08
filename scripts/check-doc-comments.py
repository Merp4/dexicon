#!/usr/bin/env python3
"""Fail when a C# `/// <summary>` block is stacked under another `///` block.

Inserting a member by anchoring an edit on the next member's signature puts the new member
between that member's `///` block and its signature. Both blocks then document the new
member and the one below has none. `dotnet build -p:GenerateDocumentationFile=true` reports
no CS1587 for it, and Copilot review found it 7 times across 6 PRs.

    python scripts/check-doc-comments.py

Reads every tracked `*.cs` file and reports each `/// <summary>` line whose previous
non-blank line is also a `///` line. Paths come from `git ls-files`, relative to the
repository root, so a checkout under `.claude/worktrees/` is read like any other. Exits 1
on a hit, and when it read no files: a listing that matched nothing would otherwise report
the same clean result as a real pass.

Blind to two variants of the same edit:
- the inserted member has no `///` block of its own, so the one above it moves to the new
  member and nothing is stacked;
- the inserted member's block opens with `<inheritdoc />` rather than `<summary>`. A
  `<summary>` followed by `<inheritdoc path="..."/>` is a valid single block, so that
  opener is not flagged.
After any insertion, read the lines above the new member.
"""

from __future__ import annotations

import os
import re
import subprocess
import sys

DOC_LINE = re.compile(r"^\s*///")
SUMMARY_LINE = re.compile(r"^\s*///\s*<summary\b")


def stacked(lines: list[str]) -> list[tuple[int, int]]:
    """(line of the second `<summary>`, first line of the block above it), 1-based."""
    hits = []
    block_start = None  # first line of the `///` run above, with only blank lines since
    for number, line in enumerate(lines, 1):
        if DOC_LINE.match(line):
            if block_start is not None and SUMMARY_LINE.match(line):
                hits.append((number, block_start))
            if block_start is None:
                block_start = number
        elif line.strip():
            block_start = None
    return hits


def count(n: int, one: str, many: str) -> str:
    return f"{n} {one if n == 1 else many}"


def shown(path: str, annotate: bool) -> str:
    """A tracked path as one line of output. In a workflow command it is a property, escaped as
    @actions/core does, so a name holding a line break or `::` cannot start a command of its own."""
    for brk in ("\r", "\n", "\x85", " ", " "):
        path = path.replace(brk, f"\\u{ord(brk):04x}")
    if annotate:
        path = path.replace("%", "%25").replace(":", "%3A").replace(",", "%2C")
    return path


def main() -> int:
    if len(sys.argv) > 1:
        print("check-doc-comments.py takes no arguments: it reads every tracked *.cs file.",
              file=sys.stderr)
        return 2
    root = subprocess.run(["git", "rev-parse", "--show-toplevel"],
                          capture_output=True, text=True, check=True).stdout.strip()
    listing = subprocess.run(["git", "-C", root, "ls-files", "-z", "--", "*.cs"],
                             capture_output=True, check=True).stdout.decode("utf-8")
    paths = [p for p in listing.split("\0") if p]

    annotate = os.environ.get("GITHUB_ACTIONS") == "true"
    read, absent, found = 0, [], 0
    for path in paths:
        try:
            # Universal newlines, so a CRLF checkout numbers lines as an editor does.
            with open(os.path.join(root, path), encoding="utf-8-sig", errors="replace") as f:
                lines = f.read().split("\n")
        except FileNotFoundError:
            absent.append(path)  # deleted in the working tree, still in the index
            continue
        read += 1
        for line, start in stacked(lines):
            found += 1
            message = (f"/// <summary> stacked under the /// block starting at line {start}. "
                       f"Both document the next member; move the member inserted here above "
                       f"line {start}.")
            if annotate:
                print(f"::error file={shown(path, annotate)},line={line}::{message}")
            else:
                print(f"{shown(path, annotate)}:{line}: {message}")

    if absent:
        print(f"Skipped {count(len(absent), 'listed file', 'listed files')} absent from the "
              "working tree: " + ", ".join(shown(p, False) for p in absent))
    print(f"Read {count(read, 'C# file', 'C# files')} under {root}; "
          f"{count(found, 'stacked summary', 'stacked summaries')}.")
    if read == 0:
        message = "Read no C# files: `git ls-files -- '*.cs'` listed nothing to check."
        print(f"::error::{message}" if annotate else message)
        return 1
    return 1 if found else 0


if __name__ == "__main__":
    sys.exit(main())
