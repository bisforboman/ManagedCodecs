#!/usr/bin/env python3
"""Sets the picture size in an MP4 file's H.264 sample entries ('avc1' or 'avc3' in 'stsd'), for samples whose
container size differs from the stream's, which FFmpeg's muxer never writes (FFmpeg's demuxer takes the size from
there, not from 'tkhd'). The file keeps its length, so no offset moves.

Usage: tools/mp4-video-size.py <file> <width> <height>
"""
import struct
import sys

CONTAINERS = {'moov', 'trak', 'mdia', 'minf', 'stbl'}


def patch(data, start, end, width, height):
    count = 0
    pos = start
    while pos + 8 <= end:
        size, kind = struct.unpack_from('>I4s', data, pos)
        kind = kind.decode('latin-1')
        if size < 8:
            raise SystemExit(f'unsupported box size {size} at {pos}')
        if kind in CONTAINERS:
            count += patch(data, pos + 8, pos + size, width, height)
        elif kind == 'stsd':
            count += patch(data, pos + 16, pos + size, width, height)  # version, flags and entry count first
        elif kind in ('avc1', 'avc3'):
            # 6 reserved bytes, data_reference_index, 16 bytes of pre_defined and reserved, then width and height.
            struct.pack_into('>HH', data, pos + 8 + 24, width, height)
            count += 1
        pos += size
    return count


def main():
    path, width, height = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
    data = bytearray(open(path, 'rb').read())
    if patch(data, 0, len(data), width, height) == 0:
        raise SystemExit(f'{path}: no H.264 sample entry')
    open(path, 'wb').write(data)


if __name__ == '__main__':
    main()
