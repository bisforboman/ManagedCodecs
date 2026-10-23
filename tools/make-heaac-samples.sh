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

# HE-AAC v2: a mono core with SBR and parametric stereo data in the SBR extension.
if wanted heaacv2-stereo; then
  ff -f lavfi -i "aevalsrc=$music|$music2:s=44100:d=3" -c:a pcm_s16le "$work/in.wav"
  he -p 29 -b 32000 -o "$out/heaacv2-stereo.mp4" "$work/in.wav"
fi

ls -l "$out"
