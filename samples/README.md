# Samples

Test media is generated locally with FFmpeg and is not committed (see `.gitignore`).
Each `name.avi` needs a matching `name.framemd5` with FFmpeg's per-frame hashes; the
conformance tests decode the AVI and compare every frame against it.

Run from the repository root (PowerShell or bash):

```
# 4:2:0, the most common MJPEG layout
ffmpeg -f lavfi -i testsrc2=size=320x240:rate=25 -t 2 -c:v mjpeg -pix_fmt yuvj420p -q:v 3 samples/testsrc-420.avi

# 4:2:2, common from webcams
ffmpeg -f lavfi -i testsrc2=size=320x240:rate=25 -t 2 -c:v mjpeg -pix_fmt yuvj422p -q:v 3 samples/testsrc-422.avi

# Odd size, to test cropping of partial MCUs at the edges
ffmpeg -f lavfi -i testsrc2=size=321x239:rate=25 -t 1 -c:v mjpeg -pix_fmt yuvj420p -q:v 3 samples/testsrc-odd.avi

# 4:4:4
ffmpeg -f lavfi -i testsrc2=size=320x240:rate=25 -t 1 -c:v mjpeg -pix_fmt yuvj444p -q:v 3 samples/testsrc-444.avi

# Restart markers: FFmpeg writes DRI + RSTn when it encodes with slices
ffmpeg -f lavfi -i testsrc2=size=320x240:rate=25 -t 1 -c:v mjpeg -pix_fmt yuvj422p -q:v 3 -threads 4 -slices 4 samples/testsrc-rst.avi

# Annex K Huffman tables; the conformance tests also decode this one with DHT stripped
ffmpeg -f lavfi -i testsrc2=size=320x240:rate=25 -t 1 -c:v mjpeg -pix_fmt yuvj420p -q:v 3 -huffman default samples/testsrc-stdhuff.avi

# Noise at the highest quality: large coefficients exercise the IDCT's full range
ffmpeg -f lavfi -i testsrc2=size=320x240:rate=25,noise=alls=60:allf=t -t 1 -c:v mjpeg -pix_fmt yuvj420p -q:v 1 samples/noise-q1.avi

# Grayscale (FFmpeg's MJPEG encoder has no gray output, so ImageMagick encodes the frames)
mkdir gray-frames
ffmpeg -f lavfi -i testsrc2=size=322x238:rate=25 -t 1 -pix_fmt gray gray-frames/f%02d.png
magick gray-frames/f*.png -colorspace Gray -type Grayscale -quality 90 gray-frames/f%02d.jpg
ffmpeg -framerate 25 -i gray-frames/f%02d.jpg -c:v copy samples/gray.avi
```

MP4 samples. The MJPEG remux is decoded end to end; the others check the demuxer packet by
packet (H.264 with B-frames needs the edit list right, MPEG-4 Part 2 carries its codec private
data in `esds`, faststart puts `moov` before `mdat`, and the delayed one starts with an empty edit):

```
ffmpeg -i samples/testsrc-420.avi -c copy samples/mjpeg-420.mp4
ffmpeg -f lavfi -i testsrc2=size=320x240:rate=25 -t 2 -c:v libx264 -bf 2 -g 12 samples/h264-bframes.mp4
ffmpeg -f lavfi -i testsrc2=size=320x240:rate=30000/1001 -t 2 -c:v libx264 -bf 0 -movflags +faststart samples/h264-faststart.mp4
ffmpeg -f lavfi -i testsrc2=size=320x240:rate=25 -t 1 -c:v mpeg4 -bf 2 samples/mpeg4-bframes.mp4
ffmpeg -f lavfi -i testsrc2=size=320x240:rate=25 -t 1 -c:v libx264 -bf 0 -output_ts_offset 0.4 samples/h264-delayed.mp4
```

H.264 Constrained Baseline samples for the decoder, each isolating features: intra only without
the deblocking filter (at several QPs), intra with the filter and with filter offsets, P frames with
one reference and no filter, then several references with all partition sizes, fast motion past the
picture edges, multiple slices, an odd size (frame cropping), constrained intra prediction with a
chroma QP offset, and a long GOP where frame_num wraps. The 720p one is for benchmarking.

```
# bash; in PowerShell use $B = "..." and $B.Split(' ')
B="-c:v libx264 -profile:v baseline -pix_fmt yuv420p"
ffmpeg -f lavfi -i testsrc2=size=320x240:rate=25 -t 0.2 $B -g 1 -x264-params no-deblock=1 samples/h264-intra.mp4
ffmpeg -f lavfi -i testsrc2=size=320x240:rate=25,noise=alls=40:allf=t -t 0.2 $B -g 1 -qp 4 -x264-params no-deblock=1 samples/h264-intra-qp4.mp4
ffmpeg -f lavfi -i testsrc2=size=320x240:rate=25 -t 0.2 $B -g 1 -qp 51 -x264-params no-deblock=1 samples/h264-intra-qp51.mp4
ffmpeg -f lavfi -i testsrc2=size=320x240:rate=25 -t 0.2 $B -g 1 samples/h264-intra-deblock.mp4
ffmpeg -f lavfi -i testsrc2=size=320x240:rate=25,noise=alls=20:allf=t -t 0.2 $B -g 1 -qp 36 -x264-params deblock=2,-1 samples/h264-intra-deblock-offsets.mp4
ffmpeg -f lavfi -i testsrc2=size=320x240:rate=25 -t 1 $B -g 50 -refs 1 -x264-params no-deblock=1 samples/h264-p-ref1-nodeblock.mp4
ffmpeg -f lavfi -i testsrc2=size=320x240:rate=25 -t 2 $B -refs 4 -x264-params partitions=all samples/h264-p-refs.mp4
ffmpeg -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.013:v=-0.021,noise=alls=12:allf=t" -t 2 $B -refs 3 -qp 22 -x264-params partitions=all:me=umh:merange=32 samples/h264-p-motion.mp4
ffmpeg -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.01" -t 1 $B -refs 2 -x264-params slices=4 samples/h264-slices.mp4
ffmpeg -f lavfi -i "testsrc2=size=321x239:rate=25,scroll=v=0.02" -t 1 $B -refs 2 samples/h264-odd-size.mp4
ffmpeg -f lavfi -i "testsrc2=size=320x240:rate=25,scroll=h=0.02,noise=alls=10:allf=t" -t 1 $B -refs 2 -x264-params constrained-intra=1:chroma-qp-offset=4 samples/h264-constrained-intra.mp4
ffmpeg -f lavfi -i "testsrc2=size=176x144:rate=25,scroll=h=0.01" -t 3 $B -refs 4 -g 1000 samples/h264-long-gop.mp4
ffmpeg -f lavfi -i "testsrc2=size=1280x720:rate=25,scroll=h=0.005,noise=alls=6:allf=t" -t 2 $B -refs 3 samples/h264-bench-720p.mp4
```

Then write the reference hashes for every sample. `name.framemd5` hashes decoded frames;
`name.packets.framemd5` hashes the demuxed packets and `name.packets.flags` lists their keyframe
flags. `-copyts` keeps FFmpeg from moving the
timestamps to start at zero, so they are the demuxer's own:

```
# bash
for f in samples/*.avi samples/*.mp4; do ffmpeg -i "$f" -f framemd5 "${f%.*}.framemd5"; done
for f in samples/*.mp4; do ffmpeg -copyts -i "$f" -c copy -f framemd5 "${f%.mp4}.packets.framemd5"; done
for f in samples/*.mp4; do ffprobe -v error -select_streams v:0 -show_packets -show_entries packet=flags -of csv=p=0 "$f" > "${f%.mp4}.packets.flags"; done
# PowerShell
Get-ChildItem samples/*.avi, samples/*.mp4 | ForEach-Object { ffmpeg -i $_ -f framemd5 ($_.FullName -replace '\.\w+$', '.framemd5') }
Get-ChildItem samples/*.mp4 | ForEach-Object { ffmpeg -copyts -i $_ -c copy -f framemd5 ($_.FullName -replace '\.mp4$', '.packets.framemd5') }
Get-ChildItem samples/*.mp4 | ForEach-Object { ffprobe -v error -select_streams v:0 -show_packets -show_entries packet=flags -of csv=p=0 $_ | Set-Content ($_.FullName -replace '\.mp4$', '.packets.flags') }
```

(ImageMagick 6 calls the command `convert` instead of `magick`.)

The decoder tests pick their samples by content, not by name: the MJPEG test decodes every sample
whose demuxed codec is MJPEG, and the H.264 test every MP4 whose avcC says Baseline profile (66).
The other MP4s (`h264-bframes`, `h264-faststart` and `h264-delayed` are High profile, `mpeg4-bframes`
is MPEG-4 Part 2) only feed the demuxer tests, so their `.framemd5` files are unused.

`-f framemd5` hashes FFmpeg's decoded frames in their native layout (yuvj420p, yuvj422p),
which is the same tightly packed layout `VideoFrame.WritePacked` produces.

Real camera files are the best test once the synthetic ones pass: they often omit the
Huffman tables (DHT) and use restart markers.
