#!/usr/bin/env python3
"""Replaces a track's edit list in an MP4 file whose 'moov' comes after its 'mdat' (FFmpeg's default layout, so no
chunk offset moves), for samples with edit lists FFmpeg's muxer never writes.

Usage: tools/mp4-edit-list.py <file> <track> <duration:media_time>[,...]
  track: 0 for the first 'trak' in 'moov', 1 for the second, ...
  duration in the movie timescale ('mvhd'), media_time in the track's timescale; -1 for an empty edit.
  The track must already have an edit list ('edts' with 'elst'), as FFmpeg writes one for every track.
"""
import struct
import sys


def boxes(data, start, end):
    pos = start
    while pos + 8 <= end:
        size, kind = struct.unpack_from('>I4s', data, pos)
        if size < 8:
            raise SystemExit(f'unsupported box size {size} at {pos}')
        yield pos, size, kind.decode('latin-1')
        pos += size


def find(data, start, end, kind):
    return [(pos, size) for pos, size, k in boxes(data, start, end) if k == kind]


def main():
    path, track, spec = sys.argv[1], int(sys.argv[2]), sys.argv[3]
    edits = [tuple(int(x) for x in item.split(':')) for item in spec.split(',')]
    data = bytearray(open(path, 'rb').read())

    (moov, moov_size), = find(data, 0, len(data), 'moov')
    if any(pos > moov for pos, _ in find(data, 0, len(data), 'mdat')):
        raise SystemExit("'mdat' after 'moov': chunk offsets would move")
    trak, trak_size = find(data, moov + 8, moov + moov_size, 'trak')[track]
    (edts, edts_size), = find(data, trak + 8, trak + trak_size, 'edts')
    (elst, elst_size), = find(data, edts + 8, edts + edts_size, 'elst')

    # Version 0: 32-bit duration and media_time, then media_rate 1.0.
    body = struct.pack('>I4sII', 16 + 12 * len(edits), b'elst', 0, len(edits))
    for duration, media_time in edits:
        body += struct.pack('>IiI', duration, media_time, 0x00010000)
    delta = len(body) - elst_size
    data[elst:elst + elst_size] = body
    for pos in (moov, trak, edts):
        struct.pack_into('>I', data, pos, struct.unpack_from('>I', data, pos)[0] + delta)
    open(path, 'wb').write(data)


main()
