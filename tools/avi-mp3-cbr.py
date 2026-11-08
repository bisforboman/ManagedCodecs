#!/usr/bin/env python3
"""Rewrites an FFmpeg AVI's MP3 audio into the layout VirtualDub and older muxers write ("CBR" MP3): dwScale 1, dwRate
the bytes a second, dwSampleSize 1 and nBlockAlign 1, and the frames' bytes in chunks of any length, cut anywhere in a
frame, which FFmpeg's muxer never writes (it writes one frame a chunk, dwScale 1152). Each chunk takes about one video
frame's share of the bytes, give or take a third, from a fixed seed; the video chunks stay as they are, the audio ones
follow each video chunk, and 'idx1' is rebuilt. The reference is ffprobe's packets of the rewritten file: FFmpeg's
MPEG audio parser, not this script, says where the frames are.

Usage: tools/avi-mp3-cbr.py <in.avi> <out.avi>
"""
import random
import struct
import sys


def chunks(data, start, end):
    """Yields (id, payload start, size, list type or None) of the chunks from start to end."""
    pos = start
    while pos + 8 <= end:
        cid, size = data[pos:pos + 4], struct.unpack_from('<I', data, pos + 4)[0]
        yield pos, cid, size, data[pos + 8:pos + 12] if cid in (b'RIFF', b'LIST') else None
        pos += 8 + size + (size & 1)


def chunk(cid, payload):
    return cid + struct.pack('<I', len(payload)) + payload + (b'\0' if len(payload) & 1 else b'')


def main():
    src, dst = sys.argv[1], sys.argv[2]
    data = bytearray(open(src, 'rb').read())
    hdrl = movi = None
    for pos, cid, size, kind in chunks(data, 12, len(data)):
        if kind == b'hdrl':
            hdrl = (pos, size)
        elif kind == b'movi':
            movi = (pos, size)
    if hdrl is None or movi is None:
        raise SystemExit(f'{src}: no hdrl or movi list')

    # The stream lists, in order: the audio one's strh and strf are rewritten in place (their sizes stay).
    audio, video, fps, stream = None, None, None, 0
    for pos, cid, size, kind in chunks(data, hdrl[0] + 12, hdrl[0] + 8 + hdrl[1]):
        if kind != b'strl':
            continue
        strh = strf = None
        for p, c, s, _ in chunks(data, pos + 12, pos + 8 + size):
            if c == b'strh':
                strh = p + 8
            elif c == b'strf':
                strf = p + 8
        if data[strh:strh + 4] == b'vids' and video is None:
            video = stream
            scale, rate = struct.unpack_from('<II', data, strh + 20)
            fps = rate / scale
        elif data[strh:strh + 4] == b'auds' and audio is None:
            if struct.unpack_from('<H', data, strf)[0] != 0x55:
                raise SystemExit(f'{src}: the first audio stream is not MP3')
            audio, audio_strh, audio_strf = stream, strh, strf
        stream += 1
    if audio is None or video is None:
        raise SystemExit(f'{src}: no MP3 audio stream beside a video one')

    audio_id = b'%02dwb' % audio
    video_prefix = b'%02d' % video
    mp3 = bytearray()
    video_chunks = []
    for pos, cid, size, kind in chunks(data, movi[0] + 12, movi[0] + 8 + movi[1]):
        if cid == audio_id:
            mp3 += data[pos + 8:pos + 8 + size]
        elif cid[:2] == video_prefix:
            video_chunks.append((cid, bytes(data[pos + 8:pos + 8 + size])))
        elif kind is not None:
            raise SystemExit(f"{src}: a LIST in 'movi' (rec lists aren't rewritten)")

    bytes_per_second = struct.unpack_from('<I', data, audio_strf + 8)[0]
    struct.pack_into('<II', data, audio_strh + 20, 1, bytes_per_second)  # dwScale, dwRate
    struct.pack_into('<I', data, audio_strh + 32, len(mp3))  # dwLength, in bytes
    struct.pack_into('<I', data, audio_strh + 44, 1)  # dwSampleSize
    struct.pack_into('<H', data, audio_strf + 12, 1)  # nBlockAlign

    # Audio after each video chunk, its share of the bytes give or take a third, the rest after the last.
    # The original idx1's keyframe flags of the video chunks, in order (true without one).
    keyframes = []
    for pos, cid, size, _ in chunks(data, movi[0] + 8 + movi[1] + (movi[1] & 1), len(data)):
        if cid == b'idx1':
            for k in range(size // 16):
                c, f, _, _ = struct.unpack_from('<4sIII', data, pos + 8 + 16 * k)
                if c[:2] == video_prefix:
                    keyframes.append(bool(f & 0x10))

    rng = random.Random(415)
    share = bytes_per_second / fps
    body, index, taken = bytearray(b'movi'), bytearray(), 0
    for i, (cid, payload) in enumerate(video_chunks):
        parts = [(cid, payload)]
        want = len(mp3) - taken if i == len(video_chunks) - 1 else int(share * rng.uniform(2 / 3, 4 / 3))
        if want > 0 and taken < len(mp3):
            parts.append((audio_id, bytes(mp3[taken:taken + want])))
            taken += len(parts[-1][1])
        for c, p in parts:
            # idx1's offsets count from the 'movi' FourCC; audio chunks are keyframes (AVIIF_KEYFRAME), video ones as
            # the original index marked them.
            flags = 0x10 if c == audio_id or (keyframes[i] if i < len(keyframes) else True) else 0
            index += struct.pack('<4sIII', c, flags, len(body), len(p))
            body += chunk(c, p)

    head = data[:movi[0]]
    tail = bytearray()
    for pos, cid, size, kind in chunks(data, movi[0] + 8 + movi[1] + (movi[1] & 1), len(data)):
        if cid != b'idx1':
            tail += data[pos:pos + 8 + size + (size & 1)]
    out = head + chunk(b'LIST', bytes(body)) + chunk(b'idx1', bytes(index)) + tail
    struct.pack_into('<I', out, 4, len(out) - 8)
    open(dst, 'wb').write(out)



if __name__ == '__main__':
    main()
