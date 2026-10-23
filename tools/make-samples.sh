#!/usr/bin/env bash
# Generates the test media in samples/ and FFmpeg's reference output for each file; the conformance tests
# compare our decoders and demuxers against these. Needs ffmpeg/ffprobe (with libx264) and ImageMagick on PATH.
# Run from the repository root: tools/make-samples.sh (on Windows: Git Bash or WSL). Takes a minute or two.
# Each sample isolates something; samples/README.md says what the references are and how the tests pick samples.
set -euo pipefail

cd "$(dirname "$0")/.."
mkdir -p samples
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
# Noise at the highest quality: large coefficients exercise the IDCT's full range
ff -f lavfi -i testsrc2=size=320x240:rate=25,noise=alls=60:allf=t -t 1 -c:v mjpeg -pix_fmt yuvj420p -q:v 1 samples/noise-q1.avi
# Grayscale (FFmpeg's MJPEG encoder has no gray output, so ImageMagick encodes the frames)
frames=$(mktemp -d)
ff -f lavfi -i testsrc2=size=322x238:rate=25 -t 1 -pix_fmt gray "$frames/f%02d.png"
"$magick_cmd" "$frames"/f*.png -colorspace Gray -type Grayscale -quality 90 "$frames/f%02d.jpg"
ff -framerate 25 -i "$frames/f%02d.jpg" -c:v copy samples/gray.avi
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

# ---- MP4 for the demuxer ----
# The MJPEG remux is decoded end to end; H.264 with B-frames needs the edit list right, MPEG-4 Part 2 carries its
# codec private data in esds, faststart puts moov before mdat, and the delayed one starts with an empty edit.
ff -i samples/testsrc-420.avi -c copy samples/mjpeg-420.mp4
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
ff -f lavfi -i "aevalsrc=0.5*sin(2*PI*300*t)*sin(2*PI*2*t)+0.05*sin(2*PI*1700*t):s=8000:d=2" -c:a aac -b:a 16k -aac_pns 0 samples/aac-mono-8k.mp4
ff -f lavfi -i "aevalsrc=0.5*sin(2*PI*600*t+4*sin(2*PI*3*t))|0.3*sin(2*PI*900*t):s=22050:d=2" -c:a aac -b:a 40k -aac_pns 0 samples/aac-stereo-22k.mp4
ff -f lavfi -i "aevalsrc=0.6*sin(2*PI*440*t)*gt(mod(t\,0.1)\,0.05)+0.5*lt(mod(t\,0.25)\,0.0005):s=44100:d=2" -c:a aac -b:a 128k -aac_pns 0 samples/aac-transients.mp4
ff -f lavfi -i "aevalsrc=0.7*(random(0)-0.5)*exp(-60*mod(t\,0.17))+0.2*sin(2*PI*2500*t):s=44100:d=2" -c:a aac -b:a 128k -aac_pns 0 samples/aac-tns.mp4
ff -f lavfi -i "aevalsrc=0.3*sin(2*PI*220*t)|0.3*sin(2*PI*330*t)|0.3*sin(2*PI*440*t)|0.3*sin(2*PI*55*t)|0.3*sin(2*PI*660*t)|0.3*sin(2*PI*880*t):s=48000:d=1:c=5.1" -c:a aac -b:a 256k -aac_pns 0 samples/aac-5.1.mp4
# Eight channels: FFmpeg writes 7.1 as channel configuration 7, and 7.1(wide) with a program config element
eight="0.2*sin(2*PI*147*t)|0.2*sin(2*PI*257*t)|0.2*sin(2*PI*367*t)|0.2*sin(2*PI*477*t)|0.2*sin(2*PI*587*t)|0.2*sin(2*PI*697*t)|0.2*sin(2*PI*807*t)|0.2*sin(2*PI*917*t)"
ff -f lavfi -i "aevalsrc=$eight:s=48000:d=1:c=7.1" -c:a aac -b:a 320k -aac_pns 0 samples/aac-7.1.mp4
ff -f lavfi -i "aevalsrc=$eight:s=48000:d=1:c=7.1(wide)" -c:a aac -b:a 320k -aac_pns 0 samples/aac-pce-8ch.mp4
ff -f lavfi -i "anoisesrc=color=pink:amplitude=0.3:sample_rate=44100:duration=2,highpass=f=4000" -c:a aac -b:a 48k samples/aac-pns.mp4
ff -f lavfi -i "anoisesrc=color=pink:amplitude=0.3:sample_rate=44100:duration=2:seed=1,highpass=f=4000[a];anoisesrc=color=pink:amplitude=0.3:sample_rate=44100:duration=2:seed=2,highpass=f=4000[b];[a][b]amerge=inputs=2" -c:a aac -b:a 64k samples/aac-pns-stereo.mp4
ff -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.01" -f lavfi -i "sine=frequency=330:sample_rate=48000:duration=1" -t 1 -c:v libx264 -profile:v baseline -pix_fmt yuv420p -refs 2 -c:a aac -ac 2 -aac_pns 0 samples/h264-with-audio.mp4
# Raw AAC in ADTS frames (the ADTS demuxer): stereo at 44.1 kHz, and 5.1 behind an ID3v2 tag (which the demuxer skips).
ff -f lavfi -i "aevalsrc=0.4*sin(2*PI*440*t)|0.3*sin(2*PI*660*t):s=44100:d=1" -c:a aac -b:a 96k -aac_pns 0 -f adts samples/aac-adts.aac
ff -f lavfi -i "aevalsrc=0.3*sin(2*PI*220*t)|0.3*sin(2*PI*330*t)|0.3*sin(2*PI*440*t)|0.3*sin(2*PI*55*t)|0.3*sin(2*PI*660*t)|0.3*sin(2*PI*880*t):s=48000:d=1:c=5.1" -c:a aac -b:a 256k -aac_pns 0 -f adts -write_id3v2 1 -metadata title=ManagedCodecs samples/aac-adts-5.1-id3.aac
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
python3 -I tools/mp4-edit-list.py samples/h264-edits.mp4 0 200:1024,240:9216
python3 -I tools/mp4-edit-list.py samples/h264-edit-end.mp4 0 1000:1024
ff -f lavfi -i "testsrc2=size=320x240:rate=25" -f lavfi -i "sine=frequency=330:sample_rate=48000:duration=2" -t 2 -c:v libx264 -bf 2 -g 12 -c:a aac -ac 2 -aac_pns 0 samples/h264-audio-edits.mp4
python3 -I tools/mp4-edit-list.py samples/h264-audio-edits.mp4 0 400:1024,400:15360
python3 -I tools/mp4-edit-list.py samples/h264-audio-edits.mp4 1 400:1024,400:48500

# ---- H.264 from OpenH264 ----
# Committed in samples/openh264/ (CI has no OpenH264): long-term references and the list modification and memory
# management operations that come with them, POC type 2, non-reference P frames, slices by size, changing and repeated
# parameter sets, and an SVC stream's base layer. tools/make-openh264-samples.sh says how each was made.
cp samples/openh264/*.mp4 samples/

# ---- H.264 from JM ----
# Committed in samples/jm/ (CI has no JM): field pictures (PAFF), alone and mixed with frame pictures.
# tools/make-jm-samples.sh says how each was made.
cp samples/jm/*.mp4 samples/

# ---- FFmpeg's references ----
# name.framemd5: decoded frames. name.packets.csv: demuxed packets as ffprobe lists them (the demuxer's own timestamps,
# flags with discard, MD5 of the data); name.packets.framemd5 for the codec private data (and the ADTS packets).
# name.f32: decoded audio, edit list ignored (every packet, whole; not where the edits leave out packets or repeat
# them: aac-edit-trim and the samples with several edits). name.trim.f32: decoded audio as
# FFmpeg gives it by default, trimmed by the edit list. -idct simple: FFmpeg's C IDCT for JPEG, the arithmetic the MJPEG
# decoder follows; its default picks by CPU, the same results on x64 (SSE2) but not on ARM64 (NEON rounds otherwise).
for f in samples/*.avi samples/*.mp4; do ff -idct simple -i "$f" -f framemd5 "${f%.*}.framemd5"; done
# Monochrome H.264 (by the name): FFmpeg decodes it to 4:2:0 with flat chroma, so the references take its Y plane
# alone, as it is (extractplanes; -pix_fmt gray would rescale a limited-range stream's samples to full range).
for f in samples/*gray*.mp4; do ff -i "$f" -vf extractplanes=y -f framemd5 "${f%.*}.framemd5"; done
for f in samples/*.mp4; do
  ff -copyts -i "$f" -c copy -f framemd5 "${f%.mp4}.packets.framemd5"
  ffprobe -v error -show_packets -show_data_hash MD5 -show_entries packet=stream_index,pts,dts,duration,size,flags,data_hash \
    -of csv=p=0 "$f" > "${f%.mp4}.packets.csv"
done
# Raw AAC: packets as FFmpeg's demuxer gives them (whole ADTS frames), and every decoded sample (no edit list).
for f in samples/*.aac; do
  ff -copyts -i "$f" -c copy -f framemd5 "${f%.aac}.packets.framemd5"
  ff -i "$f" -f f32le "${f%.aac}.f32"
done
for f in samples/aac-*.mp4 samples/h264-with-audio.mp4 samples/h264-audio-edits.mp4 samples/fmp4-*.mp4; do
  [[ $f == samples/aac-edit-trim.mp4 || $f == *edits* ]] || ff -ignore_editlist 1 -i "$f" -map 0:a:0 -f f32le "${f%.mp4}.f32"
  ff -i "$f" -map 0:a:0 -f f32le "${f%.mp4}.trim.f32"
done

echo "Samples and references written to samples/."
