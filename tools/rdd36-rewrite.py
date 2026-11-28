#!/usr/bin/env python3
"""Rewrites the frames of a SMPTE RDD 36 stream in a MOV file, for what FFmpeg's encoders never write (issue #461;
src/ManagedCodecs.Rdd36/CLAUDE.md). Written from RDD 36:2022 (https://pub.smpte.org/doc/rdd36/): it parses each frame
down to its quantized coefficients and writes it again, so FFmpeg's decoder can be the reference for:

  --quantization-index A,B,...  each slice takes the next index of the list (around again at its end) and its
                                coefficients are scaled to it, towards zero: indices above 128, where qScale grows
                                by 4 an index (Table 15), and the reserved ones (0, 225 to 255)
  --version N                   bitstream_version (1: the version that allows 4:4:4 and alpha, here still 4:2:2)
  --zero-bytes N                N zero bytes (up to 3) after each component's coefficients, as "older encoders"
                                wrote (5.3.2, 6.3.2)
  --extra F,P                   F and P more bytes in every frame header and picture header than their syntax
                                elements take (0xA5 each): a decoder finds what follows by the sizes the headers
                                give (6.4)
  --aspect N                    aspect_ratio_information (Table 3)
  --colour P,T,M                color_primaries, transfer_characteristic and matrix_coefficients

What RDD 36 allows and FFmpeg 6.1's decoder refuses can't have its reference, and is covered by hand-made frames in
Rdd36DecoderTests instead: a slice header longer than its syntax elements (FFmpeg: "invalid plane data size"), and
reserved bits that are not 0 (it reads the byte before bitstream_version as part of the version, and the picture
header's reserved bits as part of the slice size).

Each frame keeps its size in the file: what the rewriting saves becomes stuffing (5.1.2), which FFmpeg's encoders
never write either, and a rewrite that makes a frame larger is refused (scale the coefficients down with
--quantization-index). Only 4:2:2 progressive frames without alpha. Needs ffprobe, for the packets' places.

    python3 -I tools/rdd36-rewrite.py in.mov out.mov --quantization-index 129,160,224 --zero-bytes 2
"""

import argparse
import json
import struct
import subprocess
import sys

# Tables 9 to 11: an exponential-Golomb order, or (lastRiceQ, kRice, kExp).
DC_CODES = [0, 1, (1, 2, 3), 3]
RUN_CODES = [(2, 0, 1), (2, 0, 1), (1, 0, 1), (1, 0, 1), 0, (1, 1, 2), (1, 1, 2), (1, 1, 2), (1, 1, 2), 1, 1, 1, 1, 1, 1, 2]
LEVEL_CODES = [(2, 0, 2), (1, 0, 1), (2, 0, 1), 0, 1, 1, 1, 1, 2]


class Reader:
    def __init__(self, data):
        self.value = int.from_bytes(data, "big")
        self.length = len(data) * 8
        self.position = 0

    def bits(self, count):
        if self.position + count > self.length:
            raise ValueError("a codeword runs past its component's data")
        self.position += count
        return (self.value >> (self.length - self.position)) & ((1 << count) - 1)

    def end(self):
        """endOfData(): 31 bits or fewer remain, all of them 0."""
        left = self.length - self.position
        return left <= 31 and (self.value & ((1 << left) - 1)) == 0

    def exp_golomb(self, order):
        level = 0
        while self.bits(1) == 0:
            level += 1
        return ((1 << (level + order)) | self.bits(level + order)) - (1 << order)

    def code(self, book):
        if isinstance(book, int):
            return self.exp_golomb(book)
        last, rice, exp = book
        level = 0
        while self.bits(1) == 0:
            level += 1
            if level > last:
                return self.exp_golomb(exp) + ((last + 1) << rice)
        return (level << rice) | self.bits(rice)


class Writer:
    def __init__(self):
        self.value = 0
        self.length = 0

    def bits(self, value, count):
        self.value = (self.value << count) | value
        self.length += count

    def exp_golomb(self, symbol, order):
        value = symbol + (1 << order)
        self.bits(value, 2 * value.bit_length() - order - 1)  # level zeros, then the value

    def code(self, symbol, book):
        if isinstance(book, int):
            return self.exp_golomb(symbol, book)
        last, rice, exp = book
        if symbol < (last + 1) << rice:
            self.bits(1, (symbol >> rice) + 1)
            self.bits(symbol & ((1 << rice) - 1), rice)
        else:
            self.bits(0, last + 1)
            self.exp_golomb(symbol - ((last + 1) << rice), exp)

    def bytes(self):
        pad = -self.length % 8
        return ((self.value << pad).to_bytes((self.length + pad) // 8, "big"))


def signed(symbol):
    return symbol >> 1 if symbol % 2 == 0 else -((symbol + 1) >> 1)


def symbol_of(n):
    return 2 * n if n >= 0 else -2 * n - 1


def read_coefficients(data, blocks):
    """scanned_coefficients(): the scanned array, blocks * 64 quantized coefficients."""
    reader = Reader(data)
    coefficients = [0] * (blocks * 64)
    coefficients[0] = signed(reader.exp_golomb(5))
    previous = 3
    for n in range(1, blocks):
        difference = signed(reader.code(DC_CODES[min(abs(previous), 3)]))
        if previous < 0:
            difference = -difference
        coefficients[n] = coefficients[n - 1] + difference
        previous = difference
    n, previous_run, previous_level = blocks, 4, 1
    while not reader.end():
        run = reader.code(RUN_CODES[min(previous_run, 15)])
        level = reader.code(LEVEL_CODES[min(previous_level, 8)])
        sign = reader.bits(1)
        n += run
        coefficients[n] = -(level + 1) if sign else level + 1
        n += 1
        previous_run, previous_level = run, level
    return coefficients


def write_coefficients(coefficients, blocks):
    writer = Writer()
    writer.exp_golomb(symbol_of(coefficients[0]), 5)
    previous = 3
    for n in range(1, blocks):
        difference = coefficients[n] - coefficients[n - 1]
        writer.code(symbol_of(difference if previous >= 0 else -difference), DC_CODES[min(abs(previous), 3)])
        previous = difference
    previous_run, previous_level, run = 4, 1, 0
    for n in range(blocks, len(coefficients)):
        if coefficients[n] == 0:
            run += 1
            continue
        level = abs(coefficients[n]) - 1
        writer.code(run, RUN_CODES[min(previous_run, 15)])
        writer.code(level, LEVEL_CODES[min(previous_level, 8)])
        writer.bits(1 if coefficients[n] < 0 else 0, 1)
        previous_run, previous_level, run = run, level, 0
    return writer.bytes()


def scale_of(index):
    """qScale (Table 15), the reserved indices as FFmpeg takes them: 0 as 1, above 224 as 224."""
    index = min(max(index, 1), 224)
    return index if index <= 128 else 128 + 4 * (index - 128)


def slice_sizes(width_in_mb, log2_size):
    sizes, size, left = [], 1 << log2_size, width_in_mb
    while True:
        while left >= size:
            sizes.append(size)
            left -= size
        size //= 2
        if left <= 0:
            return sizes


def rewrite(frame, options, counter):
    size, identifier = struct.unpack(">I4s", frame[:8])
    if identifier != b"icpf":
        sys.exit("not an RDD 36 frame")
    header = bytearray(frame[8:8 + struct.unpack(">H", frame[8:10])[0]])
    width, height = struct.unpack(">HH", header[8:12])
    if header[12] >> 6 != 2 or (header[12] >> 2) & 3 != 0 or header[17] & 15 != 0:
        sys.exit("only 4:2:2 progressive frames without alpha")
    matrices = bytes(header[20:20 + 64 * ((header[19] >> 1 & 1) + (header[19] & 1))])

    picture = frame[8 + len(header):]
    picture_header_size = picture[0] >> 3
    log2_size = (picture[7] >> 4) & 3
    width_in_mb, height_in_mb = (width + 15) // 16, (height + 15) // 16
    sizes = slice_sizes(width_in_mb, log2_size)
    count = height_in_mb * len(sizes)
    coded = struct.unpack(f">{count}H", picture[picture_header_size:picture_header_size + 2 * count])
    at = picture_header_size + 2 * count

    slices = []
    for index in range(count):
        data = picture[at:at + coded[index]]
        at += coded[index]
        header_size, old_index = data[0] >> 3, data[1]
        luma_size, cb_size = struct.unpack(">HH", data[2:6])
        parts = [data[header_size:header_size + luma_size], data[header_size + luma_size:header_size + luma_size + cb_size],
                 data[header_size + luma_size + cb_size:]]
        new_index = old_index
        if options.quantization_index:
            new_index = options.quantization_index[counter[0] % len(options.quantization_index)]
            counter[0] += 1
        written = []
        for component, part in enumerate(parts):
            blocks = sizes[index % len(sizes)] * (4 if component == 0 else 2)
            coefficients = read_coefficients(part, blocks)
            if new_index != old_index:
                for n, value in enumerate(coefficients):
                    scaled = abs(value) * scale_of(old_index) // scale_of(new_index)
                    coefficients[n] = scaled if value > 0 else -scaled
            written.append(write_coefficients(coefficients, blocks) + bytes(options.zero_bytes))
        slices.append(bytes([6 << 3, new_index]) + struct.pack(">HH", len(written[0]), len(written[1])) + b"".join(written))

    if options.version is not None:
        header[3] = options.version
    if options.aspect is not None:
        header[13] = (options.aspect << 4) | (header[13] & 15)
    if options.colour:
        header[14:17] = bytes(options.colour)
    new_header = bytes(header[:20]) + matrices + bytes([0xA5]) * options.extra[0]
    new_header = struct.pack(">H", len(new_header)) + new_header[2:]

    extra = bytes([0xA5]) * options.extra[1]
    table = b"".join(struct.pack(">H", len(s)) for s in slices)
    picture_size = 8 + len(extra) + len(table) + sum(map(len, slices))
    new_picture = (bytes([(8 + len(extra)) << 3]) + struct.pack(">IH", picture_size, count if count <= 65535 else 0)
                   + bytes([log2_size << 4]) + extra + table + b"".join(slices))
    stuffing = len(frame) - 8 - len(new_header) - len(new_picture)
    if stuffing < 0:
        sys.exit(f"a rewritten frame is {-stuffing} bytes larger than the frame it replaces")
    return struct.pack(">I4s", len(frame), b"icpf") + new_header + new_picture + bytes(stuffing)


def numbers(text):
    return [int(n) for n in text.split(",")]


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("source")
    parser.add_argument("output")
    parser.add_argument("--quantization-index", type=numbers)
    parser.add_argument("--version", type=int)
    parser.add_argument("--zero-bytes", type=int, default=0, choices=range(4))
    parser.add_argument("--extra", type=numbers, default=[0, 0])
    parser.add_argument("--aspect", type=int)
    parser.add_argument("--colour", type=numbers)
    options = parser.parse_args()

    probed = subprocess.run(
        ["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pos,size", "-of", "json", options.source],
        capture_output=True, text=True, check=True).stdout
    with open(options.source, "rb") as f:
        data = bytearray(f.read())
    counter = [0]
    for packet in json.loads(probed)["packets"]:
        position, size = int(packet["pos"]), int(packet["size"])
        data[position:position + size] = rewrite(bytes(data[position:position + size]), options, counter)
    with open(options.output, "wb") as f:
        f.write(data)


if __name__ == "__main__":
    main()
