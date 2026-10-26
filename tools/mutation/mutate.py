#!/usr/bin/env python3
"""Mutation checks for the guards listed in tools/mutation/mutations.py: each mutation breaks one guard on purpose,
and a test the entry names must then fail. Fails when a mutation survives (no test covers that guard), no longer
applies (the code changed: update its find text), no longer compiles, or its filter selects no test.

    tools/mutation/mutate.py [--shard I --shards N] [--only TEXT] [--repeat N]

--shard/--shards: only every N-th mutation from the I-th (0-based), for CI's parallel jobs. --only: the mutations
whose file or find text contains TEXT. --repeat: every mutation's test runs up to N times (default: the entry's
own repeat, else 1) until one fails, for guards whose breaking shows only now and then (the threading waits, under
the tests' random delays). Each mutation is undone with 'git checkout', so the mutated files must have no uncommitted
changes (checked first); locally, run it in a worktree of its own. Needs the samples (tools/make-samples.sh).
"""

import argparse
import os
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from mutations import MUTATIONS  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
TESTS = ROOT / "tests" / "ManagedCodecs.Tests"


def run(args):
    return subprocess.run(args, cwd=ROOT, capture_output=True, text=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--shard", type=int, default=0)
    parser.add_argument("--shards", type=int, default=1)
    parser.add_argument("--only", default=None)
    parser.add_argument("--repeat", type=int, default=None)
    args = parser.parse_args()

    mine = MUTATIONS[args.shard::args.shards]
    if args.only:
        mine = [m for m in mine if args.only in m["file"] or args.only in m["find"]]

    dirty = run(["git", "status", "--porcelain", "--", *sorted({m["file"] for m in mine})]).stdout.strip()
    if dirty:
        sys.exit("Uncommitted changes in files to mutate (each mutation is undone with git checkout):\n" + dirty)

    build = run(["dotnet", "build", str(TESTS), "-c", "Release", "-v", "q", "--nologo"])
    if build.returncode != 0:
        sys.exit("The unmutated build fails:\n" + build.stdout[-4000:])

    results = []
    for m in mine:
        path = ROOT / m["file"]
        text = path.read_text(encoding="utf-8")
        count = text.count(m["find"])
        if count != 1:
            results.append(("STALE", m, f"found {count} times"))
            continue

        path.write_text(text.replace(m["find"], m["replace"]), encoding="utf-8")
        try:
            # The build once, then the tests as often as asked until one run fails.
            built = run(["dotnet", "build", str(TESTS), "-c", "Release", "-v", "q", "--nologo"])
            repeat = args.repeat or m.get("repeat", 1)
            result, note = "SURVIVED", m["tests"]
            if built.returncode != 0:
                result, note, repeat = "INVALID", "does not compile", 0
            for attempt in range(1, repeat + 1):
                test = run(["dotnet", "test", str(TESTS), "-c", "Release", "--no-build", "--nologo", "--filter", m["tests"]])
                if "No test matches the given testcase filter" in test.stdout:
                    result, note = "INVALID", f"no test matches {m['tests']}"
                    break
                if test.returncode != 0:
                    result, note = "KILLED", f"{m['tests']} (run {attempt} of {repeat})"
                    break
        finally:
            run(["git", "checkout", "-q", "--", m["file"]])

        results.append((result, m, note))
        print(f"{result:9} {m['file']}: {m['find'].strip()}  [{note}]", flush=True)

    for result, m, note in results:
        if result == "STALE":
            print(f"{result:9} {m['file']}: {m['find'].strip()}  [{note}]")

    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as f:
            f.write("### Mutation checks\n\n| Result | File | Guard | Tests |\n|---|---|---|---|\n")
            for result, m, note in results:
                guard = m["find"].strip().replace("|", "\\|")
                f.write(f"| {result} | {m['file']} | `{guard}` | {note} |\n")

    bad = [r for r in results if r[0] != "KILLED"]
    if bad:
        sys.exit(f"{len(bad)} of {len(results)} mutations survived, no longer apply or are invalid.")
    print(f"All {len(results)} mutations were caught.")


if __name__ == "__main__":
    main()
