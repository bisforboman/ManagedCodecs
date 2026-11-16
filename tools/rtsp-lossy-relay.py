#!/usr/bin/env python3
"""A network that loses, repeats and reorders packets, between an RTSP server and LivePlayer (issue #527;
docs/decisions/2026-11-14-live-rtsp-udp.md: the loopback interface does none of that). It relays the RTSP connection as
it is, except that each SETUP for RTP over UDP is rewritten to pass through the relay's own ports, and of the RTP
packets the server then sends it drops some, sends some twice and holds some back for a few milliseconds, by a seeded
random choice. RTCP passes untouched both ways, and the receiver reports the client sends are read.

    tools/rtsp-lossy-relay.py --listen 8555 --server 127.0.0.1:8554 --loss 0.02 --summary relay.json

The first --skip packets of each stream pass untouched (a packet lost before the client's first isn't missed by it),
then packets are impaired for --seconds, then the rest pass again, so that a client still playing afterwards has seen every loss.
On SIGTERM or SIGINT it writes the summary: for each stream (by SSRC) the packets forwarded, dropped, repeated and
held back, and the last receiver report the client sent about it (RFC 3550, 6.4.1: the cumulative number of packets
lost, which counts a repeated packet as one received, so it is dropped minus repeated once the impairing is over).
tools/rtsp-nightly.py runs it between mediamtx and `mcdec --live`.
"""

import argparse
import json
import random
import re
import signal
import socket
import struct
import sys
import threading
import time

LOCK = threading.Lock()
STREAMS = {}  # SSRC -> counts
REPORTS = {}  # SSRC reported on -> the client's last report block


def pair(host):
    """Two UDP sockets on an even port and the next (RFC 3550, 11)."""
    for _ in range(100):
        rtp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        rtp.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 8 * 1024 * 1024)
        rtp.bind((host, 0))
        port = rtp.getsockname()[1]
        if port % 2 == 0:
            rtcp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            try:
                rtcp.bind((host, port + 1))
                return rtp, rtcp
            except OSError:
                rtcp.close()
        rtp.close()
    raise OSError("no even UDP port with the next one free")


class Track:
    """One SETUP's ports: the server sends to `inner`, the client to `outer`."""

    def __init__(self, args, client_host, client_ports, server_host, number):
        self.args = args
        self.random = random.Random(args.seed + number)
        self.client = [(client_host, port) for port in client_ports]
        self.server_host = server_host
        self.server = None
        self.inner = pair("127.0.0.1")
        self.outer = pair("127.0.0.1")
        self.count = 0
        self.until = 0

    def start(self, server_ports):
        self.server = [(self.server_host, port) for port in server_ports]
        for index in (0, 1):
            threading.Thread(target=self.from_server, args=(index,), daemon=True).start()
            threading.Thread(target=self.from_client, args=(index,), daemon=True).start()

    def from_client(self, index):
        while True:
            try:
                data, _ = self.outer[index].recvfrom(65536)
            except OSError:
                return
            if index == 1:
                read_reports(data)
            self.inner[index].sendto(data, self.server[index])

    def from_server(self, index):
        while True:
            try:
                data, _ = self.inner[index].recvfrom(65536)
            except OSError:
                return
            if index == 1 or len(data) < 12:
                self.outer[index].sendto(data, self.client[index])
                continue
            self.rtp(data)

    def rtp(self, data):
        args = self.args
        ssrc = struct.unpack(">I", data[8:12])[0]
        with LOCK:
            counts = STREAMS.setdefault(ssrc, dict(forwarded=0, dropped=0, repeated=0, held=0))
            self.count += 1
            if self.count == args.skip + 1:
                self.until = time.monotonic() + args.seconds
            impaired = self.count > args.skip and time.monotonic() < self.until
            choice = self.random.random() if impaired else 1.0
            if choice < args.loss:
                counts["dropped"] += 1
                return
            counts["forwarded"] += 1
            repeat = args.loss <= choice < args.loss + args.repeat
            hold = args.loss + args.repeat <= choice < args.loss + args.repeat + args.reorder
            counts["repeated"] += repeat
            counts["held"] += hold
        send = lambda: self.outer[0].sendto(data, self.client[0])
        if hold:
            threading.Timer(args.hold_ms / 1000, send).start()
            return
        send()
        if repeat:
            send()

    def close(self):
        for sock in (*self.inner, *self.outer):
            sock.close()


def read_reports(data):
    """The report blocks of the receiver reports in a compound RTCP packet (RFC 3550, 6.4.2)."""
    at = 0
    while at + 8 <= len(data):
        count, kind, words = data[at] & 31, data[at + 1], struct.unpack(">H", data[at + 2:at + 4])[0]
        if kind == 201:
            for block in range(count):
                start = at + 8 + 24 * block
                if start + 24 > len(data):
                    break
                ssrc, lost, highest, jitter = struct.unpack(">IIII", data[start:start + 16])
                cumulative = lost & 0xFFFFFF
                with LOCK:
                    REPORTS[ssrc] = dict(fraction=lost >> 24, lost=cumulative - (1 << 24 if cumulative & 0x800000 else 0),
                                         highest=highest, jitter=jitter, reports=REPORTS.get(ssrc, {}).get("reports", 0) + 1)
        at += 4 * (words + 1)


def read_message(reader):
    """An RTSP request or answer: its head's lines and its body. None at the end of the connection."""
    lines = []
    while True:
        line = reader.readline()
        if not line:
            return None
        if line in (b"\r\n", b"\n"):
            if lines:
                break
            continue
        lines.append(line.decode("latin-1").rstrip("\r\n"))
    length = next((int(l.split(":", 1)[1]) for l in lines if l.lower().startswith("content-length:")), 0)
    return lines, reader.read(length) if length else b""


def write_message(sock, lines, body):
    sock.sendall(("\r\n".join(lines) + "\r\n\r\n").encode("latin-1") + body)


def header(lines, name):
    return next((l.split(":", 1)[1].strip() for l in lines if l.lower().startswith(name.lower() + ":")), None)


def serve(client, args):
    host, port = args.server.rsplit(":", 1)
    server = socket.create_connection((host, int(port)))
    client_host = client.getpeername()[0]
    pending = {}  # CSeq -> the Track its SETUP asked for
    tracks = []

    def requests():
        reader = client.makefile("rb")
        try:
            while (message := read_message(reader)) is not None:
                lines, body = message
                transport = header(lines, "Transport")
                ports = re.search(r"client_port=(\d+)-(\d+)", transport or "")
                if lines[0].startswith("SETUP") and ports:
                    track = Track(args, client_host, (int(ports[1]), int(ports[2])), host, len(tracks))
                    tracks.append(track)
                    pending[header(lines, "CSeq")] = track
                    inner = track.inner[0].getsockname()[1]
                    lines = [l.replace(ports[0], f"client_port={inner}-{inner + 1}") if l.lower().startswith("transport:") else l
                             for l in lines]
                write_message(server, lines, body)
        except OSError:
            pass
        finally:
            server.close()

    def answers():
        reader = server.makefile("rb")
        try:
            while (message := read_message(reader)) is not None:
                lines, body = message
                track = pending.pop(header(lines, "CSeq"), None)
                ports = re.search(r"server_port=(\d+)-(\d+)", header(lines, "Transport") or "")
                if track is not None and ports:
                    track.start((int(ports[1]), int(ports[2])))
                    outer = track.outer[0].getsockname()[1]
                    inner = track.inner[0].getsockname()[1]
                    lines = [l.replace(ports[0], f"server_port={outer}-{outer + 1}")
                              .replace(f"client_port={inner}-{inner + 1}", f"client_port={track.client[0][1]}-{track.client[1][1]}")
                             if l.lower().startswith("transport:") else l for l in lines]
                write_message(client, lines, body)
        except OSError:
            pass
        finally:
            client.close()
            for track in tracks:
                track.close()

    threading.Thread(target=requests, daemon=True).start()
    threading.Thread(target=answers, daemon=True).start()


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--listen", type=int, required=True, help="the port clients connect to")
    parser.add_argument("--server", required=True, help="the RTSP server, host:port")
    parser.add_argument("--loss", type=float, default=0, help="the share of RTP packets dropped")
    parser.add_argument("--repeat", type=float, default=0, help="the share sent twice")
    parser.add_argument("--reorder", type=float, default=0, help="the share held back, so that later packets pass them")
    parser.add_argument("--hold-ms", type=float, default=10, help="how long a held packet waits (default 10 ms)")
    parser.add_argument("--skip", type=int, default=50, help="packets of each stream passed before any is impaired")
    parser.add_argument("--seconds", type=float, default=1e9, help="how long each stream is impaired after those")
    parser.add_argument("--seed", type=int, default=527)
    parser.add_argument("--summary", help="where to write the counts as JSON at the end")
    args = parser.parse_args()

    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    listener.bind(("127.0.0.1", args.listen))
    listener.listen()

    def finish(*_):
        with LOCK:
            summary = dict(streams={f"{ssrc:08X}": counts for ssrc, counts in STREAMS.items()},
                           reports={f"{ssrc:08X}": report for ssrc, report in REPORTS.items()})
        text = json.dumps(summary, indent=2)
        if args.summary:
            with open(args.summary, "w") as f:
                f.write(text)
        else:
            print(text)
        sys.exit(0)

    signal.signal(signal.SIGTERM, finish)
    signal.signal(signal.SIGINT, finish)
    print(f"rtsp://127.0.0.1:{args.listen} to {args.server}", flush=True)
    while True:
        client, _ = listener.accept()
        serve(client, args)


if __name__ == "__main__":
    main()
