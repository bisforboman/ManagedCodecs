# ManagedCodecs

Pure managed C# media codecs: no native binaries, Native AOT friendly.
The first goal is a baseline MJPEG decoder whose output is **bit-exact** with FFmpeg,
then the same approach for H.264 + AAC in MP4.

## Layout

| Project | What it holds |
| --- | --- |
| `src/ManagedCodecs.Core` | `VideoFrame`, `PixelFormat`, `Packet`, `Rational`, `IDemuxer`, `IVideoDecoder` |
| `src/ManagedCodecs.Containers` | `AviDemuxer`, `Mp4Demuxer`, and `Demuxer.Open` to pick one by content |
| `src/ManagedCodecs.Mjpeg` | `MjpegDecoder`: baseline/extended Huffman JPEG, bit-exact with FFmpeg |
| `src/ManagedCodecs.H264` | `H264Decoder`: Constrained Baseline H.264 (CAVLC, I/P, deblocking), bit-exact |
| `src/ManagedCodecs.Aac` | `AacDecoder`: AAC LC (mono to 7.1, M/S, intensity, PNS, TNS), within 3e-7 of FFmpeg |
| `src/Shared` | Source files linked into several codec projects (the VLC decoder) |
| `tests/ManagedCodecs.Tests` | Unit tests and FFmpeg conformance tests (framemd5 for frames and packets) |
| `benchmarks/ManagedCodecs.Benchmarks` | BenchmarkDotNet decode benchmark |

## Getting started

Requires the .NET 10 SDK. FFmpeg on `PATH` is only needed to generate test samples.

```
dotnet build
dotnet test
```

The conformance tests need samples (see `samples/README.md`) and fail if `samples/` has no
`.avi` + `.framemd5` pairs, which is intentional. They decode every frame and compare its MD5 with
FFmpeg's.

## Benchmarking

```
dotnet run -c Release --project benchmarks/ManagedCodecs.Benchmarks
ffmpeg -benchmark -threads 1 -stream_loop 39 -i samples/testsrc-420.avi -f null -
```

Divide FFmpeg's `utime` by the number of frames decoded (40 loops of the file). Its figure also
includes demuxing and passing frames on, so it slightly flatters us. `--filter *H264*` runs only
the H.264 benchmark, which decodes `samples/h264-bench-720p.mp4` (50 frames) per iteration;
`--filter *Aac*` decodes `samples/aac-5.1.mp4`. Audio figures are per 1024-sample frame. Single-threaded, on an
AVX2 Xeon cloud VM (µs per frame):

| Sample | ManagedCodecs | FFmpeg 6.1 |
| --- | ---: | ---: |
| `testsrc-420` (320x240 4:2:0) | 198 | 292 |
| `testsrc-444` | 315 | 377 |
| `noise-q1` (noise, highest quality) | 773 | 1157 |
| `h264-p-refs` (320x240 H.264, P frames) | 632 | 154 |
| `h264-bench-720p` (1280x720 H.264) | 8166 | 2290 |
| `aac-stereo-48k` (AAC, stereo) | 85 | 25 |
| `aac-5.1` (AAC, 5.1) | 290 | 67 |

## Roadmap

1. ~~MJPEG baseline decoder, bit-exact against FFmpeg (4:2:0, 4:2:2, 4:4:4, gray, restart markers, missing DHT)~~ done;
   still open: interlaced AVI1 frames (two fields per packet), real camera files
2. SIMD: `Span`, `Vector128`/`Vector256` IDCT and colour paths; measure the ratio to FFmpeg.
   Done so far: AVX2 IDCT, DC-only blocks, single-lookup coefficient decoding, buffer reuse.
   Still open: an ARM64 (AdvSimd) IDCT, decoding straight into the output frame
3. ~~MP4 demuxer (ISO BMFF: `moov`, `trak`, `stbl` sample tables)~~ done: packets, timestamps and
   codec private data match FFmpeg. Still open: fragmented MP4, multi-edit edit lists, audio tracks
4. ~~H.264 decoder, starting with Constrained Baseline (CAVLC, I/P frames)~~ done: bit-exact with
   FFmpeg on libx264 samples covering intra and inter prediction, all partitions, multiple
   references and slices, cropping and constrained intra. Still open: CABAC / Main profile with
   B frames (and output reordering), error concealment, more speed (about 3.6x FFmpeg at 720p)
5. ~~AAC-LC decoder~~ done: 1 to 8 channels, all window sequences and shapes, M/S, intensity,
   PNS (including correlated noise), TNS and pulse data. Output matches FFmpeg within 3e-7 (a
   tolerance test, since both are floating point). Still open: HE-AAC (SBR, PS), ADTS input,
   trimming priming and padding (the demuxer's timestamps and durations say how much)

## License note

FFmpeg is LGPL-2.1-or-later (some parts GPL). Code ported from FFmpeg's sources must keep that
license. Implementing from the public specifications (ITU-T T.81 for JPEG, ITU-T H.264,
ISO/IEC 14496-12 for MP4) and using FFmpeg only as a reference for test output keeps the
license choice open. Codec patents (e.g. H.264, AAC) apply regardless of language.

Two exceptions to "from the spec only": the MJPEG IDCT reproduces FFmpeg's integer IDCT arithmetic
(that is what bit-exact JPEG output requires; see `SimpleIdct`), and the H.264 CAVLC tables, kept
in the spec's own layout, were cross-checked against h264bsd (Apache-2.0) by enumerating its decoder.
The AAC Huffman codebooks were cross-checked the same way against the PacketVideo AAC decoder
from Android (Apache-2.0). AAC's noise substitution uses the Numerical Recipes random generator
with the seed FFmpeg uses (the spec leaves the generator open), so noise bands match FFmpeg too.
