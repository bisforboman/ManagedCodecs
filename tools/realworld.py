#!/usr/bin/env python3
"""Decodes real public films (Blender's open movies, CC-BY: real H.264 and AAC from real encoders, where every sample
in samples/ is a test pattern) with mcdec and with FFmpeg, and fails unless every video frame is identical and the
audio is within 2^-17, the conformance tests' tolerance. The films are pinned by SHA-256 and cached in
samples/realworld/ (gitignored).

Usage (from the repository root, after a Release build of tools/ManagedCodecs.Cli):
  tools/realworld.py [name ...] [--report realworld.md]
  tools/realworld.py --list
"""

import argparse
import hashlib
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request
import zipfile

import numpy as np

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CACHE = os.path.join(ROOT, "samples", "realworld")
MCDEC = os.path.join(ROOT, "tools", "ManagedCodecs.Cli", "bin", "Release", "net10.0", "mcdec.dll")
TOLERANCE = 2.0 ** -17

# name: (url, sha256, what it is). Add a line (download once, `sha256sum` it) and a matrix entry in realworld.yml. A
# film served in a .zip is extracted (the archive's one file); its SHA-256 is the film's, not the archive's.
FILMS = {
    "sintel_trailer-1080p.mp4": (
        "https://download.blender.org/durian/trailer/sintel_trailer-1080p.mp4",
        "34bbd52a4b89fdf63c8ace50b268da26653a59508288100cd3c23de276db7931",
        "Sintel's trailer: 1920x1080 H.264 High, 52 s, stereo AAC LC from FAAC 1.26.1"),
    "elephantsdream-480-h264-st-aac.mov": (
        "https://download.blender.org/ED/elephantsdream-480-h264-st-aac.mov",
        "a7dbacae5fa2bc345dbd7b10588575f3e2bba36012e9a93df5346467053b0634",
        "Elephants Dream, the whole film: 480x270 H.264 Main in an Apple MOV, 11 minutes, stereo AAC (in a 'wave' box), negative composition offsets"),
    "tears_of_steel_720p.mov": (
        "https://download.blender.org/demo/movies/ToS/tears_of_steel_720p.mov",
        "efa9062d9cdb7a338e40ad530dfdf234806743f29ae6a1a136b97ece4e588e8f",
        "Tears of Steel, the whole film: 1280x534 H.264 Main in a MOV, 12 minutes, live action (its MP3 audio is not decoded)"),
    "caminandes_gran_dillama.mp4": (
        "https://download.blender.org/demo/movies/caminandes_gran_dillama.mp4.zip",
        "468e6743c674689a728726bbe4bb4b2a65bd8702a89f021af26a8bb4d450eebd",
        "Caminandes 2: 1920x1080 H.264 High from MainConcept, 2.5 minutes, CABAC, B pictures, explicit weights in P slices, four PPS, stereo AAC LC at 48 kHz"),
    "BigBuckBunny_320x180.mp4": (
        "https://download.blender.org/peach/bigbuckbunny_movies/BigBuckBunny_320x180.mp4.zip",
        "f78f39603e6774907f2faafabf26a667f4a6fc31769ec304a8a8f7c62d280508",
        "Big Buck Bunny, the whole film: 320x180 H.264 Constrained Baseline muxed by FFmpeg in 2008 (Lavf52), 10 minutes, deblocking off, stereo AAC LC at 48 kHz"),
}


def fail(message):
    print(f"::error::{message}" if os.environ.get("GITHUB_ACTIONS") else message, file=sys.stderr)
    sys.exit(1)


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as f:
        while chunk := f.read(1 << 20):
            digest.update(chunk)
    return digest.hexdigest()


def fetch(name):
    url, pinned, _ = FILMS[name]
    path = os.path.join(CACHE, name)
    if not os.path.exists(path):
        os.makedirs(CACHE, exist_ok=True)
        request = urllib.request.Request(url, headers={"User-Agent": "ManagedCodecs real-world check"})
        for attempt in range(1, 5):
            print(f"downloading {url}" + (f" (try {attempt})" if attempt > 1 else ""), flush=True)
            try:
                with urllib.request.urlopen(request, timeout=60) as response, open(path + ".part", "wb") as out:
                    while chunk := response.read(1 << 20):
                        out.write(chunk)
                if url.endswith(".zip"):
                    with zipfile.ZipFile(path + ".part") as archive, archive.open(archive.namelist()[0]) as film, \
                            open(path + ".film", "wb") as out:
                        while chunk := film.read(1 << 20):
                            out.write(chunk)
                    os.replace(path + ".film", path)
                    os.remove(path + ".part")
                else:
                    os.replace(path + ".part", path)
                break
            except (urllib.error.URLError, OSError, zipfile.BadZipFile) as e:
                if attempt == 4:
                    fail(f"{name}: download failed: {e}")
                time.sleep(10 * attempt)
    actual = sha256(path)
    if pinned and actual != pinned:
        os.remove(path)
        fail(f"{name}: SHA-256 {actual}, expected {pinned}")
    return path


def ffmpeg(*args):
    result = subprocess.run(["ffmpeg", "-v", "error", "-nostdin", *args], capture_output=True, check=False)
    if result.returncode != 0:
        fail(f"ffmpeg {' '.join(args)} failed:\n{result.stderr.decode(errors='replace')}")
    return result.stdout


def compare(name, work):
    """A report line, and whether the film matched."""
    path = fetch(name)
    prefix = os.path.join(work, name)
    run = subprocess.run(["dotnet", MCDEC, "--dump", path, prefix], capture_output=True, text=True, check=False)
    if run.returncode != 0:
        return f"| `{name}` | mcdec failed | {run.stderr.strip().splitlines()[-1] if run.stderr.strip() else run.returncode} | |", False
    print(run.stdout.strip(), flush=True)

    ok = True
    video = "no video decoder"
    if os.path.exists(prefix + ".video.md5"):
        ours = open(prefix + ".video.md5", encoding="ascii").read().split()
        lines = ffmpeg("-i", path, "-map", "0:v:0", "-fps_mode", "passthrough", "-f", "framemd5", "-").decode().splitlines()
        theirs = [line.split(",")[5].strip() for line in lines if line and not line.startswith("#")]
        first = next((i for i, (a, b) in enumerate(zip(ours, theirs)) if a != b), None)
        if first is None and len(ours) == len(theirs):
            video = f"{len(ours)} frames identical"
        else:
            ok = False
            video = f"ours {len(ours)} frames, FFmpeg's {len(theirs)}" + (f", first different: frame {first}" if first is not None else "")

    audio = "no audio decoder"
    if os.path.exists(prefix + ".audio.f32"):
        ours = np.fromfile(prefix + ".audio.f32", dtype="<f4")
        theirs = np.frombuffer(ffmpeg("-i", path, "-map", "0:a:0", "-f", "f32le", "-"), dtype="<f4")
        common = min(len(ours), len(theirs))
        largest = float(np.abs(ours[:common] - theirs[:common]).max()) if common else 0.0
        if len(ours) == len(theirs) and largest <= TOLERANCE:
            audio = f"{len(ours)} values within {largest:.2g}"
        else:
            ok = False
            audio = f"ours {len(ours)} values, FFmpeg's {len(theirs)}; largest difference {largest:.3g}"
        os.remove(prefix + ".audio.f32")

    return f"| `{name}` | {'match' if ok else '**differs**'} | {video} | {audio} |", ok


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("names", nargs="*", help="films to check (default: all)")
    parser.add_argument("--list", action="store_true")
    parser.add_argument("--work", default=os.path.join(CACHE, "work"))
    parser.add_argument("--report", help="Markdown table to write")
    args = parser.parse_args()
    if args.list:
        for name, (url, _, what) in FILMS.items():
            print(f"{name}: {what} ({url})")
        return

    names = args.names or list(FILMS)
    unknown = [n for n in names if n not in FILMS]
    if unknown:
        fail(f"unknown films: {', '.join(unknown)} (--list shows them)")
    os.makedirs(args.work, exist_ok=True)
    lines = ["| Film | Result | Video | Audio |", "| --- | --- | --- | --- |"]
    all_ok = True
    for name in names:
        line, ok = compare(name, args.work)
        lines.append(line)
        all_ok &= ok
    report = "\n".join(lines) + "\n"
    print(report)
    if args.report:
        with open(args.report, "w", encoding="utf-8") as out:
            out.write(report)
    if not all_ok:
        fail("some films differ from FFmpeg (table above)")


if __name__ == "__main__":
    main()
