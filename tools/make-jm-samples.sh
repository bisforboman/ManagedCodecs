#!/usr/bin/env bash
# Encodes the H.264 samples in samples/jm/ with JM's encoder (lencod, the ITU-T reference software), which writes
# what libx264 and OpenH264 never do: field pictures (PAFF), each field of a frame its own picture, in pairs whose
# second field predicts from the first, pictures that switch between frame and field coding, and B field pictures
# whose direct prediction looks into fields and frames, in 4:2:0, 4:2:2 and 4:0:0, also next to MBAFF frames, and
# weighted afterwards by tools/h264-add-weights.py; bit depths 9, 12 and 14; and I_PCM, cabac_init_idc 1 and 2, and
# Cb and Cr QP offsets of their own. These files
# are committed: CI has no JM (and it takes a minute a sample), so tools/make-samples.sh only copies them into samples/
# and writes their FFmpeg references like any other sample's.
#
# Usage: tools/make-jm-samples.sh <JM source tree with lencod built> [sample names, to make only those]
#   (git clone https://github.com/shihuade/JM && make -C lencod CFLAGS="-fcommon -O2"; made with JM 19.0)
set -euo pipefail

jm=$(cd "${1:?usage: tools/make-jm-samples.sh <JM tree> [names]}" && pwd)
tools=$(cd "$(dirname "$0")" && pwd)
only=" ${*:2} "
wanted() { [[ $only == "  " || $only == *" $1 "* ]]; }
cd "$(dirname "$0")/.."
out=$(pwd)/samples/jm
mkdir -p "$out"
work=$(mktemp -d)
[[ -n ${KEEP_WORK:-} ]] && echo "work: $work" || trap 'rm -rf "$work"' EXIT  # KEEP_WORK=1 keeps the logs and raw streams

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
  mux-b "$name"
}

# mux-b name: $work/name.264, with B pictures, into samples/jm/jm-name.mp4 (encode-b's timestamps).
mux-b() {
  local name=$1 poc
  poc=($(ffmpeg -hide_banner -i "$work/$name.264" -c copy -bsf:v trace_headers -f null - 2>&1 |
    awk '/first_mb_in_slice/ { first = ($NF == 0) } /pic_order_cnt_lsb/ && first { print $NF }'))
  local pts=${poc[-1]}
  for ((i = ${#poc[@]} - 2; i >= 0; i--)); do pts="if(eq(N\\,$i)\\,${poc[i]}\\,$pts)"; done
  ffmpeg -hide_banner -loglevel error -y -r 25 -i "$work/$name.264" -c copy \
    -bsf:v "setts=pts=($pts)*256:dts=(N-4)*${dur:-256}:duration=${dur:-256}:time_base=1/12800" "$out/jm-$name.mp4"
}

# weigh name base implicit|explicit: samples/jm/jm-base.mp4 with weighted prediction turned on afterwards by
# tools/h264-add-weights.py (JM crashes when it weights field pictures itself): weighted_bipred_idc 2, or explicit
# weights for P and B slices, a pred_weight_table in each slice header. JM's own decoder must decode the result exactly
# as FFmpeg does, else the rewrite is wrong.
weigh() {
  local name=$1 base=$2 mode=$3
  wanted "$name" || return 0
  ffmpeg -hide_banner -loglevel error -y -i "$out/jm-$base.mp4" -c copy -bsf:v h264_mp4toannexb -f h264 "$work/$base-in.264"
  python3 "$tools/h264-add-weights.py" "$mode" "$work/$base-in.264" "$work/$name.264"
  (cd "$work" && "$jm/bin/ldecod.exe" -i "$name.264" -o "$name-jm.yuv" > "$name-ldecod.log" 2>&1)
  ffmpeg -hide_banner -loglevel error -y -i "$work/$name.264" -fps_mode passthrough -f rawvideo "$work/$name-ff.yuv"
  cmp "$work/$name-jm.yuv" "$work/$name-ff.yuv"
  if ffmpeg -hide_banner -i "$work/$name.264" -c copy -bsf:v trace_headers -f null - 2>&1 |
    awk '/ slice_type / && $NF % 5 == 1 { b = 1 } END { exit !b }'; then
    mux-b "$name"
  else
    ffmpeg -hide_banner -loglevel error -y -r 25 -i "$work/$name.264" -c copy "$out/jm-$name.mp4"
  fi
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
# MBAFF frames (MbInterlace=1: every macroblock pair a field pair; JM refuses the adaptive 2 with PicInterlace=2) next
# to field pictures, with B pictures: references and co-located pictures of both kinds. JM supports neither its
# reference reordering nor its POC-based memory management with MBAFF.
encode-b paff-mbaff -p InputFile=noisy.yuv -p PicInterlace=2 -p MbInterlace=1 -p ReferenceReorder=0 \
  -p PocMemoryManagement=0 -p NumberBFrames=1 -p IntraPeriod=4
# 4:0:0 (High; 8 bits, where JM's chroma depth matches luma's): field pairs, and frames switching between frame and
# field coding with B pictures and CAVLC. Their references are taken as gray (make-samples.sh, by the name).
encode paff-gray -p InputFile=srcgray.yuv -p YUVFormat=0 -p ProfileIDC=100 -p PicInterlace=1
encode-b paff-gray-b-adaptive -p InputFile=noisygray.yuv -p YUVFormat=0 -p ProfileIDC=100 -p PicInterlace=2 \
  -p NumberBFrames=1 -p IntraPeriod=4 -p SymbolMode=0

# Weighted prediction in field pictures, added to the samples above (see weigh): implicit weights from field order
# counts in B fields over fields and frames, and next to MBAFF frames; explicit weights in P fields with CAVLC (four
# slices a field) and CABAC, in B fields with CAVLC and over frames and fields, and next to MBAFF frames (whose field
# macroblocks take a frame index's weights for both its fields).
weigh paff-b-adaptive-implicit paff-b-adaptive implicit
weigh paff-mbaff-implicit paff-mbaff implicit
weigh paff-cavlc-wp paff-cavlc explicit
weigh paff-wp paff explicit
weigh paff-b-cavlc-wp paff-b-cavlc explicit
weigh paff-b-adaptive-wp paff-b-adaptive explicit
weigh paff-mbaff-wp paff-mbaff explicit

# ---- Bit depths 9, 12 and 14 (libx264 writes only 8 and 10), and 8 bits for what libx264 never writes ----
# Progressive frames (176x144, 10 of them) of a moving noisy test pattern, at each depth and chroma format the
# sample needs (noise=N sets the noise). deep name bits yuvformat [lencod options]: High 10 for 9 bits, High 4:4:4 Predictive above 10 (no
# other profile allows them; High for 8 bits), 8x8 transforms, B pictures, CABAC, QP 16 so the levels get large.
deep() {
  local name=$1 bits=$2 yuv=$3; shift 3
  wanted "$name" || return 0
  local pix=(gray yuv420p yuv422p yuv444p)
  local fmt=${pix[yuv]}
  if ((bits > 8)); then [[ $fmt == gray ]] && fmt=gray${bits}le || fmt=${fmt}${bits}le; fi
  ffmpeg -hide_banner -loglevel error -y -f lavfi -i "testsrc2=size=176x144:rate=25,scroll=h=0.01:v=0.005,noise=alls=${noise:-8}:allf=t" \
    -frames:v 10 -pix_fmt "$fmt" -f rawvideo "$work/deep-$name.yuv"
  local profile=244
  ((bits <= 10 && yuv <= 1)) && profile=110
  ((bits == 8 && yuv <= 1)) && profile=100
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

# ---- 8 bits, through deep: what libx264 never writes ----
# I_PCM: on heavy noise JM codes macroblocks as I_PCM below about QP 16, all of them below about QP 12; at QP 14 (CAVLC)
# and 15 (CABAC) about half of the P pictures' macroblocks, next to intra and inter ones (few in I pictures, none in B).
noise=60 deep pcm-cavlc 8 1 -p SymbolMode=0 -p QPISlice=14 -p QPPSlice=14 -p QPBSlice=16
noise=60 deep pcm 8 1 -p QPISlice=15 -p QPPSlice=15 -p QPBSlice=17
# cabac_init_idc 1 and 2 throughout (JM's adaptive choice, in the samples above, takes 1 and 2 in some slices).
deep cabac-init1 8 1 -p ContextInitMethod=0 -p FixedModelNumber=1
deep cabac-init2 8 1 -p ContextInitMethod=0 -p FixedModelNumber=2
# Scaling matrices (JM writes them where libx264 never does): every list different, Cb's and Cr's too, from a matrix
# file made here (qm.cfg; qm-default.cfg sends some lists as useDefaultScalingMatrixFlag, a first value of 0), each list
# in the SPS, the PPS, both or neither by ScalingListPresentFlagN (1, 2, 3, 0). In the SPS only, falling back by rule A;
# in both, where an absent picture list falls back by rule B (lists 0, 3, 6, 7 to the sequence's, the others to the
# list before); CAVLC; and 4:4:4 with Cb's and Cr's 8x8 lists (8 to 11).
python3 - "$work" <<'PY'
import sys
names = ['INTRA4X4_LUMA', 'INTRA4X4_CHROMAU', 'INTRA4X4_CHROMAV', 'INTER4X4_LUMA', 'INTER4X4_CHROMAU', 'INTER4X4_CHROMAV',
         'INTRA8X8_LUMA', 'INTER8X8_LUMA', 'INTRA8X8_CHROMAU', 'INTER8X8_CHROMAU', 'INTRA8X8_CHROMAV', 'INTER8X8_CHROMAV']
for file, defaults in (('qm.cfg', ()), ('qm-default.cfg', (1, 5, 7, 10))):
    with open(f'{sys.argv[1]}/{file}', 'w') as f:
        for k, name in enumerate(names):
            n = 4 if k < 6 else 8
            values = [0] * (n * n) if k in defaults else [6 + (k * 29 + i * 7 + (i // n) * 13) % 70 for i in range(n * n)]
            f.write(f'{name} =\n' + ',\n'.join(','.join(map(str, values[r * n:(r + 1) * n])) for r in range(n)) + '\n\n')
PY
qm() { local n; for n in "$@"; do echo -n " -p ScalingListPresentFlag$((k++))=$n"; done; }
deep qm-sps 8 1 -p QmatrixFile=qm.cfg -p ScalingMatrixPresentFlag=1 $(k=0 qm 1 1 1 1 1 1 1 1)
deep qm-rule-b 8 1 -p QmatrixFile=qm.cfg -p ScalingMatrixPresentFlag=3 $(k=0 qm 1 3 2 3 1 0 1 2)
deep qm-default-cavlc 8 1 -p QmatrixFile=qm-default.cfg -p ScalingMatrixPresentFlag=3 -p SymbolMode=0 $(k=0 qm 3 3 1 2 3 3 3 1)
deep qm-444 8 3 -p QmatrixFile=qm.cfg -p ScalingMatrixPresentFlag=3 $(k=0 qm 3 1 2 3 3 1 3 3 1 2 3 0)
# Cb and Cr QP offsets of their own (chroma_qp_index_offset -3, second_chroma_qp_index_offset 4): dequantization
# and deblocking, in 4:2:0, 4:2:2 and 4:4:4 (where chroma takes the luma filter at QP_C).
deep chroma-qp 8 1 -p CbQPOffset=-3 -p CrQPOffset=4
deep chroma-qp-422 8 2 -p CbQPOffset=-3 -p CrQPOffset=4
deep chroma-qp-444 8 3 -p CbQPOffset=-3 -p CrQPOffset=4
