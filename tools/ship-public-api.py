#!/usr/bin/env python3
"""Moves each src/ project's PublicAPI.Unshipped.txt into its PublicAPI.Shipped.txt, for a release pull request (the
API as of the release becomes the shipped one): the added lines join Shipped, the *REMOVED* ones leave it, and
Unshipped keeps only its header. Lines stay sorted, as the analyzer's code fix writes them.

    tools/ship-public-api.py
"""

import sys
from pathlib import Path

HEADER = "#nullable enable"


def entries(path):
    return [line for line in path.read_text(encoding="utf-8").splitlines() if line and not line.startswith("#")]


def main():
    root = Path(__file__).resolve().parents[1] / "src"
    for unshipped in sorted(root.glob("*/PublicAPI.Unshipped.txt")):
        shipped = unshipped.with_name("PublicAPI.Shipped.txt")
        lines = set(entries(shipped))
        changes = entries(unshipped)
        for line in changes:
            if line.startswith("*REMOVED*"):
                removed = line[len("*REMOVED*"):]
                if removed not in lines:
                    sys.exit(f"{unshipped}: removes '{removed}', which {shipped.name} doesn't list")
                lines.discard(removed)
            else:
                lines.add(line)
        shipped.write_text(HEADER + "\n" + "".join(line + "\n" for line in sorted(lines)), encoding="utf-8")
        unshipped.write_text(HEADER + "\n", encoding="utf-8")
        print(f"{unshipped.parent.name}: {len(changes)} changes shipped, {len(lines)} lines")


if __name__ == "__main__":
    main()
