#!/usr/bin/env python3
"""LivePlayer against live555MediaServer (issue #527; many cameras' RTSP servers are built on live555). The server
streams files from the folder it runs in, each reader from the file's start and once, without looping, so every clip
is longer than its run, and `mcdec --live` checks each frame shown, and each chunk of sound, against the clip's own
(tools/ManagedCodecs.Cli/LiveCheck.cs). The runs:
- H.264 from a `.264` file (Annex B, libx264 with B pictures and without), RTP over the RTSP connection.
- The same with the server refusing RTP over the connection (461), so that LivePlayer takes UDP
  (docs/decisions/2026-11-14-live-rtsp-udp.md).
- H.264 and AAC from a `.mkv`, with RtspSource.Audio (`--audio`), over both transports: live555 sends one access unit a
  packet (RFC 3640, `mode=AAC-hbr`), as cameras do.
- Digest authentication, the only kind live555 has.
- The end of a file, over both transports: live555 sends RTCP BYE and stops, leaving the connection open. LivePlayer
  must connect again at once (`--reconnects 1`), not after its 5 s stall timeout, and the file plays again from its
  start: the longest wait between two frames shown must be under END_GAP (docs/decisions/2026-11-15-live-rtcp.md, #571).
- A session held longer than live555's 65 s without a sign of life from the reader (`--hold`, 90 s), over both
  transports: it must play on without a reconnect. live555 takes an RTSP request with the session's id, or an RTCP
  receiver report, for that sign (docs/decisions/2026-11-15-live-rtcp.md).
Fails on a frame not in the clip, a lost packet, a damaged frame, a reconnect (but the one at a file's end), too few
frames, a held session shorter than `--hold`, or a wait at a file's end of END_GAP or more.

live555's source is downloaded once from Ubuntu's archive (live555.com keeps only its newest version), pinned by
SHA-256, and built in samples/realworld/ (gitignored; needs g++, make and libssl-dev). live555MediaServer has no options, so
its main() is patched (PATCHES, below) to read three environment variables: a user and password
(UserAuthenticationDatabase, what its own ACCESS_CONTROL does), and RTSPServer::disableStreamingRTPOverTCP(). The
library is built as it is.

Usage (from the repository root, after a Release build of tools/ManagedCodecs.Cli; needs ffmpeg with libx264):
  tools/live555-check.py [--frames N] [--hold SECONDS] [--report live555.md]
"""

import argparse
import hashlib
import os
import re
import socket
import subprocess
import sys
import tarfile
import tempfile
import threading
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CACHE = os.path.join(ROOT, "samples", "realworld")
MCDEC = os.path.join(ROOT, "tools", "ManagedCodecs.Cli", "bin", "Release", "net10.0", "mcdec.dll")

# live555's source as Ubuntu's liblivemedia package has it (LGPL; built and run here, nothing of it in the repository).
LIVE555_VERSION = "2026.11.08"
LIVE555_URL = f"http://archive.ubuntu.com/ubuntu/pool/universe/libl/liblivemedia/liblivemedia_{LIVE555_VERSION}.orig.tar.gz"
LIVE555_SHA256 = "95eb2ad572a5f88d817717c00c534dbdfdeb2df0afaa0e0052a52c4f9f041a7c"

# mediaServer/live555MediaServer.cpp: each text is found once and gets the lines after it put before it.
PATCHES = (
    ("#include <BasicUsageEnvironment.hh>\n", "#include <stdlib.h>\n"),
    ("  // Create the RTSP server.  Try first", """\
  if (getenv("LIVE555_CHECK_USER") != NULL && getenv("LIVE555_CHECK_PASSWORD") != NULL) {
    authDB = new UserAuthenticationDatabase;
    authDB->addUserRecord(getenv("LIVE555_CHECK_USER"), getenv("LIVE555_CHECK_PASSWORD"));
  }
"""),
    ("  *env << \"LIVE555 Media Server\\n\";\n", """\
  if (getenv("LIVE555_CHECK_NO_TCP") != NULL) rtspServer->disableStreamingRTPOverTCP();
"""),
)

USER, PASSWORD = "viewer", "s3cret"
FPS = 25

# The end-of-file runs: a clip of END_SECONDS played for END_FRAMES frames (once and then some), and the longest wait
# allowed between two frames shown. Connecting again after BYE takes the player's first backoff (0.5 s) and an RTSP
# session's setup; waiting out the stall timeout instead would take more than 5 s.
END_SECONDS, END_FRAMES, END_GAP = 6, 200, 2.5


def live555():
    """live555MediaServer, downloaded, checked, patched and built once."""
    folder = os.path.join(CACHE, f"live555-{LIVE555_VERSION}")
    binary = os.path.join(folder, "live", "mediaServer", "live555MediaServer")
    if os.path.exists(binary):
        return binary
    os.makedirs(folder, exist_ok=True)
    print(f"Downloading {LIVE555_URL}", flush=True)
    data = urllib.request.urlopen(LIVE555_URL, timeout=120).read()
    if hashlib.sha256(data).hexdigest() != LIVE555_SHA256:
        sys.exit(f"{LIVE555_URL}: SHA-256 {hashlib.sha256(data).hexdigest()}, not the pinned {LIVE555_SHA256}.")
    archive = os.path.join(folder, "live555.tar.gz")
    with open(archive, "wb") as f:
        f.write(data)
    with tarfile.open(archive) as tar:
        tar.extractall(folder, filter="data")
    os.remove(archive)

    source = os.path.join(folder, "live")
    main = os.path.join(source, "mediaServer", "live555MediaServer.cpp")
    with open(main) as f:
        text = f.read()
    for at, lines in PATCHES:
        if text.count(at) != 1:
            sys.exit(f"{main} has no single {at.strip()!r} to patch at.")
        text = text.replace(at, lines + at)
    with open(main, "w") as f:
        f.write(text)

    print("Building live555MediaServer", flush=True)
    steps = [["./genMakefiles", "linux"]]
    steps += [["make", "-C", part, f"-j{os.cpu_count() or 2}"]
              for part in ("liveMedia", "groupsock", "UsageEnvironment", "BasicUsageEnvironment", "mediaServer")]
    for step in steps:
        build = subprocess.run(step, cwd=source, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        if build.returncode != 0:
            sys.exit(f"{' '.join(step)} failed:\n{build.stdout[-4000:]}")
    return binary


def ffmpeg(*arguments):
    subprocess.run(["ffmpeg", "-v", "error", "-y", *arguments], check=True)


def make_h264(folder, name, seconds, size="1280x720", b_pictures=True):
    """A libx264 clip with an IDR picture every 2 s (at 720p larger than a packet: FU-A fragments), as an MP4 for
    mcdec to decode and as the same stream in Annex B (`.264`) for live555, which reads the frame rate from the SPS's
    timing (libx264 writes it; without it live555 assumes 30 fps)."""
    clip = os.path.join(folder, name + ".mp4")
    ffmpeg("-f", "lavfi", "-i", f"testsrc2=size={size}:rate={FPS}", "-t", str(seconds), "-c:v", "libx264", "-preset", "veryfast",
           "-g", str(2 * FPS), *([] if b_pictures else ["-bf", "0"]), "-pix_fmt", "yuv420p", clip)
    ffmpeg("-i", clip, "-c", "copy", "-bsf:v", "h264_mp4toannexb", "-f", "h264", os.path.join(folder, name + ".264"))
    return clip


def make_sound(folder, name, seconds):
    """640x360 H.264 with AAC LC sound (48 kHz stereo, FFmpeg's encoder) in Matroska, which live555 demuxes itself:
    noise without PNS, as tools/rtsp-nightly.py's clip and for its reasons."""
    clip = os.path.join(folder, name + ".mkv")
    ffmpeg("-f", "lavfi", "-i", f"testsrc2=size=640x360:rate={FPS}",
           "-f", "lavfi", "-i", "anoisesrc=color=pink:amplitude=0.3:sample_rate=48000", "-ac", "2", "-t", str(seconds),
           "-c:v", "libx264", "-preset", "veryfast", "-g", str(2 * FPS), "-pix_fmt", "yuv420p",
           "-c:a", "aac", "-aac_pns", "0", "-b:a", "128k", clip)
    return clip


class Server:
    """live555MediaServer serving a folder: port 554 as root, else 8554 (its startup lines say which)."""

    def __init__(self, binary, folder, udp_only=False, password=False):
        environment = {k: v for k, v in os.environ.items() if not k.startswith("LIVE555_CHECK_")}
        if udp_only:
            environment["LIVE555_CHECK_NO_TCP"] = "1"
        if password:
            environment["LIVE555_CHECK_USER"], environment["LIVE555_CHECK_PASSWORD"] = USER, PASSWORD
        self.log = []
        self.port = None
        started = threading.Event()
        self.process = subprocess.Popen([binary], cwd=folder, env=environment, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)

        def read():
            for line in self.process.stdout:
                self.log.append(line.rstrip())
                if (match := re.search(r"rtsp://[^/]*?(?::(\d+))?/<filename>", line)) and self.port is None:
                    self.port = int(match.group(1) or 554)
                if "tunneling" in line:  # its last startup line
                    started.set()
            started.set()

        threading.Thread(target=read, daemon=True).start()
        if not started.wait(20) or self.port is None or self.process.poll() is not None:
            self.close()
            sys.exit("live555MediaServer didn't start:\n" + "\n".join(self.log[-20:]))

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()

    def close(self):
        self.process.terminate()
        try:
            self.process.wait(10)
        except subprocess.TimeoutExpired:
            self.process.kill()

    def describe(self, name):
        """The server's SDP for a file (a DESCRIBE of our own), for the report."""
        url = f"rtsp://127.0.0.1:{self.port}/{name}"
        with socket.create_connection(("127.0.0.1", self.port), timeout=10) as sock:
            sock.sendall(f"DESCRIBE {url} RTSP/1.0\r\nCSeq: 1\r\nAccept: application/sdp\r\n\r\n".encode())
            reader = sock.makefile("rb")
            status = reader.readline().decode().strip()
            length = 0
            while (line := reader.readline().decode().strip()):
                if line.lower().startswith("content-length:"):
                    length = int(line.split(":")[1])
            return status + "\n" + reader.read(length).decode(errors="replace").replace("\r\n", "\n").strip()


def play(server, name, clip, frames, audio=False, password=False, hold=0, end=False):
    """mcdec --live on one of the server's files; its output and status. A run with hold must have lasted that long; one
    past the file's end (end) must have connected again once, with no wait between frames of END_GAP or more."""
    user = f"{USER}:{PASSWORD}@" if password else ""
    check = subprocess.run(["dotnet", MCDEC, "--live", f"rtsp://{user}127.0.0.1:{server.port}/{name}", clip, "--frames", str(frames),
                            *(["--audio"] if audio else []), *(["--reconnects", "1"] if end else [])], capture_output=True, text=True)
    output = (check.stdout + check.stderr).strip()
    status = check.returncode
    if hold and status == 0:
        shown = re.search(r"frames shown in ([\d.]+) s", output)
        if shown is None or float(shown.group(1)) < hold:
            output += f"\nThe session wasn't held for {hold} s."
            status = 1
    if end and status == 0:
        gap = re.search(r"longest wait between two frames shown: ([\d.]+) s", output)
        if gap is None or float(gap.group(1)) >= END_GAP or "RTCP BYE" not in output:
            output += f"\nThe file's end wasn't a BYE followed by a new session within {END_GAP} s."
            status = 1
    if status != 0:
        output += "\nlive555MediaServer's output:\n" + "\n".join(server.log[-10:])
    return output, status


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--frames", type=int, default=400, help="frames to show a run (default 400, of a 20 s clip's 500)")
    parser.add_argument("--hold", type=int, default=90, help="seconds the held sessions last (default 90; 0 for none)")
    parser.add_argument("--report", help="also write the results as Markdown")
    args = parser.parse_args()
    if not os.path.exists(MCDEC):
        sys.exit(f"Build tools/ManagedCodecs.Cli in Release first ({MCDEC}).")
    if not 0 < args.frames <= 18 * FPS:
        sys.exit(f"--frames is at most {18 * FPS}: the clips are 20 s, and live555 plays a file once.")

    binary = live555()
    failed = 0
    report = [f"live555MediaServer, LIVE555 {LIVE555_VERSION}, serving libx264 clips (720p{FPS}; 640x360 with AAC), {args.frames} frames a run.", ""]

    def record(label, output, status):
        nonlocal failed
        verdict = "passed" if status == 0 else "FAILED"
        print(f"== {label}: {verdict}\n{output}\n", flush=True)
        report.extend([f"**{label}**: {verdict}", "", "```", output, "```", ""])
        failed += status != 0

    with tempfile.TemporaryDirectory() as folder:
        b = make_h264(folder, "b", 20)
        plain = make_h264(folder, "plain", 20, b_pictures=False)
        sound = make_sound(folder, "sound", 20)
        # The held sessions' clip: 15 s longer than the hold, since the player shows the newest frame and may pass some by.
        held = make_h264(folder, "held", args.hold + 15, size="640x360") if args.hold else None
        short = make_h264(folder, "short", END_SECONDS, size="640x360", b_pictures=False)

        with Server(binary, folder) as server:
            print("\n".join(server.log[:2]), flush=True)
            report[0] = f"{server.log[1].strip()}; libx264 clips (720p{FPS}; 640x360 with AAC), {args.frames} frames a run."
            for name in ("b.264", "sound.mkv"):
                sdp = server.describe(name)
                print(f"== DESCRIBE {name}\n{sdp}\n", flush=True)
                report.extend([f"DESCRIBE {name}:", "", "```", sdp, "```", ""])
            record("H.264 with B pictures (.264)", *play(server, "b.264", b, args.frames))
            record("H.264 without B pictures (.264)", *play(server, "plain.264", plain, args.frames))
            record("H.264 and AAC (.mkv)", *play(server, "sound.mkv", sound, args.frames, audio=True))
            if held:
                record(f"H.264 held for {args.hold} s", *play(server, "held.264", held, args.hold * FPS, hold=args.hold))
            record(f"The end of a {END_SECONDS} s file (RTCP BYE)", *play(server, "short.264", short, END_FRAMES, end=True))
        with Server(binary, folder, udp_only=True) as server:
            record("H.264 with B pictures, RTP over UDP only", *play(server, "b.264", b, args.frames))
            record("H.264 and AAC, RTP over UDP only", *play(server, "sound.mkv", sound, args.frames, audio=True))
            if held:
                record(f"H.264 held for {args.hold} s, RTP over UDP only", *play(server, "held.264", held, args.hold * FPS, hold=args.hold))
            record(f"The end of a {END_SECONDS} s file (RTCP BYE), RTP over UDP only", *play(server, "short.264", short, END_FRAMES, end=True))
        with Server(binary, folder, password=True) as server:
            record("H.264 with B pictures, digest", *play(server, "b.264", b, args.frames, password=True))

    if args.report:
        with open(args.report, "w") as f:
            f.write("\n".join(report))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
