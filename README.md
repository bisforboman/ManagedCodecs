# ManagedCodecs

Pure managed C# media codecs: no native binaries, Native AOT friendly.
The first goal is a baseline MJPEG decoder whose output is **bit-exact** with FFmpeg,
then the same approach for H.264 + AAC in MP4.

## Layout

| Project | What it holds |
| --- | --- |
| `src/ManagedCodecs.Core` | `VideoFrame`, `PixelFormat`, `Packet`, `IDemuxer`, `IVideoDecoder` |
| `src/ManagedCodecs.Containers` | `AviDemuxer` (MP4 to come) |
| `src/ManagedCodecs.Mjpeg` | `MjpegDecoder`: baseline/extended Huffman JPEG, bit-exact with FFmpeg |
| `tests/ManagedCodecs.Tests` | Unit tests and FFmpeg conformance tests (framemd5) |
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
ffmpeg -benchmark -i samples/testsrc-420.avi -f null -
```

## Roadmap

1. ~~MJPEG baseline decoder, bit-exact against FFmpeg (4:2:0, 4:2:2, 4:4:4, gray, restart markers, missing DHT)~~ done;
   still open: interlaced AVI1 frames (two fields per packet), real camera files
2. SIMD: `Span`, `Vector128`/`Vector256` IDCT and colour paths; measure the ratio to FFmpeg
3. MP4 demuxer (ISO BMFF: `moov`, `trak`, `stbl` sample tables)
4. H.264 decoder, starting with Constrained Baseline (CAVLC, I/P frames)
5. AAC-LC decoder

## License note

FFmpeg is LGPL-2.1-or-later (some parts GPL). Code ported from FFmpeg's sources must keep that
license. Implementing from the public specifications (ITU-T T.81 for JPEG, ITU-T H.264,
ISO/IEC 14496-12 for MP4) and using FFmpeg only as a reference for test output keeps the
license choice open. Codec patents (e.g. H.264, AAC) apply regardless of language.
