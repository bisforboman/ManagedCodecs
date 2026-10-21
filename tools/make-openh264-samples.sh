#!/usr/bin/env bash
# Encodes the H.264 samples in samples/openh264/ with OpenH264's encoder (h264enc), which writes what libx264 never
# does: POC type 2, reference list modification with long-term pictures, memory management operations 4 and 6,
# non-reference P frames (temporal layers), slices by size that start mid-row, changing SPS/PPS ids and parameter
# sets repeated in-band, SVC streams whose base layer comes with prefix NAL units, a subset SPS and enhancement
# slices that an AVC decoder skips, and temporal layers dropped to leave gaps in frame_num. These files are committed: CI has no OpenH264, so tools/make-samples.sh only
# copies them into samples/ and writes their FFmpeg references like any other sample's.
#
# Usage: tools/make-openh264-samples.sh <OpenH264 source tree with h264enc built>
#   (git clone https://github.com/cisco/openh264 && make USE_ASM=No h264enc; made with v2.7.0)
set -euo pipefail

openh264=$(cd "${1:?usage: tools/make-openh264-samples.sh <openh264 tree>}" && pwd)
cd "$(dirname "$0")/.."
out=$(pwd)/samples/openh264
mkdir -p "$out"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# 176x144, 30 frames of moving test pattern with a little noise.
ffmpeg -hide_banner -loglevel error -y -f lavfi -i "testsrc2=size=176x144:rate=25,scroll=h=0.01:v=0.005,noise=alls=2:allf=t" \
  -frames:v 30 -pix_fmt yuv420p -f rawvideo "$work/src.yuv"

# The encoder reads its configuration files relative to its working directory.
cp "$openh264"/testbin/welsenc.cfg "$openh264"/testbin/layer2.cfg "$work/"
sed 's/^PrefixNALAddingCtrl.*/PrefixNALAddingCtrl 1/' "$work/welsenc.cfg" > "$work/svc.cfg"

# encode name [h264enc options]: welsenc.cfg's settings (2 temporal layers, long-term references, CAVLC Baseline,
# increasing SPS/PPS ids) at 150 kbit/s with the options on top, then remuxed into MP4.
encode() {
  local name=$1; shift
  (cd "$work" && "$openh264/h264enc" welsenc.cfg -org src.yuv -bf "$name.264" -sw 176 -sh 144 -frin 25 -frms 30 \
    -dw 0 176 -dh 0 144 -frout 0 25 -tarb 150 -ltarb 0 150 -threadIdc 1 "$@" > "$name.log")
  ffmpeg -hide_banner -loglevel error -y -r 25 -i "$work/$name.264" -c copy "$out/oh-$name.mp4"
}

encode default                                    # temporal layers: non-reference P frames; long-term list modification
encode cabac -cabac 1 -dprofile 0 77                # the same with CABAC (Main profile; Baseline ignores -cabac)
encode ltr -numtl 1 -ltr 1 -ltrnum 2 -ltrper 4    # long-term marking: MMCO 1, 4 and 6
encode slices-num -slcmd 0 1 -slcnum 0 4          # four slices per picture
encode slices-size -slcmd 0 3 -slcsize 0 450 -nalsize 500  # slices by size, starting mid-row
encode idr-period -numtl 1 -ltr 0 -iper 8         # an IDR every 8 frames, each with new SPS and PPS ids
encode repeated-sps -spsid 2 -iper 16             # SPS and PPS repeated in-band before each IDR
encode deblock-slices -deblockIdc 2 -alphaOffset 3 -betaOffset -2 -slcmd 0 1 -slcnum 0 3  # no filtering across slices

# Two spatial layers (SVC): the AVC base layer (88x72, cropped) is what FFmpeg and we decode.
cp "$work/layer2.cfg" "$work/l0.cfg"
cp "$work/layer2.cfg" "$work/l1.cfg"
(cd "$work" && "$openh264/h264enc" svc.cfg -numl 2 -lconfig 0 l0.cfg -lconfig 1 l1.cfg -org src.yuv -bf svc.264 \
  -sw 176 -sh 144 -frin 25 -frms 30 -dw 0 88 -dh 0 72 -dw 1 176 -dh 1 144 -frout 0 25 -frout 1 25 -tarb 225 -ltarb 0 75 -ltarb 1 150 -threadIdc 1 > svc.log)
ffmpeg -hide_banner -loglevel error -y -r 25 -i "$work/svc.264" -c copy "$out/oh-svc.mp4"

# Gaps in frame_num: four temporal layers (GOP of 8; T0 at picture 0, 8, ..., T1 at 4, 12, ..., T2 at 2, 6, ...,
# T3 at the odd ones, which are not references), then whole layers dropped, as a receiver of a scalable stream does.
# T0 alone keeps frame_num 0, 4, 8, ... and T0 + T1 keeps 0, 2, 4, ...: the missing reference frames leave gaps
# (gaps_in_frame_num_value_allowed_flag is 1). OpenH264 names every reference explicitly, so these streams check that
# gaps decode, not where the inferred frames sit in the default lists (H264FrameNumGapTests does that).
ffmpeg -hide_banner -loglevel error -y -f lavfi -i "testsrc2=size=176x144:rate=25,scroll=h=0.01:v=0.005,noise=alls=2:allf=t" \
  -frames:v 64 -pix_fmt yuv420p -f rawvideo "$work/src64.yuv"
layers() {
  local name=$1 period=$2; shift 2
  (cd "$work" && "$openh264/h264enc" welsenc.cfg -org src64.yuv -bf "$name-all.264" -sw 176 -sh 144 -frin 25 -frms 64 \
    -dw 0 176 -dh 0 144 -frout 0 25 -tarb 150 -ltarb 0 150 -threadIdc 1 -numtl 4 "$@" > "$name.log")
  # Keep the parameter sets and every picture whose index is a multiple of the period (one slice per picture).
  python3 -I - "$work/$name-all.264" "$work/$name.264" "$period" <<'PY'
import re, sys
data = open(sys.argv[1], 'rb').read()
period = int(sys.argv[3])
starts = [m.start() for m in re.finditer(b'\x00\x00\x01', data)] + [len(data)]
out = bytearray()
picture = -1
for begin, end in zip(starts, starts[1:]):
    nal = data[begin:end]
    kind = nal[3] & 31
    if kind in (1, 5) and nal[4] & 0x80:  # first_mb_in_slice is 0: a new picture
        picture += 1
    if kind in (1, 5) and picture % period != 0:
        continue
    out += b'\x00' + nal
open(sys.argv[2], 'wb').write(out)
PY
  ffmpeg -hide_banner -loglevel error -y -r 25 -i "$work/$name.264" -c copy "$out/oh-$name.mp4"
}
layers gap-t0 8                # T0 only: gaps of 3, with OpenH264's long-term reference
layers gap-t0t1 4 -ltr 0       # T0 and T1: gaps of 1, short-term references only

ls -l "$out"
