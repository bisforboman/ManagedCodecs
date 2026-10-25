#!/usr/bin/env python3
"""Splices three 4:0:0 H.264 streams into one with separate colour planes, for streams no encoder at hand writes.

With separate_colour_plane_flag each colour plane is coded as a monochrome picture of its own (7.4.2.1.1), and JM's
encoder and decoder support that in frames only (both crash on field pictures and MBAFF frames). Three 4:0:0 encodes
of one GOP structure (the Y, Cb and Cr planes of a 4:4:4 source, the same settings, so that frame_num, the order
counts, the picture types and the reference marking agree) make one: the first stream's SPS as High 4:4:4 Predictive
with chroma_format_idc 3 and separate_colour_plane_flag 1, its PPS, then each picture's slices of the three streams in
turn, each with its colour_plane_id (0, 1, 2) inserted after pic_parameter_set_id. With CAVLC the rest of the slice
follows two bits later; with CABAC the slice header ends where FFmpeg's trace_headers says, its alignment ones are
redone and the slice data follows from its byte. Each plane then decodes as its stream did, so the three streams'
monochrome decodes, stacked, are the reference.

Usage: tools/h264-splice-planes.py y.264 cb.264 cr.264 out.264
"""
import re
import subprocess
import sys

def nals(data):
    out = []; i = 0; starts = []
    while True:
        j = data.find(b'\x00\x00\x01', i)
        if j < 0: break
        starts.append(j + 3); i = j + 3
    for k, s in enumerate(starts):
        e = starts[k + 1] - 3 if k + 1 < len(starts) else len(data)
        nal = data[s:e]
        while nal.endswith(b'\x00') and k + 1 < len(starts): nal = nal[:-1]
        out.append(nal)
    return out

def unescape(b):
    out = bytearray(); z = 0
    for x in b:
        if z >= 2 and x == 3: z = 0; continue
        out.append(x); z = z + 1 if x == 0 else 0
    return bytes(out)

def escape(b):
    out = bytearray(); z = 0
    for x in b:
        if z >= 2 and x <= 3: out.append(3); z = 0
        out.append(x); z = z + 1 if x == 0 else 0
    return bytes(out)

class R:
    def __init__(s, b): s.bits = ''.join(f'{x:08b}' for x in b); s.p = 0
    def u(s, n): v = int(s.bits[s.p:s.p + n], 2) if n else 0; s.p += n; return v
    def ue(s):
        z = 0
        while s.bits[s.p] == '0': z += 1; s.p += 1
        s.p += 1; return (1 << z) - 1 + s.u(z)
    def rest(s):
        end = s.bits.rstrip('0')  # drop zero padding, then the stop bit
        return end[s.p:len(end) - 1]

class W:
    def __init__(s): s.bits = []
    def u(s, n, v): s.bits.append(format(v, f'0{n}b') if n else '')
    def ue(s, v): x = v + 1; n = x.bit_length(); s.bits.append('0' * (n - 1) + format(x, 'b'))
    def raw(s, b): s.bits.append(b)
    def done(s):
        b = ''.join(s.bits) + '1'; b += '0' * (-len(b) % 8)
        return bytes(int(b[i:i + 8], 2) for i in range(0, len(b), 8))

def sps444(nal):
    r = R(unescape(nal[1:])); w = W()
    profile = r.u(8); w.u(8, 244); w.u(8, r.u(8)); w.u(8, r.u(8))
    w.ue(r.ue())
    assert profile in (100, 110, 122, 244), profile
    chroma = r.ue(); assert chroma == 0, chroma
    w.ue(3); w.u(1, 1)  # separate_colour_plane_flag
    w.ue(r.ue()); depth_chroma = r.ue(); w.ue(depth_chroma)
    w.u(1, r.u(1))
    assert r.u(1) == 0, 'seq_scaling_matrix_present_flag'
    w.u(1, 0)
    w.raw(r.rest())
    return nal[:1] + escape(w.done())

AGREE = ('nal_ref_idc', 'nal_unit_type', 'first_mb_in_slice', 'slice_type', 'frame_num', 'field_pic_flag',
         'bottom_field_flag', 'idr_pic_id', 'pic_order_cnt_lsb', 'delta_pic_order_cnt_bottom', 'direct_spatial_mv_pred_flag',
         'num_ref_idx_active_override_flag', 'num_ref_idx_l0_active_minus1', 'num_ref_idx_l1_active_minus1',
         'ref_pic_list_modification_flag_l0', 'ref_pic_list_modification_flag_l1', 'long_term_reference_flag',
         'adaptive_ref_pic_marking_mode_flag', 'memory_management_control_operation', 'no_output_of_prior_pics_flag')


def slice_headers(path):
    """Each slice header's fields that must agree between the planes, from FFmpeg's trace."""
    trace = subprocess.run(['ffmpeg', '-hide_banner', '-i', path, '-c', 'copy', '-bsf:v', 'trace_headers', '-f', 'null', '-'],
                           capture_output=True, text=True).stderr
    headers = []
    for line in trace.splitlines():
        m = re.search(r'\] \d+ +(\w+) +[01]+ = (-?\d+)', line)
        if 'Slice Header' in line:
            headers.append([])
        elif m and headers and m.group(1) in AGREE:
            headers[-1].append((m.group(1), m.group(2)))
    return headers


def header_ends(path):
    """Each slice header's end, in RBSP bits after the NAL header, from FFmpeg's trace (the CABAC alignment after it)."""
    trace = subprocess.run(['ffmpeg', '-hide_banner', '-i', path, '-c', 'copy', '-bsf:v', 'trace_headers', '-f', 'null', '-'],
                           capture_output=True, text=True).stderr
    ends = []; end = None
    for line in trace.splitlines():
        m = re.search(r'\] (\d+) +(\w+) +([01]+) = ', line)
        if 'Slice Header' in line:
            if end is not None: ends.append(end)
            end = 0
        elif m and end is not None and m.group(2) != 'cabac_alignment_one_bit':
            end = int(m.group(1)) + len(m.group(3)) - 8
    if end is not None: ends.append(end)
    return ends

def slice_with_plane(nal, plane, cabac, end):
    rbsp = unescape(nal[1:]); r = R(rbsp); w = W()
    for _ in range(3): w.ue(r.ue())  # first_mb_in_slice, slice_type, pic_parameter_set_id
    w.u(2, plane)
    if not cabac:
        w.raw(r.rest())
        return nal[:1] + escape(w.done())
    # CABAC: the rest of the header, the alignment ones redone, then the slice data from its byte on.
    w.raw(r.bits[r.p:end])
    b = ''.join(w.bits); b += '1' * (-len(b) % 8)
    head = bytes(int(b[i:i + 8], 2) for i in range(0, len(b), 8))
    return nal[:1] + escape(head + rbsp[(end + 7) // 8:])

streams = [nals(open(f, 'rb').read()) for f in sys.argv[1:4]]
out = bytearray()
kinds = [[n[0] & 0x1F for n in s] for s in streams]
slices = [[n for n in s if (n[0] & 0x1F) in (1, 5)] for s in streams]
assert len({len(s) for s in slices}) == 1, [len(s) for s in slices]
headers = [slice_headers(f) for f in sys.argv[1:4]]
for i, h in enumerate(headers[0]):
    assert headers[1][i] == h and headers[2][i] == h, f'slice {i}: the planes disagree on {h} / {headers[1][i]} / {headers[2][i]}'

for n in streams[0]:
    t = n[0] & 0x1F
    if t == 7: out += b'\x00\x00\x00\x01' + sps444(n)
    elif t == 8: out += b'\x00\x00\x00\x01' + n
pps = next(n for n in streams[0] if n[0] & 0x1F == 8)
r = R(unescape(pps[1:])); r.ue(); r.ue(); cabac = r.u(1) == 1
ends = [header_ends(f) for f in sys.argv[1:4]] if cabac else None
if cabac: assert all(len(e) == len(slices[0]) for e in ends), [len(e) for e in ends]
for i in range(len(slices[0])):
    for p in range(3):
        out += b'\x00\x00\x00\x01' + slice_with_plane(slices[p][i], p, cabac, ends[p][i] if cabac else None)
open(sys.argv[4], 'wb').write(out)
print(len(slices[0]), 'slices a plane;', 'kinds', sorted(set(kinds[0])))
