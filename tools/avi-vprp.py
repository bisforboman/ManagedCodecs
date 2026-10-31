#!/usr/bin/env python3
"""Sets the picture aspect ratio (dwFrameAspectRatio) of an AVI file's 'vprp' chunk, for samples whose container ratio
differs from the JPEG images' JFIF density, which FFmpeg's muxer never writes (it takes both from one source). The
file keeps its length, so no offset moves.

Usage: tools/avi-vprp.py <file> <x> <y>    (the shown picture's width:height, e.g. 16 9)
"""
import struct
import sys


def main():
    path, x, y = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
    data = bytearray(open(path, 'rb').read())
    pos = data.find(b'vprp')
    if pos < 0:
        raise SystemExit(f'{path}: no vprp chunk')
    # The chunk's payload follows its 8-byte header; dwFrameAspectRatio is at 20, Y in its low word, X in its high.
    struct.pack_into('<HH', data, pos + 8 + 20, y, x)
    open(path, 'wb').write(data)


if __name__ == '__main__':
    main()
