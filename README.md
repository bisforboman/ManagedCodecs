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
| `src/ManagedCodecs.Aac` | `AacDecoder`: AAC LC (mono to 7.1, M/S, intensity, PNS, TNS), within 3e-7 of FFmpeg's float output |
| `src/Shared` | Source files linked into several codec projects (the VLC decoder) |
| `tools/ManagedCodecs.Cli` | `mcdec`: decodes a file and times it, to compare with `ffmpeg -benchmark` |
| `tools/make-samples.sh` | Generates the test samples and FFmpeg's references |
| `tests/ManagedCodecs.Tests` | Unit tests and FFmpeg conformance tests (framemd5 for frames and packets) |
| `benchmarks/ManagedCodecs.Benchmarks` | BenchmarkDotNet decode benchmark |

## Getting started

Requires the .NET 10 SDK. The tests also need samples made with FFmpeg (with libx264) and ImageMagick:

```
tools/make-samples.sh    # once; Git Bash or WSL on Windows
dotnet build
dotnet test
```

The conformance tests compare every decoded frame, packet and audio sample with FFmpeg's (see
`samples/README.md`) and fail if `samples/` is empty, which is intentional. CI (`.github/workflows/ci.yml`) runs
the same steps on every pull request.

## Benchmarking

`mcdec` (`tools/ManagedCodecs.Cli`) takes the same file as FFmpeg, decodes every stream it has a decoder for, and
prints FFmpeg's `bench:` line:

```
f=samples/h264-bench-720p.mp4    # any sample, or a file of your own
dotnet run -c Release --project tools/ManagedCodecs.Cli -- $f --loops 10
ffmpeg -benchmark -threads 1 -stream_loop 9 -i $f -f null -
```

Both lines time the whole process, demuxing included; mcdec's also includes .NET's startup and JIT, and its
`last loop` line shows the steady speed per frame. For statistics on the bundled samples there is
BenchmarkDotNet: `dotnet run -c Release --project benchmarks/ManagedCodecs.Benchmarks` (`--filter *H264*` or
`*Aac*` for one codec). A snapshot (2026-10-20, single-threaded on an AVX2 Xeon cloud VM; µs per frame, audio
frames are 1024 samples):

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
5. ~~AAC-LC decoder~~ done: channel configurations 1 to 7 and program config elements (1 to 8
   channels), all window sequences and shapes, M/S, intensity, PNS, TNS. Both decoders are floating
   point, so the tests compare values: they require 2^-17 (about 7.6e-6) and measure at most 3e-7.
   Implemented but never produced by FFmpeg's encoder, so untested against it: pulse data and
   correlated noise. Still open: HE-AAC (SBR, PS), ADTS input, trimming priming and padding (the
   demuxer's timestamps and durations say how much)

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
