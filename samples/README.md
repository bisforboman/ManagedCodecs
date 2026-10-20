# Samples

Test media is generated locally with FFmpeg and is not committed (see `.gitignore`).
Each `name.avi` needs a matching `name.framemd5` with FFmpeg's per-frame hashes; the
conformance tests decode the AVI and compare every frame against it.

Run from the repository root (PowerShell or bash):

```
# 4:2:0, the most common MJPEG layout
ffmpeg -f lavfi -i testsrc2=size=320x240:rate=25 -t 2 -c:v mjpeg -pix_fmt yuvj420p -q:v 3 samples/testsrc-420.avi
ffmpeg -i samples/testsrc-420.avi -f framemd5 samples/testsrc-420.framemd5

# 4:2:2, common from webcams
ffmpeg -f lavfi -i testsrc2=size=320x240:rate=25 -t 2 -c:v mjpeg -pix_fmt yuvj422p -q:v 3 samples/testsrc-422.avi
ffmpeg -i samples/testsrc-422.avi -f framemd5 samples/testsrc-422.framemd5

# Odd size, to test cropping of partial MCUs at the edges
ffmpeg -f lavfi -i testsrc2=size=321x239:rate=25 -t 1 -c:v mjpeg -pix_fmt yuvj420p -q:v 3 samples/testsrc-odd.avi
ffmpeg -i samples/testsrc-odd.avi -f framemd5 samples/testsrc-odd.framemd5
```

`-f framemd5` hashes FFmpeg's decoded frames in their native layout (yuvj420p, yuvj422p),
which is the same tightly packed layout `VideoFrame.WritePacked` produces.

Real camera files are the best test once the synthetic ones pass: they often omit the
Huffman tables (DHT) and use restart markers.
