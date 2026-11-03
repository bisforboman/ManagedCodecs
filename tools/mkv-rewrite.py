#!/usr/bin/env python3
"""Rewrites a Matroska file FFmpeg wrote into the forms its muxer never writes, for MatroskaDemuxer's tests.

    tools/mkv-rewrite.py in.mkv out.mkv [--lace xiph|ebml|fixed] [--lace-frames N] [--block-groups] [--unknown-size]
    tools/mkv-rewrite.py --dump in.mkv

--lace: puts each run of up to N (default 4) consecutive blocks of the audio track in one cluster into one laced
SimpleBlock (Matroska's Block Lacing: Xiph, EBML or fixed-size; fixed only for runs of one size), and gives the track
a DefaultDuration (the first packet's, from the packet timestamps) where it has none: a lace's later frames take their
timestamps from it.
--block-groups: writes the video track's blocks as BlockGroups (a Block, BlockDuration from the track's
DefaultDuration, and a ReferenceBlock for each block that is not a keyframe) instead of SimpleBlocks.
--unknown-size: writes the Segment and every Cluster with the unknown size (all ones), as a live stream does.

A rewritten Tracks or Cluster loses its CRC-32 element, which would no longer hold. The SeekHead and the Cues are left out (their positions would no longer hold), so the result has no Cues either.
--dump prints the element tree (IDs, sizes, the values of small numbers and the blocks' headers).
"""

import argparse
import struct
import sys

SEGMENT, SEEKHEAD, INFO, TRACKS, CLUSTER, CUES, VOID = 0x18538067, 0x114D9B74, 0x1549A966, 0x1654AE6B, 0x1F43B675, 0x1C53BB6B, 0xEC
TRACKENTRY, TRACKNUMBER, TRACKTYPE, DEFAULTDURATION = 0xAE, 0xD7, 0x83, 0x23E383
TIMESTAMP, SIMPLEBLOCK, BLOCKGROUP, BLOCK, BLOCKDURATION, REFERENCEBLOCK = 0xE7, 0xA3, 0xA0, 0xA1, 0x9B, 0xFB
TIMESTAMPSCALE, CRC32 = 0x2AD7B1, 0xBF
MASTERS = {SEGMENT, SEEKHEAD, INFO, TRACKS, TRACKENTRY, CLUSTER, CUES, BLOCKGROUP, 0xE0, 0xE1, 0x4DBB, 0xBB, 0xB7, 0x55B0,
           0x1254C367, 0x7373, 0x67C8, 0x63C0, 0x6D80, 0x6240, 0x5034}
UNKNOWN = -1


def read_vint(data, pos, keep_marker):
    first = data[pos]
    length = 1
    while length <= 8 and not first & (0x80 >> (length - 1)):
        length += 1
    if length > 8:
        raise ValueError(f"bad EBML number at {pos}")
    value = first if keep_marker else first & (0xFF >> length)
    for b in data[pos + 1:pos + length]:
        value = value << 8 | b
    if not keep_marker and value == (1 << (7 * length)) - 1:
        value = UNKNOWN
    return value, pos + length


def elements(data, start, end):
    """(id, payload start, payload end, element start) of each element in [start, end); unknown sizes run to end."""
    pos = start
    while pos < end:
        eid, p = read_vint(data, pos, True)
        size, p = read_vint(data, p, False)
        stop = end if size == UNKNOWN else p + size
        if size == UNKNOWN and eid == CLUSTER:
            # An unknown-size cluster ends at the next top-level element (none of a cluster's children is one).
            q = p
            while q < end:
                cid, r = read_vint(data, q, True)
                if cid in (CLUSTER, CUES, SEEKHEAD, INFO, TRACKS, 0x1254C367, 0x1043A770, 0x1941A469):
                    break
                csize, r = read_vint(data, r, False)
                q = r + csize
            stop = q
        yield eid, p, stop, pos
        pos = stop


def uint(data, start, end):
    return int.from_bytes(data[start:end], "big")


def vint(value, length=None):
    if length is None:
        length = 1
        while value >= (1 << (7 * length)) - 1:
            length += 1
    return ((1 << (7 * length)) | value).to_bytes(length, "big")


def element(eid, payload, unknown=False):
    head = eid.to_bytes((eid.bit_length() + 7) // 8, "big")
    return head + (b"\x01\xff\xff\xff\xff\xff\xff\xff" if unknown else vint(len(payload))) + payload


def uint_element(eid, value):
    return element(eid, value.to_bytes(max(1, (value.bit_length() + 7) // 8), "big"))


def sint_element(eid, value):
    length = 1
    while not -(1 << (8 * length - 1)) <= value < (1 << (8 * length - 1)):
        length += 1
    return element(eid, value.to_bytes(length, "big", signed=True))


def block_header(data, start):
    track, p = read_vint(data, start, False)
    timestamp = struct.unpack(">h", data[p:p + 2])[0]
    return track, timestamp, data[p + 2], p + 3


def dump(data, start, end, depth):
    for eid, p, stop, _ in elements(data, start, end):
        line = f"{'  ' * depth}{eid:X} [{p}..{stop})"
        if eid in (SIMPLEBLOCK, BLOCK):
            track, timestamp, flags, _ = block_header(data, p)
            line += f" track {track} time {timestamp} flags {flags:02X} size {stop - p}"
        elif eid not in MASTERS and stop - p <= 8:
            line += f" = {uint(data, p, stop)}"
        elif eid not in MASTERS and stop - p <= 64:
            line += f" = {bytes(data[p:stop])!r}"
        print(line)
        if eid in MASTERS and eid != CLUSTER or eid == CLUSTER and depth < 2:
            dump(data, p, stop, depth + 1)


def laced(track, timestamp, flags, frames, kind):
    """A SimpleBlock's payload holding frames as one lace of kind (Matroska's Block Lacing)."""
    sizes = [len(f) for f in frames]
    if kind == "xiph":
        head = bytes([len(frames) - 1])
        for size in sizes[:-1]:
            head += b"\xff" * (size // 255) + bytes([size % 255])
        bits = 0x02
    elif kind == "fixed":
        head = bytes([len(frames) - 1])
        bits = 0x04
    else:
        head = bytes([len(frames) - 1]) + vint(sizes[0])
        for previous, size in zip(sizes, sizes[1:-1]):
            difference = size - previous
            # A signed difference: the number minus half its range (2^(7n-1) - 1), in the shortest n that holds it.
            n = 1
            while not -((1 << (7 * n - 1)) - 1) <= difference <= (1 << (7 * n - 1)) - 1:
                n += 1
            head += vint(difference + (1 << (7 * n - 1)) - 1, n)
        bits = 0x06
    return vint(track) + struct.pack(">h", timestamp) + bytes([flags & ~0x06 | bits]) + head + b"".join(frames)


def rewrite(data, args):
    (_, seg_start, seg_end, seg_element), = [e for e in elements(data, 0, len(data)) if e[0] == SEGMENT]
    children = list(elements(data, seg_start, seg_end))
    scale = 1_000_000
    tracks = {}
    for eid, p, stop, _ in children:
        if eid == INFO:
            for cid, q, end, _ in elements(data, p, stop):
                if cid == TIMESTAMPSCALE:
                    scale = uint(data, q, end)
        if eid == TRACKS:
            for cid, q, end, _ in elements(data, p, stop):
                if cid != TRACKENTRY:
                    continue
                fields = {fid: (r, e) for fid, r, e, _ in elements(data, q, end)}
                number = uint(data, *fields[TRACKNUMBER])
                default = uint(data, *fields[DEFAULTDURATION]) if DEFAULTDURATION in fields else None
                tracks[number] = (uint(data, *fields[TRACKTYPE]), default, (q, end))
    video = next((n for n, t in tracks.items() if t[0] == 1), None)
    audio = next((n for n, t in tracks.items() if t[0] == 2), None)

    # The audio track's DefaultDuration where it has none: its first two blocks' timestamps apart, in nanoseconds.
    added_default = None
    if args.lace and audio is not None and tracks[audio][1] is None:
        times = []
        for eid, p, stop, _ in children:
            if eid != CLUSTER or len(times) >= 2:
                continue
            base = next(uint(data, q, e) for c, q, e, _ in elements(data, p, stop) if c == TIMESTAMP)
            for cid, q, e, _ in elements(data, p, stop):
                if cid == SIMPLEBLOCK and block_header(data, q)[0] == audio:
                    times.append(base + block_header(data, q)[1])
        added_default = (times[1] - times[0]) * scale

    out = b""
    for eid, p, stop, _ in children:
        if eid in (SEEKHEAD, CUES):
            continue
        if eid == TRACKS:
            entries = b""
            for cid, q, e, _ in elements(data, p, stop):
                if cid == CRC32:
                    continue
                payload = bytes(data[q:e])
                if added_default is not None and uint(data, *next((r, s) for f, r, s, _ in elements(data, q, e) if f == TRACKNUMBER)) == audio:
                    payload += uint_element(DEFAULTDURATION, added_default)
                entries += element(cid, payload)
            out += element(TRACKS, entries)
        elif eid == CLUSTER:
            out += rewrite_cluster(data, p, stop, args, video, audio, tracks, scale)
        else:
            out += element(eid, bytes(data[p:stop]))
    # The EBML header as it is.
    return bytes(data[:seg_element]) + element(SEGMENT, out, unknown=args.unknown_size)


def rewrite_cluster(data, p, stop, args, video, audio, tracks, scale):
    body = b""
    pending = []  # (timestamp, flags, frame) of the audio blocks waiting to be laced

    def flush():
        nonlocal body, pending
        while pending:
            run = [pending[0]]
            for item in pending[1:args.lace_frames]:
                if args.lace == "fixed" and len(item[2]) != len(run[0][2]):
                    break
                run.append(item)
            pending = pending[len(run):]
            if len(run) == 1:
                body += element(SIMPLEBLOCK, vint(audio) + struct.pack(">h", run[0][0]) + bytes([run[0][1]]) + run[0][2])
            else:
                body += element(SIMPLEBLOCK, laced(audio, run[0][0], run[0][1], [r[2] for r in run], args.lace))

    previous_video = None
    for cid, q, e, _ in elements(data, p, stop):
        if cid == SIMPLEBLOCK:
            track, timestamp, flags, payload_start = block_header(data, q)
            if flags & 0x06:
                raise SystemExit("the input already laces")
            frame = bytes(data[payload_start:e])
            if args.lace and track == audio:
                pending.append((timestamp, flags, frame))
                continue
            flush()
            if args.block_groups and track == video:
                group = element(BLOCK, vint(track) + struct.pack(">h", timestamp) + bytes([flags & 0x0F]) + frame)
                if tracks[video][1]:
                    group += uint_element(BLOCKDURATION, tracks[video][1] // scale)
                if not flags & 0x80:
                    group += sint_element(REFERENCEBLOCK, (previous_video if previous_video is not None else timestamp) - timestamp or -1)
                body += element(BLOCKGROUP, group)
                previous_video = timestamp
                continue
            body += element(SIMPLEBLOCK, bytes(data[q:e]))
        elif cid != CRC32:
            flush()
            body += element(cid, bytes(data[q:e]))
    flush()
    return element(CLUSTER, body, unknown=args.unknown_size)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("input")
    parser.add_argument("output", nargs="?")
    parser.add_argument("--dump", action="store_true")
    parser.add_argument("--lace", choices=["xiph", "ebml", "fixed"])
    parser.add_argument("--lace-frames", type=int, default=4)
    parser.add_argument("--block-groups", action="store_true")
    parser.add_argument("--unknown-size", action="store_true")
    args = parser.parse_args()
    with open(args.input, "rb") as f:
        data = f.read()
    if args.dump:
        dump(data, 0, len(data), 0)
        return
    if not args.output:
        sys.exit("an output file is needed")
    with open(args.output, "wb") as f:
        f.write(rewrite(data, args))


if __name__ == "__main__":
    main()
