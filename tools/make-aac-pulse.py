#!/usr/bin/env python3
"""Writes an AAC LC stream (ADTS, mono, 44.1 kHz) whose every frame carries pulse_data, which no encoder at hand
writes (FFmpeg's never does), so that FFmpeg's decoder can be the reference for it (make-samples.sh).

Each frame is one single_channel_element, written by the syntax of ISO/IEC 14496-3 4.4.2: a long window, eight
scalefactor bands of spectrum codebook 1 (signed quads of -1..1, one quad a band at 44.1 kHz), random quads, one to
four pulses of random amplitude (1 to 15) at random positions in those bands (some on zero values), and no TNS. The
Huffman codewords are read from the decoder's own tables (src/ManagedCodecs.Aac/AacTables.cs, Tables 4.A.1 and 4.A.2).

Usage: tools/make-aac-pulse.py <out.aac> [frames]
"""

import random
import re
import sys
from pathlib import Path

TABLES = Path(__file__).resolve().parents[1] / "src" / "ManagedCodecs.Aac" / "AacTables.cs"
SAMPLE_RATE_INDEX = 4  # 44100 Hz
MAX_SFB = 8  # bands 0..7: spectral lines 0..31, four a band (swb_offset_long_44)
GLOBAL_GAIN = 166  # a gain of 2^16.5: samples of about 0.1 to 0.5, well above the comparison tolerance


def codebook(source, name):
    body = re.search(name + r"\s*=\s*\[(.*?)\];", source, re.S).group(1)
    return [code.replace(" ", "") for code in re.findall(r'"([01 ]+)"', body)]


class Bits:
    def __init__(self):
        self.bits = []

    def put(self, value, count):
        self.bits.extend((value >> (count - 1 - i)) & 1 for i in range(count))

    def code(self, text):
        self.bits.extend(int(c) for c in text)

    def align(self):
        while len(self.bits) % 8:
            self.bits.append(0)

    def bytes(self):
        return bytes(int("".join(map(str, self.bits[i:i + 8])), 2) for i in range(0, len(self.bits), 8))


def raw_data_block(rng, scalefactor_codes, book1):
    b = Bits()
    b.put(0, 3)  # ID_SCE
    b.put(0, 4)  # element_instance_tag
    b.put(GLOBAL_GAIN, 8)
    b.put(0, 1)  # ics_reserved_bit
    b.put(0, 2)  # ONLY_LONG_SEQUENCE
    b.put(0, 1)  # window_shape: sine
    b.put(MAX_SFB, 6)
    b.put(0, 1)  # predictor_data_present
    b.put(1, 4)  # section_data: one section of codebook 1 ...
    b.put(MAX_SFB, 5)  # ... over all MAX_SFB bands (below the escape value 31)
    for _ in range(MAX_SFB):
        b.code(scalefactor_codes[60])  # scalefactor difference 0: every band at global_gain
    b.put(1, 1)  # pulse_data_present
    count = rng.randint(1, 4)
    start = rng.randint(0, MAX_SFB - 1)
    position = start * 4
    offsets = []
    for _ in range(count):
        offset = rng.randint(0, 31)
        if position + offset >= MAX_SFB * 4:
            offset = 0
        position += offset
        offsets.append(offset)
    b.put(count - 1, 2)
    b.put(start, 6)
    for offset in offsets:
        b.put(offset, 5)
        b.put(rng.randint(1, 15), 4)
    b.put(0, 1)  # tns_data_present
    b.put(0, 1)  # gain_control_data_present
    for _ in range(MAX_SFB):  # spectral_data: one quad a band
        w, x, y, z = (rng.randint(-1, 1) for _ in range(4))
        b.code(book1[(w + 1) * 27 + (x + 1) * 9 + (y + 1) * 3 + (z + 1)])
    b.put(7, 3)  # ID_END
    b.align()
    return b.bytes()


def adts(payload):
    length = 7 + len(payload)
    h = Bits()
    h.put(0xFFF, 12)
    h.put(0, 1)  # ID: MPEG-4
    h.put(0, 2)  # layer
    h.put(1, 1)  # protection_absent
    h.put(1, 2)  # profile_ObjectType: AAC LC
    h.put(SAMPLE_RATE_INDEX, 4)
    h.put(0, 1)  # private_bit
    h.put(1, 3)  # channel_configuration: mono
    h.put(0, 4)  # original_copy, home, copyright_identification_bit, copyright_identification_start
    h.put(length, 13)
    h.put(0x7FF, 11)  # adts_buffer_fullness: variable rate
    h.put(0, 2)  # number_of_raw_data_blocks_in_frame - 1
    return h.bytes() + payload


def main():
    out = sys.argv[1]
    frames = int(sys.argv[2]) if len(sys.argv) > 2 else 43
    source = TABLES.read_text(encoding="utf-8")
    scalefactor_codes = codebook(source, "ScalefactorCodebook")
    book1 = codebook(source, "SpectrumCodebook1")
    assert len(scalefactor_codes) == 121 and len(book1) == 81
    rng = random.Random(1)
    with open(out, "wb") as f:
        for _ in range(frames):
            f.write(adts(raw_data_block(rng, scalefactor_codes, book1)))


if __name__ == "__main__":
    main()
