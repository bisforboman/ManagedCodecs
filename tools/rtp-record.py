#!/usr/bin/env python3
"""Records the RTP packets sent to a UDP port on the loopback interface into a file, for the RTP depacketizer's tests
(docs/decisions/2026-11-10-live-rtsp-h264.md). Each packet is written as a 16-bit big-endian length, then the packet.

    tools/rtp-record.py <port> <out.rtp> &
    ffmpeg ... -f rtp rtp://127.0.0.1:<port>
    wait

Stops once no packet has come for a second (after the first). Fails if a sequence number is missing or out of order,
so a recording that lost a packet on the way (a full socket buffer) is never mistaken for FFmpeg's output.
"""
import socket
import struct
import sys


def main():
    if len(sys.argv) != 3:
        sys.exit("usage: rtp-record.py <port> <out.rtp>")
    port, out = int(sys.argv[1]), sys.argv[2]
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 16 * 1024 * 1024)
    sock.bind(("127.0.0.1", port))
    sock.settimeout(30)  # for the first packet: FFmpeg starting up
    packets = []
    while True:
        try:
            data, _ = sock.recvfrom(65536)
        except socket.timeout:
            break
        packets.append(data)
        sock.settimeout(1)
    if not packets:
        sys.exit(f"rtp-record.py: no packet on port {port}")
    for previous, packet in zip(packets, packets[1:]):
        expected = (struct.unpack(">H", previous[2:4])[0] + 1) & 0xFFFF
        if struct.unpack(">H", packet[2:4])[0] != expected:
            sys.exit(f"rtp-record.py: a packet is missing or out of order after sequence number {expected - 1}")
    with open(out, "wb") as f:
        for packet in packets:
            f.write(struct.pack(">H", len(packet)))
            f.write(packet)
    print(f"rtp-record.py: {len(packets)} packets to {out}")


if __name__ == "__main__":
    main()
