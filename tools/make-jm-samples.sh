#!/usr/bin/env bash
# Encodes the H.264 samples in samples/jm/ with JM's encoder (lencod, the ITU-T reference software), which writes
# what libx264 and OpenH264 never do: field pictures (PAFF), each field of a frame its own picture, in pairs whose
# second field predicts from the first, and pictures that switch between frame and field coding. These files are
# committed: CI has no JM (and it takes a minute a sample), so tools/make-samples.sh only copies them into samples/
# and writes their FFmpeg references like any other sample's.
#
# Usage: tools/make-jm-samples.sh <JM source tree with lencod built>
#   (git clone https://github.com/shihuade/JM && make -C lencod CFLAGS="-fcommon -O2"; made with JM 19.0)
set -euo pipefail

jm=$(cd "${1:?usage: tools/make-jm-samples.sh <JM tree>}" && pwd)
cd "$(dirname "$0")/.."
out=$(pwd)/samples/jm
mkdir -p "$out"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# 320x256, 10 interlaced frames (20 fields of moving test pattern, top field first).
ffmpeg -hide_banner -loglevel error -y -f lavfi -i "testsrc2=size=320x256:rate=50" -vf tinterlace=interleave_top \
  -frames:v 10 -pix_fmt yuv420p -f rawvideo "$work/src.yuv"
cp "$jm/bin/encoder_main.cfg" "$work/"

# encode name [lencod options]: encoder_main.cfg's settings (Main profile, CABAC, 5 reference frames, QP 28) without
# B pictures (B field pictures are not supported yet), with the options on top, then remuxed into MP4 (one packet a
# field, as FFmpeg's muxer writes it).
encode() {
  local name=$1; shift
  (cd "$work" && "$jm/bin/lencod.exe" -d encoder_main.cfg -p InputFile=src.yuv -p SourceWidth=320 -p SourceHeight=256 \
    -p FramesToBeEncoded=10 -p FrameRate=25 -p NumberBFrames=0 -p OutputFile="$name.264" -p ReconFile=rec.yuv "$@" > "$name.log")
  ffmpeg -hide_banner -loglevel error -y -r 25 -i "$work/$name.264" -c copy "$out/jm-$name.mp4"
}

encode paff -p PicInterlace=1             # every frame as two field pictures: an I and a P field, then P fields
encode paff-cavlc -p PicInterlace=1 -p SymbolMode=0 -p SliceMode=1 -p SliceArgument=40 -p PicOrderCntType=1 \
  -p UseConstrainedIntraPred=1            # CAVLC, four slices a field, POC type 1, constrained intra prediction
encode paff-adaptive -p PicInterlace=2 -p PicOrderCntType=2 -p ProfileIDC=100 -p Transform8x8Mode=1 \
  -p IntraPeriod=4                        # frame or field pictures as fits each frame; POC type 2, 8x8 transform
