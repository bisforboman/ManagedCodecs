#!/usr/bin/env python3
"""Turns weighted prediction on in an H.264 Annex B stream (ITU-T H.264 7.3.2.2, 7.3.3, 7.3.3.2).

JM's encoder crashes when asked to weight field pictures, so tools/make-jm-samples.sh weights its field-picture streams
afterwards with this:

  h264-add-weights.py implicit in.264 out.264   weighted_bipred_idc 2 in the PPS: implicit weights in B slices, no
                                                slice syntax changes.
  h264-add-weights.py explicit in.264 out.264   weighted_pred_flag 1 and weighted_bipred_idc 1, and a
                                                pred_weight_table in every P and B slice header, each reference index
                                                weighted differently (so a decoder that maps them wrongly shows).

Only what JM's streams here contain is handled (one slice group, no separate colour planes, no SP/SI slices); anything
else stops with an assertion. The decoded result is checked against FFmpeg and JM's own decoder by the caller.
"""
import sys


class Reader:
    def __init__(self, data):
        self.data, self.pos = data, 0

    def u(self, n):
        v = 0
        for _ in range(n):
            v = (v << 1) | ((self.data[self.pos >> 3] >> (7 - (self.pos & 7))) & 1)
            self.pos += 1
        return v

    def ue(self):
        zeros = 0
        while self.u(1) == 0:
            zeros += 1
        return (1 << zeros) - 1 + self.u(zeros)

    def se(self):
        k = self.ue()
        return (k + 1) // 2 if k & 1 else -(k // 2)


class Writer:
    def __init__(self):
        self.bits = []

    def u(self, n, v):
        self.bits += [(v >> (n - 1 - i)) & 1 for i in range(n)]

    def ue(self, v):
        n = (v + 1).bit_length()
        self.u(n - 1, 0)
        self.u(n, v + 1)

    def se(self, v):
        self.ue(2 * v - 1 if v > 0 else -2 * v)

    def copy(self, r, start, end):
        self.bits += [(r.data[i >> 3] >> (7 - (i & 7))) & 1 for i in range(start, end)]

    def bytes(self):
        assert len(self.bits) % 8 == 0
        return bytes(int(''.join(map(str, self.bits[i:i + 8])), 2) for i in range(0, len(self.bits), 8))


def unescape(nal):
    out, zeros = bytearray(), 0
    for b in nal:
        if zeros >= 2 and b == 3:
            zeros = 0
            continue
        out.append(b)
        zeros = zeros + 1 if b == 0 else 0
    return bytes(out)


def escape(rbsp):
    out, zeros = bytearray(), 0
    for b in rbsp:
        if zeros >= 2 and b <= 3:
            out.append(3)
            zeros = 0
        out.append(b)
        zeros = zeros + 1 if b == 0 else 0
    return bytes(out)


def stop_bit(data):
    """Position of the rbsp_stop_one_bit: the last 1 bit."""
    i = len(data) - 1
    while data[i] == 0:
        i -= 1
    return i * 8 + 7 - (data[i] & -data[i]).bit_length() + 1


def split_nals(stream):
    starts, i = [], 0
    while True:
        i = stream.find(b'\x00\x00\x01', i)
        if i < 0:
            break
        starts.append(i + 3)
        i += 3
    for k, s in enumerate(starts):
        end = starts[k + 1] - 3 if k + 1 < len(starts) else len(stream)
        while end > s and stream[end - 1] == 0:
            end -= 1
        yield stream[s:end]


def parse_sps(r):
    sps = {'chroma_format_idc': 1}
    profile = r.u(8)
    r.u(16)
    r.ue()
    if profile in (100, 110, 122, 244, 44, 83, 86, 118, 128, 138, 139, 134, 135):
        sps['chroma_format_idc'] = r.ue()
        if sps['chroma_format_idc'] == 3:
            assert r.u(1) == 0, 'separate colour planes'
        r.ue()
        r.ue()
        r.u(1)
        assert r.u(1) == 0, 'SPS scaling lists'
    sps['log2_max_frame_num'] = r.ue() + 4
    sps['poc_type'] = r.ue()
    if sps['poc_type'] == 0:
        sps['log2_max_poc_lsb'] = r.ue() + 4
    elif sps['poc_type'] == 1:
        sps['delta_pic_order_always_zero'] = r.u(1)
        r.se()
        r.se()
        for _ in range(r.ue()):
            r.se()
    r.ue()
    r.u(1)
    r.ue()
    r.ue()
    sps['frame_mbs_only'] = r.u(1)
    return sps


def rewrite_pps(r, mode):
    """The PPS with the weighting flags set; returns (new RBSP, parsed fields)."""
    w = Writer()
    pps = {}
    start = r.pos
    r.ue()
    r.ue()
    pps['entropy'] = r.u(1)
    pps['bottom_field_pic_order'] = r.u(1)
    assert r.ue() == 0, 'slice groups'
    pps['num_ref_idx_l0_default'] = r.ue() + 1
    pps['num_ref_idx_l1_default'] = r.ue() + 1
    w.copy(r, start, r.pos)
    r.u(1)
    r.u(2)
    pps['weighted_pred'] = 1 if mode == 'explicit' else 0
    pps['weighted_bipred'] = 1 if mode == 'explicit' else 2
    w.u(1, pps['weighted_pred'])
    w.u(2, pps['weighted_bipred'])
    rest = r.pos
    r.se()
    r.se()
    r.se()
    pps['deblocking_control'] = r.u(1)
    r.u(1)
    pps['redundant_pic_cnt'] = r.u(1)
    w.copy(r, rest, stop_bit(r.data) + 1)
    while len(w.bits) % 8:
        w.u(1, 0)
    return w.bytes(), pps


def pred_weight_table(w, sps, slice_type, counts):
    """Weights and offsets that differ per reference index and list; some entries left at the default."""
    chroma = sps['chroma_format_idc'] != 0
    w.ue(5)  # luma_log2_weight_denom
    if chroma:
        w.ue(4)  # chroma_log2_weight_denom
    for lst in range(2 if slice_type == 1 else 1):
        for i in range(counts[lst]):
            k = i + 7 * lst
            luma = k % 3 != 2
            w.u(1, luma)
            if luma:
                w.se(32 + (k * 5) % 13 - 6)
                w.se((k * 3) % 11 - 5)
            if chroma:
                flag = k % 2 == 0
                w.u(1, flag)
                if flag:
                    for c in range(2):
                        w.se(16 + (k * 7 + c * 3) % 9 - 4)
                        w.se((k * 5 + c) % 9 - 4)


def ref_pic_list_modification(r, slice_type):
    for lst in range(2 if slice_type == 1 else 1):
        if r.u(1):
            while True:
                idc = r.ue()
                if idc == 3:
                    break
                r.ue()


def dec_ref_pic_marking(r, idr):
    if idr:
        r.u(1)
        r.u(1)
    elif r.u(1):
        while True:
            op = r.ue()
            if op == 0:
                break
            if op in (1, 3):
                r.ue()
            if op == 2:
                r.ue()
            if op in (3, 6):
                r.ue()
            if op == 4:
                r.ue()


def rewrite_slice(r, nal_type, ref_idc, sps, pps):
    first = r.ue()
    slice_type = r.ue() % 5
    assert slice_type in (0, 1, 2), 'SP/SI slices'
    r.ue()
    r.u(sps['log2_max_frame_num'])
    field = 0
    if not sps['frame_mbs_only']:
        field = r.u(1)
        if field:
            r.u(1)
    if nal_type == 5:
        r.ue()
    if sps['poc_type'] == 0:
        r.u(sps['log2_max_poc_lsb'])
        if pps['bottom_field_pic_order'] and not field:
            r.se()
    if sps['poc_type'] == 1 and not sps['delta_pic_order_always_zero']:
        r.se()
        if pps['bottom_field_pic_order'] and not field:
            r.se()
    if pps['redundant_pic_cnt']:
        r.ue()
    if slice_type == 1:
        r.u(1)
    # The PPS defaults, in field pictures too (as FFmpeg and JM's decoder read them; not twice as many).
    counts = [pps['num_ref_idx_l0_default'], pps['num_ref_idx_l1_default']]
    if slice_type in (0, 1):
        if r.u(1):
            counts[0] = r.ue() + 1
            if slice_type == 1:
                counts[1] = r.ue() + 1
    if slice_type != 2:
        ref_pic_list_modification(r, slice_type)
    weighted = (slice_type == 0 and pps['weighted_pred']) or (slice_type == 1 and pps['weighted_bipred'] == 1)
    insert = r.pos
    w = Writer()
    w.copy(r, 0, insert)
    if weighted:
        pred_weight_table(w, sps, slice_type, counts)
    if not pps['entropy']:
        # CAVLC: the slice data follows the header bit by bit.
        w.copy(r, insert, stop_bit(r.data))
        w.u(1, 1)
        while len(w.bits) % 8:
            w.u(1, 0)
        return w.bytes(), weighted
    # CABAC: the rest of the header, then cabac_alignment_one_bits and byte-aligned slice data, copied as it is.
    if ref_idc:
        dec_ref_pic_marking(r, nal_type == 5)
    if slice_type != 2:
        r.ue()  # cabac_init_idc
    r.se()  # slice_qp_delta
    if pps['deblocking_control']:
        if r.ue() != 1:
            r.se()
            r.se()
    header_end = r.pos
    while r.pos % 8:
        assert r.u(1) == 1, 'cabac_alignment_one_bit'
    w.copy(r, insert, header_end)
    while len(w.bits) % 8:
        w.u(1, 1)
    return w.bytes() + r.data[r.pos >> 3:], weighted


def main():
    mode, source, target = sys.argv[1:]
    assert mode in ('implicit', 'explicit')
    stream = open(source, 'rb').read()
    out = bytearray()
    sps = pps = None
    weighted = 0
    for nal in split_nals(stream):
        header, rbsp = nal[0], unescape(nal[1:])
        nal_type, ref_idc = header & 31, header >> 5
        if nal_type == 7:
            sps = parse_sps(Reader(rbsp))
        elif nal_type == 8:
            rbsp, pps = rewrite_pps(Reader(rbsp), mode)
        elif nal_type in (1, 5) and mode == 'explicit':
            rbsp, w = rewrite_slice(Reader(rbsp), nal_type, ref_idc, sps, pps)
            weighted += w
        else:
            assert nal_type not in (2, 3, 4, 20), f'NAL type {nal_type}'
        out += b'\x00\x00\x00\x01' + bytes([header]) + escape(rbsp)
    open(target, 'wb').write(out)
    print(f'{target}: {weighted} slice headers given a pred_weight_table', file=sys.stderr)


main()
