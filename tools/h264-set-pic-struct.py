#!/usr/bin/env python3
"""Rewrites the picture timing SEI messages of an Annex B H.264 stream with the given pic_struct and ct_type values.

libx264 writes pic_struct 0 (progressive frames) or 3 (interlaced ones, whichever field comes first) and no clock
timestamps, so no ct_type: this gives each SEI NAL unit holding a picture timing message (in stream order, one an access
unit) the next of the values, cycling through them, with one clock timestamp carrying the ct_type where one is given.
The stream's SPS must have pic_struct_present_flag and no HRD parameters (libx264 with pic-struct=1, or interlaced),
so that the message is pic_struct and its clock timestamps alone, time_offset 24 bits long (E.2.2). Every other NAL unit
is copied as it is.

Usage: tools/h264-set-pic-struct.py in.264 out.264 PIC_STRUCT[:CT_TYPE] ...
  e.g. 3 4:1 0:0 (pic_struct 3 without a timestamp, 4 with ct_type 1, 0 with ct_type 0)
"""
import sys

NUM_CLOCK_TS = [1, 1, 1, 2, 2, 3, 3, 2, 3]


def unescape(nal):
    out, zeros = bytearray(), 0
    for b in nal:
        if zeros >= 2 and b == 3:
            zeros = 0
            continue
        out.append(b)
        zeros = zeros + 1 if b == 0 else 0
    return out


def escape(rbsp):
    out, zeros = bytearray(), 0
    for b in rbsp:
        if zeros >= 2 and b <= 3:
            out.append(3)
            zeros = 0
        out.append(b)
        zeros = zeros + 1 if b == 0 else 0
    return out


def split_annexb(data):
    """The NAL units of an Annex B stream, without their start codes."""
    starts, i = [], 0
    while True:
        i = data.find(b'\x00\x00\x01', i)
        if i < 0:
            break
        starts.append(i + 3)
        i += 3
    nals = []
    for k, start in enumerate(starts):
        end = starts[k + 1] - 3 if k + 1 < len(starts) else len(data)
        nal = data[start:end]
        while k + 1 < len(starts) and nal.endswith(b'\x00'):
            nal = nal[:-1]  # the next start code's leading zero byte
        nals.append(nal)
    return nals


def pic_timing(pic_struct, ct_type):
    """pic_timing (D.1.3) without HRD delays: pic_struct, then one full clock timestamp of ct_type, if given."""
    bits = f'{pic_struct:04b}'
    for i in range(NUM_CLOCK_TS[pic_struct]):
        if i == 0 and ct_type is not None:
            # clock_timestamp_flag, ct_type, nuit_field_based_flag, counting_type, full_timestamp_flag,
            # discontinuity_flag, cnt_dropped_flag, n_frames, seconds, minutes, hours, time_offset (24 bits)
            bits += '1' + f'{ct_type:02b}' + '0' + '00000' + '1' + '0' + '0' + '0' * 8 + '0' * 17 + '0' * 24
        else:
            bits += '0'
    bits += '1'  # bit_equal_to_one: the payload's byte alignment
    bits += '0' * (-len(bits) % 8)
    return bytes(int(bits[i:i + 8], 2) for i in range(0, len(bits), 8))


def sei_payloads(rbsp):
    """(type, payload) of each message of an SEI RBSP (without its NAL header)."""
    messages, pos = [], 0
    while pos < len(rbsp) and rbsp[pos] != 0x80:
        kind = size = 0
        while rbsp[pos] == 255:
            kind += 255
            pos += 1
        kind += rbsp[pos]
        pos += 1
        while rbsp[pos] == 255:
            size += 255
            pos += 1
        size += rbsp[pos]
        pos += 1
        messages.append((kind, bytes(rbsp[pos:pos + size])))
        pos += size
    return messages


def sei_rbsp(messages):
    out = bytearray()
    for kind, payload in messages:
        for value in (kind, len(payload)):
            while value >= 255:
                out.append(255)
                value -= 255
            out.append(value)
        out += payload
    out.append(0x80)  # rbsp_trailing_bits
    return out


def main():
    source, target, *specs = sys.argv[1:]
    values = []
    for spec in specs:
        pic_struct, _, ct_type = spec.partition(':')
        values.append((int(pic_struct), int(ct_type) if ct_type else None))
    data = open(source, 'rb').read()
    out, count = bytearray(), 0
    for nal in split_annexb(data):
        if nal and nal[0] & 0x1F == 6:
            messages = sei_payloads(unescape(nal[1:]))
            if any(kind == 1 for kind, _ in messages):
                pic_struct, ct_type = values[count % len(values)]
                count += 1
                messages = [(kind, pic_timing(pic_struct, ct_type) if kind == 1 else payload) for kind, payload in messages]
                nal = bytes(nal[:1]) + bytes(escape(sei_rbsp(messages)))
        out += b'\x00\x00\x00\x01' + nal
    open(target, 'wb').write(out)
    print(f'{count} picture timing messages rewritten', file=sys.stderr)


main()
