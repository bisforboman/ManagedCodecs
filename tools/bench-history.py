#!/usr/bin/env python3
"""Writes the history page of the weekly benchmark (.github/workflows/benchmark.yml): every run's JSON record
(tools/bench.py --json) in a directory, one table a machine architecture, newest run first, a column a sample with
ManagedCodecs' time over FFmpeg's (lower is faster), and the 4-thread row's share of one thread's time.

Usage: tools/bench-history.py <directory of records> > README.md
"""

import json
import os
import sys


def main():
    directory = sys.argv[1]
    records = []
    for name in sorted(os.listdir(directory)):
        if name.endswith(".json"):
            with open(os.path.join(directory, name), encoding="utf-8") as record:
                records.append(json.load(record))

    print("# ManagedCodecs benchmark history")
    print()
    print("Written by `.github/workflows/benchmark.yml` (weekly and on demand), from `tools/bench.py`'s records in `runs/`:")
    print("ManagedCodecs' wall time per decoded frame over FFmpeg's, single-threaded (lower is faster; below 1 is faster")
    print("than FFmpeg), the medians of interleaved runs on GitHub-hosted runners, which vary by a few percent from run to")
    print("run. *4 threads* is mcdec's time with 4 threads over its time with 1 on the 720p sample. A run's own record has")
    print("the times in µs, the CPU and the FFmpeg and .NET versions. The first table a machine is mcdec's whole process,")
    print(".NET's startup and JIT included (a large share of the short samples' time); the second, from 2026-10-29 on,")
    print("its last loop alone (steady state) over FFmpeg's time.")
    for arch in sorted({r["machine"]["arch"] for r in records}):
        runs = sorted((r for r in records if r["machine"]["arch"] == arch), key=lambda r: r["date"], reverse=True)
        samples = []
        for run in runs:
            for result in run["results"]:
                if result["sample"] not in samples:
                    samples.append(result["sample"])

        print()
        print(f"## {arch}")
        table(runs, samples, "ratio", threads=True)
        steady = [r for r in runs if any("steady_ratio" in result for result in r["results"])]
        if steady:
            print()
            print(f"### {arch}, steady state")
            table(steady, samples, "steady_ratio", threads=False)


def table(runs, samples, key, threads):
    print()
    short = [os.path.splitext(os.path.basename(s))[0] for s in samples]
    print("| Date | Commit | " + " | ".join(short) + (" | 4 threads |" if threads else " |"))
    print("| --- | --- | " + " | ".join("---:" for _ in samples) + (" | ---: |" if threads else " |"))
    for run in runs:
        ratios = {r["sample"]: r[key] for r in run["results"] if key in r}
        cells = [f"{ratios[s]:.2f}" if s in ratios else "" for s in samples]
        commit = (run.get("commit") or "")[:7]
        row = f"| {run['date'][:10]} | {commit} | " + " | ".join(cells) + " |"
        if threads:
            row += f" {run['threads']['ratio']:.2f} |" if "threads" in run else "  |"
        print(row)


if __name__ == "__main__":
    main()
