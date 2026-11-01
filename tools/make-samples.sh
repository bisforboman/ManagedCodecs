#!/usr/bin/env bash
# Generates the test media in samples/ and FFmpeg's reference output for each file; the conformance tests
# compare our decoders and demuxers against these. Needs ffmpeg/ffprobe (with libx264) and ImageMagick on PATH.
# Run from the repository root: tools/make-samples.sh (on Windows: Git Bash or WSL). Takes a minute or two.
# Each sample isolates something; samples/README.md says what the references are and how the tests pick samples.
set -euo pipefail

cd "$(dirname "$0")/.."
mkdir -p samples
# A previous run's copies of samples/jm-ref (below), which FFmpeg's references for every sample would fail on.
for f in samples/jm-ref/*; do rm -f "samples/${f##*/}"; done
ff() { ffmpeg -hide_banner -loglevel error -y "$@"; }
magick_cmd=$(command -v magick || command -v convert)

# ---- MJPEG in AVI ----
# 4:2:0, the most common MJPEG layout; 4:2:2, common from webcams; an odd size, for partial MCUs at the edges; 4:4:4
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 2 -c:v mjpeg -pix_fmt yuvj420p -q:v 3 samples/testsrc-420.avi
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 2 -c:v mjpeg -pix_fmt yuvj422p -q:v 3 samples/testsrc-422.avi
ff -f lavfi -i testsrc2=size=321x239:rate=25 -t 1 -c:v mjpeg -pix_fmt yuvj420p -q:v 3 samples/testsrc-odd.avi
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 1 -c:v mjpeg -pix_fmt yuvj444p -q:v 3 samples/testsrc-444.avi
# Restart markers: FFmpeg writes DRI + RSTn when it encodes with slices
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 1 -c:v mjpeg -pix_fmt yuvj422p -q:v 3 -threads 4 -slices 4 samples/testsrc-rst.avi
# Annex K Huffman tables; the conformance tests also decode this one with DHT stripped
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 1 -c:v mjpeg -pix_fmt yuvj420p -q:v 3 -huffman default samples/testsrc-stdhuff.avi
# The samples' meaning (name.color): limited range, which FFmpeg's encoder marks with a "CS=ITU601" comment (and its
# decoder then gives yuv420p, tv), and a JFIF density of 2:1, the frames' aspect ratio.
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 0.2 -c:v mjpeg -pix_fmt yuv420p -strict -1 -q:v 3 samples/mjpeg-limited-range.avi
ff -f lavfi -i testsrc2=size=320x240:rate=25,setsar=2/1 -t 0.2 -c:v mjpeg -pix_fmt yuvj420p -q:v 3 samples/mjpeg-sar.avi
# The AVI's 'vprp' against the JFIF density (tools/avi-vprp.py; FFmpeg's muxer writes both from one ratio): a picture of
# 16:9, a sample ratio of 4:3 at 320x240, over JFIF's 2:1. ffprobe shows the container's, and so do the frames.
cp samples/mjpeg-sar.avi samples/mjpeg-vprp-conflict.avi
python3 tools/avi-vprp.py samples/mjpeg-vprp-conflict.avi 16 9
# Another FourCC than MJPG, as capture cards and editors write (dmb1, Matrox): the AVI demuxer maps the ones FFmpeg's
# reads as MJPEG (AviDemuxerTests lists them), here in lower case as written.
ff -i samples/mjpeg-sar.avi -c copy -tag:v dmb1 samples/mjpeg-fourcc-dmb1.avi
# Noise at the highest quality: large coefficients exercise the IDCT's full range
ff -f lavfi -i testsrc2=size=320x240:rate=25,noise=alls=60:allf=t -t 1 -c:v mjpeg -pix_fmt yuvj420p -q:v 1 samples/noise-q1.avi
# A dropped frame: with MP3 alongside, FFmpeg's muxer writes an empty '00dc' chunk after the first frame while
# interleaving the audio. FFmpeg's demuxer counts it, giving no packet for it but pts 0, 2, 3, ... after
# (AviDemuxerTests checks the gap is there). Small, under the 64 KiB a fuzzing seed may have (tools/fuzz.sh).
ff -f lavfi -i testsrc2=size=64x48:rate=25:duration=1,format=yuvj420p -f lavfi -i sine=duration=1.5 -c:v mjpeg -q:v 5 \
  -c:a libmp3lame -b:a 32k samples/avi-dropped-frame.avi
# Grayscale (FFmpeg's MJPEG encoder has no gray output, so ImageMagick encodes the frames)
frames=$(mktemp -d)
ff -f lavfi -i testsrc2=size=322x238:rate=25 -t 1 -pix_fmt gray "$frames/f%02d.png"
"$magick_cmd" "$frames"/f*.png -colorspace Gray -type Grayscale -quality 90 "$frames/f%02d.jpg"
ff -framerate 25 -i "$frames/f%02d.jpg" -c:v copy samples/gray.avi
rm -rf "$frames"
# 4:1:1 and 4:4:0 (cameras' layouts FFmpeg's encoder doesn't write; ImageMagick encodes the frames): mjpeg-411 with
# luma 4x1 and chroma 1x1 (yuvj411p), mjpeg-440 1x2 and 1x1 (yuvj440p), mjpeg-440-2x2 the same ratio as 2x2 and 2x1.
# 322x238 so the quarter-wide chroma rounds up (81 of 80.5) and the MCUs overhang the picture.
frames=$(mktemp -d)
ff -f lavfi -i testsrc2=size=322x238:rate=25 -t 0.2 "$frames/f%02d.png"
for layout in 411:4x1,1x1,1x1 440:1x2,1x1,1x1 440-2x2:2x2,2x1,2x1; do
  mkdir "$frames/${layout%%:*}"
  for png in "$frames"/f*.png; do
    "$magick_cmd" "$png" -sampling-factor "${layout#*:}" -quality 90 "$frames/${layout%%:*}/$(basename "${png%.png}").jpg"
  done
  ff -framerate 25 -i "$frames/${layout%%:*}/f%02d.jpg" -c:v copy "samples/mjpeg-${layout%%:*}.avi"
done
rm -rf "$frames"
# Interlaced MJPEG (AVI1, as capture cards write it): each packet holds a frame's two fields as two JPEG images, each
# with an AVI1 APP0 segment, in a stream twice their height. FFmpeg's encoder refuses interlacing, so it encodes the
# fields of a top-field-first source as images of their own, and Python pairs them into packets (field bytes 1, 2)
# in an AVI of 320x240. FFmpeg's decoder weaves them, the first field on the odd rows (the order it takes for AVI,
# which does not tell); 4:2:0 and 4:2:2 fields.
for chroma in 420 422; do
  fields=$(mktemp)
  ff -f lavfi -i testsrc2=size=320x240:rate=50 -t 0.8 -vf "tinterlace=interleave_top,setfield=tff,separatefields,format=yuvj${chroma}p" \
    -c:v mjpeg -q:v 3 -f avi "$fields"
  python3 -I - "$fields" "samples/mjpeg-interlaced-$chroma.avi" <<'PY'
import struct, sys
data = open(sys.argv[1], 'rb').read()

def chunks(buf, start, end):
    i = start
    while i + 8 <= end:
        fourcc, size = buf[i:i + 4], struct.unpack_from('<I', buf, i + 4)[0]
        yield fourcc, i + 8, size
        i += 8 + size + (size & 1)

# The source's fields (one JPEG a chunk), size and rate.
movi = data.find(b'movi') + 4
end = movi - 4 + struct.unpack_from('<I', data, movi - 8)[0]
fields = [data[o:o + n] for c, o, n in chunks(data, movi, end) if c == b'00dc']
width, field_height = struct.unpack_from('<ii', data, data.find(b'strf') + 12)
scale, rate = struct.unpack_from('<II', data, data.find(b'strh') + 28)
height = 2 * field_height

def with_app0(jpeg, field):
    # APP0 'AVI1': the field byte (1 odd, 2 even) and reserved bytes, after SOI.
    return jpeg[:2] + b'\xff\xe0' + struct.pack('>H', 16) + b'AVI1' + bytes([field]) + bytes(9) + jpeg[2:]

frames = [with_app0(fields[i], 1) + with_app0(fields[i + 1], 2) for i in range(0, len(fields) - 1, 2)]

def chunk(fourcc, payload):
    return fourcc + struct.pack('<I', len(payload)) + payload + (b'\0' if len(payload) & 1 else b'')

def lst(kind, payload):
    return b'LIST' + struct.pack('<I', len(payload) + 4) + kind + payload

biggest = max(map(len, frames))
avih = struct.pack('<14I', int(1e6 * scale * 2 / rate), 0, 0, 0x10, len(frames), 0, 1, biggest, width, height, 0, 0, 0, 0)
strh = b'vidsMJPG' + struct.pack('<IHHIIIIIIIIhhhh', 0, 0, 0, 0, scale * 2, rate, 0, len(frames), biggest, 0xFFFFFFFF, 0, 0, 0, width, height)
strf = struct.pack('<IiiHH4sIiiII', 40, width, height, 1, 24, b'MJPG', width * height * 3, 0, 0, 0, 0)
hdrl = lst(b'hdrl', chunk(b'avih', avih) + lst(b'strl', chunk(b'strh', strh) + chunk(b'strf', strf)))
index, offset = b'', 4
for f in frames:
    index += b'00dc' + struct.pack('<III', 0x10, offset, len(f))
    offset += 8 + len(f) + (len(f) & 1)
body = b'AVI ' + hdrl + lst(b'movi', b''.join(chunk(b'00dc', f) for f in frames)) + chunk(b'idx1', index)
open(sys.argv[2], 'wb').write(b'RIFF' + struct.pack('<I', len(body)) + body)
PY
  rm "$fields"
done

# Damaged frames as cameras and capture cards write them, FFmpeg's decoder the oracle (src/ManagedCodecs.Mjpeg/CLAUDE.md):
# testsrc-420's frames, rewritten in Python. A frame cut short or damaged mid-scan is the second of three, between
# clean ones: FFmpeg leaves what such a scan does not reach as its frame buffer was, 0 in a buffer it has not used
# before (its second frame's) but an earlier frame's samples in a reused one, where the decoder leaves 0. mjpeg-cut:
# cut at 2/3, no EOI; mjpeg-cut-eoi: cut at 1/3, an EOI after it; mjpeg-cut-stuffed: cut after the 0xFF of a stuffed
# pair (FF 00); mjpeg-cut-ff-eoi: the same with an EOI after it (a fill byte before the marker, FF FF D9);
# mjpeg-cut-stuffed-eoi: cut after a stuffed pair, an EOI after it. mjpeg-junk: frames that decode whole,
# bytes before the SOI marker (the second) and no EOI marker (the third). (No sample of a byte damaged mid-scan: FFmpeg
# decodes on through the codes that are not valid, its tables' own reading, so it matches only up to the first such
# code; MjpegDecoderTests covers it.)
damaged=$(mktemp -d)
ff -i samples/testsrc-420.avi -frames:v 4 -c:v copy -f image2 "$damaged/f%02d.jpg"
python3 -I - "$damaged" <<'PY'
import os, sys
folder = sys.argv[1]
frames = [open(os.path.join(folder, f'f{i:02d}.jpg'), 'rb').read() for i in range(1, 5)]
def at(d, fraction):
    sos = d.find(b'\xff\xda')
    start = sos + 2 + int.from_bytes(d[sos + 2:sos + 4], 'big')
    return start + int((len(d) - 2 - start) * fraction)
def stuffed(d, fraction):
    # The 0xFF of the first stuffed pair (FF 00) from there on.
    return d.index(b'\xff\x00', at(d, fraction))
samples = {
    'mjpeg-cut': [frames[0], frames[1][:at(frames[1], 2 / 3)], frames[2]],
    'mjpeg-cut-eoi': [frames[0], frames[1][:at(frames[1], 1 / 3)] + b'\xff\xd9', frames[2]],
    'mjpeg-cut-stuffed': [frames[0], frames[1][:stuffed(frames[1], 1 / 2) + 1], frames[2]],
    'mjpeg-cut-ff-eoi': [frames[0], frames[1][:stuffed(frames[1], 1 / 2) + 1] + b'\xff\xd9', frames[2]],
    'mjpeg-cut-stuffed-eoi': [frames[0], frames[1][:stuffed(frames[1], 1 / 2) + 2] + b'\xff\xd9', frames[2]],
    'mjpeg-junk': [frames[0], b'\0\0\0\0camera' + frames[1], frames[2][:-2], frames[3]],
}
for name, packets in samples.items():
    os.makedirs(os.path.join(folder, name))
    for i, packet in enumerate(packets):
        open(os.path.join(folder, name, f'f{i + 1:02d}.jpg'), 'wb').write(packet)
PY
for name in mjpeg-cut mjpeg-cut-eoi mjpeg-cut-stuffed mjpeg-cut-ff-eoi mjpeg-cut-stuffed-eoi mjpeg-junk; do
  ff -framerate 25 -i "$damaged/$name/f%02d.jpg" -c:v copy "samples/$name.avi"
done
rm -r "$damaged"

# ---- MP4 for the demuxer ----
# The MJPEG remux is decoded end to end; H.264 with B-frames needs the edit list right, MPEG-4 Part 2 carries its
# codec private data in esds, faststart puts moov before mdat, and the delayed one starts with an empty edit.
ff -i samples/testsrc-420.avi -c copy samples/mjpeg-420.mp4
# MJPEG with a 'colr' (name.color): its primaries and transfer reach the frames, its range and matrix do not (the
# JPEG decoder's own).
ff -i samples/testsrc-420.avi -c copy -color_primaries bt709 -color_trc smpte2084 -colorspace bt709 -color_range tv \
  samples/mjpeg-colr.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 2 -c:v libx264 -bf 2 -g 12 samples/h264-bframes.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=30000/1001 -t 2 -c:v libx264 -bf 0 -movflags +faststart samples/h264-faststart.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 1 -c:v mpeg4 -bf 2 samples/mpeg4-bframes.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 1 -c:v libx264 -bf 0 -output_ts_offset 0.4 samples/h264-delayed.mp4
# Fragmented MP4 (moof/traf/trun) with H.264 (B frames) and AAC: an empty moov and a fragment per keyframe, with data
# offsets from an explicit base; the same with default-base-is-moof and fragments every 0.4 s; and the first fragment's
# samples in the moov's own tables, the rest in fragments. None has an edit list.
FRAG=(-f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.01" -f lavfi -i "sine=frequency=330:sample_rate=48000:duration=2" -t 2
  -c:v libx264 -profile:v main -pix_fmt yuv420p -bf 2 -g 12 -c:a aac -ac 2)
ff "${FRAG[@]}" -movflags frag_keyframe+empty_moov samples/fmp4-empty-moov.mp4
ff "${FRAG[@]}" -movflags frag_keyframe+empty_moov+default_base_moof -frag_duration 400000 samples/fmp4-base-moof.mp4
ff "${FRAG[@]}" -movflags frag_keyframe samples/fmp4-first-in-moov.mp4
# Version 1 track runs with negative composition offsets (-512 for the B frames).
ff "${FRAG[@]}" -movflags frag_keyframe+empty_moov+negative_cts_offsets samples/fmp4-negative-cts.mp4
# The same offsets in the moov's own 'ctts' (version 1, -512 for the B frames), unfragmented: there FFmpeg moves every dts
# back by the most negative one, where fragments move every pts forward (and its edit list is in the pts' times).
ff -f lavfi -i "testsrc2=size=320x240:rate=25" -t 2 -c:v libx264 -bf 2 -movflags negative_cts_offsets samples/h264-negative-ctts.mp4
# Its last entry made more negative than the shift (which leaves the last two entries out): that sample's pts comes
# before its dts, which FFmpeg reports as invalid and keeps for H.264 (it fills a dts in from the pts only for codecs
# without reordering).
cp samples/h264-negative-ctts.mp4 samples/h264-negative-ctts-last.mp4
python3 -I - samples/h264-negative-ctts-last.mp4 <<'PY'
import struct, sys
data = bytearray(open(sys.argv[1], 'rb').read())
at = data.find(b'ctts')
assert at > 0 and data[at + 4] == 1  # version 1: signed offsets
entries = struct.unpack_from('>I', data, at + 8)[0]
lowest = min(struct.unpack_from('>i', data, at + 16 + 8 * e)[0] for e in range(entries))
struct.pack_into('>i', data, at + 16 + 8 * (entries - 1), lowest - 2048)
open(sys.argv[1], 'wb').write(data)
PY

# ---- H.264 Constrained Baseline ----
# Intra only without the deblocking filter (at several QPs), intra with the filter and with filter offsets, P frames
# with one reference and no filter, then several references with all partition sizes, fast motion past the picture
# edges, multiple slices, an odd size (frame cropping), constrained intra prediction with a chroma QP offset, and a
# long GOP where frame_num wraps. The 720p one is for benchmarking.
B=(-c:v libx264 -profile:v baseline -pix_fmt yuv420p)
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 0.2 "${B[@]}" -g 1 -x264-params no-deblock=1 samples/h264-intra.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25,noise=alls=40:allf=t -t 0.2 "${B[@]}" -g 1 -qp 4 -x264-params no-deblock=1 samples/h264-intra-qp4.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 0.2 "${B[@]}" -g 1 -qp 51 -x264-params no-deblock=1 samples/h264-intra-qp51.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 0.2 "${B[@]}" -g 1 samples/h264-intra-deblock.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25,noise=alls=20:allf=t -t 0.2 "${B[@]}" -g 1 -qp 36 -x264-params deblock=2,-1 samples/h264-intra-deblock-offsets.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 1 "${B[@]}" -g 50 -refs 1 -x264-params no-deblock=1 samples/h264-p-ref1-nodeblock.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 2 "${B[@]}" -refs 4 -x264-params partitions=all samples/h264-p-refs.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.013:v=-0.021,noise=alls=12:allf=t" -t 2 "${B[@]}" -refs 3 -qp 22 -x264-params partitions=all:me=umh:merange=32 samples/h264-p-motion.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.01" -t 1 "${B[@]}" -refs 2 -x264-params slices=4 samples/h264-slices.mp4
ff -f lavfi -i "testsrc2=size=321x239:rate=25,scroll=v=0.02" -t 1 "${B[@]}" -refs 2 samples/h264-odd-size.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.02,noise=alls=10:allf=t" -t 1 "${B[@]}" -refs 2 -x264-params constrained-intra=1:chroma-qp-offset=4 samples/h264-constrained-intra.mp4
ff -f lavfi -i "testsrc2=size=176x144:rate=25,scroll=h=0.01" -t 3 "${B[@]}" -refs 4 -g 1000 samples/h264-long-gop.mp4
ff -f lavfi -i "testsrc2=size=1280x720:rate=25,scroll=h=0.005,noise=alls=6:allf=t" -t 2 "${B[@]}" -refs 3 samples/h264-bench-720p.mp4

# ---- H.264 Main profile (CABAC), without B frames ----
# The same cases with CABAC: intra only (also at QP 4 on noise, for large levels and the Exp-Golomb suffixes), P frames
# with several references and all partitions, fast motion, slices, an odd size, constrained intra prediction and a
# long GOP. Weighted prediction is off (Main turns it on by default). libx264 always writes cabac_init_idc 0.
M=(-c:v libx264 -profile:v main -pix_fmt yuv420p -bf 0 -weightp 0)
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 0.2 "${M[@]}" -g 1 samples/h264-main-intra.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25,noise=alls=40:allf=t -t 0.2 "${M[@]}" -g 1 -qp 4 samples/h264-main-intra-qp4.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 2 "${M[@]}" -refs 4 -x264-params partitions=all samples/h264-main-p-refs.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.013:v=-0.021,noise=alls=12:allf=t" -t 2 "${M[@]}" -refs 3 -qp 22 -x264-params partitions=all:me=umh:merange=32 samples/h264-main-p-motion.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.01" -t 1 "${M[@]}" -refs 2 -x264-params slices=4 samples/h264-main-slices.mp4
ff -f lavfi -i "testsrc2=size=321x239:rate=25,scroll=v=0.02" -t 1 "${M[@]}" -refs 2 samples/h264-main-odd-size.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.02,noise=alls=10:allf=t" -t 1 "${M[@]}" -refs 2 -x264-params constrained-intra=1:chroma-qp-offset=4 samples/h264-main-constrained-intra.mp4
ff -f lavfi -i "testsrc2=size=176x144:rate=25,scroll=h=0.01" -t 3 "${M[@]}" -refs 4 -g 1000 samples/h264-main-long-gop.mp4

# ---- H.264 B frames (Main profile) ----
# CAVLC first: B frames without direct prediction (lists, bi-prediction, reordering, B deblocking), then spatial and
# temporal direct prediction with all partitions. Weighted bi-prediction is off (libx264's default is implicit).
BM=(-c:v libx264 -profile:v main -pix_fmt yuv420p -weightp 0)
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.01" -t 1 "${BM[@]}" -bf 2 -refs 2 -x264-params cabac=0:direct=none:weightb=0:b-pyramid=none samples/h264-b-nodirect-cavlc.mp4
for d in spatial temporal; do
  ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.01:v=0.005" -t 1 "${BM[@]}" -bf 3 -refs 3 -x264-params cabac=0:direct=$d:weightb=0:b-pyramid=none:partitions=all samples/h264-b-$d-cavlc.mp4
done
# The same three with CABAC.
for d in none spatial temporal; do
  ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.01:v=0.005" -t 1 "${BM[@]}" -bf 3 -refs 3 -x264-params direct=$d:weightb=0:b-pyramid=none:partitions=all samples/h264-b-$d.mp4
done
# B pictures as references (b-pyramid, which also brings memory management operation 1), fast motion with
# noise and direct=auto, slices, an odd size and a long GOP.
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.012:v=-0.007" -t 2 "${BM[@]}" -bf 3 -refs 4 -x264-params weightb=0:b-pyramid=normal:partitions=all samples/h264-b-pyramid.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.013:v=-0.021,noise=alls=12:allf=t" -t 2 "${BM[@]}" -bf 3 -refs 3 -qp 22 -x264-params weightb=0:partitions=all:me=umh:merange=32:direct=auto samples/h264-b-motion.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.01" -t 1 "${BM[@]}" -bf 2 -refs 2 -x264-params weightb=0:slices=4 samples/h264-b-slices.mp4
# Direct prediction without direct_8x8_inference_flag (each 4x4 block takes its own co-located motion), which libx264
# never writes: the temporal and the motion (direct=auto: spatial and temporal slices) samples with the flag cleared
# in the SPS, which changes 9 and 37 of their frames (in libx264's spatial-only samples a still 4x4 block's corner
# block is still too, so nothing changes). In Main profile (no 8x8 transform) the flag changes only how direct
# motion is derived, not the parse, so FFmpeg decodes the same stream to its own reference; both levels are below
# 3.0, where Table A-4 would require the flag.
for d in temporal motion; do
  python3 -I - samples/h264-b-$d.mp4 samples/h264-b-no8x8inference-$d.mp4 <<'PY'
import sys
data = bytearray(open(sys.argv[1], 'rb').read())
at = data.find(b'avcC') + 4
assert data[at] == 1 and data[at + 5] & 0x1f == 1  # one SPS
length = int.from_bytes(data[at + 6:at + 8], 'big')
nal = data[at + 8:at + 8 + length]
rbsp = bytearray(nal[1:].replace(b'\x00\x00\x03', b'\x00\x00'))
assert rbsp[0] == 77 and rbsp[2] < 30  # Main profile, level below 3.0

pos = 24  # bits: profile_idc, constraint flags, level_idc
def bit():
    global pos
    pos += 1
    return (rbsp[(pos - 1) >> 3] >> (7 - ((pos - 1) & 7))) & 1
def ue():
    zeros = 0
    while bit() == 0:
        zeros += 1
    return (1 << zeros) - 1 + sum(bit() << (zeros - 1 - i) for i in range(zeros))
ue(); ue()  # seq_parameter_set_id, log2_max_frame_num_minus4
poc_type = ue()
assert poc_type == 0
ue()  # log2_max_pic_order_cnt_lsb_minus4
ue(); bit(); ue(); ue()  # max_num_ref_frames, gaps flag, width and height in macroblocks
assert bit() == 1  # frame_mbs_only_flag
assert (rbsp[pos >> 3] >> (7 - (pos & 7))) & 1 == 1
rbsp[pos >> 3] &= ~(1 << (7 - (pos & 7)) & 0xff)  # direct_8x8_inference_flag = 0

# Back to a NAL unit with emulation prevention; the length must not change (no box sizes to fix).
out = bytearray([nal[0]])
zeros = 0
for b in rbsp:
    if zeros >= 2 and b <= 3:
        out.append(3)
        zeros = 0
    out.append(b)
    zeros = zeros + 1 if b == 0 else 0
assert len(out) == length
data[at + 8:at + 8 + length] = out
open(sys.argv[2], 'wb').write(data)
PY
done
ff -f lavfi -i "testsrc2=size=321x239:rate=25,scroll=v=0.02" -t 1 "${BM[@]}" -bf 2 -refs 2 -x264-params weightb=0 samples/h264-b-odd-size.mp4
ff -f lavfi -i "testsrc2=size=176x144:rate=25,scroll=h=0.01" -t 3 "${BM[@]}" -bf 3 -refs 4 -g 1000 -x264-params weightb=0:b-pyramid=normal samples/h264-b-long-gop.mp4

# ---- H.264 weighted prediction ----
# libx264's own Main defaults (explicit weights for P frames on fades, implicit weights for B frames), with CABAC
# and with CAVLC; explicit weights alone (P frames only); implicit weights alone (no fade, -weightp 0).
F="testsrc2=size=320x240:rate=25,fade=in:0:20,fade=out:30:20"
ff -f lavfi -i "$F" -t 2 -c:v libx264 -profile:v main -pix_fmt yuv420p samples/h264-wp-default.mp4
ff -f lavfi -i "$F" -t 2 -c:v libx264 -profile:v main -pix_fmt yuv420p -x264-params cabac=0 samples/h264-wp-default-cavlc.mp4
ff -f lavfi -i "$F" -t 2 -c:v libx264 -profile:v main -pix_fmt yuv420p -bf 0 -refs 3 samples/h264-wp-p.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.01" -t 2 -c:v libx264 -profile:v main -pix_fmt yuv420p -weightp 0 -bf 3 samples/h264-wp-implicit.mp4

# ---- H.264 High profile (8x8 transform, Intra 8x8) ----
# Flat scaling matrices only (libx264's default). Intra only with CABAC and CAVLC (also at QP 4 on noise, for large
# 8x8 levels), P frames with all partitions (8x8 transforms next to sub-8x8 partitions, which must use 4x4), B frames
# with CABAC and CAVLC, fast motion, slices, an odd size, constrained intra prediction with a chroma QP offset, and
# libx264's stock High settings. libx264 always writes the same offset for Cb and Cr.
H=(-c:v libx264 -profile:v high -pix_fmt yuv420p)
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 0.2 "${H[@]}" -g 1 samples/h264-high-intra.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 0.2 "${H[@]}" -g 1 -x264-params cabac=0 samples/h264-high-intra-cavlc.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25,noise=alls=40:allf=t -t 0.2 "${H[@]}" -g 1 -qp 4 samples/h264-high-intra-qp4.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25,noise=alls=40:allf=t -t 0.2 "${H[@]}" -g 1 -qp 4 -x264-params cabac=0 samples/h264-high-intra-qp4-cavlc.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.01:v=0.005" -t 1 "${H[@]}" -bf 0 -refs 3 -x264-params partitions=all samples/h264-high-p.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.01:v=0.005" -t 1 "${H[@]}" -bf 0 -refs 3 -x264-params cabac=0:partitions=all samples/h264-high-p-cavlc.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.01:v=0.005" -t 1 "${H[@]}" -bf 3 -refs 3 -x264-params partitions=all:direct=auto samples/h264-high-b.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.01:v=0.005" -t 1 "${H[@]}" -bf 3 -refs 3 -x264-params cabac=0:partitions=all:direct=auto samples/h264-high-b-cavlc.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.013:v=-0.021,noise=alls=12:allf=t" -t 2 "${H[@]}" -bf 3 -refs 3 -qp 22 -x264-params partitions=all:me=umh:merange=32 samples/h264-high-motion.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.01" -t 1 "${H[@]}" -bf 2 -refs 2 -x264-params slices=4 samples/h264-high-slices.mp4
ff -f lavfi -i "testsrc2=size=321x239:rate=25,scroll=v=0.02" -t 1 "${H[@]}" -bf 2 -refs 2 samples/h264-high-odd-size.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.02,noise=alls=10:allf=t" -t 1 "${H[@]}" -bf 2 -refs 2 -x264-params constrained-intra=1:chroma-qp-offset=4 samples/h264-high-constrained-intra.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,fade=in:0:20,fade=out:30:20" -t 2 "${H[@]}" samples/h264-high-default.mp4

# ---- H.264 colour description and sample aspect ratio (the VUI) ----
# Each frame's colour properties and aspect ratio (VideoFrame.ColorRange to SampleAspectRatio; name.color): BT.709 in
# limited range; BT.2020 with PQ in full range and the chroma at the top left (chroma_sample_loc_type 2); SMPTE 170M
# with HLG and a ratio of Table E-1 (16:11, aspect_ratio_idc 4); a ratio of its own (64:45, Extended_SAR). The other
# samples send no colour description (libx264's default) or no VUI at all (JM's).
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 0.2 "${H[@]}" -color_range tv -colorspace bt709 -color_primaries bt709 \
  -color_trc bt709 samples/h264-color-bt709.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 0.2 "${H[@]}" -color_range pc -colorspace bt2020nc -color_primaries bt2020 \
  -color_trc smpte2084 -x264-params chromaloc=2 samples/h264-color-bt2020-pq.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25,setsar=16/11 -t 0.2 "${H[@]}" -colorspace smpte170m -color_primaries smpte170m \
  -color_trc arib-std-b67 samples/h264-color-hlg-sar.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25,setsar=64/45 -t 0.2 "${H[@]}" samples/h264-sar-extended.mp4
# The container's say (VideoStreamInfo, and frames where the stream says nothing; name.display for the ratio and
# rotation): a 'colr' with no colour description in the VUI (frames take the container's), one against the VUI's (the
# VUI wins), a 'pasp' of 4:3 over a VUI of 64:45 (ffprobe shows the container's; tools/mp4-pasp.py, FFmpeg's muxer
# copies the VUI's), a track matrix turning by 90, 180 and 270 degrees, and MJPEG with a 'colr' (its primaries and
# transfer, which JPEG has none of; mjpeg-colr, above).
ff -i samples/h264-high-default.mp4 -c copy -color_primaries bt709 -color_trc bt709 -colorspace bt709 -color_range tv \
  samples/h264-colr-only.mp4
ff -i samples/h264-color-bt709.mp4 -c copy -color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc \
  -color_range pc samples/h264-colr-conflict.mp4
cp samples/h264-sar-extended.mp4 samples/h264-pasp-conflict.mp4
python3 tools/mp4-pasp.py samples/h264-pasp-conflict.mp4 4 3
for r in 90 180 270; do ff -display_rotation $r -i samples/h264-high-default.mp4 -c copy "samples/h264-rotate-$r.mp4"; done
# ---- H.264 High 4:2:2 ----
# 4:2:2 chroma (chroma_format_idc 2): 8x16 chroma blocks of eight 4x4 blocks, a 2x4 chroma DC transform at QP_C + 3,
# CAVLC's chroma DC tables for nC = -2 and CABAC's chroma DC contexts for 8 coefficients, plane and DC intra
# prediction over 16 chroma rows, chroma motion with quarter-sample vertical vectors, and chroma deblocking along 16
# rows and across four horizontal edges (with the 8x8 transform too). Intra with CABAC and CAVLC, intra at QP 4 on
# noise (large DC levels), 4x4 transforms only, P and B frames with every partition (CAVLC too), motion on noise, an
# odd size (cropping in 4:2:2 units), constrained intra with a chroma QP offset, and libx264's defaults on fades
# (explicit and implicit weights).
H2=(-c:v libx264 -profile:v high422 -pix_fmt yuv422p)
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 0.2 "${H2[@]}" -g 1 samples/h264-422-intra.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 0.2 "${H2[@]}" -g 1 -x264-params cabac=0 samples/h264-422-intra-cavlc.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25,noise=alls=40:allf=t -t 0.2 "${H2[@]}" -g 1 -qp 4 samples/h264-422-intra-qp4.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25,noise=alls=40:allf=t -t 0.2 "${H2[@]}" -g 1 -qp 4 -x264-params cabac=0 samples/h264-422-intra-qp4-cavlc.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.01:v=0.005" -t 1 "${H2[@]}" -bf 2 -refs 2 -x264-params 8x8dct=0 samples/h264-422-4x4.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.01:v=0.005" -t 1 "${H2[@]}" -bf 3 -refs 3 -x264-params partitions=all:direct=auto samples/h264-422-b.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.01:v=0.005" -t 1 "${H2[@]}" -bf 3 -refs 3 -x264-params cabac=0:partitions=all:direct=auto samples/h264-422-b-cavlc.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.013:v=-0.021,noise=alls=12:allf=t" -t 2 "${H2[@]}" -bf 3 -refs 3 -qp 22 -x264-params partitions=all:me=umh:merange=32 samples/h264-422-motion.mp4
ff -f lavfi -i "testsrc2=size=321x239:rate=25,scroll=v=0.02" -t 1 "${H2[@]}" -bf 2 -refs 2 samples/h264-422-odd-size.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.02,noise=alls=10:allf=t" -t 1 "${H2[@]}" -bf 2 -refs 2 -x264-params constrained-intra=1:chroma-qp-offset=4 samples/h264-422-constrained-intra.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,fade=in:0:20,fade=out:30:20" -t 2 "${H2[@]}" samples/h264-422-default.mp4

# ---- H.264 interlaced: MBAFF ----
# Interlaced content (two moving frames woven into one) with libx264's macroblock-adaptive frame/field coding: each
# macroblock pair is coded as a frame or a field pair, and testsrc2's still background next to its moving parts gives
# both kinds side by side and above one another (the conformance test checks every mixture occurs). CABAC with the 8x8
# transform and a 240-line picture cropped from 256 (crop units of 4 lines), CAVLC, CABAC with 4x4 blocks only, and
# slices with filter offsets on noise. -debug mb_type shows which pairs are field pairs ('=').
I=(-vf "tinterlace=interleave_top,setfield=tff" -c:v libx264 -profile:v high -pix_fmt yuv420p -flags +ildct+ilme)
ff -f lavfi -i testsrc2=size=320x240:rate=50 -t 0.4 "${I[@]}" -g 1 samples/h264-mbaff-intra.mp4
ff -f lavfi -i testsrc2=size=320x256:rate=50 -t 0.4 "${I[@]}" -g 1 -x264-params cabac=0 samples/h264-mbaff-intra-cavlc.mp4
ff -f lavfi -i testsrc2=size=320x256:rate=50 -t 0.4 "${I[@]}" -g 1 -x264-params 8x8dct=0 samples/h264-mbaff-intra-4x4.mp4
ff -f lavfi -i testsrc2=size=320x256:rate=50,noise=alls=10:allf=t -t 0.4 "${I[@]}" -g 1 -x264-params slices=4:deblock=2,-1 samples/h264-mbaff-intra-slices.mp4
# P slices: pairs skipped whole (their kind inferred from the neighbours) or only at the top (the kind sent with the
# bottom macroblock), field macroblocks predicting from either field of a reference frame, CAVLC, all partitions on
# noisy motion, and slices. libx264 writes no weighted prediction for interlaced video.
ff -f lavfi -i testsrc2=size=320x240:rate=50 -t 0.6 "${I[@]}" -bf 0 -refs 3 samples/h264-mbaff-p.mp4
ff -f lavfi -i testsrc2=size=320x256:rate=50 -t 0.6 "${I[@]}" -bf 0 -refs 3 -x264-params cabac=0 samples/h264-mbaff-p-cavlc.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=50,noise=alls=8:allf=t -t 1 "${I[@]}" -bf 0 -refs 4 -x264-params partitions=all:me=umh:merange=32 samples/h264-mbaff-p-motion.mp4
ff -f lavfi -i testsrc2=size=320x256:rate=50 -t 0.6 "${I[@]}" -bf 0 -refs 2 -x264-params slices=4:8x8dct=0 samples/h264-mbaff-p-slices.mp4
# B slices: temporal and spatial direct prediction in field and frame macroblocks, with co-located pairs of the
# other kind (the conformance test checks both occur), implicit weights from field order counts, B-pyramid on noisy
# motion, slices, and libx264's stock interlaced settings.
ff -f lavfi -i testsrc2=size=320x240:rate=50 -t 1 "${I[@]}" -bf 3 -refs 3 -x264-params direct=temporal samples/h264-mbaff-b-temporal.mp4
ff -f lavfi -i testsrc2=size=320x256:rate=50 -t 1 "${I[@]}" -bf 3 -refs 3 -x264-params direct=spatial:cabac=0 samples/h264-mbaff-b-spatial-cavlc.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=50,noise=alls=8:allf=t -t 2 "${I[@]}" -bf 3 -refs 4 -x264-params b-pyramid=normal:partitions=all:me=umh:direct=auto samples/h264-mbaff-b-pyramid.mp4
ff -f lavfi -i testsrc2=size=320x256:rate=50 -t 1 "${I[@]}" -bf 2 -refs 2 -x264-params slices=4:direct=temporal:cabac=0 samples/h264-mbaff-b-slices-temporal.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=50,fade=in:0:40 -t 2 "${I[@]}" samples/h264-mbaff-default.mp4
# Interlaced-capable parameter sets (frame_mbs_only_flag 0) on progressive frames, with P and B frames.
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.01" -t 1 "${H[@]}" -bf 2 -x264-params fake-interlaced=1 samples/h264-fake-interlaced.mp4
# Interlacing as FFmpeg reports it (VideoFrame.IsInterlaced, IsTopFieldFirst; H264Decoder.FieldOrder; name.interlacing).
# Bottom field first, for which libx264 still writes pic_struct 3 (top field first) in its picture timing SEI: the
# parser's field order follows pic_struct (tt), each frame's top_field_first the field order counts (bottom first).
ff -f lavfi -i testsrc2=size=320x240:rate=50 -t 0.6 -vf "tinterlace=interleave_bottom,setfield=bff" "${I[@]:2}" -bf 2 samples/h264-mbaff-bff.mp4
# With HRD parameters (nal-hrd): the picture timing SEI starts with the CPB and DPB delays (13 bits here), read with
# the SPS's lengths before pic_struct.
ff -f lavfi -i testsrc2=size=320x240:rate=50 -t 0.6 -vf "tinterlace=interleave_bottom,setfield=bff" "${I[@]:2}" -bf 2 \
  -b:v 500k -maxrate 500k -bufsize 1000k -x264-params nal-hrd=vbr samples/h264-mbaff-hrd.mp4
# Without the SEI (pic_struct_present_flag still set), bottom and top field first: the order counts alone, bb and tt,
# and interlaced because the frames are MBAFF frames.
ff -i samples/h264-mbaff-bff.mp4 -c copy -bsf:v filter_units=remove_types=6 samples/h264-mbaff-bff-nosei.mp4
ff -i samples/h264-mbaff-p.mp4 -c copy -bsf:v filter_units=remove_types=6 samples/h264-mbaff-nosei.mp4
# Every pic_struct, and ct_type overriding it, which libx264 writes none of beyond pic_struct 0 and 3
# (tools/h264-set-pic-struct.py): on MBAFF frames, whose order counts say top field first (the first, 4, makes the
# stream bb); and on progressive frames, where pic_struct 3 and 4 keep the previous frame's flag (soft telecine) and
# equal order counts leave top_field_first to pic_struct.
picstruct=$(mktemp -d)
ff -f lavfi -i testsrc2=size=320x240:rate=50 -t 0.64 "${I[@]}" -bf 0 -f h264 "$picstruct/mbaff.264"
python3 tools/h264-set-pic-struct.py "$picstruct/mbaff.264" "$picstruct/mbaff-set.264" 4 0 1 2 3 5 6 7 8 0:1 3:0 4:2 5:1 1:0 2:1 6
ff -r 25 -i "$picstruct/mbaff-set.264" -c copy samples/h264-mbaff-pic-struct.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 0.8 "${H[@]}" -bf 0 -x264-params pic-struct=1 -f h264 "$picstruct/frames.264"
python3 tools/h264-set-pic-struct.py "$picstruct/frames.264" "$picstruct/frames-set.264" 1 3 0 3 4 2 4 5 6 0 3:1 4 7 8 3 6:1
ff -r 25 -i "$picstruct/frames-set.264" -c copy samples/h264-pic-struct.mp4
# Smaller, pic_struct 3, 0, 3: decoded after h264-pic-struct by one decoder, its first frame is interlaced again, the
# previous frame's flag starting over at a new picture size (H264InterlacingTests).
ff -f lavfi -i testsrc2=size=160x128:rate=25 -t 0.12 "${H[@]}" -bf 0 -x264-params pic-struct=1 -f h264 "$picstruct/small.264"
python3 tools/h264-set-pic-struct.py "$picstruct/small.264" "$picstruct/small-set.264" 3 0 3
ff -r 25 -i "$picstruct/small-set.264" -c copy samples/h264-pic-struct-small.mp4
rm -rf "$picstruct"
# MBAFF in 4:2:2: field and frame pairs with 16 chroma rows a macroblock, so intra prediction gathers 16 rows of
# neighbours, and chroma deblocking meets the left and upper pairs line by line as luma does (each chroma line takes
# its luma line's bS) and filters the upper edge twice under a field pair. Intra with CABAC (cropped), CAVLC and 4x4
# transforms; P frames, also with all partitions on noise; B frames with temporal and with spatial direct (CAVLC);
# slices with filter offsets; and libx264's stock interlaced settings on a fade.
I2=(-vf "tinterlace=interleave_top,setfield=tff" -c:v libx264 -profile:v high422 -pix_fmt yuv422p -flags +ildct+ilme)
ff -f lavfi -i testsrc2=size=320x240:rate=50 -t 0.4 "${I2[@]}" -g 1 samples/h264-422-mbaff-intra.mp4
ff -f lavfi -i testsrc2=size=320x256:rate=50 -t 0.4 "${I2[@]}" -g 1 -x264-params cabac=0 samples/h264-422-mbaff-intra-cavlc.mp4
ff -f lavfi -i testsrc2=size=320x256:rate=50 -t 0.4 "${I2[@]}" -g 1 -x264-params 8x8dct=0 samples/h264-422-mbaff-intra-4x4.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=50 -t 0.6 "${I2[@]}" -bf 0 -refs 3 samples/h264-422-mbaff-p.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=50,noise=alls=8:allf=t -t 1 "${I2[@]}" -bf 0 -refs 4 -x264-params partitions=all:me=umh:merange=32 samples/h264-422-mbaff-p-motion.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=50 -t 1 "${I2[@]}" -bf 3 -refs 3 -x264-params direct=temporal samples/h264-422-mbaff-b-temporal.mp4
ff -f lavfi -i testsrc2=size=320x256:rate=50 -t 1 "${I2[@]}" -bf 3 -refs 3 -x264-params direct=spatial:cabac=0 samples/h264-422-mbaff-b-spatial-cavlc.mp4
ff -f lavfi -i testsrc2=size=320x256:rate=50,noise=alls=10:allf=t -t 1 "${I2[@]}" -bf 2 -refs 2 -x264-params slices=4:deblock=2,-1 samples/h264-422-mbaff-slices.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=50,fade=in:0:40 -t 2 "${I2[@]}" samples/h264-422-mbaff-default.mp4

# ---- H.264 10-bit (High 10, High 4:2:2) ----
# 10-bit samples: QP_Y from -12 (QpBdOffset), dequantization at QP'Y, Clip1 to 1023, deblocking thresholds and
# weighted-prediction offsets scaled by 4, 16-bit output. Intra with CABAC and CAVLC; at libx264's -qp 1 (QP_Y about
# -12) on noise, for negative luma and chroma QPs and levels long enough for CAVLC's level_prefix of 16 and more; P and
# B frames with every partition (CABAC and CAVLC); motion on noise; an odd size; constrained intra with a chroma QP
# offset; libx264's defaults on fades (explicit weights with offsets, implicit weights); 4:2:2 at 10 bits; and MBAFF.
T=(-c:v libx264 -profile:v high10 -pix_fmt yuv420p10le)
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 0.2 "${T[@]}" -g 1 samples/h264-10bit-intra.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 0.2 "${T[@]}" -g 1 -x264-params cabac=0 samples/h264-10bit-intra-cavlc.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25,noise=alls=40:allf=t -t 0.2 "${T[@]}" -g 1 -qp 1 samples/h264-10bit-intra-qp1.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25,noise=alls=40:allf=t -t 0.2 "${T[@]}" -g 1 -qp 1 -x264-params cabac=0 samples/h264-10bit-intra-qp1-cavlc.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.01:v=0.005" -t 1 "${T[@]}" -bf 3 -refs 3 -x264-params partitions=all:direct=auto samples/h264-10bit-b.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.01:v=0.005" -t 1 "${T[@]}" -bf 3 -refs 3 -x264-params cabac=0:partitions=all:direct=auto samples/h264-10bit-b-cavlc.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.013:v=-0.021,noise=alls=12:allf=t" -t 2 "${T[@]}" -bf 3 -refs 3 -qp 34 -x264-params partitions=all:me=umh:merange=32 samples/h264-10bit-motion.mp4
ff -f lavfi -i "testsrc2=size=321x239:rate=25,scroll=v=0.02" -t 1 "${T[@]}" -bf 2 -refs 2 samples/h264-10bit-odd-size.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.02,noise=alls=10:allf=t" -t 1 "${T[@]}" -bf 2 -refs 2 -x264-params constrained-intra=1:chroma-qp-offset=4 samples/h264-10bit-constrained-intra.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,fade=in:0:20,fade=out:30:20" -t 2 "${T[@]}" samples/h264-10bit-default.mp4
T2=(-c:v libx264 -profile:v high422 -pix_fmt yuv422p10le)
ff -f lavfi -i testsrc2=size=320x240:rate=25,noise=alls=40:allf=t -t 0.2 "${T2[@]}" -g 1 -qp 1 samples/h264-422-10bit-intra-qp1.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.01:v=0.005" -t 1 "${T2[@]}" -bf 3 -refs 3 -x264-params partitions=all:direct=auto samples/h264-422-10bit-b.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=50 -t 1 "${T[@]}" -vf "tinterlace=interleave_top,setfield=tff" -flags +ildct+ilme -bf 3 -refs 3 samples/h264-10bit-mbaff.mp4

# ---- H.264 High 4:4:4 Predictive ----
# 4:4:4 (chroma_format_idc 3): Cb and Cr coded as luma (Intra16x16 DC/AC, 4x4 and 8x8 blocks, the 8x8 blocks'
# coded_block_flag sent with CABAC, CABAC contexts 460 to 1023, Table 9-4's 4:4:4 coded_block_pattern), predicted
# with the luma intra modes and the luma 6-tap filter, and deblocked with the luma filter at their own QPs. Intra with
# CABAC and CAVLC and at QP 4 on noise; 4x4 transforms only; P and B frames with every partition (CABAC and CAVLC);
# motion on noise; an odd size (crop units of one sample); constrained intra with a chroma QP offset; libx264's
# defaults on fades; MBAFF; and 4:4:4 at 10 bits. libx264 writes no transform bypass here (that is -qp 0).
F=(-c:v libx264 -profile:v high444 -pix_fmt yuv444p)
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 0.2 "${F[@]}" -g 1 samples/h264-444-intra.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 0.2 "${F[@]}" -g 1 -x264-params cabac=0 samples/h264-444-intra-cavlc.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25,noise=alls=40:allf=t -t 0.2 "${F[@]}" -g 1 -qp 4 samples/h264-444-intra-qp4.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25,noise=alls=40:allf=t -t 0.2 "${F[@]}" -g 1 -qp 4 -x264-params cabac=0 samples/h264-444-intra-qp4-cavlc.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.01:v=0.005" -t 1 "${F[@]}" -bf 2 -refs 2 -x264-params 8x8dct=0 samples/h264-444-4x4.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.01:v=0.005" -t 1 "${F[@]}" -bf 3 -refs 3 -x264-params partitions=all:direct=auto samples/h264-444-b.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.01:v=0.005" -t 1 "${F[@]}" -bf 3 -refs 3 -x264-params cabac=0:partitions=all:direct=auto samples/h264-444-b-cavlc.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.013:v=-0.021,noise=alls=12:allf=t" -t 2 "${F[@]}" -bf 3 -refs 3 -qp 22 -x264-params partitions=all:me=umh:merange=32 samples/h264-444-motion.mp4
ff -f lavfi -i "testsrc2=size=321x239:rate=25,scroll=v=0.02" -t 1 "${F[@]}" -bf 2 -refs 2 samples/h264-444-odd-size.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.02,noise=alls=10:allf=t" -t 1 "${F[@]}" -bf 2 -refs 2 -x264-params constrained-intra=1:chroma-qp-offset=4 samples/h264-444-constrained-intra.mp4
# A fade in from black over the first second, a keyframe every 5 frames: VideoThumbnail's SkipDark steps keyframe by
# keyframe past the dark ones (the first at 10% of white's mean luma is at 0.4 s).
ff -f lavfi -i "testsrc2=size=320x240:rate=25,fade=in:st=0:d=1" -t 2 -c:v libx264 -bf 0 -x264-params keyint=5:min-keyint=5:scenecut=0 \
  samples/h264-fade-in.mp4
# RGB coded as 4:4:4 (libx264rgb): matrix_coefficients 0, the planes G, B and R (FFmpeg's gbrp), full range, for the
# thumbnails' conversion of the identity matrix; its frames are the decoder's 4:4:4 as any other.
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.02" -t 0.4 -c:v libx264rgb -pix_fmt bgr24 -bf 2 samples/h264-444-gbr.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,fade=in:0:20,fade=out:30:20" -t 2 "${F[@]}" samples/h264-444-default.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=50 -t 1 "${F[@]}" -vf "tinterlace=interleave_top,setfield=tff" -flags +ildct+ilme -bf 3 -refs 3 samples/h264-444-mbaff.mp4
F10=(-c:v libx264 -profile:v high444 -pix_fmt yuv444p10le)
ff -f lavfi -i testsrc2=size=320x240:rate=25,noise=alls=40:allf=t -t 0.2 "${F10[@]}" -g 1 -qp 1 samples/h264-444-10bit-intra-qp1.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.01:v=0.005" -t 1 "${F10[@]}" -bf 3 -refs 3 -x264-params partitions=all:direct=auto samples/h264-444-10bit-b.mp4

# ---- H.264 lossless (transform bypass) ----
# libx264's -qp 0 sets qpprime_y_zero_transform_bypass_flag (profile High 4:4:4 Predictive, whatever the chroma format)
# with QP'Y 0 (QP_Y -12 at 10 bits): the levels are the residual, with no scaling or transform, and intra blocks
# predicted vertically or horizontally sum it first along that direction (over the whole macroblock for Intra_16x16
# and for chroma). 4:2:0 intra with CABAC and CAVLC, and with P and B frames; 4:2:2 intra (chroma summed over 16 rows);
# 4:4:4 intra and with B frames, CAVLC too; 4:4:4 at 10 bits; 4x4 transforms only; MBAFF. Noise keeps the levels large.
LL="testsrc2=size=320x240:rate=25,noise=alls=20:allf=t"
LM="testsrc2=size=320x240:rate=25,scroll=h=0.01:v=0.005,noise=alls=6:allf=t"
X=(-c:v libx264 -qp 0)
ff -f lavfi -i "$LL" -t 0.2 "${X[@]}" -pix_fmt yuv420p -g 1 samples/h264-lossless-intra.mp4
ff -f lavfi -i "$LL" -t 0.2 "${X[@]}" -pix_fmt yuv420p -g 1 -x264-params cabac=0 samples/h264-lossless-intra-cavlc.mp4
ff -f lavfi -i "$LM" -t 0.6 "${X[@]}" -pix_fmt yuv420p -bf 2 -refs 2 -x264-params partitions=all samples/h264-lossless-b.mp4
ff -f lavfi -i "$LL" -t 0.2 "${X[@]}" -pix_fmt yuv422p -g 1 samples/h264-lossless-422-intra.mp4
ff -f lavfi -i "$LL" -t 0.2 "${X[@]}" -pix_fmt yuv444p -g 1 samples/h264-lossless-444-intra.mp4
ff -f lavfi -i "$LM" -t 0.6 "${X[@]}" -pix_fmt yuv444p -bf 2 -refs 2 -x264-params partitions=all samples/h264-lossless-444-b.mp4
ff -f lavfi -i "$LM" -t 0.6 "${X[@]}" -pix_fmt yuv444p -bf 2 -refs 2 -x264-params cabac=0:partitions=all samples/h264-lossless-444-b-cavlc.mp4
ff -f lavfi -i "$LM" -t 0.6 "${X[@]}" -pix_fmt yuv444p10le -bf 2 -refs 2 samples/h264-lossless-444-10bit.mp4
ff -f lavfi -i "$LL" -t 0.2 "${X[@]}" -pix_fmt yuv420p -g 1 -x264-params 8x8dct=0 samples/h264-lossless-4x4.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=50,noise=alls=10:allf=t" -t 0.4 "${X[@]}" -pix_fmt yuv420p -vf "tinterlace=interleave_top,setfield=tff" -flags +ildct+ilme -bf 2 samples/h264-lossless-mbaff.mp4

# ---- H.264 monochrome (4:0:0) ----
# libx264's gray input gives chroma_format_idc 0 (High, High 10): no chroma planes, no intra_chroma_pred_mode, the
# coded_block_pattern without a chroma part (Table 9-4's 4:0:0 column, CABAC's chroma bins absent), no chroma weights
# in pred_weight_table. Intra with CABAC and CAVLC; B frames, CAVLC too; motion; fades with explicit weights in P
# (weightp=2); odd size; 10 bits intra and with B frames; MBAFF; lossless. FFmpeg's decoder outputs gray as 4:2:0 with
# flat chroma, so their references take its Y plane alone (below).
G=(-c:v libx264 -profile:v high -pix_fmt gray)
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 0.2 "${G[@]}" -g 1 samples/h264-gray-intra.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 0.2 "${G[@]}" -g 1 -x264-params cabac=0 samples/h264-gray-intra-cavlc.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.01:v=0.005" -t 1 "${G[@]}" -bf 3 -refs 3 -x264-params partitions=all:direct=auto samples/h264-gray-b.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.01:v=0.005" -t 1 "${G[@]}" -bf 3 -refs 3 -x264-params cabac=0:partitions=all:direct=auto samples/h264-gray-b-cavlc.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.013:v=-0.021,noise=alls=12:allf=t" -t 2 "${G[@]}" -bf 3 -refs 3 -qp 22 -x264-params partitions=all:me=umh:merange=32 samples/h264-gray-motion.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,fade=in:0:20,fade=out:30:20" -t 2 "${G[@]}" -refs 3 -x264-params weightp=2 samples/h264-gray-weighted.mp4
ff -f lavfi -i "testsrc2=size=321x239:rate=25,scroll=v=0.02" -t 1 "${G[@]}" -bf 2 -refs 2 samples/h264-gray-odd-size.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=50 -t 1 "${G[@]}" -vf "tinterlace=interleave_top,setfield=tff" -flags +ildct+ilme -bf 3 -refs 3 samples/h264-gray-mbaff.mp4
G10=(-c:v libx264 -profile:v high10 -pix_fmt gray10le)
ff -f lavfi -i testsrc2=size=320x240:rate=25,noise=alls=40:allf=t -t 0.2 "${G10[@]}" -g 1 -qp 1 samples/h264-gray-10bit-intra-qp1.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,fade=in:0:20" -t 1 "${G10[@]}" -bf 3 -refs 3 -x264-params partitions=all:direct=auto:weightp=2 samples/h264-gray-10bit-b.mp4
ff -f lavfi -i "$LM" -t 0.6 "${X[@]}" -pix_fmt gray -bf 2 -refs 2 -x264-params partitions=all samples/h264-gray-lossless.mp4

# ---- H.264 scaling matrices (High profile) ----
# libx264 writes them in the PPS only, so absent lists fall back by rule A (Table 7-2). cqm=jvt sends no lists at all:
# every list is the default one. The custom files give each list its own values (libx264 reads one chroma list per
# intra and inter, so Cr's list is absent and falls back to Cb's); with CABAC and CAVLC, with large weights at a low
# QP for wide products, and without the 8x8 transform, where the PPS has 6 lists instead of 8. (This libx264 fails to
# parse cqm8iy and cqm8py, hence the files.) SPS lists, fall-back rule B, useDefaultScalingMatrixFlag and a Cr list
# of its own never come from libx264; H264ScalingMatrixTests covers them. In 4:4:4 libx264 also sends 8x8 lists for Cb
# (Cr's again fall back to Cb's): 12 lists in the PPS, with CABAC and CAVLC.
cqm=$(mktemp -d)
list() { printf '%s =\n' "$1"; seq -s, "$2" "$3" "$4"; }
{ list INTRA4X4_LUMA 6 3 51; list INTRA4X4_CHROMAU 9 2 39; list INTER4X4_LUMA 12 2 42; list INTER4X4_CHROMAU 30 -1 15
  list INTRA8X8_LUMA 8 2 134; list INTER8X8_LUMA 70 -1 7; } > "$cqm/custom.cfg"
{ list INTRA4X4_LUMA 255 -1 240; list INTRA4X4_CHROMAU 240 1 255; list INTER4X4_LUMA 200 3 245; list INTER4X4_CHROMAU 250 -2 220
  list INTRA8X8_LUMA 255 -2 129; list INTER8X8_LUMA 128 2 254; } > "$cqm/large.cfg"
{ cat "$cqm/custom.cfg"; list INTRA8X8_CHROMA 100 1 163; list INTER8X8_CHROMA 20 3 209; } > "$cqm/custom444.cfg"
S="testsrc2=size=320x240:rate=25,scroll=h=0.01:v=0.005"
ff -f lavfi -i "$S" -t 1 "${H[@]}" -bf 2 -refs 2 -x264-params cqm=jvt samples/h264-cqm-jvt.mp4
ff -f lavfi -i "$S" -t 1 "${H[@]}" -bf 2 -refs 2 -x264-params "cqmfile=$cqm/custom.cfg:partitions=all" samples/h264-cqm-custom.mp4
ff -f lavfi -i "$S" -t 1 "${H[@]}" -bf 2 -refs 2 -x264-params "cqmfile=$cqm/custom.cfg:partitions=all:cabac=0" samples/h264-cqm-custom-cavlc.mp4
ff -f lavfi -i "$S,noise=alls=40:allf=t" -t 0.4 "${H[@]}" -bf 2 -qp 4 -x264-params "cqmfile=$cqm/large.cfg" samples/h264-cqm-large.mp4
ff -f lavfi -i "$S" -t 1 "${H[@]}" -bf 2 -refs 2 -x264-params "cqmfile=$cqm/custom.cfg:8x8dct=0" samples/h264-cqm-no8x8.mp4
ff -f lavfi -i "$S" -t 1 "${F[@]}" -bf 2 -refs 2 -x264-params "cqmfile=$cqm/custom444.cfg:partitions=all" samples/h264-444-cqm.mp4
ff -f lavfi -i "$S" -t 1 "${F[@]}" -bf 2 -refs 2 -x264-params "cqmfile=$cqm/custom444.cfg:partitions=all:cabac=0" samples/h264-444-cqm-cavlc.mp4
rm -rf "$cqm"

# ---- AAC LC ----
# Each aimed at one tool; the conformance test checks the tool really occurs. PNS is off except in the PNS samples.
ff -f lavfi -i "sine=frequency=440:sample_rate=44100:duration=1" -c:a aac -b:a 96k -aac_pns 0 samples/aac-mono-44k.mp4
ff -f lavfi -i "aevalsrc=0.4*sin(2*PI*440*t)+0.1*sin(2*PI*3000*t)|0.4*sin(2*PI*440*t+0.3)+0.08*sin(2*PI*5200*t):s=48000:d=2" -c:a aac -b:a 48k -aac_pns 0 samples/aac-stereo-48k.mp4
# The same AAC in a QuickTime file (-f mov), whose sound description holds the 'esds' in a 'wave' box; named .mp4 so
# the MP4 references and tests take it.
ff -i samples/aac-stereo-48k.mp4 -c copy -f mov samples/aac-quicktime.mp4
ff -f lavfi -i "aevalsrc=0.5*sin(2*PI*300*t)*sin(2*PI*2*t)+0.05*sin(2*PI*1700*t):s=8000:d=2" -c:a aac -b:a 16k -aac_pns 0 samples/aac-mono-8k.mp4
ff -f lavfi -i "aevalsrc=0.5*sin(2*PI*600*t+4*sin(2*PI*3*t))|0.3*sin(2*PI*900*t):s=22050:d=2" -c:a aac -b:a 40k -aac_pns 0 samples/aac-stereo-22k.mp4
ff -f lavfi -i "aevalsrc=0.6*sin(2*PI*440*t)*gt(mod(t\,0.1)\,0.05)+0.5*lt(mod(t\,0.25)\,0.0005):s=44100:d=2" -c:a aac -b:a 128k -aac_pns 0 samples/aac-transients.mp4
ff -f lavfi -i "aevalsrc=0.7*(random(0)-0.5)*exp(-60*mod(t\,0.17))+0.2*sin(2*PI*2500*t):s=44100:d=2" -c:a aac -b:a 128k -aac_pns 0 samples/aac-tns.mp4
ff -f lavfi -i "aevalsrc=0.3*sin(2*PI*220*t)|0.3*sin(2*PI*330*t)|0.3*sin(2*PI*440*t)|0.3*sin(2*PI*55*t)|0.3*sin(2*PI*660*t)|0.3*sin(2*PI*880*t):s=48000:d=1:c=5.1" -c:a aac -b:a 256k -aac_pns 0 samples/aac-5.1.mp4
# Eight channels: FFmpeg writes 7.1 as channel configuration 7, and 7.1(wide) with a program config element
eight="0.2*sin(2*PI*147*t)|0.2*sin(2*PI*257*t)|0.2*sin(2*PI*367*t)|0.2*sin(2*PI*477*t)|0.2*sin(2*PI*587*t)|0.2*sin(2*PI*697*t)|0.2*sin(2*PI*807*t)|0.2*sin(2*PI*917*t)"
ff -f lavfi -i "aevalsrc=$eight:s=48000:d=1:c=7.1" -c:a aac -b:a 320k -aac_pns 0 samples/aac-7.1.mp4
ff -f lavfi -i "aevalsrc=$eight:s=48000:d=1:c=7.1(wide)" -c:a aac -b:a 320k -aac_pns 0 samples/aac-pce-8ch.mp4
# A program config element with two front pairs (6.0(front): FL FR FLC FRC SL SR), the inner listed first: FFmpeg
# outputs them in WAVE order, outer pair first, not as listed (the only one of its encoder's layouts it reorders).
ff -f lavfi -i "aevalsrc=0.2*sin(2*PI*147*t)|0.2*sin(2*PI*257*t)|0.2*sin(2*PI*367*t)|0.2*sin(2*PI*477*t)|0.2*sin(2*PI*587*t)|0.2*sin(2*PI*697*t):s=48000:d=1:c=6.0(front)" -c:a aac -b:a 256k -aac_pns 0 -aac_pce 1 samples/aac-pce-front.mp4
ff -f lavfi -i "anoisesrc=color=pink:amplitude=0.3:sample_rate=44100:duration=2:seed=1,highpass=f=4000" -c:a aac -b:a 48k samples/aac-pns.mp4
ff -f lavfi -i "anoisesrc=color=pink:amplitude=0.3:sample_rate=44100:duration=2:seed=1,highpass=f=4000[a];anoisesrc=color=pink:amplitude=0.3:sample_rate=44100:duration=2:seed=2,highpass=f=4000[b];[a][b]amerge=inputs=2" -c:a aac -b:a 64k samples/aac-pns-stereo.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.01" -f lavfi -i "sine=frequency=330:sample_rate=48000:duration=1" -t 1 -c:v libx264 -profile:v baseline -pix_fmt yuv420p -refs 2 -c:a aac -ac 2 -aac_pns 0 samples/h264-with-audio.mp4
# Raw AAC in ADTS frames (the ADTS demuxer): stereo at 44.1 kHz, and 5.1 behind an ID3v2 tag (which the demuxer skips).
ff -f lavfi -i "aevalsrc=0.4*sin(2*PI*440*t)|0.3*sin(2*PI*660*t):s=44100:d=1" -c:a aac -b:a 96k -aac_pns 0 -f adts samples/aac-adts.aac
# Pulse data, which no encoder at hand writes (FFmpeg's never does): a stream written by its syntax, every frame one
# mono element of codebook 1 with one to four pulses, some on zero values (tools/make-aac-pulse.py).
python3 -I tools/make-aac-pulse.py samples/aac-pulse.aac
# The same, its encoder name (in the first frame's fill element) rewritten to read as FAAC's, where FFmpeg's decoder
# looks for it: it then skips FAAC's encoder delay, 1024 samples, which FAAC signals nowhere else (Sintel's trailer).
cp samples/aac-adts.aac samples/aac-faac-delay.aac && python3 -I tools/aac-encoder-name.py samples/aac-faac-delay.aac > /dev/null
ff -f lavfi -i "aevalsrc=0.3*sin(2*PI*220*t)|0.3*sin(2*PI*330*t)|0.3*sin(2*PI*440*t)|0.3*sin(2*PI*55*t)|0.3*sin(2*PI*660*t)|0.3*sin(2*PI*880*t):s=48000:d=1:c=5.1" -c:a aac -b:a 256k -aac_pns 0 -f adts -write_id3v2 1 -metadata title=ManagedCodecs samples/aac-adts-5.1-id3.aac
# Channel configuration 0 in ADTS: the 5.1 channels in a program config element, which FFmpeg writes at the start of the
# first frame only.
ff -f lavfi -i "aevalsrc=0.3*sin(2*PI*220*t)|0.3*sin(2*PI*330*t)|0.3*sin(2*PI*440*t)|0.3*sin(2*PI*55*t)|0.3*sin(2*PI*660*t)|0.3*sin(2*PI*880*t):s=48000:d=1:c=5.1" -c:a aac -b:a 256k -aac_pns 0 -aac_pce 1 -f adts samples/aac-adts-pce.aac
# The same remuxed (FFmpeg's ADTS-to-MP4 filter copies the element into the AudioSpecificConfig, its comment, FFmpeg's
# name, after the realigned bits): the config the ADTS demuxer builds must equal it.
ff -i samples/aac-adts-pce.aac -c copy samples/aac-adts-pce-remux.mp4
# An edit list that trims more than FFmpeg's muxer writes (it only cuts the 1024 samples of priming): media time 2500,
# so two whole packets and part of a third come before the start, and 335 ms, which ends mid-packet and leaves whole
# packets after the end (335 ms is 14773.5 samples, so the end also shows how the movie timescale is rounded).
ff -f lavfi -i "sine=frequency=440:sample_rate=44100:duration=1" -c:a aac -b:a 96k -aac_pns 0 samples/aac-edit-trim.mp4
python3 -I - samples/aac-edit-trim.mp4 <<'PY'
import struct, sys
data = bytearray(open(sys.argv[1], 'rb').read())
at = data.find(b'elst')
assert at > 0 and data[at + 4] == 0 and struct.unpack_from('>I', data, at + 8)[0] == 1  # version 0, one entry
struct.pack_into('>Ii', data, at + 12, 335, 2500)  # segment_duration (movie timescale 1000), media_time
open(sys.argv[1], 'wb').write(data)
PY

# Edit lists of several edits, which FFmpeg's muxer never writes (tools/mp4-edit-list.py rewrites them; durations in
# the movie timescale, 1000, media times in the track's). Audio: a second edit from 1.5 s, inside a packet (FFmpeg starts
# it at the next packet, after a second of packets decoded and not shown); and an empty edit between two, which FFmpeg
# reads as media time -1 (it plays the media's start again). Video, B frames and a keyframe every 12 frames: a second
# edit starting and ending inside a GOP (decoded from the keyframe before, and on to the second keyframe after its end),
# and a single edit that ends a second before the track. Both tracks of a file at once.
ff -f lavfi -i "aevalsrc=0.4*sin(2*PI*440*t)+0.1*sin(2*PI*3000*t)|0.4*sin(2*PI*440*t+0.3):s=48000:d=2" -c:a aac -b:a 48k -aac_pns 0 samples/aac-edits.mp4
cp samples/aac-edits.mp4 samples/aac-edits-empty.mp4
python3 -I tools/mp4-edit-list.py samples/aac-edits.mp4 0 510:1024,310:72000
python3 -I tools/mp4-edit-list.py samples/aac-edits-empty.mp4 0 500:1024,200:-1,300:72704
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 2 -c:v libx264 -bf 2 -g 12 samples/h264-edits.mp4
cp samples/h264-edits.mp4 samples/h264-edit-end.mp4
for e in zero gap empty-zero; do cp samples/h264-edits.mp4 samples/h264-edit-$e.mp4; done
python3 -I tools/mp4-edit-list.py samples/h264-edits.mp4 0 200:1024,240:9216
python3 -I tools/mp4-edit-list.py samples/h264-edit-end.mp4 0 1000:1024
# Edits whose shown frames start after the edit's start, which FFmpeg's muxer never writes (it sets media time to the first
# composition offset): FFmpeg then moves every timestamp of the track back so that the smallest pts of a frame shown
# (discarded ones don't count), less any empty edits, is 0 (mov_fix_index). Media time 0 with the first pts at 1024;
# 1280, past the first frame (discarded), the next shown at 1536; and an empty edit before a media time of 0.
python3 -I tools/mp4-edit-list.py samples/h264-edit-zero.mp4 0 2000:0
python3 -I tools/mp4-edit-list.py samples/h264-edit-gap.mp4 0 1500:1280
python3 -I tools/mp4-edit-list.py samples/h264-edit-empty-zero.mp4 0 200:-1,2000:0
ff -f lavfi -i "testsrc2=size=320x240:rate=25" -f lavfi -i "sine=frequency=330:sample_rate=48000:duration=2" -t 2 -c:v libx264 -bf 2 -g 12 -c:a aac -ac 2 -aac_pns 0 samples/h264-audio-edits.mp4
python3 -I tools/mp4-edit-list.py samples/h264-audio-edits.mp4 0 400:1024,400:15360
python3 -I tools/mp4-edit-list.py samples/h264-audio-edits.mp4 1 400:1024,400:48500
# The container's picture size smaller than the stream's (an Apple-encoded film stores 640x359 for 640x360), which
# FFmpeg's decoder takes as more cropping (init_dimensions): 317x233 from 320x240 (odd, so chroma rounds up), and in
# 4:2:2 at 10 bits; not taken where it rounds up to another multiple of 16 (320x224), nor where the SPS crops the top.
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=v=0.02" -t 1 "${H[@]}" -bf 2 samples/h264-container-crop.mp4
python3 -I tools/mp4-video-size.py samples/h264-container-crop.mp4 317 233
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=v=0.02" -t 1 -c:v libx264 -profile:v high422 -pix_fmt yuv422p10le -bf 2 samples/h264-container-crop-422-10bit.mp4
python3 -I tools/mp4-video-size.py samples/h264-container-crop-422-10bit.mp4 319 239
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=v=0.02" -t 1 "${H[@]}" -bf 2 samples/h264-container-crop-misaligned.mp4
python3 -I tools/mp4-video-size.py samples/h264-container-crop-misaligned.mp4 320 224
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=v=0.02" -t 1 "${H[@]}" -bf 2 -bsf:v h264_metadata=crop_top=2 samples/h264-container-crop-sps-top.mp4
python3 -I tools/mp4-video-size.py samples/h264-container-crop-sps-top.mp4 320 237

# ---- H.264 from OpenH264 ----
# Committed in samples/openh264/ (CI has no OpenH264): long-term references and the list modification and memory
# management operations that come with them, POC type 2, non-reference P frames, slices by size, changing and repeated
# parameter sets, and an SVC stream's base layer. tools/make-openh264-samples.sh says how each was made.
cp samples/openh264/*.mp4 samples/

# ---- H.264 from JM ----
# Committed in samples/jm/ (CI has no JM): field pictures (PAFF), alone and mixed with frame pictures.
# tools/make-jm-samples.sh says how each was made.
cp samples/jm/*.mp4 samples/

# ---- HE-AAC from the FDK AAC encoder ----
# Committed in samples/heaac/ (CI has no fdk-aac with SBR): spectral band replication, signalled in the config and only
# by its data (ADTS), mono to 5.1, coupled pairs, and parametric stereo; and an AAC LC stream with a program config
# element as the spec lists one. tools/make-heaac-samples.sh says how each was
# made.
cp samples/heaac/* samples/
# HE-AAC remuxed by FFmpeg, whose muxer writes the encoder delay as an edit list in the SBR rate's timescale (the FDK
# front end writes an iTunSMPB tag instead): the edit's samples are the decoder's output samples, not the core's.
ff -i samples/heaac-stereo-44k.mp4 -c copy samples/heaac-edit.mp4
# The FDK encoder's 5.1 in a program config element, as the spec lists it, remuxed: FFmpeg's ADTS-to-MP4 filter copies
# the element into the AudioSpecificConfig.
ff -i samples/aac-fdk-pce.aac -c copy samples/aac-fdk-pce-remux.mp4

# ---- FFmpeg's references ----
# name.framemd5: decoded frames. name.packets.csv: demuxed packets as ffprobe lists them (the demuxer's own timestamps,
# flags with discard, MD5 of the data); name.packets.framemd5 for the codec private data (and the ADTS packets).
# name.f32: decoded audio, edit list ignored (every packet, whole; not where the edits leave out packets or repeat
# them: aac-edit-trim and the samples with several edits). name.trim.f32: decoded audio as
# FFmpeg gives it by default, trimmed by the edit list. -idct simple: FFmpeg's C IDCT for JPEG, the arithmetic the MJPEG
# decoder follows; its default picks by CPU, the same results on x64 (SSE2) but not on ARM64 (NEON rounds otherwise).
# -noautorotate: FFmpeg's command line turns frames by a track's rotation (h264-rotate-*), which decoders do not.
for f in samples/*.avi samples/*.mp4; do ff -idct simple -noautorotate -i "$f" -f framemd5 "${f%.*}.framemd5"; done
# Monochrome H.264 (by the name): FFmpeg decodes it to 4:2:0 with flat chroma, so the references take its Y plane
# alone, as it is (extractplanes; -pix_fmt gray would rescale a limited-range stream's samples to full range).
for f in samples/*gray*.mp4; do ff -noautorotate -i "$f" -vf extractplanes=y -f framemd5 "${f%.*}.framemd5"; done
# h264-negative-ctts-last is for the demuxer alone: FFmpeg drops the frame whose pts goes back, which the decoder,
# outputting by picture order, keeps.
rm samples/h264-negative-ctts-last.framemd5
# name.picttypes: each decoded video frame's pict_type as ffprobe lists them, in output order (I, P or B; i and p for SI
# and SP; a frame of two field pictures takes its first field's), for VideoFrame.PictureType. The first field alone:
# side data (x264's SEI) follows it on the first frame.
for f in samples/*.avi samples/*.mp4; do
  t=$(ffprobe -v error -select_streams v:0 -show_entries frame=pict_type -of csv=p=0 "$f" | cut -d, -f1)
  if [ -n "$t" ]; then echo "$t" > "${f%.*}.picttypes"; fi
done
# name.interlacing: each decoded video frame's interlaced_frame and top_field_first as ffprobe lists them ("frame,1,1"),
# in output order, and the stream's field_order ("stream,tt"), for VideoFrame.IsInterlaced, IsTopFieldFirst and
# H264Decoder.FieldOrder.
for f in samples/*.avi samples/*.mp4; do
  t=$(ffprobe -v error -select_streams v:0 -show_entries stream=field_order:frame=interlaced_frame,top_field_first -of csv "$f" \
    | cut -d, -f1-3)
  if [ -n "$t" ]; then echo "$t" > "${f%.*}.interlacing"; fi
done
# name.display: the video stream's sample aspect ratio and rotation as ffprobe lists them (MP4 and AVI) ("sample_aspect_ratio=4:3",
# "rotation=-90"; the ratio the container's where it has one, else the decoder's), for VideoStreamInfo.SampleAspectRatio
# and Rotation.
for f in samples/*.mp4 samples/*.avi; do
  t=$(ffprobe -v error -select_streams v:0 -of default=nw=1 -show_entries stream=sample_aspect_ratio:stream_side_data=rotation "$f")
  if [ -n "$t" ]; then echo "$t" > "${f%.*}.display"; fi
done
# name.probe.json: the file's duration and its streams' facts as ffprobe reports them, for VideoProbe (Thumbnails).
for f in samples/*.mp4 samples/*.avi; do
  ffprobe -v error -of json -show_entries format=duration,start_time:stream=index,codec_type,codec_name,width,height,sample_aspect_ratio,r_frame_rate,avg_frame_rate,sample_rate,channels,duration:stream_side_data=rotation \
    "$f" > "${f%.*}.probe.json"
done
# name.thumb.rgba: the first video frame as FFmpeg's scaler turns it into a thumbnail (RGBA, 3/8 of the size as shown,
# by area), for the Thumbnails package's conversion, scaling and rotation: autorotate on (not -noautorotate as above),
# converted to RGB at full size first and scaled after (format=rgba before scale, the order RgbaImage follows), only
# where the video is H.264 or MJPEG (not the separate colour planes, jm-planes*, which FFmpeg doesn't decode).
# - The scaler's flags: without accurate_rnd and full chroma, swscale takes RGB through 4:2:0 YUV of its own to scale
#   it, which put it up to 10 levels from the exact area mean at colour edges; with them, 1.
# - Deeper than 8 bits, FFmpeg first reduces the samples to 8 (the same chroma format): its direct conversion from 9 to
#   14 bits to RGBA was up to 87 levels from the definition (and from its own 8-bit route), the 8-bit route within 3.
# - Not an odd width or height (the container crops): there swscale leaves its fast path, which takes the nearest
#   chroma sample as RgbaConverter does, for its general one, which interpolates chroma (up to 194 levels apart at a
#   colour edge). RgbaImageTests checks odd sizes against the definition instead, and 4:1:1 and 4:4:0 (mjpeg-411,
#   mjpeg-440*), which take the general path at any size (6 levels from ours at the first pixel that differed).
for f in samples/*.mp4 samples/*.avi; do
  case $f in samples/jm-planes*) continue ;; esac
  IFS=, read -r codec width height pix <<< \
    "$(ffprobe -v error -select_streams v:0 -of csv=p=0 -show_entries stream=codec_name,width,height,pix_fmt "$f")"
  case $codec in h264 | mjpeg) ;; *) continue ;; esac
  if (( width % 2 || height % 2 )); then continue; fi
  case $pix in yuvj411p | yuvj440p) continue ;; esac
  case $pix in yuv4[24][024]p[0-9]*) eight="format=${pix:0:7}," ;; gray[0-9]*) eight="format=gray," ;; *) eight="" ;; esac
  ff -idct simple -i "$f" -map 0:v:0 -frames:v 1 \
    -vf "${eight}format=rgba,scale=max(1\\,trunc(iw*3/8)):max(1\\,trunc(ih*3/8)):flags=area+accurate_rnd+full_chroma_int+full_chroma_inp,format=rgba" \
    -f rawvideo "${f%.*}.thumb.rgba"
done
# name.atNNNN.thumb.rgba: as name.thumb.rgba, the first frame shown at NNNN ms from the file's start (ffmpeg's -ss
# before -i, counted from the start time as ThumbnailOptions.At is), for VideoThumbnail.Create: between keyframes, on
# one, in a fragmented file that starts late, past an empty edit, MJPEG, and in the fade-in. testsrc-420.avi:610 is a
# quarter frame past 600 in a 1/25 time base: FFmpeg rescales -ss to the nearest unit, so the frame at 600 ms.
for s in h264-bframes.mp4:1200 h264-bframes.mp4:960 fmp4-base-moof.mp4:200 h264-edit-gap.mp4:500 testsrc-420.avi:600 \
  testsrc-420.avi:610 h264-fade-in.mp4:400; do
  f=samples/${s%%:*}; ms=${s##*:}
  ff -ss "$(printf '%d.%03d' $((ms / 1000)) $((ms % 1000)))" -idct simple -i "$f" -map 0:v:0 -frames:v 1 \
    -vf "format=rgba,scale=max(1\\,trunc(iw*3/8)):max(1\\,trunc(ih*3/8)):flags=area+accurate_rnd+full_chroma_int+full_chroma_inp,format=rgba" \
    -f rawvideo "${f%.*}.at$ms.thumb.rgba"
done
# name.color: each decoded video frame's colour properties and sample aspect ratio as ffprobe lists them, one a line in
# output order ("color_range=tv|color_space=bt709|..."), for VideoFrame.ColorRange, ColorPrimaries, ColorTransfer,
# ColorMatrix, ChromaLocation and SampleAspectRatio.
for f in samples/*.avi samples/*.mp4; do
  t=$(ffprobe -v error -select_streams v:0 -of compact=p=0 \
    -show_entries frame=color_range,color_space,color_primaries,color_transfer,chroma_location,sample_aspect_ratio "$f")
  if [ -n "$t" ]; then echo "$t" > "${f%.*}.color"; fi
done
for f in samples/*.mp4; do
  ff -copyts -i "$f" -c copy -f framemd5 "${f%.mp4}.packets.framemd5"
  ffprobe -v error -show_packets -show_data_hash MD5 -show_entries packet=stream_index,pts,dts,duration,size,flags,data_hash \
    -of csv=p=0 "$f" > "${f%.mp4}.packets.csv"
done
# name.seekNNNN.framemd5: the first video frame FFmpeg's accurate seek gives at NNNN ms, for IDemuxer.Seek (-ss before
# -i: from the keyframe at or before the time, the frames shown before it dropped; -seek_timestamp 1: the time is the
# packets' own, not counted from the file's start time). Between keyframes, on one, in a fragmented file that starts
# late, in a file with an empty edit, and in MJPEG.
for s in h264-bframes.mp4:1200 h264-bframes.mp4:960 h264-bframes.mp4:1000 fmp4-base-moof.mp4:1200 \
  h264-edit-gap.mp4:1000 testsrc-420.avi:600; do
  f=samples/${s%%:*}; ms=${s##*:}
  ff -seek_timestamp 1 -ss "$(printf '%d.%03d' $((ms / 1000)) $((ms % 1000)))" -idct simple -noautorotate -i "$f" -map 0:v:0 \
    -frames:v 1 -f framemd5 "${f%.*}.seek$ms.framemd5"
done
# Raw AAC: packets as FFmpeg's demuxer gives them (whole ADTS frames), and every decoded sample (no edit list).
for f in samples/*.aac; do
  ff -copyts -i "$f" -c copy -f framemd5 "${f%.aac}.packets.framemd5"
  [[ $f == samples/heaac-* || $f == *faac* ]] || ff -i "$f" -f f32le "${f%.aac}.f32"  # HE-AAC: below
done
# H.264 streams FFmpeg does not decode (separate colour planes), from JM, committed with references from JM's own
# decoder (tools/make-jm-samples.sh): copied after FFmpeg's references, which would fail on them.
cp samples/jm-ref/* samples/
for f in samples/aac-*.mp4 samples/h264-with-audio.mp4 samples/h264-audio-edits.mp4 samples/fmp4-*.mp4; do
  [[ $f == samples/aac-edit-trim.mp4 || $f == *edits* ]] || ff -ignore_editlist 1 -i "$f" -map 0:a:0 -f f32le "${f%.mp4}.f32"
  ff -i "$f" -map 0:a:0 -f f32le "${f%.mp4}.trim.f32"
done
# HE-AAC: name.sbr.f32, decoded audio as FFmpeg gives it by default: at the SBR rate, without the encoder delay an MP4
# file's iTunSMPB tag gives (ADTS has none), and in stereo for a mono stream (FFmpeg's decoder assumes parametric
# stereo could come).
for f in samples/heaac-*.mp4 samples/heaacv2-*.mp4 samples/heaac-*.aac; do ff -i "$f" -f f32le "${f%.*}.sbr.f32"; done
# A stream naming FAAC: name.delay.f32, decoded audio as FFmpeg gives it, without FAAC's encoder delay.
for f in samples/*faac*.aac; do ff -i "$f" -f f32le "${f%.*}.delay.f32"; done

# name.audioframes.csv: the decoded audio frames as ffprobe lists them, by default (the edit list applied): FFmpeg's
# time base for the stream on the first line, then each frame's pts in it and its sample count; for AudioFrame.Pts.
for f in samples/*.mp4 samples/*.aac; do
  tb=$(ffprobe -v error -select_streams a:0 -show_entries stream=time_base -of csv=p=0 "$f")
  if [ -n "$tb" ]; then
    { echo "$tb"; ffprobe -v error -select_streams a:0 -show_entries frame=pts,nb_samples -of csv=p=0 "$f"; } > "${f%.*}.audioframes.csv"
  fi
done

echo "Samples and references written to samples/."
