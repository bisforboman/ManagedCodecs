#!/usr/bin/env python3
"""Rewrites an MXF file into layouts FFmpeg's muxer never writes, for the MXF partition tests: no random index pack at
the end (--strip-rip), or a run-in before the header partition (--run-in N, SMPTE ST 377-1 6.6: under 64 KiB, without
the partition pack label's first 11 bytes). Neither moves a partition relative to the header partition pack, whose key
the offsets in the file count from, so the original file's partition reference holds for the result.

--cut-in essence|key|index cuts the file short instead, as a recording that broke off or is still being written: inside
the first essence element past the file's middle (halfway into its value), eight bytes into the key of the first item
past the middle, or halfway into the first index table segment past the middle. Found by walking the file's KLV items,
so the cut lands in the same kind of item whatever bytes the encoder wrote.

Usage: tools/mxf-rewrite.py <in> <out> [--strip-rip] [--run-in N] [--cut-in essence|key|index]
"""
import argparse
import struct

RIP_KEY = bytes.fromhex('060e2b34020501010d0102010111 0100'.replace(' ', ''))
PARTITION_PREFIX = bytes.fromhex('060e2b3402050101 0d0102'.replace(' ', ''))
ESSENCE_PREFIX = bytes.fromhex('060e2b34010201010d010301')


def items(data):
    """(start, value start, value length, key) of each KLV item from the header partition pack (offset 0) on."""
    i = 0
    while i + 17 <= len(data):
        key = data[i:i + 16]
        n = data[i + 16]
        if n < 0x80:
            length, value = n, i + 17
        else:
            length, value = int.from_bytes(data[i + 17:i + 17 + (n & 0x7f)], 'big'), i + 17 + (n & 0x7f)
        yield i, value, length, key
        i = value + length


def cut(data, kind):
    for start, value, length, key in items(data):
        if start < len(data) // 2:
            continue
        if kind == 'key':
            return start + 8
        if kind == 'essence' and key[:7] + key[8:12] == ESSENCE_PREFIX[:7] + ESSENCE_PREFIX[8:12]:
            return value + length // 2
        if kind == 'index' and key[4] == 0x02 and key[8:14] == bytes.fromhex('0d0102010110'):
            return value + length // 2
    raise SystemExit(f'no {kind} item past the middle')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('input')
    parser.add_argument('output')
    parser.add_argument('--strip-rip', action='store_true')
    parser.add_argument('--run-in', type=int, default=0)
    parser.add_argument('--cut-in', choices=['essence', 'key', 'index'])
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
    if args.cut_in:
        data = data[:cut(data, args.cut_in)]
    open(args.output, 'wb').write(data)


if __name__ == '__main__':
    main()
