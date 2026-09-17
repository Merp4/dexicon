#!/usr/bin/env python3
"""Every dependency that ships, and what it is licensed under.

Run this after any dependency change that ADDS a package rather than bumps one. The two
things worth re-checking are always the same: whether anything new is copyleft, and whether
anything non-permissive has moved from build-time into the shipped browser bundle.

    python scripts/licence-review.py

Licences come from each package's own metadata — `.nuspec` for NuGet, `package.json` for
npm — not from a name lookup or a guess. Two packages predate SPDX expressions and declare
a licence FILE or URL instead; those are reported rather than silently dropped, because a
package with no parseable licence is the one you most want to know about.

The written-up result lives in docs/10-security-secrets.md. This script is what produced
it, so the numbers there can be checked rather than believed.
"""

from __future__ import annotations

import json
import pathlib
import subprocess
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict

REPO = pathlib.Path(__file__).resolve().parent.parent
NUGET_CACHE = pathlib.Path.home() / ".nuget" / "packages"

# Licences that need no further thought: permissive, no attribution beyond the notice,
# no source-sharing obligation.
PLAINLY_PERMISSIVE = {"MIT", "Apache-2.0", "BSD-3-Clause", "BSD-2-Clause", "ISC",
                      "Unlicense", "0BSD", "MIT-0"}

# Anything matching these is a release blocker, not a footnote.
COPYLEFT = ("gpl", "agpl", "lgpl", "sspl", "commons clause", "cc-by-sa", "epl", "cddl")


def nuget_packages() -> set[tuple[str, str]]:
    """The full transitive graph, as restore resolved it."""
    found = set()
    for assets in list(REPO.glob("src/*/obj/project.assets.json")) + \
            list(REPO.glob("tests/*/obj/project.assets.json")):
        data = json.loads(assets.read_text(encoding="utf-8"))
        for target in data.get("targets", {}).values():
            for key, meta in target.items():
                if meta.get("type") == "package":
                    name, _, version = key.partition("/")
                    found.add((name, version))
    return found


def nuget_licence(name: str, version: str) -> str:
    directory = NUGET_CACHE / name.lower() / version
    nuspec = next(iter(directory.glob("*.nuspec")), None)
    if nuspec is None:
        return "(package not in the local cache — run dotnet restore)"

    try:
        root = ET.fromstring(nuspec.read_text(encoding="utf-8", errors="replace"))
    except ET.ParseError:
        return "(unparseable nuspec)"

    ns = {"n": root.tag.split("}")[0].strip("{")} if "}" in root.tag else {}

    def text(tag: str) -> str | None:
        el = root.find(f".//n:{tag}", ns) if ns else root.find(f".//{tag}")
        return el.text.strip() if el is not None and el.text else None

    expression = text("license")

    # A licence declared as a FILE. Read it: "LICENSE" is not a licence.
    if expression and expression.upper().startswith("LICENSE"):
        licence_file = next(iter(directory.glob("LICENSE*")), None)
        if licence_file:
            first = licence_file.read_text(encoding="utf-8", errors="replace").strip().splitlines()
            if first:
                return f"{first[0].strip()}  (from the bundled {licence_file.name})"
        return f"(file: {expression})"

    return expression or text("licenseUrl") or "(none declared)"


def npm_production() -> set[str]:
    """Packages that reach the browser bundle, per npm itself."""
    ui = REPO / "clients" / "web-ui"
    try:
        out = subprocess.run(["npm", "ls", "--omit=dev", "--all", "--json"],
                             cwd=ui, capture_output=True, text=True, shell=True).stdout
        tree = json.loads(out)
    except Exception as e:                                  # noqa: BLE001
        print(f"  ! could not resolve production dependencies: {e}", file=sys.stderr)
        return set()

    names: set[str] = set()

    def walk(node):
        for name, meta in (node.get("dependencies") or {}).items():
            names.add(name)
            walk(meta)

    walk(tree)
    return names


def npm_installed() -> dict[tuple[str, str], str]:
    packages = {}
    for pj in (REPO / "clients" / "web-ui" / "node_modules").rglob("package.json"):
        try:
            data = json.loads(pj.read_text(encoding="utf-8", errors="replace"))
        except Exception:                                   # noqa: BLE001
            continue
        name, version = data.get("name"), data.get("version")
        if not name or not version:
            continue
        licence = data.get("license")
        if isinstance(licence, dict):
            licence = licence.get("type")
        if not licence and isinstance(data.get("licenses"), list):
            licence = "/".join(x.get("type", "?") for x in data["licenses"])
        packages.setdefault((name, version), licence or "(none declared)")
    return packages


def main() -> int:
    blockers: list[str] = []

    print("── NuGet ─────────────────────────────────────────────────────")
    by_licence: dict[str, list[str]] = defaultdict(list)
    for name, version in sorted(nuget_packages()):
        licence = nuget_licence(name, version)
        by_licence[licence].append(f"{name} {version}")
        if any(c in licence.lower() for c in COPYLEFT):
            blockers.append(f"NuGet {name} {version}: {licence}")

    total = sum(len(v) for v in by_licence.values())
    print(f"{total} packages in the restored graph\n")
    for licence, names in sorted(by_licence.items(), key=lambda kv: -len(kv[1])):
        mark = " " if licence in PLAINLY_PERMISSIVE else "*"
        print(f" {mark} {licence}  ({len(names)})")
        if licence not in PLAINLY_PERMISSIVE:
            for n in names:
                print(f"       {n}")

    print("\n── npm ───────────────────────────────────────────────────────")
    ships = npm_production()
    installed = npm_installed()
    print(f"{len(installed)} installed, {len(ships)} of them reach the browser bundle\n")

    counts: dict[str, int] = defaultdict(int)
    for (_, _), licence in installed.items():
        counts[licence] += 1
    for licence, n in sorted(counts.items(), key=lambda kv: -kv[1]):
        if licence in PLAINLY_PERMISSIVE:
            print(f"   {licence}  ({n})")

    print("\n   Anything not plainly permissive:")
    for (name, version), licence in sorted(installed.items()):
        if licence in PLAINLY_PERMISSIVE:
            continue
        shipped = name in ships
        print(f"   * {name} {version}: {licence}  — "
              f"{'SHIPS in the bundle' if shipped else 'build/dev only'}")
        if any(c in licence.lower() for c in COPYLEFT) and shipped:
            blockers.append(f"npm {name} {version}: {licence} SHIPS")

    print()
    if blockers:
        print("COPYLEFT IN THE SHIPPED SET — resolve before releasing:")
        for b in blockers:
            print(f"   {b}")
        return 1

    print("No GPL/LGPL/AGPL/SSPL/Commons Clause in the shipped set.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
