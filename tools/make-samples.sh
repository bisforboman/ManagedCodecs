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

# ---- MP4 for the demuxer ----
# The MJPEG remux is decoded end to end; H.264 with B-frames needs the edit list right, MPEG-4 Part 2 carries its
# codec private data in esds, faststart puts moov before mdat, and the delayed one starts with an empty edit.
ff -i samples/testsrc-420.avi -c copy samples/mjpeg-420.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 2 -c:v libx264 -bf 2 -g 12 samples/h264-bframes.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=30000/1001 -t 2 -c:v libx264 -bf 0 -movflags +faststart samples/h264-faststart.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 1 -c:v mpeg4 -bf 2 samples/mpeg4-bframes.mp4
ff -f lavfi -i testsrc2=size=320x240:rate=25 -t 1 -c:v libx264 -bf 0 -output_ts_offset 0.4 samples/h264-delayed.mp4

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

# ---- FFmpeg's references ----
# name.framemd5: decoded frames. name.packets.framemd5: demuxed packets (-copyts keeps the demuxer's own timestamps).
# name.packets.flags: keyframe flags (framemd5 has no flags column). name.f32: decoded audio, edit list ignored.
for f in samples/*.avi samples/*.mp4; do ff -i "$f" -f framemd5 "${f%.*}.framemd5"; done
for f in samples/*.mp4; do
  ff -copyts -i "$f" -c copy -f framemd5 "${f%.mp4}.packets.framemd5"
  ffprobe -v error -select_streams v:0 -show_packets -show_entries packet=flags -of csv=p=0 "$f" > "${f%.mp4}.packets.flags"
done
for f in samples/aac-*.mp4 samples/h264-with-audio.mp4; do ff -ignore_editlist 1 -i "$f" -map 0:a:0 -f f32le "${f%.mp4}.f32"; done

echo "Samples and references written to samples/."
