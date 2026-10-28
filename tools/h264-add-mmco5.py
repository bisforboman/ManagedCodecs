#!/usr/bin/env python3
"""Puts memory_management_control_operation 5 into a field picture of an H.264 Annex B stream (ITU-T H.264 7.3.3.3,
8.2.1, 8.2.5.4.5), which no encoder at hand writes.

  h264-add-mmco5.py in.264 out.264

The first field of the stream's first non-IDR I frame gets operation 5 (all references unmarked, the picture's
frame_num and order count counting as 0 from then on), and every slice after it a frame_num and pic_order_cnt_lsb
rebased to match: its second field frame_num 0, the frames after it 1, 2, ..., and order counts less the first
field's. For the stream to stay valid, the pictures after it must predict from nothing before it: a P-only stream
with one reference frame (JM's NumberReferenceFrames=1), whose lists after an I frame hold that frame's fields alone.
Only what JM's streams here contain is handled (POC type 0, no weighted prediction, one slice group); anything else
stops with an assertion. The result is checked against JM's own decoder by the caller.
"""
import importlib.util
import os
import sys

sys.dont_write_bytecode = True  # importing h264-add-weights.py leaves no __pycache__ in tools/

_spec = importlib.util.spec_from_file_location('h264_add_weights', os.path.join(os.path.dirname(__file__), 'h264-add-weights.py'))
_weights = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_weights)
Reader, Writer = _weights.Reader, _weights.Writer
unescape, escape, stop_bit, split_nals = _weights.unescape, _weights.escape, _weights.stop_bit, _weights.split_nals
parse_sps, ref_pic_list_modification, dec_ref_pic_marking = _weights.parse_sps, _weights.ref_pic_list_modification, _weights.dec_ref_pic_marking


def parse_pps(r):
    pps = {}
    r.ue()
    r.ue()
    pps['entropy'] = r.u(1)
    pps['bottom_field_pic_order'] = r.u(1)
    assert r.ue() == 0, 'slice groups'
    r.ue()
    r.ue()
    assert r.u(1) == 0 and r.u(2) == 0, 'weighted prediction'
    r.se()
    r.se()
    r.se()
    pps['deblocking_control'] = r.u(1)
    r.u(1)
    pps['redundant_pic_cnt'] = r.u(1)
    return pps


def slice_info(r, nal_type, sps):
    """first_mb_in_slice, slice_type, frame_num and whether it is a field picture, from the start of a slice header."""
    first = r.ue()
    slice_type = r.ue() % 5
    r.ue()
    frame_num = r.u(sps['log2_max_frame_num'])
    field = r.u(1) if not sps['frame_mbs_only'] else 0
    return first, slice_type, frame_num, field


def rewrite_slice(r, nal_type, ref_idc, sps, pps, frame_num_shift, lsb_shift, mmco5):
    """The slice with frame_num and pic_order_cnt_lsb less the shifts and, if mmco5, operation 5 as its marking."""
    w = Writer()
    w.ue(r.ue())
    slice_type = r.ue()
    w.ue(slice_type)
    slice_type %= 5
    assert slice_type in (0, 2), 'P and I slices only'
    w.ue(r.ue())
    n = sps['log2_max_frame_num']
    w.u(n, (r.u(n) - frame_num_shift) % (1 << n))
    field = 0
    if not sps['frame_mbs_only']:
        field = r.u(1)
        w.u(1, field)
        if field:
            w.u(1, r.u(1))
    if nal_type == 5:
        w.ue(r.ue())
    assert sps['poc_type'] == 0, 'POC type 0 only'
    n = sps['log2_max_poc_lsb']
    w.u(n, (r.u(n) - lsb_shift) % (1 << n))
    if pps['bottom_field_pic_order'] and not field:
        w.se(r.se())
    if pps['redundant_pic_cnt']:
        w.ue(r.ue())
    start = r.pos
    if slice_type == 0:
        if r.u(1):
            r.ue()
        ref_pic_list_modification(r, slice_type)
    w.copy(r, start, r.pos)
    if ref_idc:
        start = r.pos
        dec_ref_pic_marking(r, nal_type == 5)
        if mmco5:
            assert nal_type != 5
            w.u(1, 1)  # adaptive_ref_pic_marking_mode_flag
            w.ue(5)
            w.ue(0)
        else:
            w.copy(r, start, r.pos)
    if not pps['entropy']:
        # CAVLC: the slice data follows the header bit by bit.
        w.copy(r, r.pos, stop_bit(r.data))
        w.u(1, 1)
        while len(w.bits) % 8:
            w.u(1, 0)
        return w.bytes()
    # CABAC: the rest of the header, then cabac_alignment_one_bits and the byte-aligned slice data as it is.
    start = r.pos
    if slice_type != 2:
        r.ue()  # cabac_init_idc
    r.se()  # slice_qp_delta
    if pps['deblocking_control']:
        if r.ue() != 1:
            r.se()
            r.se()
    w.copy(r, start, r.pos)
    while r.pos % 8:
        assert r.u(1) == 1, 'cabac_alignment_one_bit'
    while len(w.bits) % 8:
        w.u(1, 1)
    return w.bytes() + r.data[r.pos >> 3:]


def main():
    source, target = sys.argv[1:]
    stream = open(source, 'rb').read()
    out = bytearray()
    sps = pps = None
    state = 'before'  # then 'first' (the operation 5 field's slices), 'after'
    frame_num_shift = lsb_shift = 0
    for nal in split_nals(stream):
        header, rbsp = nal[0], unescape(nal[1:])
        nal_type, ref_idc = header & 31, header >> 5
        if nal_type == 7:
            sps = parse_sps(Reader(rbsp))
        elif nal_type == 8:
            pps = parse_pps(Reader(rbsp))
        elif nal_type in (1, 5):
            first, slice_type, frame_num, field = slice_info(Reader(rbsp), nal_type, sps)
            if state == 'before' and nal_type == 1 and slice_type == 2 and field and ref_idc and first == 0:
                state = 'first'
                r = Reader(rbsp)
                slice_info(r, nal_type, sps)
                r.u(1)
                lsb_shift = r.u(sps['log2_max_poc_lsb'])
                frame_num_shift = frame_num
                print(f'operation 5 at frame_num {frame_num}, pic_order_cnt_lsb {lsb_shift}', file=sys.stderr)
            elif state == 'first' and first == 0:
                state = 'after'
            if state == 'first':
                rbsp = rewrite_slice(Reader(rbsp), nal_type, ref_idc, sps, pps, 0, 0, mmco5=True)
            elif state == 'after':
                rbsp = rewrite_slice(Reader(rbsp), nal_type, ref_idc, sps, pps, frame_num_shift, lsb_shift, mmco5=False)
        else:
            assert nal_type not in (2, 3, 4, 20), f'NAL type {nal_type}'
        out += b'\x00\x00\x00\x01' + bytes([header]) + escape(rbsp)
    assert state == 'after', 'no non-IDR I field picture with a field after it'
    open(target, 'wb').write(out)


if __name__ == '__main__':
    main()
