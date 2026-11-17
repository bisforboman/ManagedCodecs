#!/usr/bin/env python3
"""LivePlayer against a real RTSP server (docs/decisions/2026-11-10-live-rtsp-h264.md, decision 4): mediamtx serves
FFmpeg's push of a clip in a loop, and `mcdec --live` plays it, checking each frame it shows against the clip's own
frames (tools/ManagedCodecs.Cli/LiveCheck.cs). An H.264 clip once per authentication method mediamtx offers (Basic,
Digest), an MJPEG one with Digest, which mediamtx sends as JPEG over RTP (RFC 2435;
docs/decisions/2026-11-12-live-rtsp-jpeg.md), and the H.264 one again with mediamtx serving RTP over UDP only: it
answers 461 to RTP over the RTSP connection, and LivePlayer takes UDP (docs/decisions/2026-11-14-live-rtsp-udp.md).
And an H.264 clip with AAC sound, over both transports, played with RtspSource.Audio (`mcdec --live --audio`): each
chunk of audio taken must be the clip's too (docs/decisions/2026-11-15-live-rtsp-audio.md). And the H.264 clip over
RTSPS (mediamtx with `rtspEncryption: strict` and a certificate openssl makes, pinned by `--certificate-hash`;
docs/decisions/2026-11-15-live-rtsps.md).
And over a network that misbehaves (issue #527), tools/rtsp-lossy-relay.py between mediamtx (UDP only) and LivePlayer:
once sending packets twice and holding some back 10 ms for 6 s, which the 50 ms reorder window must put right with nothing lost
and every frame the clip's; and once dropping 2% of the packets for 6 s, with and without sound, where the packets
LivePlayer counts lost must be the ones the relay dropped, its last receiver reports must say the same, it must not
reconnect, and the last 75 frames and chunks must be the clip's again (`mcdec --live --lossy`).
Fails on a frame not in the clip, a lost packet, a damaged frame, a reconnect, or too few frames. mediamtx is
downloaded once, pinned by SHA-256, into samples/realworld/ (gitignored). Nightly (realworld.yml), not on pull
requests: the required checks use the loopback server in the tests.

Usage (from the repository root, after a Release build of tools/ManagedCodecs.Cli; needs ffmpeg with libx264):
  tools/rtsp-nightly.py [--frames N] [--report rtsp.md]
"""

import argparse
import hashlib
import json
import os
import platform
import re
import ssl
import socket
import struct
import subprocess
import sys
import tarfile
import tempfile
import threading
import time
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CACHE = os.path.join(ROOT, "samples", "realworld")
RELAY = os.path.join(ROOT, "tools", "rtsp-lossy-relay.py")
MCDEC = os.path.join(ROOT, "tools", "ManagedCodecs.Cli", "bin", "Release", "net10.0", "mcdec.dll")

# mediamtx's release (bluenviron/mediamtx, MIT): its checksums.sha256 lists these.
MEDIAMTX_VERSION = "v1.21.3"
MEDIAMTX = {
    "x86_64": ("linux_amd64", "0a2ea7eba96023631efa1bea7804823a9145b043b6646a8a05798d4ac24ed635"),
}

# rtsp-lossy-relay.py's options: a twentieth of the packets sent twice and a tenth held back 10 ms, inside LivePlayer's
# 50 ms reorder window; and 2% dropped; each for the first 6 s.
REORDERING = ["--repeat", "0.05", "--reorder", "0.1", "--seconds", "6"]
LOSING = ["--loss", "0.02", "--seconds", "6"]

USER, PASSWORD = "viewer", "s3cret"
PORT = 8554
RELAY_PORT = 8555
TLS_PORT = 8322

# RTSPS only: TLS on its own port, RTP inside it (or SRTP, which LivePlayer doesn't take).
TLS = """\
rtspEncryption: "strict"
rtspsAddress: :{tls_port}
rtspServerKey: {key}
rtspServerCert: {cert}
"""

# One RTP transport (tcp, or udp to check LivePlayer's fallback to it), the publisher from this machine without a password, a reader with one.
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
rtspTransports: [{transports}]
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


def make_audio_clip(folder):
    """The H.264 clip's picture at 640x360 with 10 s of AAC LC sound (48 kHz stereo, FFmpeg's encoder): noise, so no
    two frames decode alike, and without PNS, whose decoded noise depends on every frame before it, so that a frame's
    samples are the file's whenever the frame before it was the file's (mcdec --live --audio)."""
    clip = os.path.join(folder, "clip-audio.mp4")
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25",
                    "-f", "lavfi", "-i", "anoisesrc=color=pink:amplitude=0.3:sample_rate=48000", "-ac", "2", "-t", "10",
                    "-c:v", "libx264", "-preset", "veryfast", "-g", "50", "-pix_fmt", "yuv420p",
                    "-c:a", "aac", "-aac_pns", "0", "-b:a", "128k", clip], check=True)
    return clip


def make_mjpeg_clip(folder):
    """10 s of 640x480 MJPEG at 25 fps, 4:2:0 with T.81's standard Huffman tables, the only ones RFC 2435 can carry
    (FFmpeg's RTP muxer refuses others)."""
    clip = os.path.join(folder, "clip.avi")
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=640x480:rate=25", "-t", "10",
                    "-c:v", "mjpeg", "-q:v", "4", "-huffman", "default", "-pix_fmt", "yuvj420p", clip], check=True)
    return clip


def record_rtp(clip):
    """FFmpeg's RFC 2435 packets for an MJPEG clip (-c copy -f rtp to a loopback UDP port), for publish_jpeg."""
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 16 * 1024 * 1024)
    sock.bind(("127.0.0.1", 0))
    sock.settimeout(1)
    port = sock.getsockname()[1]
    ffmpeg = subprocess.Popen(["ffmpeg", "-v", "error", "-i", clip, "-c:v", "copy", "-f", "rtp", f"rtp://127.0.0.1:{port}"],
                              stdout=subprocess.DEVNULL)
    packets = []
    while True:
        try:
            packets.append(sock.recvfrom(65536)[0])
        except socket.timeout:
            if ffmpeg.poll() is not None:
                break  # FFmpeg has finished and nothing came for a second
    sock.close()
    return packets


def two_tables(packet):
    """A frame's first packet with FFmpeg's one 64-byte table (luma and chroma alike) sent as two, as mediamtx (1.21.3)
    needs: given one, it drops every frame ("received wrong fragment"). RFC 2435's Length allows either; the decoded
    frames are the same."""
    offset = int.from_bytes(packet[13:16], "big")
    if offset != 0 or packet[17] < 128 or packet[16] >= 64:
        return packet
    at = 12 + 8
    if struct.unpack(">H", packet[at + 2:at + 4])[0] != 64:
        return packet
    table = packet[at + 4:at + 68]
    return packet[:at + 2] + struct.pack(">H", 128) + table + table + packet[at + 68:]


def publish_jpeg(packets, stop):
    """Publishes the packets to mediamtx in a loop at 25 frames a second: ANNOUNCE, SETUP (RTP over the connection),
    RECORD (RFC 2326, 10.11), then the packets, their sequence numbers and timestamps going on across loops. FFmpeg's
    own RTSP push can't be used: its packets need two_tables first."""
    sock = socket.create_connection(("127.0.0.1", PORT))
    reader = sock.makefile("rb")
    url = f"rtsp://127.0.0.1:{PORT}/live"

    def request(method, target, headers="", body=b""):
        request.cseq += 1
        length = f"Content-Length: {len(body)}\r\n" if body else ""
        sock.sendall(f"{method} {target} RTSP/1.0\r\nCSeq: {request.cseq}\r\n{headers}{length}\r\n".encode() + body)
        status = reader.readline().decode()
        answer = {}
        while (line := reader.readline().decode().strip()):
            name, _, value = line.partition(":")
            answer[name.lower()] = value.strip()
        reader.read(int(answer.get("content-length", 0)))
        if " 200 " not in status:
            raise RuntimeError(f"{method}: {status.strip()}")
        return answer

    request.cseq = 0
    sdp = b"v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=clip\r\nt=0 0\r\nm=video 0 RTP/AVP 26\r\na=control:trackID=0\r\n"
    request("ANNOUNCE", url, "Content-Type: application/sdp\r\n", sdp)
    session = request("SETUP", url + "/trackID=0", "Transport: RTP/AVP/TCP;unicast;interleaved=0-1;mode=record\r\n")["session"]
    request("RECORD", url, f"Session: {session.split(';')[0]}\r\nRange: npt=0.000-\r\n")
    packets = [bytearray(two_tables(p)) for p in packets]
    span = (struct.unpack(">I", packets[-1][4:8])[0] - struct.unpack(">I", packets[0][4:8])[0] + 3600) & 0xFFFFFFFF
    sequence, loop = 0, 0
    try:
        while not stop.is_set():
            for packet in packets:
                packet[2:4] = struct.pack(">H", sequence & 0xFFFF)
                sequence += 1
                timestamp = (struct.unpack(">I", bytes(packet[4:8]))[0] + loop * span) & 0xFFFFFFFF
                out = bytes(packet[:4]) + struct.pack(">I", timestamp) + bytes(packet[8:])
                sock.sendall(b"$\x00" + struct.pack(">H", len(out)) + out)
                if packet[1] & 0x80:
                    time.sleep(0.04)
                if stop.is_set():
                    return
            loop += 1
    except OSError:
        pass
    finally:
        sock.close()


def wait_for_port(port, seconds=20):
    deadline = time.time() + seconds
    while time.time() < deadline:
        try:
            with socket.create_connection(("127.0.0.1", port), timeout=1):
                return
        except OSError:
            time.sleep(0.2)
    sys.exit(f"Nothing is listening on port {port}.")


def make_certificate(folder):
    """A self-signed certificate for localhost, as a camera has (openssl), and its SHA-256 for --certificate-hash."""
    key, cert = os.path.join(folder, "server.key"), os.path.join(folder, "server.crt")
    subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-keyout", key, "-out", cert, "-days", "2",
                    "-subj", "/CN=localhost", "-addext", "subjectAltName=DNS:localhost,IP:127.0.0.1"],
                   check=True, capture_output=True)
    with open(cert) as f:
        return key, cert, hashlib.sha256(ssl.PEM_cert_to_DER_cert(f.read())).hexdigest()


def check_relay(summary, output, lossy):
    """What the relay did against what mcdec printed and what LivePlayer's receiver reports told the server; the
    lines to add to the output, and whether they agree."""
    streams, reports = summary["streams"], summary["reports"]
    dropped = sum(s["dropped"] for s in streams.values())
    repeated = sum(s["repeated"] for s in streams.values())
    held = sum(s["held"] for s in streams.values())
    lost = re.search(r"lost packets (\d+)", output)
    lines = [f"relay: {sum(s['forwarded'] for s in streams.values())} packets forwarded in {len(streams)} stream(s), "
             f"{dropped} dropped, {repeated} sent twice, {held} held back."]
    ok = bool(streams) and lost is not None and int(lost[1]) == dropped
    if lost is not None and int(lost[1]) != dropped:
        lines.append(f"LivePlayer counted {lost[1]} packets lost, the relay dropped {dropped}.")
    if lossy and dropped == 0 or not lossy and (repeated == 0 or held == 0):
        lines.append("The relay impaired nothing: the run checked nothing.")
        ok = False
    for ssrc, counts in streams.items():
        report = reports.get(ssrc)
        # RFC 3550, A.3: packets expected less packets received, a packet sent twice received twice.
        expected = counts["dropped"] - counts["repeated"]
        lines.append(f"stream {ssrc}: receiver reports {report['reports'] if report else 0}, the last one's cumulative lost "
                     f"{report['lost'] if report else 'none'} (dropped less sent twice: {expected}).")
        ok &= report is not None and report["lost"] == expected
    return lines, ok


def run(method, binary, clip, folder, frames, transports="tcp", audio=False, tls=False, relay=None):
    """mediamtx with one authentication method and one transport (tcp or udp), over RTSPS with tls, the clip pushed
    to it, then mcdec --live; its output and status. With relay (rtsp-lossy-relay.py's options), through the relay,
    and its counts checked against mcdec's."""
    config = os.path.join(folder, f"mediamtx-{method}-{transports}{'-tls' if tls else ''}.yml")
    with open(config, "w") as f:
        f.write(CONFIG.format(port=PORT, method=method, user=USER, password=PASSWORD, transports=transports))
        if tls:
            key, cert, digest = make_certificate(folder)
            f.write(TLS.format(tls_port=TLS_PORT, key=key, cert=cert))
    scheme, host, port = ("rtsps", "localhost", TLS_PORT) if tls else ("rtsp", "127.0.0.1", PORT)
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
    lossy_relay = None
    summary = os.path.join(folder, "relay.json")
    lossy = relay is not None and "--loss" in relay
    stop = threading.Event()
    try:
        wait_for_port(port)
        if clip.endswith(".avi"):
            publisher = threading.Thread(target=publish_jpeg, args=(record_rtp(clip), stop), daemon=True)
            publisher.start()
        else:
            push = subprocess.Popen(["ffmpeg", "-v", "error", "-re", "-stream_loop", "-1", "-i", clip, "-c", "copy",
                                     "-f", "rtsp", "-rtsp_transport", transports, f"{scheme}://{host}:{port}/live"])
        if not published.wait(20):
            return "\n".join(["The push never reached mediamtx.", *log[-20:]]), 1
        if relay is not None:
            lossy_relay = subprocess.Popen([sys.executable, RELAY, "--listen", str(RELAY_PORT), "--server", f"127.0.0.1:{port}",
                                            "--summary", summary, *relay], stdout=subprocess.DEVNULL)
            wait_for_port(RELAY_PORT)
            port = RELAY_PORT
        url = f"{scheme}://{USER}:{PASSWORD}@{host}:{port}/live"
        check = subprocess.run(["dotnet", MCDEC, "--live", url, clip, "--frames", str(frames), *(["--audio"] if audio else []),
                                *(["--certificate-hash", digest] if tls else []), *(["--lossy"] if lossy else [])],
                               capture_output=True, text=True)
        output = check.stdout + check.stderr
        status = check.returncode
        if lossy_relay is not None:
            lossy_relay.terminate()
            lossy_relay.wait(10)
            with open(summary) as f:
                lines, agree = check_relay(json.load(f), output, lossy)
            output += "\n".join(lines) + ("" if agree else "\nFAILED: the relay's counts and LivePlayer's differ.")
            status = status or (0 if agree else 1)
        if status != 0:
            output += "\nmediamtx's log:\n" + "\n".join(log[-30:])
        return output.strip(), status
    finally:
        stop.set()
        for process in (lossy_relay, push, server):
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
    parser.add_argument("--only", help="only the runs whose label has this text")
    args = parser.parse_args()
    if not os.path.exists(MCDEC):
        sys.exit(f"Build tools/ManagedCodecs.Cli in Release first ({MCDEC}).")

    binary = mediamtx()
    version = subprocess.run([binary, "--version"], capture_output=True, text=True).stdout.strip()
    failed = 0
    report = [f"mediamtx {version}, FFmpeg pushing a clip in a loop (720p25 libx264, 640x480 MJPEG), {args.frames} frames a run.", ""]
    with tempfile.TemporaryDirectory() as folder:
        h264 = make_clip(folder)
        mjpeg = make_mjpeg_clip(folder)
        sound = make_audio_clip(folder)
        runs = (("H.264, basic", "basic", h264, "tcp", False), ("H.264, digest", "digest", h264, "tcp", False),
                ("JPEG, digest", "digest", mjpeg, "tcp", False), ("H.264, digest, RTP over UDP only", "digest", h264, "udp", False),
                ("H.264 and AAC, digest", "digest", sound, "tcp", True),
                ("H.264 and AAC, digest, RTP over UDP only", "digest", sound, "udp", True),
                ("H.264, digest, RTSPS", "digest", h264, "tcp", False, True),
                ("H.264 over UDP, packets sent twice and reordered", "digest", h264, "udp", False, False, REORDERING),
                ("H.264 over UDP, 2% of the packets lost for 6 s", "digest", h264, "udp", False, False, LOSING),
                ("H.264 and AAC over UDP, 2% of the packets lost for 6 s", "digest", sound, "udp", True, False, LOSING))
        for label, method, clip, transports, audio, *more in runs:
            if args.only and args.only not in label:
                continue
            tls, relay = (*more, *(False, None)[len(more):])

            # A run through the relay goes on for 16 s: 6 s of impairing, a receiver report after it (one every 5 s), and
            # 3 s of clean frames.
            frames = max(args.frames, 400) if relay else args.frames
            output, status = run(method, binary, clip, folder, frames, transports, audio, tls, relay)
            verdict = "passed" if status == 0 else "FAILED"
            print(f"== {label}: {verdict}\n{output}\n", flush=True)
            report += [f"**{label}**: {verdict}", "", "```", output, "```", ""]
            failed += status != 0

    if args.report:
        with open(args.report, "w") as f:
            f.write("\n".join(report))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
