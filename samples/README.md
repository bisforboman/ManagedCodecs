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

Then write the reference hashes for every sample:

```
# bash
for f in samples/*.avi; do ffmpeg -i "$f" -f framemd5 "${f%.avi}.framemd5"; done
# PowerShell
Get-ChildItem samples/*.avi | ForEach-Object { ffmpeg -i $_ -f framemd5 ($_.FullName -replace '\.avi$', '.framemd5') }
```

(ImageMagick 6 calls the command `convert` instead of `magick`.)

`-f framemd5` hashes FFmpeg's decoded frames in their native layout (yuvj420p, yuvj422p),
which is the same tightly packed layout `VideoFrame.WritePacked` produces.

Real camera files are the best test once the synthetic ones pass: they often omit the
Huffman tables (DHT) and use restart markers.
