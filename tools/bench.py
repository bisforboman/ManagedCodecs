#!/usr/bin/env python3
"""Times ManagedCodecs (mcdec) against FFmpeg on the samples README's Benchmarking section lists, or one mcdec build
against another (--base), and writes a Markdown table and a JSON record of the run.

Every sample is decoded --runs times by each side, the two interleaved and the order alternating (with the same one
first in every pair, an A/A test of one build once showed a 10% "regression"), and the median wall time per decoded
frame is taken. mcdec's time is its whole process (demuxing, .NET's startup and JIT, as tools/compare.sh), so the loop
counts are chosen to wash startup out. FFmpeg decodes single-threaded (-threads 1), as mcdec does by default; one more
row times mcdec on the 720p sample with 4 threads against 1.

Usage (from the repository root, after tools/make-samples.sh and a Release build of tools/ManagedCodecs.Cli):
  tools/bench.py --runs 5 --report bench.md --json bench.json
  tools/bench.py --base <other mcdec.dll> --runs 5 --report compare.md
"""

import argparse
import datetime
import json
import os
import platform
import re
import statistics
import subprocess
import sys

# README's table: sample, loops, what it is.
SAMPLES = [
    ("testsrc-420.avi", 100, "320x240 MJPEG 4:2:0"),
    ("testsrc-444.avi", 100, "MJPEG 4:4:4"),
    ("noise-q1.avi", 100, "MJPEG, noise at the highest quality"),
    ("h264-p-refs.mp4", 40, "320x240 H.264, P frames"),
    ("h264-bench-720p.mp4", 10, "1280x720 H.264"),
    ("h264-main-p-refs.mp4", 40, "320x240 H.264 Main, CABAC"),
    ("h264-b-pyramid.mp4", 20, "320x240 H.264 Main, CABAC, B frames"),
    ("h264-wp-default.mp4", 20, "320x240 H.264, libx264's Main defaults: B frames, weighted prediction"),
    ("h264-high-default.mp4", 20, "320x240 H.264, libx264's High defaults: also the 8x8 transform"),
    ("h264-high-b.mp4", 40, "320x240 H.264 High, B frames, all partitions"),
    ("h264-10bit-b.mp4", 40, "the same at 10 bits, High 10"),
    ("h264-high-motion.mp4", 20, "320x240 H.264 High, fast motion and noise, QP 22"),
    ("h264-10bit-motion.mp4", 20, "the same at 10 bits, QP 34"),
    ("h264-mbaff-default.mp4", 40, "320x240 H.264 MBAFF, libx264's interlaced defaults"),
    ("jm/jm-paff-b.mp4", 200, "320x256 H.264 field pictures from JM, CABAC, B pictures"),
    ("aac-stereo-48k.mp4", 100, "AAC, stereo"),
    ("aac-5.1.mp4", 100, "AAC, 5.1"),
    ("heaac/heaac-5.1.mp4", 100, "HE-AAC, 5.1"),
    ("heaac/heaacv2-stereo.mp4", 100, "HE-AAC v2: parametric stereo"),
]

THREADS_SAMPLE = ("h264-bench-720p.mp4", 10)


def run(command):
    result = subprocess.run(command, capture_output=True, text=True, check=False)
    if result.returncode != 0:
        sys.exit(f"{' '.join(command)} failed:\n{result.stdout}{result.stderr}")
    return result.stdout + result.stderr


def rtime(output):
    match = re.search(r"rtime=([0-9.]+)s", output)
    if not match:
        sys.exit(f"no bench: line in:\n{output}")
    return float(match.group(1))


def frames(output):
    match = re.search(r"^decoded (\d+) video and (\d+) audio", output, re.MULTILINE)
    if not match:
        sys.exit(f"no frame count in:\n{output}")
    return int(match.group(1)) + int(match.group(2))


def mcdec(dll, path, loops, threads=1):
    """Wall time per decoded frame in microseconds, and the frames per loop."""
    # A .dll runs on the runtime; anything else is a native executable (a Native AOT publish of mcdec).
    command = (["dotnet", dll] if dll.endswith(".dll") else [dll]) + [path, "--loops", str(loops)]
    if threads != 1:
        command += ["--threads", str(threads)]
    output = run(command)
    count = frames(output)
    return rtime(output) * 1e6 / (count * loops), count


def ffmpeg(path, loops, count):
    output = run(["ffmpeg", "-hide_banner", "-nostdin", "-benchmark", "-threads", "1", "-stream_loop", str(loops - 1),
                  "-i", path, "-f", "null", "-"])
    return rtime(output) * 1e6 / (count * loops)


def interleaved(runs, first, second):
    """Medians of runs of two timings, interleaved, the order alternating."""
    a, b = [], []
    for i in range(runs):
        if i % 2 == 0:
            a.append(first())
            b.append(second())
        else:
            b.append(second())
            a.append(first())
    return statistics.median(a), statistics.median(b)


def version(command, pattern):
    try:
        output = subprocess.run(command, capture_output=True, text=True, check=False).stdout
    except FileNotFoundError:
        return None
    match = re.search(pattern, output)
    return match.group(1) if match else None


def machine():
    cpu = platform.processor() or platform.machine()
    try:
        with open("/proc/cpuinfo", encoding="utf-8") as info:
            for line in info:
                if line.startswith("model name") or line.startswith("Model"):
                    cpu = line.split(":", 1)[1].strip()
                    break
    except OSError:
        pass
    return {"arch": platform.machine(), "cpu": cpu, "cpus": os.cpu_count()}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--mcdec", default="tools/ManagedCodecs.Cli/bin/Release/net10.0/mcdec.dll")
    parser.add_argument("--base", help="another mcdec.dll to compare with, instead of FFmpeg")
    parser.add_argument("--runs", type=int, default=5)
    parser.add_argument("--samples", default="samples")
    parser.add_argument("--only", nargs="*", help="sample names to run (default: all)")
    parser.add_argument("--report", help="Markdown table to write")
    parser.add_argument("--json", help="JSON record to write")
    args = parser.parse_args()

    samples = [s for s in SAMPLES if not args.only or s[0] in args.only]
    record = {
        "date": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "commit": version(["git", "rev-parse", "HEAD"], r"(\w+)"),
        "machine": machine(),
        "ffmpeg": version(["ffmpeg", "-hide_banner", "-version"], r"ffmpeg version (\S+)"),
        "dotnet": version(["dotnet", "--version"], r"(\S+)"),
        "runs": args.runs,
        "results": [],
    }

    if args.base:
        header = "| Sample | Loops | Base (µs) | Branch (µs) | Branch / base |\n| --- | ---: | ---: | ---: | ---: |"
    else:
        header = "| Sample | Loops | ManagedCodecs (µs) | FFmpeg (µs) | Ratio |\n| --- | ---: | ---: | ---: | ---: |"
    lines = [header]
    for name, loops, description in samples:
        path = os.path.join(args.samples, name)
        print(f"{name} x {loops} loops", file=sys.stderr, flush=True)
        _, count = mcdec(args.mcdec, path, 1)  # warms the file cache and counts frames
        if args.base:
            base, ours = interleaved(args.runs, lambda: mcdec(args.base, path, loops)[0], lambda: mcdec(args.mcdec, path, loops)[0])
            record["results"].append({"sample": name, "loops": loops, "base_us": round(base, 1), "branch_us": round(ours, 1), "ratio": round(ours / base, 3)})
            lines.append(f"| `{name}` ({description}) | {loops} | {base:.0f} | {ours:.0f} | {ours / base:.3f} |")
        else:
            ours, theirs = interleaved(args.runs, lambda: mcdec(args.mcdec, path, loops)[0], lambda: ffmpeg(path, loops, count))
            record["results"].append({"sample": name, "loops": loops, "managedcodecs_us": round(ours, 1), "ffmpeg_us": round(theirs, 1), "ratio": round(ours / theirs, 3)})
            lines.append(f"| `{name}` ({description}) | {loops} | {ours:.0f} | {theirs:.0f} | {ours / theirs:.2f} |")

    if not args.base and (not args.only or THREADS_SAMPLE[0] in args.only):
        name, loops = THREADS_SAMPLE
        path = os.path.join(args.samples, name)
        print(f"{name} x {loops} loops, 1 and 4 threads", file=sys.stderr, flush=True)
        one, four = interleaved(args.runs, lambda: mcdec(args.mcdec, path, loops)[0], lambda: mcdec(args.mcdec, path, loops, threads=4)[0])
        record["threads"] = {"sample": name, "loops": loops, "one_us": round(one, 1), "four_us": round(four, 1), "ratio": round(four / one, 3)}
        lines.append("")
        lines.append(f"`{name}` with 4 threads: {four:.0f} µs a frame against {one:.0f} with 1 ({four / one:.2f} of the time).")

    m = record["machine"]
    lines.append("")
    lines.append(f"{m['cpus']} CPUs ({m['cpu']}, {m['arch']}), FFmpeg {record['ffmpeg']}, .NET SDK {record['dotnet']}; "
                 f"medians of {args.runs} interleaved runs, wall time per decoded frame (AAC frames 1024 samples, HE-AAC 2048).")
    report = "\n".join(lines) + "\n"
    print(report)
    if args.report:
        with open(args.report, "w", encoding="utf-8") as out:
            out.write(report)
    if args.json:
        with open(args.json, "w", encoding="utf-8") as out:
            json.dump(record, out, indent=1)
            out.write("\n")


if __name__ == "__main__":
    main()
