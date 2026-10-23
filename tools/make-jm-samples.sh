#!/usr/bin/env bash
# Encodes the H.264 samples in samples/jm/ with JM's encoder (lencod, the ITU-T reference software), which writes
# what libx264 and OpenH264 never do: field pictures (PAFF), each field of a frame its own picture, in pairs whose
# second field predicts from the first, pictures that switch between frame and field coding, and B field pictures
# whose direct prediction looks into fields and frames, in 4:2:0 and 4:2:2; and bit depths 9, 12 and 14. These files
# are committed: CI has no JM (and it takes a minute a sample), so tools/make-samples.sh only copies them into samples/
# and writes their FFmpeg references like any other sample's.
#
# Usage: tools/make-jm-samples.sh <JM source tree with lencod built> [sample names, to make only those]
#   (git clone https://github.com/shihuade/JM && make -C lencod CFLAGS="-fcommon -O2"; made with JM 19.0)
set -euo pipefail

jm=$(cd "${1:?usage: tools/make-jm-samples.sh <JM tree> [names]}" && pwd)
only=" ${*:2} "
wanted() { [[ $only == "  " || $only == *" $1 "* ]]; }
cd "$(dirname "$0")/.."
out=$(pwd)/samples/jm
mkdir -p "$out"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# 320x256, 10 interlaced frames (20 fields of moving test pattern, top field first).
ffmpeg -hide_banner -loglevel error -y -f lavfi -i "testsrc2=size=320x256:rate=50" -vf tinterlace=interleave_top \
  -frames:v 10 -pix_fmt yuv420p -f rawvideo "$work/src.yuv"
# The same scrolling and with noise: smaller partitions, and JM codes more of it as fields.
ffmpeg -hide_banner -loglevel error -y -f lavfi -i "testsrc2=size=320x256:rate=50,scroll=h=0.01:v=0.005,noise=alls=6:allf=t" \
  -vf tinterlace=interleave_top -frames:v 10 -pix_fmt yuv420p -f rawvideo "$work/noisy.yuv"
# Both in 4:2:2.
ffmpeg -hide_banner -loglevel error -y -f lavfi -i "testsrc2=size=320x256:rate=50" -vf tinterlace=interleave_top \
  -frames:v 10 -pix_fmt yuv422p -f rawvideo "$work/src422.yuv"
ffmpeg -hide_banner -loglevel error -y -f lavfi -i "testsrc2=size=320x256:rate=50,scroll=h=0.01:v=0.005,noise=alls=6:allf=t" \
  -vf tinterlace=interleave_top -frames:v 10 -pix_fmt yuv422p -f rawvideo "$work/noisy422.yuv"
# Both in 4:0:0.
ffmpeg -hide_banner -loglevel error -y -f lavfi -i "testsrc2=size=320x256:rate=50" -vf tinterlace=interleave_top \
  -frames:v 10 -pix_fmt gray -f rawvideo "$work/srcgray.yuv"
ffmpeg -hide_banner -loglevel error -y -f lavfi -i "testsrc2=size=320x256:rate=50,scroll=h=0.01:v=0.005,noise=alls=6:allf=t" \
  -vf tinterlace=interleave_top -frames:v 10 -pix_fmt gray -f rawvideo "$work/noisygray.yuv"
cp "$jm/bin/encoder_main.cfg" "$work/"

# lencod name [options]: encoder_main.cfg's settings with the options on top, into $work/name.264. In 4:0:0 JM exits
# with 100 after writing the whole stream ("free_mem2Dpel: trying to free unused memory", freeing chroma planes it
# never made): that one is not a failure.
lencod() {
  local name=$1; shift
  (cd "$work" && "$jm/bin/lencod.exe" -d encoder_main.cfg -p InputFile=src.yuv -p SourceWidth=320 -p SourceHeight=256 \
    -p FramesToBeEncoded=10 -p FrameRate=25 -p OutputFile="$name.264" -p ReconFile=rec.yuv "$@" > "$name.log" 2>&1) ||
    grep -q "free_mem2Dpel: trying to free unused memory" "$work/$name.log"
}

# encode name [lencod options]: encoder_main.cfg's settings (Main profile, CABAC, 5 reference frames, QP 28, POC
# type 0) without B pictures, with the options on top, then remuxed into MP4 (one packet a field, as FFmpeg's muxer
# writes it).
encode() {
  local name=$1; shift
  wanted "$name" || return 0
  lencod "$name" -p NumberBFrames=0 "$@"
  ffmpeg -hide_banner -loglevel error -y -r 25 -i "$work/$name.264" -c copy "$out/jm-$name.mp4"
}

# encode-b name [lencod options]: the same with B pictures. A raw stream has no timestamps and FFmpeg gives its
# packets pts = dts in decoding order, which leaves B pictures out of place (and FFmpeg drops frames). So each packet
# (a field, or a frame) gets pts from its first slice's pic_order_cnt_lsb, which counts fields here (POC type 0, no
# wrap in 10 frames), dts four fields behind its position and a field's duration (else the last sample's is 1; dur=512 gives frames' instead), as
# setts expressions.
encode-b() {
  local name=$1; shift
  wanted "$name" || return 0
  lencod "$name" "$@"
  local poc
  poc=($(ffmpeg -hide_banner -i "$work/$name.264" -c copy -bsf:v trace_headers -f null - 2>&1 |
    awk '/first_mb_in_slice/ { first = ($NF == 0) } /pic_order_cnt_lsb/ && first { print $NF }'))
  local pts=${poc[-1]}
  for ((i = ${#poc[@]} - 2; i >= 0; i--)); do pts="if(eq(N\\,$i)\\,${poc[i]}\\,$pts)"; done
  ffmpeg -hide_banner -loglevel error -y -r 25 -i "$work/$name.264" -c copy \
    -bsf:v "setts=pts=($pts)*256:dts=(N-4)*${dur:-256}:duration=${dur:-256}:time_base=1/12800" "$out/jm-$name.mp4"
}

encode paff -p PicInterlace=1             # every frame as two field pictures: an I and a P field, then P fields
encode paff-cavlc -p PicInterlace=1 -p SymbolMode=0 -p SliceMode=1 -p SliceArgument=40 -p PicOrderCntType=1 \
  -p UseConstrainedIntraPred=1            # CAVLC, four slices a field, POC type 1, constrained intra prediction
encode paff-adaptive -p PicInterlace=2 -p PicOrderCntType=2 -p ProfileIDC=100 -p Transform8x8Mode=1 \
  -p IntraPeriod=4                        # frame or field pictures as fits each frame; POC type 2, 8x8 transform
encode-b paff-b -p PicInterlace=1 -p NumberBFrames=1  # B field pairs, spatial direct (co-located in fields)
encode-b paff-b-temporal -p PicInterlace=1 -p NumberBFrames=1 -p DirectModeType=0  # temporal direct
encode-b paff-b-cavlc -p PicInterlace=1 -p NumberBFrames=2 -p SymbolMode=0 -p DirectModeType=0  # two B frames, CAVLC
encode-b paff-b-adaptive -p PicInterlace=2 -p NumberBFrames=1 -p IntraPeriod=4  # B fields over frames and over
                                                                                  # fields, a B frame over fields
encode-b paff-b-adaptive-temporal -p PicInterlace=2 -p NumberBFrames=1 -p IntraPeriod=4 -p DirectModeType=0  # temporal
                                                                                  # direct; B frames over fields
# Temporal direct over the noisy source: B fields over a frame, from co-located partitions smaller than 8x8.
encode-b paff-b-noisy -p InputFile=noisy.yuv -p PicInterlace=2 -p NumberBFrames=1 -p IntraPeriod=3 -p DirectModeType=0
# 4:2:2 (High 4:2:2): field pairs with 16 chroma rows a macroblock; and frames switching between frame and field
# coding with B pictures, with CAVLC (on the noisy source: on the clean one JM codes every frame as a frame).
encode paff-422 -p InputFile=src422.yuv -p YUVFormat=2 -p ProfileIDC=122 -p PicInterlace=1
encode-b paff-422-b-adaptive -p InputFile=noisy422.yuv -p YUVFormat=2 -p ProfileIDC=122 -p PicInterlace=2 -p NumberBFrames=1 \
  -p IntraPeriod=4 -p SymbolMode=0
# 4:0:0 (High; 8 bits, where JM's chroma depth matches luma's): field pairs, and frames switching between frame and
# field coding with B pictures and CAVLC. Their references are taken as gray (make-samples.sh, by the name).
encode paff-gray -p InputFile=srcgray.yuv -p YUVFormat=0 -p ProfileIDC=100 -p PicInterlace=1
encode-b paff-gray-b-adaptive -p InputFile=noisygray.yuv -p YUVFormat=0 -p ProfileIDC=100 -p PicInterlace=2 \
  -p NumberBFrames=1 -p IntraPeriod=4 -p SymbolMode=0

# ---- Bit depths 9, 12 and 14 (libx264 writes only 8 and 10) ----
# Progressive frames (176x144, 10 of them) of a moving noisy test pattern, at each depth and chroma format the
# sample needs (noise=N sets the noise). deep name bits yuvformat [lencod options]: High 10 for 9 bits, High 4:4:4 Predictive above 10 (no
# other profile allows them), 8x8 transforms, B pictures, CABAC, QP 16 so the levels get large.
deep() {
  local name=$1 bits=$2 yuv=$3; shift 3
  wanted "$name" || return 0
  local pix=(gray yuv420p yuv422p yuv444p)
  local fmt=${pix[yuv]}
  [[ $fmt == gray ]] && fmt=gray${bits}le || fmt=${fmt}${bits}le
  ffmpeg -hide_banner -loglevel error -y -f lavfi -i "testsrc2=size=176x144:rate=25,scroll=h=0.01:v=0.005,noise=alls=${noise:-8}:allf=t" \
    -frames:v 10 -pix_fmt "$fmt" -f rawvideo "$work/deep-$name.yuv"
  local profile=244
  ((bits <= 10 && yuv <= 1)) && profile=110
  dur=512 encode-b "$name" -p InputFile="deep-$name.yuv" -p SourceWidth=176 -p SourceHeight=144 -p YUVFormat="$yuv" \
    -p SourceBitDepthLuma="$bits" -p SourceBitDepthChroma="$bits" -p OutputBitDepthLuma="$bits" -p OutputBitDepthChroma="$bits" \
    -p ProfileIDC="$profile" -p Transform8x8Mode=1 -p NumberBFrames=1 -p QPISlice=16 -p QPPSlice=16 -p QPBSlice=18 "$@"
}
deep 9bit 9 1                                     # 9 bits: High 10
deep 12bit 12 1 -p WeightedPrediction=1 -p WeightedBiprediction=1  # 12 bits, explicit weights in P and B
deep 12bit-cavlc 12 1 -p SymbolMode=0 -p IntraPeriod=3  # CAVLC
deep 14bit 14 1 -p QPISlice=-6 -p QPPSlice=-6 -p QPBSlice=-4  # 14 bits at low QP: levels past 2^15
noise=40 deep 14bit-cavlc 14 1 -p SymbolMode=0 -p QPISlice=-6 -p QPPSlice=-6 -p QPBSlice=-4  # CAVLC, level_prefix 15
          # (16 and more is in CavlcLevelTests: below QP -6 JM codes this source as I_PCM, and crashes with EnableIPCM=0)
deep 12bit-422 12 2                               # 4:2:2
deep 14bit-444 14 3                               # 4:4:4
# No 4:0:0: JM writes bit_depth_chroma 8 there, and FFmpeg refuses luma and chroma depths that differ.
