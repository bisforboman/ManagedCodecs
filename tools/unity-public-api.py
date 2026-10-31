#!/usr/bin/env python3
"""Checks that src/ManagedCodecs.Unity's public API files list exactly the other packages' API together: it compiles
their sources, so a public member missing from its files (or only there) means one side was not updated, or a public
member sits inside `#if NET` (the Unity build would then lack it). Shipped and Unshipped lines count together, with
each *REMOVED* line taking its member out.

    tools/unity-public-api.py
"""

import sys
from pathlib import Path

PACKAGES = ["ManagedCodecs.Core", "ManagedCodecs.Containers", "ManagedCodecs.Mjpeg", "ManagedCodecs.H264", "ManagedCodecs.Aac", "ManagedCodecs.Thumbnails"]


def api(project):
    lines, removed = set(), set()
    for name in ("PublicAPI.Shipped.txt", "PublicAPI.Unshipped.txt"):
        for line in (project / name).read_text(encoding="utf-8").splitlines():
            if not line or line.startswith("#"):
                continue
            if line.startswith("*REMOVED*"):
                removed.add(line[len("*REMOVED*"):])
            else:
                lines.add(line)
    return lines - removed


def main():
    src = Path(__file__).resolve().parents[1] / "src"
    expected = set().union(*(api(src / p) for p in PACKAGES))
    unity = api(src / "ManagedCodecs.Unity")
    missing, extra = sorted(expected - unity), sorted(unity - expected)
    for line in missing:
        print(f"only in the other packages: {line}")
    for line in extra:
        print(f"only in ManagedCodecs.Unity: {line}")
    if missing or extra:
        sys.exit("src/ManagedCodecs.Unity's PublicAPI files differ from the other packages' together (above); "
                 "dotnet format analyzers src/<project> --diagnostics RS0016 --severity info updates a project's")
    print(f"ManagedCodecs.Unity's public API is the other packages' together ({len(unity)} lines).")


if __name__ == "__main__":
    main()
