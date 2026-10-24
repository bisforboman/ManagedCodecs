#!/usr/bin/env python3
"""Muxes an Annex B H.264 stream into an MP4 file, for streams FFmpeg cannot mux.

FFmpeg's muxer needs its parser to understand the SPS (for the picture size), and that parser refuses what its decoder
does not decode: separate colour planes, luma and chroma bit depths that differ. This writes the plain MP4 FFmpeg would:
one video track, the first SPS and PPS in the avcC, each access unit a sample of length-prefixed NAL units (parameter
sets and access unit delimiters left out), a sync sample table for the IDR pictures, and composition offsets.

Usage: tools/mux-h264.py in.264 out.mp4 duration [pts...]
  duration: each sample's duration in a 1/12800 timescale (512 for 25 frames a second);
  pts: each access unit's presentation time in that timescale, in decoding order; without them, pic_order_cnt_lsb
  times half the duration (POC type 0, a frame two apart, as make-jm-samples.sh's setts takes it; no wrap-around).
An access unit starts at a slice whose first_mb_in_slice is 0 (and, with separate colour planes, whose colour_plane_id
is 0).
"""
import struct
import sys

TIMESCALE = 12800


def nal_units(data):
    """The NAL units of an Annex B stream, without their start codes."""
    starts, i = [], 0
    while True:
        i = data.find(b'\x00\x00\x01', i)
        if i < 0:
            break
        starts.append(i + 3)
        i += 3
    for k, s in enumerate(starts):
        end = starts[k + 1] - 3 if k + 1 < len(starts) else len(data)
        nal = data[s:end]
        if k + 1 < len(starts):
            nal = nal.rstrip(b'\x00')  # trailing_zero_8bits, and the leading zero of a 4-byte start code
        if nal:
            yield nal


def rbsp(nal):
    out, zeros = bytearray(), 0
    for b in nal[1:]:
        if zeros >= 2 and b == 3:
            zeros = 0
            continue
        out.append(b)
        zeros = zeros + 1 if b == 0 else 0
    return out


class Bits:
    def __init__(self, data):
        self.data, self.pos = data, 0

    def u(self, n):
        value = 0
        for _ in range(n):
            value = (value << 1) | ((self.data[self.pos >> 3] >> (7 - (self.pos & 7))) & 1)
            self.pos += 1
        return value

    def ue(self):
        zeros = 0
        while self.u(1) == 0:
            zeros += 1
        return (1 << zeros) - 1 + self.u(zeros)


    def se(self):
        k = self.ue()
        return (k + 1) // 2 if k & 1 else -(k // 2)


class Sps:
    """The fields of an SPS (7.3.2.1.1) the muxer needs: the chroma format and depths, frame_num and POC sizes, size."""

    def __init__(self, sps):
        r = Bits(rbsp(sps))
        profile = r.u(8)
        r.u(16)
        r.ue()
        self.chroma, self.separate, self.luma_depth, self.chroma_depth = 1, 0, 0, 0
        if profile in (100, 110, 122, 244, 44, 83, 86, 118, 128, 138, 139, 134, 135):
            self.chroma = r.ue()
            self.separate = r.u(1) if self.chroma == 3 else 0
            self.luma_depth, self.chroma_depth = r.ue(), r.ue()
            r.u(1)  # qpprime_y_zero_transform_bypass_flag
            if r.u(1):  # seq_scaling_matrix_present_flag
                for i in range(8 if self.chroma != 3 else 12):
                    if r.u(1):
                        last, next_ = 8, 8
                        for _ in range(16 if i < 6 else 64):
                            if next_ != 0:
                                next_ = (last + r.se() + 256) % 256
                            last = next_ if next_ != 0 else last
        self.frame_num_bits = r.ue() + 4
        self.poc_type = r.ue()
        if self.poc_type != 0:
            sys.exit('POC type 0 only')
        self.poc_bits = r.ue() + 4
        r.ue()  # max_num_ref_frames
        r.u(1)  # gaps_in_frame_num_value_allowed_flag
        self.width = (r.ue() + 1) * 16
        map_units = r.ue() + 1
        self.frame_mbs_only = r.u(1)
        self.height = map_units * 16 * (2 - self.frame_mbs_only)


def slice_poc(nal, sps):
    """pic_order_cnt_lsb of a slice (7.3.3)."""
    r = Bits(rbsp(nal))
    r.ue()  # first_mb_in_slice
    r.ue()  # slice_type
    r.ue()  # pic_parameter_set_id
    if sps.separate:
        r.u(2)  # colour_plane_id
    r.u(sps.frame_num_bits)
    if not sps.frame_mbs_only and r.u(1):  # field_pic_flag
        r.u(1)  # bottom_field_flag
    if nal[0] & 0x1f == 5:
        r.ue()  # idr_pic_id
    return r.u(sps.poc_bits)


def starts_access_unit(nal, separate):
    if nal[0] & 0x1f not in (1, 5):
        return False
    r = Bits(rbsp(nal))
    if r.ue() != 0:  # first_mb_in_slice
        return False
    if separate:
        r.ue()  # slice_type
        r.ue()  # pic_parameter_set_id
        return r.u(2) == 0  # colour_plane_id
    return True


def box(kind, *payload):
    body = b''.join(payload)
    return struct.pack('>I', 8 + len(body)) + kind + body


def full(kind, version, flags, *payload):
    return box(kind, struct.pack('>I', (version << 24) | flags), *payload)


def main():
    path, out_path, duration = sys.argv[1], sys.argv[2], int(sys.argv[3])
    pts = [int(p) for p in sys.argv[4:]]
    nals = list(nal_units(open(path, 'rb').read()))
    sps = next(n for n in nals if n[0] & 0x1f == 7)
    pps = next(n for n in nals if n[0] & 0x1f == 8)
    info = Sps(sps)
    chroma, separate, luma_depth, chroma_depth = info.chroma, info.separate, info.luma_depth, info.chroma_depth
    width, height = info.width, info.height

    # Access units in decoding order: the slices (and SEI) from one first slice to the next.
    units = []
    for nal in nals:
        kind = nal[0] & 0x1f
        if kind in (7, 8, 9):
            continue
        if starts_access_unit(nal, separate) or not units:
            units.append([])
        units[-1].append(nal)
    if not pts:
        pts = [slice_poc(next(n for n in unit if n[0] & 0x1f in (1, 5)), info) * (duration // 2) for unit in units]
    if len(units) != len(pts):
        sys.exit(f'{len(units)} access units, {len(pts)} timestamps')

    samples = [b''.join(struct.pack('>I', len(n)) + n for n in unit) for unit in units]
    sync = [i + 1 for i, unit in enumerate(units) if any(n[0] & 0x1f == 5 for n in unit)]

    # Decoding times one duration apart, starting low enough that no composition offset is negative.
    shift = max(0, max(i * duration - p for i, p in enumerate(pts)))
    offsets = [p + shift - i * duration for i, p in enumerate(pts)]
    total = duration * len(samples)

    avcc = bytes([1, sps[1], sps[2], sps[3], 0xFF, 0xE1]) + struct.pack('>H', len(sps)) + sps + bytes([1]) + struct.pack('>H', len(pps)) + pps
    if sps[1] in (100, 110, 122, 244):
        avcc += bytes([0xFC | chroma, 0xF8 | luma_depth, 0xF8 | chroma_depth, 0])
    avc1 = box(b'avc1', bytes(6), struct.pack('>H', 1), bytes(16), struct.pack('>HH', width, height),
               struct.pack('>II', 0x480000, 0x480000), bytes(4), struct.pack('>H', 1), bytes(32), struct.pack('>Hh', 24, -1),
               box(b'avcC', avcc))

    def stbl(chunk_offset):
        return box(b'stbl',
                   full(b'stsd', 0, 0, struct.pack('>I', 1), avc1),
                   full(b'stts', 0, 0, struct.pack('>III', 1, len(samples), duration)),
                   full(b'stss', 0, 0, struct.pack('>I', len(sync)), *[struct.pack('>I', s) for s in sync]),
                   full(b'ctts', 0, 0, struct.pack('>I', len(offsets)), *[struct.pack('>II', 1, o) for o in offsets]),
                   full(b'stsc', 0, 0, struct.pack('>IIII', 1, 1, len(samples), 1)),
                   full(b'stsz', 0, 0, struct.pack('>II', 0, len(samples)), *[struct.pack('>I', len(s)) for s in samples]),
                   full(b'stco', 0, 0, struct.pack('>II', 1, chunk_offset)))

    def moov(chunk_offset):
        movie_duration = total * 1000 // TIMESCALE
        matrix = struct.pack('>9I', 0x10000, 0, 0, 0, 0x10000, 0, 0, 0, 0x40000000)
        return box(b'moov',
                   full(b'mvhd', 0, 0, struct.pack('>IIII', 0, 0, 1000, movie_duration), struct.pack('>IH', 0x10000, 0x100),
                        bytes(10), matrix, bytes(24), struct.pack('>I', 2)),
                   box(b'trak',
                       full(b'tkhd', 0, 3, struct.pack('>IIIII', 0, 0, 1, 0, movie_duration), bytes(8), struct.pack('>HHHH', 0, 0, 0, 0),
                            matrix, struct.pack('>II', width << 16, height << 16)),
                       box(b'mdia',
                           full(b'mdhd', 0, 0, struct.pack('>IIII', 0, 0, TIMESCALE, total), struct.pack('>HH', 0x55C4, 0)),
                           full(b'hdlr', 0, 0, bytes(4), b'vide', bytes(12), b'VideoHandler\x00'),
                           box(b'minf',
                               full(b'vmhd', 0, 1, bytes(8)),
                               box(b'dinf', full(b'dref', 0, 0, struct.pack('>I', 1), full(b'url ', 0, 1))),
                               stbl(chunk_offset)))))

    ftyp = box(b'ftyp', b'isom', struct.pack('>I', 512), b'isomiso2avc1mp41')
    size = len(ftyp) + len(moov(0))
    with open(out_path, 'wb') as f:
        f.write(ftyp)
        f.write(moov(size + 8))  # the samples follow the mdat header
        f.write(box(b'mdat', *samples))


if __name__ == '__main__':
    main()
