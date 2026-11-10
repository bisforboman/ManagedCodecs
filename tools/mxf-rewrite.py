#!/usr/bin/env python3
"""Rewrites an MXF file into layouts FFmpeg's muxer never writes, for the MXF partition tests: no random index pack at
the end (--strip-rip), or a run-in before the header partition (--run-in N, SMPTE ST 377-1 6.6: under 64 KiB, without
the partition pack label's first 11 bytes). Neither moves a partition relative to the header partition pack, whose key
the offsets in the file count from, so the original file's partition reference holds for the result.

Usage: tools/mxf-rewrite.py <in> <out> [--strip-rip] [--run-in N]
"""
import argparse
import struct

RIP_KEY = bytes.fromhex('060e2b34020501010d0102010111 0100'.replace(' ', ''))
PARTITION_PREFIX = bytes.fromhex('060e2b3402050101 0d0102'.replace(' ', ''))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('input')
    parser.add_argument('output')
    parser.add_argument('--strip-rip', action='store_true')
    parser.add_argument('--run-in', type=int, default=0)
    args = parser.parse_args()

    data = open(args.input, 'rb').read()
    if args.strip_rip:
        length = struct.unpack('>I', data[-4:])[0]
        if data[-length:-length + 16][:7] + data[-length:-length + 16][8:] != RIP_KEY[:7] + RIP_KEY[8:]:
            raise SystemExit(f'{args.input}: no random index pack at the end')
        data = data[:-length]
    if args.run_in:
        if not 0 < args.run_in < 65536:
            raise SystemExit('a run-in is under 64 KiB')
        # Bytes that cannot hold the label's start (0x06 never appears), so a decoder's search finds the header.
        run_in = bytes(7 + i % 249 for i in range(args.run_in))
        assert PARTITION_PREFIX[:1] not in run_in
        data = run_in + data
    open(args.output, 'wb').write(data)


if __name__ == '__main__':
    main()
