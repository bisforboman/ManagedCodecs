#!/usr/bin/env python3
"""Sets bit_depth_chroma_minus8 to bit_depth_luma_minus8 in the SPSs of an Annex B H.264 stream of chroma_format_idc 0.

JM's encoder writes 4:0:0 streams with an 8-bit chroma depth whatever the luma's, which no plane uses (7.4.2.1.1), but
FFmpeg's parser refuses an SPS whose depths differ and so cannot mux the stream. Everything else is copied as it is.

Usage: tools/h264-chroma-depth.py in.264 out.264
"""
import sys


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


class Reader:
    def __init__(self, data):
        self.bits = ''.join(f'{b:08b}' for b in data)
        self.pos = 0

    def u(self, n):
        value = int(self.bits[self.pos:self.pos + n], 2)
        self.pos += n
        return value

    def ue(self):
        zeros = 0
        while self.bits[self.pos] == '0':
            zeros += 1
            self.pos += 1
        return self.u(zeros + 1) - 1


def ue_bits(value):
    code = f'{value + 1:b}'
    return '0' * (len(code) - 1) + code


def rewrite_sps(rbsp):
    r = Reader(rbsp)
    header = r.u(8)  # NAL header
    profile = r.u(8)
    r.u(16)  # constraint flags, level_idc
    r.ue()  # seq_parameter_set_id
    if profile not in (100, 110, 122, 244, 44, 83, 86, 118, 128, 138, 139, 134, 135):
        return rbsp
    start = r.pos - len(ue_bits(0)) * 0
    chroma_format = r.ue()
    if chroma_format != 0:
        return rbsp
    depth_start = r.pos
    luma = r.ue()
    r.ue()  # bit_depth_chroma_minus8
    rest = r.bits[r.pos:]
    bits = r.bits[:depth_start] + ue_bits(luma) + ue_bits(luma) + rest
    # The RBSP ends in its stop bit and zero bits to the byte: drop those, then align again.
    bits = bits.rstrip('0')
    assert bits.endswith('1')
    bits += '0' * (-len(bits) % 8)
    return bytearray(int(bits[i:i + 8], 2) for i in range(0, len(bits), 8))


def main():
    data = open(sys.argv[1], 'rb').read()
    # Start codes (00 00 01, also 00 00 00 01): split into NAL units.
    starts = []
    i = 0
    while True:
        i = data.find(b'\x00\x00\x01', i)
        if i < 0:
            break
        starts.append(i + 3)
        i += 3
    out = bytearray()
    for k, s in enumerate(starts):
        end = starts[k + 1] - 3 if k + 1 < len(starts) else len(data)
        nal = data[s:end]
        if k + 1 < len(starts) and nal.endswith(b'\x00'):
            nal = nal.rstrip(b'\x00')  # the leading zero of the next 4-byte start code
        if nal and nal[0] & 0x1f == 7:
            nal = bytes(escape(rewrite_sps(unescape(nal))))
        out += b'\x00\x00\x00\x01' + nal
    open(sys.argv[2], 'wb').write(out)


if __name__ == '__main__':
    main()
