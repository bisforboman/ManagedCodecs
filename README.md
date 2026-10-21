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
| `src/ManagedCodecs.H264` | `H264Decoder`: Constrained Baseline, Main and High profile H.264 (CAVLC, CABAC, I/P/B, weighted prediction, 8x8 transform, scaling matrices, deblocking), bit-exact |
| `src/ManagedCodecs.Aac` | `AacDecoder`: AAC LC (mono to 7.1, M/S, intensity, PNS, TNS), within 3e-7 of FFmpeg's float output |
| `src/Shared` | Source files linked into several codec projects (the VLC decoder) |
| `tools/ManagedCodecs.Cli` | `mcdec`: decodes a file and times it, to compare with `ffmpeg -benchmark` |
| `tools/make-samples.sh` | Generates the test samples and FFmpeg's references |
| `tools/compare.sh` | Times mcdec and FFmpeg on the same file |
| `tests/ManagedCodecs.Tests` | Unit tests and FFmpeg conformance tests (framemd5 for frames and packets) |
| `benchmarks/ManagedCodecs.Benchmarks` | BenchmarkDotNet decode benchmark |

## Getting started

Requires the .NET 10 SDK. The tests also need samples made with FFmpeg (with libx264) and ImageMagick:

```
tools/make-samples.sh    # once; Git Bash or WSL on Windows
dotnet build
dotnet test
git config core.hooksPath .githooks    # once: formats staged C# with StyleBro before each commit
```

The conformance tests compare every decoded frame, packet and audio sample with FFmpeg's (see
`samples/README.md`) and fail if `samples/` is empty, which is intentional. CI (`.github/workflows/ci.yml`) runs
the same steps on every pull request.

## Decoding a file

Packets go in in decode order; frames come out in display order, each with the `Pts` of its packet. A decoder may
hold frames back while later packets can still be shown before them (H.264 frame reordering), so `Decode` returns
zero or more frames and `Flush` returns the rest:

```csharp
using var demuxer = Demuxer.Open("video.mp4");
var video = demuxer.Video!;
IVideoDecoder decoder = video.Codec == CodecIds.H264 ? new H264Decoder(video) : new MjpegDecoder();

foreach (var packet in demuxer.ReadPackets().Where(p => p.StreamIndex == video.StreamIndex))
{
    foreach (var frame in decoder.Decode(packet))
    {
        Show(frame); // frame.Pts is in video.TimeBase
    }
}

foreach (var frame in decoder.Flush())
{
    Show(frame);
}
```

`AacDecoder.Decode(packet)` returns one `AudioFrame`: the packet's 1024 samples per channel, less the
`packet.SkipSamples` at its start. The MP4 demuxer sets those from the edit list, so encoder priming is dropped and the
output matches FFmpeg's default (`Decode(packet.Data.Span)` gives all 1024).

## Benchmarking

`mcdec` (`tools/ManagedCodecs.Cli`) takes the same file as FFmpeg, decodes every stream it has a decoder for, and
prints FFmpeg's `bench:` line. `tools/compare.sh` runs both on one file, the same number of times, and prints each
one's wall time per frame:

```
tools/compare.sh samples/h264-bench-720p.mp4 10    # any sample, or a file of your own; 10 loops
```

which comes down to these two commands on the same `$f`:

```
dotnet run -c Release --project tools/ManagedCodecs.Cli -- $f --loops 10
ffmpeg -benchmark -threads 1 -stream_loop 9 -i $f -f null -
```

Both time the whole process, demuxing included; mcdec's also includes .NET's startup and JIT (mcdec's `last loop`
line shows the steady speed without them). A snapshot, made that way (2026-10-20, FFmpeg 6.1, single-threaded on a
4-core AVX2 Xeon cloud VM, median of three runs; µs per frame, audio frames are 1024 samples):

| Sample | Loops | ManagedCodecs | FFmpeg |
| --- | ---: | ---: | ---: |
| `testsrc-420.avi` (320x240 MJPEG 4:2:0) | 100 | 343 | 386 |
| `testsrc-444.avi` (MJPEG 4:4:4) | 100 | 512 | 526 |
| `noise-q1.avi` (MJPEG, noise at the highest quality) | 100 | 1133 | 1332 |
| `h264-p-refs.mp4` (320x240 H.264, P frames) | 40 | 613 | 197 |
| `h264-bench-720p.mp4` (1280x720 H.264) | 10 | 7066 | 2352 |
| `h264-main-p-refs.mp4` (320x240 H.264 Main, CABAC) | 40 | 909 | 354 |
| `h264-b-pyramid.mp4` (320x240 H.264 Main, CABAC, B frames) | 20 | 1558 | 418 |
| `h264-wp-default.mp4` (320x240 H.264, libx264's Main defaults: B frames, weighted prediction) | 20 | 1268 | 344 |
| `h264-high-default.mp4` (320x240 H.264, libx264's High defaults: also the 8x8 transform; 2026-10-21) | 20 | 1258 | 370 |
| `aac-stereo-48k.mp4` (AAC, stereo) | 100 | 58 | 65 |
| `aac-5.1.mp4` (AAC, 5.1) | 100 | 205 | 85 |

Cloud VMs are noisy (single runs differed by up to a third), so compare before and after on one machine. For
statistics on the decoder alone there is BenchmarkDotNet, `dotnet run -c Release --project
benchmarks/ManagedCodecs.Benchmarks` (`--filter *H264*` or `*Aac*` for one codec). It decodes packets already read
into memory, from `samples/testsrc-420.avi`, `samples/h264-bench-720p.mp4` and `samples/aac-5.1.mp4` (or the files
in `MANAGEDCODECS_SAMPLE`, `MANAGEDCODECS_H264_SAMPLE` and `MANAGEDCODECS_AAC_SAMPLE`), so its numbers leave out
what the two commands above include and are not comparable with FFmpeg's `bench:` line.

## Roadmap

1. ~~MJPEG baseline decoder, bit-exact against FFmpeg (4:2:0, 4:2:2, 4:4:4, gray, restart markers, missing DHT)~~ done;
   still open: interlaced AVI1 frames (two fields per packet), real camera files
2. SIMD: `Span`, `Vector128`/`Vector256` IDCT and colour paths; measure the ratio to FFmpeg.
   Done so far: AVX2 IDCT, DC-only blocks, single-lookup coefficient decoding, buffer reuse.
   Still open: an ARM64 (AdvSimd) IDCT, decoding straight into the output frame
3. ~~MP4 demuxer (ISO BMFF: `moov`, `trak`, `stbl` sample tables)~~ done: packets, timestamps and
   codec private data match FFmpeg, for video and audio tracks, fragmented files (moof) included. Audio is trimmed by the edit list as FFmpeg trims it: priming
   skipped sample by sample, packets after the edit's end left out. Still open: multi-edit edit lists, trimming
   video at the edit's end
4. ~~H.264 decoder, starting with Constrained Baseline (CAVLC, I/P frames)~~ done: bit-exact with
   FFmpeg on libx264 samples covering intra and inter prediction, all partitions, multiple
   references and slices, cropping and constrained intra, with CAVLC and with CABAC (Main profile),
   and B frames: every B macroblock type, spatial and temporal direct prediction, bi-prediction, B
   pictures as references (with memory management operation 1); explicit and implicit weighted
   prediction, so libx264's default Main profile output decodes; and High profile's 8x8 transform
   with Intra 8x8 prediction, so libx264's default output decodes too, and scaling matrices (the
   default ones and custom lists). Frames come out in display order.
   Interlaced video, first part: MBAFF frames of I slices (macroblock pairs coded as frame or
   field pairs, mixed in any way), with CAVLC and CABAC, 4x4 and 8x8 transforms, slices and the
   deblocking filter, and progressive frames from interlaced-capable parameter sets.
   Streams from a second encoder, OpenH264, are checked against FFmpeg too: long-term references
   with reference list modification and memory management operations 1, 4 and 6, POC type 2,
   non-reference P frames, slices by size, deblocking that stops at slice edges, changing and
   repeated parameter sets, the base layer of an SVC stream (prefix NAL units and enhancement
   slices skipped), and gaps in frame_num from dropped temporal layers. The frames inferred for
   such gaps (8.2.5.2) take their places in the default reference lists and the sliding window;
   no encoder names a reference by such a position, so hand-built streams test that.
   Produced by neither, so not checked against FFmpeg: I_PCM (decoded from hand-built streams,
   CAVLC and CABAC), memory management operations 2, 3 and 5 (unit tests),
   B sub-macroblock partitions smaller than 8x8, direct prediction without
   direct_8x8_inference_flag, explicit weights for bi-predicted blocks (libx264 weights B frames
   implicitly), cabac_init_idc 1 and 2 (only their tables are checked, against libavc), a Cr QP
   offset other than Cb's (second_chroma_qp_index_offset), and of scaling matrices: lists in the
   SPS, fall-back rule B, useDefaultScalingMatrixFlag and a Cr list of its own (unit tests decode
   samples with rewritten parameter sets). Still open: 4:2:2 and 4:4:4, more than 8 bits,
   P and B slices in MBAFF frames, field pictures (PAFF), error concealment, more speed (about 3x FFmpeg's time at 720p, also with
   libx264's default High output; 2x on high-bitrate intra content)
5. ~~AAC-LC decoder~~ done: channel configurations 1 to 7 and program config elements (1 to 8
   channels), all window sequences and shapes, M/S, intensity, PNS, TNS. Both decoders are floating
   point, so the tests compare values: they require 2^-17 (about 7.6e-6) and measure at most 3e-7.
   Implemented but never produced by FFmpeg's encoder, so untested against it: pulse data and
   correlated noise. Priming before the edit list's start is trimmed (`Packet.SkipSamples`); like FFmpeg, the
   padding in the last packet is not. Still open: HE-AAC (SBR, PS), ADTS input

## License note

FFmpeg is LGPL-2.1-or-later (some parts GPL). Code ported from FFmpeg's sources must keep that
license. Implementing from the public specifications (ITU-T T.81 for JPEG, ITU-T H.264,
ISO/IEC 14496-12 for MP4) and using FFmpeg only as a reference for test output keeps the
license choice open. Codec patents (e.g. H.264, AAC) apply regardless of language.

Two exceptions to "from the spec only": the MJPEG IDCT reproduces FFmpeg's integer IDCT arithmetic
(that is what bit-exact JPEG output requires; see `SimpleIdct`), and the H.264 CAVLC tables, kept
in the spec's own layout, were cross-checked against h264bsd (Apache-2.0) by enumerating its decoder.
The CABAC tables (context initialization, rangeTabLPS, state transitions) are the spec's values,
transcribed from OpenH264 (BSD-2-Clause) and verified against libavc (Apache-2.0): its precomputed
context states for every QP and initialization table equal the spec's initialization applied to ours.
The AAC Huffman codebooks were cross-checked the same way against the PacketVideo AAC decoder
from Android (Apache-2.0). AAC's noise substitution uses the Numerical Recipes random generator
with the seed FFmpeg uses (the spec leaves the generator open), so noise bands match FFmpeg too.
For MBAFF (the neighbour table 6-4, the field scans, the field CABAC context maps and the order of the
deblocking filter's field edges) the ITU's reference decoder, JM (royalty-free, from the ITU), stood
in for the specification's text where that was out of reach; no code was copied.
