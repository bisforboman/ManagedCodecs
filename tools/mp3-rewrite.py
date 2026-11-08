#!/usr/bin/env python3
"""Rewrites a raw MP3 (Layer III) stream to use what no encoder at hand writes, for make-samples.sh.

  mp3-rewrite.py in.mp3 out.mp3 intensity   sets mode_extension's intensity stereo bit in every joint stereo frame:
                                            the bands above the last the right channel codes a value in become
                                            intensity stereo, their positions the right channel's scalefactors.
  mp3-rewrite.py in.mp3 out.mp3 mixed       sets mixed_block_flag in MPEG-1's switches to short blocks, each
                                            channel's start block, short blocks and stop block together (as the
                                            ISO/IEC 11172-4 streams have them), where every short block's first
                                            scalefactors have no bits (scalefac_compress below 4): a mixed block
                                            codes one band of them fewer, so only there does the rest still parse as
                                            coded. Start and stop blocks then take the normal window in their two
                                            lowest subbands; nothing else of theirs changes. (At the lower sampling
                                            frequencies a mixed block also moves the first Huffman region's end, so
                                            none is rewritten.)

Every frame stays the length it was; the stream must have no ID3v2 tag or Xing frame (-id3v2_version 0 -write_xing 0).
"""
import sys

BITRATES = [[0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320],
            [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160]]
RATES = {3: [44100, 48000, 32000], 2: [22050, 24000, 16000], 0: [11025, 12000, 8000]}


def frames(data):
    """Each frame's offset, header and whether it is MPEG-1."""
    i = 0
    while i + 4 <= len(data):
        h = int.from_bytes(data[i:i + 4], 'big')
        if h >> 21 != 0x7FF:
            sys.exit(f'No frame header at byte {i}.')
        version = (h >> 19) & 3
        bitrate = BITRATES[0 if version == 3 else 1][(h >> 12) & 15] * 1000
        if bitrate == 0:
            sys.exit('Free format is not supported.')
        yield i, h, version == 3
        i += (144 if version == 3 else 72) * bitrate // RATES[version][(h >> 10) & 3] + ((h >> 9) & 1)


def granules(data, offset, h):
    """An MPEG-1 frame's granules in side information order: channel, block type, scalefac_compress and the bit
    position (from the frame's start) of mixed_block_flag."""
    side = 4 + (0 if (h >> 16) & 1 else 2)
    channels = 1 if (h >> 6) & 3 == 3 else 2
    bits = ''.join(f'{b:08b}' for b in data[offset + side:offset + side + 32])
    position = 9 + (5 if channels == 1 else 3) + 4 * channels  # main_data_begin, private bits, scfsi
    for _ in range(2):
        for channel in range(channels):
            compress = position + 12 + 9 + 8  # after part2_3_length, big_values, global_gain
            switching = compress + 4
            block_type = int(bits[switching + 1:switching + 3], 2) if bits[switching] == '1' else 0
            yield channel, block_type, int(bits[compress:switching], 2), 8 * side + switching + 3
            # window switching: block_type, mixed flag, two tables, three subblock gains; else three tables and the
            # two region counts; then preflag, scalefac_scale and count1table_select.
            position = switching + 1 + (2 + 1 + 10 + 9 if bits[switching] == '1' else 15 + 4 + 3) + 3


def main(source, target, what):
    data = bytearray(open(source, 'rb').read())
    changed = 0
    runs = {0: [], 1: []}  # each channel's granules since its last start block: (frame offset, mixed flag bit)
    qualifies = {0: True, 1: True}
    for offset, h, mpeg1 in list(frames(data)):
        if what == 'intensity' and (h >> 6) & 3 == 1:
            data[offset + 3] |= 0x10
            changed += 1
        elif what == 'mixed' and mpeg1:
            for channel, block_type, compress, flag in list(granules(data, offset, h)):
                if block_type == 1:
                    runs[channel], qualifies[channel] = [(offset, flag)], True
                elif block_type == 2 and runs[channel]:
                    runs[channel].append((offset, flag))
                    qualifies[channel] &= compress < 4
                elif block_type == 3 and runs[channel]:
                    if qualifies[channel]:
                        for frame, bit in runs[channel] + [(offset, flag)]:
                            data[frame + bit // 8] |= 0x80 >> (bit % 8)
                            changed += 1
                    runs[channel] = []
                else:
                    runs[channel] = []
    open(target, 'wb').write(data)
    print(f'{target}: {changed} frames or granules rewritten.')


if __name__ == '__main__':
    if len(sys.argv) != 4 or sys.argv[3] not in ('intensity', 'mixed'):
        sys.exit(__doc__)
    main(*sys.argv[1:])
