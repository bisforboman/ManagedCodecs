#!/usr/bin/env python3
"""Sets the 'pasp' box (pixel aspect ratio: hSpacing, vSpacing) of an MP4 file's H.264 sample entries, for samples whose
container ratio differs from the stream's VUI, which FFmpeg's muxer never writes (it takes 'pasp' from the stream).
The file keeps its length, so no offset moves.

Usage: tools/mp4-pasp.py <file> <hSpacing> <vSpacing>
"""
import struct
import sys

CONTAINERS = {'moov', 'trak', 'mdia', 'minf', 'stbl'}


def patch(data, start, end, h, v):
    count = 0
    pos = start
    while pos + 8 <= end:
        size, kind = struct.unpack_from('>I4s', data, pos)
        kind = kind.decode('latin-1')
        if size < 8:
            raise SystemExit(f'unsupported box size {size} at {pos}')
        if kind in CONTAINERS:
            count += patch(data, pos + 8, pos + size, h, v)
        elif kind == 'stsd':
            count += patch(data, pos + 16, pos + size, h, v)  # version, flags and entry count first
        elif kind in ('avc1', 'avc3'):
            count += patch(data, pos + 8 + 78, pos + size, h, v)  # the VisualSampleEntry's fields, then its boxes
        elif kind == 'pasp':
            struct.pack_into('>II', data, pos + 8, h, v)
            count += 1
        pos += size
    return count


def main():
    path, h, v = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
    data = bytearray(open(path, 'rb').read())
    if patch(data, 0, len(data), h, v) == 0:
        raise SystemExit(f'{path}: no pasp in an H.264 sample entry')
    open(path, 'wb').write(data)


if __name__ == '__main__':
    main()
