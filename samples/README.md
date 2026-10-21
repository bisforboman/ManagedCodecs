# Samples

Test media is generated locally with FFmpeg and is not committed (see `.gitignore`). From the repository root:

```
tools/make-samples.sh
```

It needs ffmpeg and ffprobe (with libx264) and ImageMagick on PATH, takes about 15 seconds, and is what CI runs.
Each sample isolates something (the script's comments say what), and next to each one it writes FFmpeg's
references:

- `name.framemd5`: MD5 of every decoded frame, for MJPEG and H.264, which must be bit-exact.
- `name.packets.framemd5` (with `-copyts`, so the timestamps are the demuxer's own) and `name.packets.flags`
  (keyframe flags, from ffprobe): every demuxed MP4 packet.
- `name.f32`: decoded AAC as 32-bit floats, edit list ignored so it holds 1024 samples for every packet; AAC is
  compared by value, within a tolerance.

The decoder tests pick their samples by content, not by name: the MJPEG test decodes every sample whose demuxed
codec is MJPEG, and the H.264 test every MP4 whose avcC says Baseline (66), Main (77) or High profile (100). It
compares timestamps from the first frame's, because framemd5 moves them to start at 0 (`h264-delayed` starts at
0.4 s). The other MP4 (`mpeg4-bframes`, MPEG-4 Part 2) only feeds the demuxer tests, so its `.framemd5` is unused.

`-f framemd5` hashes FFmpeg's decoded frames in their native layout (yuvj420p, yuvj422p),
which is the same tightly packed layout `VideoFrame.WritePacked` produces.

Real camera files are the best test once the synthetic ones pass: they often omit the
Huffman tables (DHT) and use restart markers.
