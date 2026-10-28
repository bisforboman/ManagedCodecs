#!/usr/bin/env bash
# Encodes the HE-AAC samples in samples/heaac/ with the Fraunhofer FDK AAC encoder: FFmpeg's own AAC encoder writes no
# spectral band replication (SBR) or parametric stereo (PS), and the libfdk-aac that Linux distributions ship is built
# without them. These files are committed: CI has no full fdk-aac, so tools/make-samples.sh only copies them into
# samples/ and writes their FFmpeg references like any other sample's.
#
# Usage: tools/make-heaac-samples.sh <fdk-aac source tree, built> [sample names, to make only those]
#   (git clone https://github.com/mstorsjo/fdk-aac && cmake -S fdk-aac -B fdk-aac/build -DBUILD_PROGRAMS=ON &&
#    cmake --build fdk-aac/build; made with commit c41c3c7, and the fdkaac 1.0.0 front end (apt install fdkaac) run
#    against that library for MP4 output; ADTS comes from the tree's own aac-enc)
set -euo pipefail

fdk=$(cd "${1:?usage: tools/make-heaac-samples.sh <fdk-aac tree> [names]}" && pwd)/build
only=" ${*:2} "
wanted() { [[ $only == "  " || $only == *" $1 "* ]]; }
cd "$(dirname "$0")/.."
out=$(pwd)/samples/heaac
mkdir -p "$out"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
ff() { ffmpeg -hide_banner -loglevel error -y "$@"; }
# fdkaac's MP4 output: -p 5 is HE-AAC (SBR), 29 HE-AAC v2 (SBR and PS); bitrates in bit/s.
he() { LD_LIBRARY_PATH=$fdk fdkaac --silent "$@"; }

# Tones with bursts of noise, on both channels: transients make the encoder vary the SBR time grid (FIXVAR, VARFIX
# and VARVAR frames, more envelopes), a tone that starts high mid-stream makes it add sinusoids (bs_add_harmonic).
music="0.3*sin(2*PI*440*t)+0.1*sin(2*PI*3500*t)+0.2*(random(0)-0.5)*exp(-40*mod(t\,0.35))+0.15*gte(t\,1)*sin(2*PI*11000*t)"
music2="0.3*sin(2*PI*660*t)+0.08*sin(2*PI*5200*t)+0.2*(random(1)-0.5)*exp(-40*mod(t+0.1\,0.35))+0.12*gte(t\,1.2)*sin(2*PI*9500*t)"

# Stereo at 44.1 kHz (a 22.05 kHz core), the default HE-AAC bitrate range: the common case.
if wanted heaac-stereo-44k; then
  ff -f lavfi -i "aevalsrc=$music|$music2:s=44100:d=3" -c:a pcm_s16le "$work/in.wav"
  he -p 5 -b 48000 -o "$out/heaac-stereo-44k.mp4" "$work/in.wav"
fi

# Mono at 48 kHz (a 24 kHz core): another rate's frequency tables, a single channel element.
if wanted heaac-mono-48k; then
  ff -f lavfi -i "aevalsrc=$music:s=48000:d=3" -c:a pcm_s16le "$work/in.wav"
  he -p 5 -b 32000 -o "$out/heaac-mono-48k.mp4" "$work/in.wav"
fi

# Stereo at a low bitrate and 32 kHz (a 16 kHz core): the encoder couples the pair (level and balance envelopes) and
# picks narrower SBR ranges.
if wanted heaac-stereo-low; then
  ff -f lavfi -i "aevalsrc=$music|$music2:s=32000:d=3" -c:a pcm_s16le "$work/in.wav"
  he -p 5 -b 20000 -o "$out/heaac-stereo-low.mp4" "$work/in.wav"
fi

# 5.1 at 48 kHz: SBR for each SCE and CPE, none for the LFE.
if wanted heaac-5.1; then
  ff -f lavfi -i "aevalsrc=$music|$music2|0.3*sin(2*PI*330*t)|0.3*sin(2*PI*55*t)|$music|$music2:s=48000:d=2:c=5.1" \
    -c:a pcm_s16le "$work/in.wav"
  he -p 5 -b 128000 -o "$out/heaac-5.1.mp4" "$work/in.wav"
fi

# ADTS (raw .aac) from the tree's aac-enc: no AudioSpecificConfig, so SBR is signalled only by its data in the fill
# elements (implicit signalling).
if wanted heaac-adts; then
  ff -f lavfi -i "aevalsrc=$music|$music2:s=44100:d=2" -c:a pcm_s16le "$work/in.wav"
  "$fdk/aac-enc" -t 5 -r 48000 "$work/in.wav" "$out/heaac-adts.aac" > /dev/null
fi

# The SBR header's second part (bs_header_extra_2): the FDK encoder writes it only with settings other than its
# defaults, which it never chooses, so decoders take the spec's (2 limiter bands an octave, limiter gains of 0 dB,
# frequency interpolation, no smoothing over time). A copy of the tree with three of them changed (3 bands, -3 dB, no
# interpolation; its smoothing setting, on, then goes out with them) makes a stream that uses the other paths.
if wanted heaac-header2; then
  git -C "$fdk/.." archive HEAD | (mkdir "$work/fdk2" && tar -x -C "$work/fdk2")
  sed -i 's/config->sbr_limiter_bands = SBR_LIMITER_BANDS_DEFAULT;/config->sbr_limiter_bands = 3;/
    s/config->sbr_limiter_gains = SBR_LIMITER_GAINS_DEFAULT;/config->sbr_limiter_gains = 0;/
    s/config->sbr_interpol_freq = SBR_INTERPOL_FREQ_DEFAULT;/config->sbr_interpol_freq = 0;/' \
    "$(find "$work/fdk2" -path '*libSBRenc/src/sbr_encoder.cpp')"
  cmake -S "$work/fdk2" -B "$work/fdk2/build" > /dev/null && cmake --build "$work/fdk2/build" -j4 > /dev/null
  ff -f lavfi -i "aevalsrc=$music|$music2:s=44100:d=3" -c:a pcm_s16le "$work/in.wav"
  LD_LIBRARY_PATH=$work/fdk2/build fdkaac --silent -p 5 -b 48000 -o "$out/heaac-header2.mp4" "$work/in.wav"
fi

# Downsampled SBR: the config gives SBR the core's own rate (44.1 kHz, sync extension), so the decoder runs SBR as for
# 88.2 kHz but synthesizes only the lower 32 subbands, at the core's rate. The FDK encoder chooses it only for AAC ELD;
# a copy of the tree with its HE-AAC default changed (sbrRatio 1 instead of 2) writes it. With parametric stereo the
# encoder stays dual-rate, so no sample has both.
if wanted heaac-downsampled; then
  git -C "$fdk/.." archive HEAD | (mkdir "$work/fdk3" && tar -x -C "$work/fdk3")
  file=$(find "$work/fdk3" -path '*libAACenc/src/aacenc_lib.cpp')
  python3 - "$file" <<'PY'
import sys
path = sys.argv[1]
source = open(path).read()
default = "    } else {\n      hAacConfig->sbrRatio = 2;\n    }"
assert source.count(default) == 1, "the encoder's SBR ratio default moved"
open(path, "w").write(source.replace(default, "    } else {\n      hAacConfig->sbrRatio = 1;\n    }"))
PY
  cmake -S "$work/fdk3" -B "$work/fdk3/build" > /dev/null && cmake --build "$work/fdk3/build" -j4 > /dev/null
  # Noise under the tones: a subband holding one pure sinusoid makes the HF generator's prediction nearly singular,
  # which FFmpeg's single precision magnifies (docs/conformance.md), and this sample's narrower band leaves more of its
  # high band to the 11 kHz tone. A third as much noise still left 4e-3 on x64 and 1e-2 under FFmpeg's ARM64 build.
  ff -f lavfi -i "aevalsrc=$music+0.01*(random(2)-0.5)|$music2+0.01*(random(3)-0.5):s=44100:d=3" -c:a pcm_s16le "$work/in.wav"
  LD_LIBRARY_PATH=$work/fdk3/build fdkaac --silent -p 5 -b 48000 -o "$out/heaac-downsampled.mp4" "$work/in.wav"
fi

# AAC LC stereo at 64 kbit/s: the FDK encoder sets M/S in bands both channels fill with noise (the spec's correlated
# noise), which FFmpeg decodes with noise of its own in each channel (docs/conformance.md); FFmpeg's encoder never does.
if wanted aac-fdk-pns-ms; then
  ff -f lavfi -i "aevalsrc=0.3*sin(2*PI*440*t)+0.1*sin(2*PI*6000*t)|0.25*sin(2*PI*660*t)+0.1*sin(2*PI*6100*t):s=48000:d=1" \
    -c:a pcm_s16le "$work/in.wav"
  "$fdk/aac-enc" -r 64000 "$work/in.wav" "$out/aac-fdk-pns-ms.aac" > /dev/null
fi

# AAC LC 5.1 with its channels in a program config element (channel configuration 0) rather than configuration 6, as
# the spec lists them (centre, front pair, back pair, LFE): FFmpeg names that layout and outputs it in WAVE order,
# unlike its own encoder's program config elements, which it outputs as listed (docs/conformance.md). The encoder has
# no setting for it, so a copy of the tree writes one for every channel mode; ADTS, the element repeated every 10th
# frame (tools/make-samples.sh remuxes it into MP4). At 256 kbit/s, no noise substitution: FFmpeg does not correlate
# noise in M/S bands as the FDK encoder means it to.
if wanted aac-fdk-pce; then
  git -C "$fdk/.." archive HEAD | (mkdir "$work/fdk4" && tar -x -C "$work/fdk4")
  file=$(find "$work/fdk4" -path '*libAACenc/src/aacenc_lib.cpp')
  grep -q 'cc->channelConfigZero = 0;' "$file" || { echo "the encoder's channelConfigZero default moved" >&2; exit 1; }
  sed -i 's/cc->channelConfigZero = 0;/cc->channelConfigZero = 1;/' "$file"
  cmake -S "$work/fdk4" -B "$work/fdk4/build" -DBUILD_PROGRAMS=ON > /dev/null && cmake --build "$work/fdk4/build" -j4 > /dev/null
  ff -f lavfi -i "aevalsrc=0.2*sin(2*PI*147*t)|0.2*sin(2*PI*247*t)|0.2*sin(2*PI*347*t)|0.2*sin(2*PI*47*t)|0.2*sin(2*PI*547*t)|0.2*sin(2*PI*647*t):s=48000:d=1:c=5.1" \
    -c:a pcm_s16le "$work/in.wav"
  LD_LIBRARY_PATH=$work/fdk4/build "$work/fdk4/build/aac-enc" -r 256000 "$work/in.wav" "$out/aac-fdk-pce.aac" > /dev/null
fi

# HE-AAC v2: a mono core with SBR and parametric stereo data in the SBR extension.
if wanted heaacv2-stereo; then
  ff -f lavfi -i "aevalsrc=$music|$music2:s=44100:d=3" -c:a pcm_s16le "$work/in.wav"
  he -p 29 -b 32000 -o "$out/heaacv2-stereo.mp4" "$work/in.wav"
fi

# Parametric stereo with mixing procedure Rb (icc_mode 3 and 4): the encoder writes only Ra (0 and 1). Rb reads the
# same IID and ICC parameters through another mixing matrix, so a copy of the tree that adds 3 to the mode it writes
# makes a valid Rb stream of the encoder's own parameters.
if wanted heaacv2-rb; then
  git -C "$fdk/.." archive HEAD | (mkdir "$work/fdk5" && tar -x -C "$work/fdk5")
  file=$(find "$work/fdk5" -path '*libSBRenc/src/ps_encode.cpp')
  grep -q 'hPsOut->iccMode = getICCMode(psBands, hPsData->iccQuantMode);' "$file" || { echo "the encoder's iccMode line moved" >&2; exit 1; }
  sed -i 's/hPsOut->iccMode = getICCMode(psBands, hPsData->iccQuantMode);/hPsOut->iccMode = getICCMode(psBands, hPsData->iccQuantMode) + 3;/' "$file"
  cmake -S "$work/fdk5" -B "$work/fdk5/build" > /dev/null && cmake --build "$work/fdk5/build" -j4 > /dev/null
  ff -f lavfi -i "aevalsrc=$music|$music2:s=44100:d=3" -c:a pcm_s16le "$work/in.wav"
  LD_LIBRARY_PATH=$work/fdk5/build fdkaac --silent -p 29 -b 32000 -o "$out/heaacv2-rb.mp4" "$work/in.wav"
fi

# Parametric stereo with IPD and OPD (inter-channel and overall phase differences, the ps_extension): the encoder has
# a writer for them but never enables it. A copy of the tree enables it with phases of its own (they are not measured
# from the signal): non-decreasing over the bands, as the writer's delta coding without wrap-around needs, moving from
# frame to frame so the decoder's smoothing over envelopes shows; and the writer's band count fixed to the spec's
# (it writes the IID's 10 or 20 bands where IPD and OPD have 5 or 11, which FFmpeg reads), its size counting the
# enable_ipdopd bit (it leaves it out, and the SBR writer then asserts), and the extension written in every frame, not
# only in those with a PS header (a header's enable_ext holds until the next one).
patch_ipd() {
  enc=$(find "$1" -path '*libSBRenc/src/ps_encode.cpp')
  bitenc=$(find "$1" -path '*libSBRenc/src/ps_bitenc.cpp')
  grep -q 'hPsOut->enableIpdOpd = 0;' "$enc" || { echo "the encoder's enableIpdOpd line moved" >&2; exit 1; }
  perl -0pi -e 's/hPsOut->enableIpdOpd = 0;/hPsOut->enableIpdOpd = 1;/;
    s/(FDKmemclear\(hPsOut->opdLast, PS_MAX_BANDS \* sizeof\(INT\)\);)/$1
    { static INT frame; frame++;
      for (env = 0; env < nEnvelopes; env++)
        for (band = 0; band < PS_MAX_BANDS; band++) {
          hPsOut->ipd[env][band] = fMin(7, (band + frame + env) \/ 3);
          hPsOut->opd[env][band] = fMin(7, (band + 2 * frame) \/ 4 % 8);
        } }/' "$enc"
  perl -0pi -e 's/(psOut->ipd\[env\], ipdLast,\s*)getNoBands\(psOut->iidMode\)/$1(getNoBands(psOut->iidMode) == PS_BANDS_COARSE ? 5 : getNoBands(psOut->iidMode) == PS_BANDS_MID ? 11 : 17)/;
    s/(psOut->opd\[env\], opdLast,\s*)getNoBands\(psOut->iidMode\)/$1(getNoBands(psOut->iidMode) == PS_BANDS_COARSE ? 5 : getNoBands(psOut->iidMode) == PS_BANDS_MID ? 11 : 17)/' "$bitenc"
  perl -0pi -e 's/^  FDKsbrEnc_WriteBits_ps\(hBitBuf, psOut->enableIpdOpd, 1\);/  bitCnt += FDKsbrEnc_WriteBits_ps(hBitBuf, psOut->enableIpdOpd, 1);/m;
    s/INT psExtEnable = 0;/INT psExtEnable = psOut != NULL && psOut->enableIpdOpd;/' "$bitenc"
  grep -q 'bitCnt += FDKsbrEnc_WriteBits_ps(hBitBuf, psOut->enableIpdOpd, 1);' "$bitenc" &&
    grep -q 'INT psExtEnable = psOut != NULL' "$bitenc" || { echo "the IPD writer's bit count did not patch" >&2; exit 1; }
  grep -q 'PS_BANDS_MID ? 11 : 17' "$bitenc" || { echo "the IPD writer's band count did not patch" >&2; exit 1; }
}

if wanted heaacv2-ipd; then
  git -C "$fdk/.." archive HEAD | (mkdir "$work/fdk6" && tar -x -C "$work/fdk6")
  patch_ipd "$work/fdk6"
  cmake -S "$work/fdk6" -B "$work/fdk6/build" > /dev/null && cmake --build "$work/fdk6/build" -j4 > /dev/null
  ff -f lavfi -i "aevalsrc=$music|$music2:s=44100:d=3" -c:a pcm_s16le "$work/in.wav"
  LD_LIBRARY_PATH=$work/fdk6/build fdkaac --silent -p 29 -b 32000 -o "$out/heaacv2-ipd.mp4" "$work/in.wav"
fi

# Parametric stereo in 34 bands (iid_mode or icc_mode 2 or 5): the encoder analyses 20 bands and its writer turns
# modes 2 and 5 into 10 bands. A copy of the tree widens its band arrays to 34, writes them for modes 2 and 5, and maps
# each envelope's 20 values to 34 by the decoder's 20-to-34 map, so the 34 parameters vary as a real signal's do. Every
# 8 frames with envelopes the header turns to the next of (20, 20), (34, 34), (34, 20) and (20, 34) bands of IID and
# ICC, so a frame is processed in 34 bands with either parameter mapped up, and the decoder switches between 20 and
# 34 (a header in every such frame); time deltas stay only where the previous envelope had the same band count.
patch_34() {
  enc=$(find "$1" -path '*libSBRenc/src/ps_encode.cpp')
  bitenc=$(find "$1" -path '*libSBRenc/src/ps_bitenc.cpp')
  const=$(find "$1" -path '*libSBRenc/src/ps_const.h')
  grep -q 'PS_MAX_BANDS = PS_BANDS_MID' "$const" || { echo "the encoder's PS_MAX_BANDS moved" >&2; exit 1; }
  sed -i 's/PS_MAX_BANDS = PS_BANDS_MID/PS_MAX_BANDS = 34/' "$const"
  perl -0pi -e 's/(    case 2:\n    case 5:  \/\* fine not supported \*\/\n)/$1      noBands = 34;\n      break;\n/' "$bitenc"
  grep -q 'noBands = 34;' "$bitenc" || { echo "the writer's band count did not patch" >&2; exit 1; }
  perl -0pi -e 's/(      hPsOut->iccLast\[band\] = hPsData->iccIdxLast\[band\];\n    \}\n)/$1
    { static const INT from[34][2] = {{0, 0}, {0, 1}, {1, 1}, {2, 2}, {2, 3}, {3, 3}, {4, 4}, {4, 4}, {5, 5}, {5, 5},
        {6, 6}, {7, 7}, {8, 8}, {8, 8}, {9, 9}, {9, 9}, {10, 10}, {11, 11}, {12, 12}, {13, 13}, {14, 14}, {14, 14},
        {15, 15}, {15, 15}, {16, 16}, {16, 16}, {17, 17}, {17, 17}, {18, 18}, {18, 18}, {18, 18}, {18, 18}, {19, 19},
        {19, 19}};
      static INT frame, lastBands[2] = {-1, -1}, lastValues[2][34];
      INT state = frame++ \/ 8 % 4, p, e, b;
      INT want34[2] = {state == 1 || state == 2, state == 1 || state == 3};
      INT *mode[2] = {&hPsOut->iidMode, &hPsOut->iccMode};
      INT enable[2] = {hPsOut->enableIID, hPsOut->enableICC};
      PS_DELTA *delta[2] = {hPsOut->deltaIID, hPsOut->deltaICC};
      INT (*values[2])[PS_MAX_BANDS] = {hPsOut->iid, hPsOut->icc};
      INT *last[2] = {hPsOut->iidLast, hPsOut->iccLast};
      hPsOut->enablePSHeader = 1;
      for (p = 0; p < 2; p++) {
        INT bands = want34[p] && psBands == 20 ? 34 : psBands;
        if (bands == 34) *mode[p] += 1;
        if (!enable[p]) { lastBands[p] = -1; continue; }
        for (e = 0; bands == 34 && e < nEnvelopes; e++) {
          INT source[20];
          for (b = 0; b < 20; b++) source[b] = values[p][e][b];
          for (b = 0; b < 34; b++) values[p][e][b] = (source[from[b][0]] + source[from[b][1]]) \/ 2;
        }
        if (lastBands[p] != bands) delta[p][0] = PS_DELTA_FREQ;
        for (b = 0; b < bands; b++) last[p][b] = lastValues[p][b];
        for (b = 0; b < bands; b++) lastValues[p][b] = values[p][nEnvelopes - 1][b];
        lastBands[p] = bands;
      } }
/' "$enc"
  grep -q 'INT want34\[2\]' "$enc" || { echo "the encoder's 34-band mapping did not patch" >&2; exit 1; }
}

if wanted heaacv2-34; then
  git -C "$fdk/.." archive HEAD | (mkdir "$work/fdk7" && tar -x -C "$work/fdk7")
  patch_34 "$work/fdk7"
  cmake -S "$work/fdk7" -B "$work/fdk7/build" > /dev/null && cmake --build "$work/fdk7/build" -j4 > /dev/null
  ff -f lavfi -i "aevalsrc=$music|$music2:s=44100:d=3" -c:a pcm_s16le "$work/in.wav"
  LD_LIBRARY_PATH=$work/fdk7/build fdkaac --silent -p 29 -b 32000 -o "$out/heaacv2-34.mp4" "$work/in.wav"
fi

# Both: phases in 34 bands (17 of them), in 20 bands, and mapped from 11 to 17 where ICC alone is in 34; the history of
# the phases' smoothing starting over at each switch.
if wanted heaacv2-34-ipd; then
  git -C "$fdk/.." archive HEAD | (mkdir "$work/fdk8" && tar -x -C "$work/fdk8")
  patch_34 "$work/fdk8"
  patch_ipd "$work/fdk8"
  cmake -S "$work/fdk8" -B "$work/fdk8/build" > /dev/null && cmake --build "$work/fdk8/build" -j4 > /dev/null
  ff -f lavfi -i "aevalsrc=$music|$music2:s=44100:d=3" -c:a pcm_s16le "$work/in.wav"
  LD_LIBRARY_PATH=$work/fdk8/build fdkaac --silent -p 29 -b 32000 -o "$out/heaacv2-34-ipd.mp4" "$work/in.wav"
fi

ls -l "$out"
