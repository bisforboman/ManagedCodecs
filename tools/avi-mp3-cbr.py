#!/usr/bin/env python3
"""Rewrites an FFmpeg AVI's MP3 audio into the "CBR" layout VirtualDub and older muxers write, which FFmpeg's muxer
never does: the audio stream's 'strh' takes dwScale 1, dwRate the bytes a second and dwSampleSize 1, its 'strf'
nBlockAlign 1, and its chunks are cut at arbitrary bytes (sizes cycling through a list, so frames straddle chunks), in
the places the original chunks were. The 'idx1' index goes (its offsets would no longer hold): FFmpeg then reads the
'movi' list in order. The MP3 must be CBR (libmp3lame with -b:a), as the layout's byte rate says.

Usage: tools/avi-mp3-cbr.py <in.avi> <out.avi>
"""
import struct
import sys

SIZES = [417, 1000, 61, 731, 2048, 5]


def chunks(data, start, end):
    """(id, payload start, size) of each chunk in [start, end), not descending into lists."""
    pos = start
    while pos + 8 <= end:
        cid = bytes(data[pos:pos + 4])
        size = struct.unpack_from('<I', data, pos + 4)[0]
        yield cid, pos + 8, size
        pos += 8 + size + (size & 1)


def main():
    source, target = sys.argv[1], sys.argv[2]
    data = bytearray(open(source, 'rb').read())
    assert data[:4] == b'RIFF' and data[8:12] == b'AVI '

    # The audio stream: its index, 'strh' and 'strf' offsets.
    hdrl = data.find(b'hdrl') - 8
    index = 0
    audio = None
    for cid, at, size in chunks(data, hdrl + 12, hdrl + 8 + struct.unpack_from('<I', data, hdrl + 4)[0]):
        if cid == b'LIST' and data[at:at + 4] == b'strl':
            parts = {c: (a, s) for c, a, s in chunks(data, at + 4, at + size)}
            if data[parts[b'strh'][0]:parts[b'strh'][0] + 4] == b'auds':
                audio = (index, parts[b'strh'][0], parts[b'strf'][0])
            index += 1
    if audio is None:
        sys.exit(f'{source}: no audio stream')
    stream, strh, strf = audio
    tag = struct.unpack_from('<H', data, strf)[0]
    if tag != 0x55:
        sys.exit(f'{source}: the audio is not MP3 (format tag {tag:#06x})')

    # The audio bytes in order, and the movi list's chunks.
    movi = data.find(b'movi') - 8
    movi_end = movi + 8 + struct.unpack_from('<I', data, movi + 4)[0]
    wb = b'%02dwb' % stream
    items = []
    stream_bytes = bytearray()
    for cid, at, size in chunks(data, movi + 12, movi_end):
        if cid == wb:
            stream_bytes += data[at:at + size]
            items.append(None)
        else:
            items.append(bytes(data[at - 8:at + size + (size & 1)]))

    # The audio again, cut into chunks of the cycling sizes at the original chunks' places, the rest at the end.
    body = bytearray()
    offset = 0
    k = 0
    for item in items:
        if item is not None:
            body += item
            continue
        if offset >= len(stream_bytes):
            continue
        piece = stream_bytes[offset:offset + SIZES[k % len(SIZES)]]
        k += 1
        offset += len(piece)
        body += wb + struct.pack('<I', len(piece)) + piece + (b'\0' if len(piece) & 1 else b'')
    while offset < len(stream_bytes):
        piece = stream_bytes[offset:offset + SIZES[k % len(SIZES)]]
        k += 1
        offset += len(piece)
        body += wb + struct.pack('<I', len(piece)) + piece + (b'\0' if len(piece) & 1 else b'')

    # The byte rate from the stream header's average (WAVEFORMATEX nAvgBytesPerSec), as the layout's muxers write it.
    byte_rate = struct.unpack_from('<I', data, strf + 8)[0]
    struct.pack_into('<II', data, strh + 20, 1, byte_rate)  # dwScale, dwRate
    struct.pack_into('<I', data, strh + 32, len(stream_bytes))  # dwLength in bytes
    struct.pack_into('<I', data, strh + 44, 1)  # dwSampleSize
    struct.pack_into('<H', data, strf + 12, 1)  # nBlockAlign

    # Header, the new movi list, nothing after it (the idx1 index), and the flags without AVIF_HASINDEX.
    avih = data.find(b'avih') + 8
    flags = struct.unpack_from('<I', data, avih + 12)[0]
    struct.pack_into('<I', data, avih + 12, flags & ~0x10 & ~0x20)
    out = data[:movi] + b'LIST' + struct.pack('<I', 4 + len(body)) + b'movi' + body
    struct.pack_into('<I', out, 4, len(out) - 8)
    open(target, 'wb').write(out)
    print(f'{target}: {len(stream_bytes)} audio bytes in {k} chunks')


if __name__ == '__main__':
    main()
