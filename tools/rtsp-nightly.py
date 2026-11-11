#!/usr/bin/env python3
"""LivePlayer against a real RTSP server (docs/decisions/2026-11-10-live-rtsp-h264.md, decision 4): mediamtx serves
FFmpeg's push of a libx264 clip in a loop, and `mcdec --live` plays it, checking each frame it shows against the clip's
own frames (tools/ManagedCodecs.Cli/LiveCheck.cs). Once per authentication method mediamtx offers (Basic, Digest).
Fails on a frame not in the clip, a lost packet, a damaged frame, a reconnect, or too few frames. mediamtx is
downloaded once, pinned by SHA-256, into samples/realworld/ (gitignored). Nightly (realworld.yml), not on pull
requests: the required checks use the loopback server in the tests.

Usage (from the repository root, after a Release build of tools/ManagedCodecs.Cli; needs ffmpeg with libx264):
  tools/rtsp-nightly.py [--frames N] [--report rtsp.md]
"""

import argparse
import hashlib
import os
import platform
import socket
import subprocess
import sys
import tarfile
import tempfile
import threading
import time
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CACHE = os.path.join(ROOT, "samples", "realworld")
MCDEC = os.path.join(ROOT, "tools", "ManagedCodecs.Cli", "bin", "Release", "net10.0", "mcdec.dll")

# mediamtx's release (bluenviron/mediamtx, MIT): its checksums.sha256 lists these.
MEDIAMTX_VERSION = "v1.21.3"
MEDIAMTX = {
    "x86_64": ("linux_amd64", "0a2ea7eba96023631efa1bea7804823a9145b043b6646a8a05798d4ac24ed635"),
}

USER, PASSWORD = "viewer", "s3cret"
PORT = 8554

# Only RTSP over TCP (what LivePlayer speaks), the publisher from this machine without a password, a reader with one.
CONFIG = """\
logLevel: info
api: false
metrics: false
pprof: false
playback: false
rtmp: false
hls: false
webrtc: false
srt: false
rtsp: true
rtspTransports: [tcp]
rtspAddress: :{port}
rtspAuthMethods: [{method}]
authInternalUsers:
  - user: any
    ips: ['127.0.0.1', '::1']
    permissions:
      - action: publish
  - user: {user}
    pass: {password}
    permissions:
      - action: read
paths:
  all_others:
"""


def mediamtx():
    """The mediamtx binary, downloaded and checked once."""
    arch = platform.machine()
    if arch not in MEDIAMTX:
        sys.exit(f"No pinned mediamtx for {arch}.")
    name, sha256 = MEDIAMTX[arch]
    folder = os.path.join(CACHE, f"mediamtx-{MEDIAMTX_VERSION}-{name}")
    binary = os.path.join(folder, "mediamtx")
    if os.path.exists(binary):
        return binary
    os.makedirs(folder, exist_ok=True)
    archive = f"mediamtx_{MEDIAMTX_VERSION}_{name}.tar.gz"
    url = f"https://github.com/bluenviron/mediamtx/releases/download/{MEDIAMTX_VERSION}/{archive}"
    print(f"Downloading {url}", flush=True)
    data = urllib.request.urlopen(url, timeout=120).read()
    if hashlib.sha256(data).hexdigest() != sha256:
        sys.exit(f"{archive}: SHA-256 {hashlib.sha256(data).hexdigest()}, not the pinned {sha256}.")
    path = os.path.join(folder, archive)
    with open(path, "wb") as f:
        f.write(data)
    with tarfile.open(path) as tar:
        tar.extract("mediamtx", folder, filter="data")
    os.remove(path)
    return binary


def make_clip(folder):
    """10 s of 720p25 from libx264 with B pictures and an IDR picture every 2 s: its IDR pictures are larger than a
    packet, so RTP carries them in FU-A fragments."""
    clip = os.path.join(folder, "clip.mp4")
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=25", "-t", "10",
                    "-c:v", "libx264", "-preset", "veryfast", "-g", "50", "-pix_fmt", "yuv420p", clip], check=True)
    return clip


def wait_for_port(port, seconds=20):
    deadline = time.time() + seconds
    while time.time() < deadline:
        try:
            with socket.create_connection(("127.0.0.1", port), timeout=1):
                return
        except OSError:
            time.sleep(0.2)
    sys.exit(f"Nothing is listening on port {port}.")


def run(method, binary, clip, folder, frames):
    """mediamtx with one authentication method, FFmpeg pushing the clip, then mcdec --live; its output and status."""
    config = os.path.join(folder, f"mediamtx-{method}.yml")
    with open(config, "w") as f:
        f.write(CONFIG.format(port=PORT, method=method, user=USER, password=PASSWORD))
    log = []
    published = threading.Event()
    # In the temporary folder: mediamtx writes a self-signed certificate (auto.crt, auto.key) where it runs.
    server = subprocess.Popen([binary, config], cwd=folder, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)

    def read():
        for line in server.stdout:
            log.append(line.rstrip())
            if "is publishing to path" in line:
                published.set()

    threading.Thread(target=read, daemon=True).start()
    push = None
    try:
        wait_for_port(PORT)
        push = subprocess.Popen(["ffmpeg", "-v", "error", "-re", "-stream_loop", "-1", "-i", clip, "-c", "copy",
                                 "-f", "rtsp", "-rtsp_transport", "tcp", f"rtsp://127.0.0.1:{PORT}/live"])
        if not published.wait(20):
            return "\n".join(["FFmpeg's push never reached mediamtx.", *log[-20:]]), 1
        url = f"rtsp://{USER}:{PASSWORD}@127.0.0.1:{PORT}/live"
        check = subprocess.run(["dotnet", MCDEC, "--live", url, clip, "--frames", str(frames)],
                               capture_output=True, text=True)
        output = check.stdout + check.stderr
        if check.returncode != 0:
            output += "\nmediamtx's log:\n" + "\n".join(log[-30:])
        return output.strip(), check.returncode
    finally:
        for process in (push, server):
            if process is not None:
                process.terminate()
                try:
                    process.wait(10)
                except subprocess.TimeoutExpired:
                    process.kill()


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--frames", type=int, default=250, help="frames to show for each method (default 250)")
    parser.add_argument("--report", help="also write the results as Markdown")
    args = parser.parse_args()
    if not os.path.exists(MCDEC):
        sys.exit(f"Build tools/ManagedCodecs.Cli in Release first ({MCDEC}).")

    binary = mediamtx()
    version = subprocess.run([binary, "--version"], capture_output=True, text=True).stdout.strip()
    failed = 0
    report = [f"mediamtx {version}, FFmpeg pushing a 720p25 libx264 clip in a loop, {args.frames} frames a method.", ""]
    with tempfile.TemporaryDirectory() as folder:
        clip = make_clip(folder)
        for method in ("basic", "digest"):
            output, status = run(method, binary, clip, folder, args.frames)
            verdict = "passed" if status == 0 else "FAILED"
            print(f"== {method}: {verdict}\n{output}\n", flush=True)
            report += [f"**{method}**: {verdict}", "", "```", output, "```", ""]
            failed += status != 0

    if args.report:
        with open(args.report, "w") as f:
            f.write("\n".join(report))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
