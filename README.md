# ManagedCodecs

Pure managed C# media codecs: no native binaries, Native AOT friendly, and running on WebAssembly too. Decoders for
MJPEG, H.264 (Constrained Baseline up to High 4:4:4 Predictive) and AAC LC, with AVI, MP4 and raw AAC (ADTS) demuxing, every one
checked against FFmpeg sample by sample: video **bit-exact**, AAC within 2^-17 of FFmpeg's float output. H.264 decodes
on several threads if asked, and every decoder has an async API.

## Layout

| Project | What it holds |
| --- | --- |
| `src/ManagedCodecs.Core` | `VideoFrame`, `PixelFormat`, `Packet`, `Rational`, `IDemuxer`, `IVideoDecoder` |
| `src/ManagedCodecs.Containers` | `AviDemuxer`, `Mp4Demuxer`, `AdtsDemuxer` (raw .aac), and `Demuxer.Open` to pick one by content |
| `src/ManagedCodecs.Mjpeg` | `MjpegDecoder`: baseline/extended Huffman JPEG, interlaced AVI1, bit-exact with FFmpeg |
| `src/ManagedCodecs.H264` | `H264Decoder`: Constrained Baseline, Main, High, High 10, High 4:2:2 and High 4:4:4 Predictive profile H.264 (CAVLC, CABAC, I/P/B, weighted prediction, 8x8 transform, scaling matrices, deblocking, monochrome, 4:2:0, 4:2:2 and 4:4:4, 8 to 14 bits, lossless, progressive and interlaced), bit-exact |
| `src/ManagedCodecs.Aac` | `AacDecoder`: AAC LC (mono to 7.1, M/S, intensity, PNS, TNS), within 3e-7 of FFmpeg's float output |
| `src/Shared` | Source files linked into several codec projects (the VLC decoder) |
| `tools/ManagedCodecs.Cli` | `mcdec`: decodes a file and times it, to compare with `ffmpeg -benchmark` (`--threads N` for H.264 frame threading) |
| `tools/make-samples.sh` | Generates the test samples and FFmpeg's references |
| `tools/compare.sh` | Times mcdec and FFmpeg on the same file |
| `tools/ManagedCodecs.Fuzz`, `tools/fuzz.sh` | Fuzzing targets for SharpFuzz and AFL++ (see Malformed input) |
| `tools/ManagedCodecs.Wasm`, `tools/wasm-check.sh` | The decoders on WebAssembly, checked against the native build (see WebAssembly) |
| `tests/ManagedCodecs.Tests` | Unit tests and FFmpeg conformance tests (framemd5 for frames and packets) |
| `benchmarks/ManagedCodecs.Benchmarks` | BenchmarkDotNet decode benchmark |

## Status

Every decoder is checked against FFmpeg, on samples generated to isolate one feature each; where an encoder never
writes a feature, unit tests and hand-built or rewritten streams cover it. [docs/conformance.md](docs/conformance.md)
says feature by feature which encoder made the samples (libx264, OpenH264, JM) and what is not checked.

- **MJPEG**: baseline and extended Huffman JPEG in 4:2:0, 4:2:2, 4:4:4 and gray, restart markers, missing Huffman
  tables, interlaced AVI1 frames; bit-exact. A vector IDCT (AVX2, and `Vector128` for ARM64 and WebAssembly), and
  output straight into the frame's planes (`ZeroCopy`).
- **H.264**: Constrained Baseline to High 4:4:4 Predictive. CAVLC and CABAC; I, P and B pictures; weighted
  prediction, the 8x8 transform, scaling matrices; MBAFF frames and field pictures; monochrome, 4:2:0, 4:2:2 and
  4:4:4; 8, 9, 10, 12 and 14 bits; lossless. Bit-exact on streams from libx264, OpenH264 and JM. Frame threading
  (`Threads`).
- **AAC LC**: mono to 7.1 and program config elements, every window sequence and shape, M/S and intensity stereo,
  PNS, TNS; within 2^-17 of FFmpeg's float output (measured: 3e-7). Encoder priming is trimmed by the MP4 edit list.
- **Containers**: AVI (video), MP4 (video and audio tracks, fragmented files too, edit lists of several edits)
  and raw AAC (ADTS), with packets and timestamps as FFmpeg's demuxers give them.
- **Throughout**: an async API, malformed input that ends in `InvalidDataException` or `NotSupportedException`
  (fuzzed), and the same output on x64, ARM64 and WebAssembly (CI checks all three).

### Open

- AAC: HE-AAC (SBR, PS): its signalling and SBR data are read, its output not decoded yet (`NotSupportedException`).
  In ADTS: frames of several raw data blocks, and channel configuration 0.
- MJPEG: files from real cameras (the samples are all FFmpeg's), and packets holding one field (FFmpeg decodes none).
- H.264: separate colour planes, luma and chroma bit depths that differ, monochrome above 10 bits (FFmpeg decodes
  none of these, so checking them needs another reference); error concealment (a damaged picture fails its
  `Decode` call).
- H.264 speed: 2.3 to 3.6 times FFmpeg's time on one thread (Benchmarking). With 4 threads a 720p P chain takes
  about half of one thread's time: each picture decodes about 28% slower than alone (its references were written on
  other cores) and threads wait for references' rows about a fifth of the time.
- `ReadPacketsAsync` reads each packet synchronously (it yields the thread between packets).

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
IVideoDecoder decoder = video.Codec == CodecIds.H264 ? new H264Decoder(video) : new MjpegDecoder(video);

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

The same asynchronously, for servers and UI threads: `ReadPacketsAsync`, `DecodeAsync` and `FlushAsync` are on the
interfaces (with defaults, so other implementations keep working). With `Threads` above 1, `H264Decoder.DecodeAsync`
awaits the pictures being decoded where `Decode` would block the calling thread; a cancelled token stops the waiting,
not a picture already started. Reading a packet is still synchronous (the demuxers yield the thread between packets).

```csharp
IVideoDecoder decoder = new H264Decoder(video) { Threads = 4 };
await foreach (var packet in demuxer.ReadPacketsAsync(cancellationToken))
{
    if (packet.StreamIndex == video.StreamIndex)
    {
        foreach (var frame in await decoder.DecodeAsync(packet, cancellationToken))
        {
            using (frame) { Show(frame); }
        }
    }
}

foreach (var frame in await decoder.FlushAsync(cancellationToken))
{
    using (frame) { Show(frame); }
}
```

Each frame owns its samples. Frames are `IDisposable`: disposing one when done with it is optional, and gives an
H.264 or MJPEG frame's planes back to the decoder for a later frame (a `VideoFramePool`), which saves allocating and
collecting about 1.4 MB a frame at 720p (MJPEG, `testsrc-420.avi`: 7.7 MB allocated a pass down to 2.0); a disposed
frame's samples must not be used again. `new MjpegDecoder(video) { ZeroCopy = true }` decodes each image straight into
its frame's planes, which are then whole MCUs wide and high (read rows through `GetRow`, or with `Strides`): 6% fewer
instructions than decoding into the decoder's buffers and copying out (callgrind, `testsrc-420.avi`), and the frame
owns its planes like any other (interlaced packets are woven into a frame all the same; `mcdec --zero-copy`).
`H264Decoder.Decode(ReadOnlySpan<byte> data, long pts)` takes a picture's NAL units from any memory, which need not
outlive the call, as `MjpegDecoder.Decode(ReadOnlySpan<byte>)` and `AacDecoder.Decode(ReadOnlySpan<byte>)` do. `new H264Decoder(video) { BorrowFrames = true }` saves the copy that takes (with the copied
frames disposed, about 5% of the decoding time at 720p and 1% at 320x240): its frames point into the decoder's own pictures, valid until the
next `Decode` or `Flush` call and not to be written. Their planes are the decoder's macroblock-aligned buffers, so
read them through `GetRow`, or with the frame's `Offsets` and `Strides`. `mcdec --borrow-frames` times it. 10-bit
video comes out as `PixelFormat.Yuv420P10`, `Yuv422P10` or `Yuv444P10`, a little-endian 16-bit word a sample (strides and offsets
count bytes), and always as copies: the decoder keeps those samples as `ushort`, which a frame's byte planes cannot
borrow.

`new H264Decoder(video) { Threads = 4 }` decodes up to four pictures at once, each on a thread of its own (frame
threading): a picture starts while the ones before it are still being decoded, and its motion compensation and direct
prediction wait only for the rows of theirs they read. The frames are the same as with one thread, but `Decode` returns
them up to that many packets later (`Flush` returns the rest), and borrowed frames are not supported with threads. On
`h264-bench-720p.mp4` (one I and 49 P pictures, each predicting from the one before) on a 4-core cloud VM, 2 threads
take 0.73 of one thread's time, 4 threads 0.50 and 6 threads 0.42 (`mcdec --threads N`, medians of five interleaved
runs): a thread waiting for a reference's rows leaves its core idle, so a few more threads than cores help. The two
fields of a frame coded as field pictures are decoded at once too (JM's `jm-paff-b.mp4` with 4 threads: 0.79 of one
thread's time). The default, 1, decodes on the calling thread, which WebAssembly in a browser needs; it costs 0.3% more
instructions than before frame threading existed (callgrind).

`AacDecoder.Decode(packet)` returns one `AudioFrame`: the packet's 1024 samples per channel, less the
`packet.SkipSamples` at its start. The MP4 demuxer sets those from the edit list, so encoder priming is dropped and the
output matches FFmpeg's default (`Decode(packet.Data.Span)` gives all 1024).

An edit list can also leave packets out, repeat them, or need some decoded only to be able to decode others: the MP4
demuxer gives every packet FFmpeg's does, those to be decoded and not shown marked `Packet.Discard` (the frames from
the keyframe before an edit's start, audio before it, which primes the decoder). Each decoder decodes such a packet and
returns nothing for it, no frame or an empty `AudioFrame`, so playing every packet gives what FFmpeg plays.

### Malformed input

Damaged, truncated or hostile files make the demuxers and decoders throw `InvalidDataException` (or
`NotSupportedException` for a valid stream using something not implemented), never another exception, and they
neither hang nor allocate without bound: an MJPEG image over 2^26 samples is refused, and an AVI chunk running past
the end of the file is an error rather than an allocation. A truncated file throws where the data runs
out (FFmpeg stops quietly instead); packets already returned stay valid. There is no error concealment yet: a
damaged H.264 picture fails its `Decode` call.

`tools/fuzz.sh <file|h264|mjpeg|aac> <minutes>` checks this with AFL++ through SharpFuzz (`file` demuxes a whole file
and decodes every track; the others feed one decoder its configuration and packets), with seeds taken from
`samples/`. Inputs that once broke the contract are kept in `tests/ManagedCodecs.Tests/Fuzz/Findings/<target>/`, and
`MalformedInputTests` replays them, along with a fixed-seed sweep of truncations and byte flips over a few samples.
The sweep and the first 20-minute runs of each target found sample sizes and durations over `int`, AVI chunks past
the end of the file, a zero image width or channel count, an `mb_type` code over `int` and huge MJPEG images, all
fixed.

### WebAssembly

The libraries run unchanged on `browser-wasm`. `tools/wasm-check.sh [aot]` decodes every sample there, under
Node, and natively (`mcdec --hash`, a SHA-256 of each file's decoded frames and audio samples), and fails unless the
output is the same. All 279 sample files agree, interpreted or compiled ahead of time, and CI's `wasm` job runs the
AOT build on every pull request. The native build is the one checked against FFmpeg, so the wasm output matches
FFmpeg as well. `tools/ManagedCodecs.Wasm` needs the `wasm-tools` workload (`dotnet workload install wasm-tools`),
so it is not part of `ManagedCodecs.slnx`.

On wasm, `Vector128` is accelerated (WebAssembly's 128-bit SIMD, on by default) and `Vector256` is not, so the paths
written for ARM64 run there, such as the MJPEG `Vector128` IDCT and the H.264 deblocking transposes.
`Environment.ProcessorCount` is 1, and there are no threads without the experimental multithreaded runtime, so the
decoders must stay usable on one thread.

Time per decode: the mean of 10 decodes of a file already in memory (`--loops 10` to `mcdec --hash` and to the wasm
tool), the median of 3 interleaved runs. Node 22, SDK 10.0.113, a 4-core cloud VM; native is the x64 JIT with AVX2
(mcdec turns tiered compilation off).

| Sample | Native | wasm AOT | wasm interpreter |
| --- | --- | --- | --- |
| testsrc-420.avi (MJPEG, 50 frames) | 40 ms | 97 ms | 413 ms |
| h264-with-audio.mp4 (H.264 Main, 73 frames, and AAC) | 80 ms | 105 ms | 582 ms |
| h264-high-b.mp4 (H.264 High with B frames, 25 frames) | 51 ms | 106 ms | 623 ms |
| aac-stereo-48k.mp4 (AAC LC, 95 frames) | 5.5 ms | 14 ms | 51 ms |
| jm-paff-b.mp4 (H.264 field pictures, 10 frames) | 23 ms | 49 ms | 272 ms |

AOT (`-p:RunAOTCompilation=true`, two minutes more to build) takes 1.3 to 2.5 times the native time, the interpreter
7 to 12 times.

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
line shows the steady speed without them). mcdec disposes each frame once it has it, as a consumer done with them
would, so H.264 frames reuse the planes of earlier ones. A snapshot, made that way (2026-10-23, FFmpeg 6.1.1,
single-threaded on a 4-core 2.8 GHz Xeon cloud VM with AVX2 and AVX-512; the median of five runs interleaved with
FFmpeg's, the order alternating; µs per frame, audio frames are 1024 samples):

| Sample | Loops | ManagedCodecs | FFmpeg | Ratio |
| --- | ---: | ---: | ---: | ---: |
| `testsrc-420.avi` (320x240 MJPEG 4:2:0) | 100 | 287 | 408 | 0.70 |
| `testsrc-444.avi` (MJPEG 4:4:4) | 100 | 548 | 602 | 0.91 |
| `noise-q1.avi` (MJPEG, noise at the highest quality) | 100 | 1064 | 1288 | 0.83 |
| `h264-p-refs.mp4` (320x240 H.264, P frames) | 40 | 726 | 280 | 2.60 |
| `h264-bench-720p.mp4` (1280x720 H.264) | 10 | 10222 | 2854 | 3.58 |
| `h264-main-p-refs.mp4` (320x240 H.264 Main, CABAC) | 40 | 1026 | 446 | 2.30 |
| `h264-b-pyramid.mp4` (320x240 H.264 Main, CABAC, B frames) | 20 | 1574 | 602 | 2.61 |
| `h264-wp-default.mp4` (320x240 H.264, libx264's Main defaults: B frames, weighted prediction) | 20 | 1385 | 454 | 3.05 |
| `h264-high-default.mp4` (320x240 H.264, libx264's High defaults: also the 8x8 transform) | 20 | 1472 | 475 | 3.10 |
| `h264-high-b.mp4` (320x240 H.264 High, B frames, all partitions) | 40 | 1716 | 631 | 2.72 |
| `h264-10bit-b.mp4` (the same at 10 bits, High 10) | 40 | 2072 | 636 | 3.26 |
| `h264-high-motion.mp4` (320x240 H.264 High, fast motion and noise, QP 22) | 20 | 6100 | 3179 | 1.92 |
| `h264-10bit-motion.mp4` (the same at 10 bits, QP 34, which is QP 22's step size) | 20 | 6442 | 3156 | 2.04 |
| `aac-stereo-48k.mp4` (AAC, stereo) | 100 | 45 | 86 | 0.52 |
| `aac-5.1.mp4` (AAC, 5.1) | 100 | 138 | 123 | 1.13 |

The short MJPEG and AAC runs are mostly .NET's startup and JIT: without them (`last loop`), `aac-5.1.mp4` takes about
90 µs a frame. H.264 takes 2.3 to 3.6 times FFmpeg's time, less on noisier content, whose residual decoding weighs
more; at 10 bits the ratio is close to the 8-bit one. A VM's speed moves from day to day, and FFmpeg's and ours do
not move together: the previous snapshot (2026-10-22, in the history of this file) had `h264-bench-720p.mp4` at
7656 µs against 2412, while callgrind counts 1.5% more instructions for `h264-p-refs.mp4` since then. Only
comparisons made on one machine at one time count.

Cloud VMs are noisy (single runs differed by up to a third), so compare before and after on one machine. For
statistics on the decoder alone there is BenchmarkDotNet, `dotnet run -c Release --project
benchmarks/ManagedCodecs.Benchmarks` (`--filter *H264*` or `*Aac*` for one codec). It decodes packets already read
into memory, from `samples/testsrc-420.avi`, `samples/h264-bench-720p.mp4` and `samples/aac-5.1.mp4` (or the files
in `MANAGEDCODECS_SAMPLE`, `MANAGEDCODECS_H264_SAMPLE` and `MANAGEDCODECS_AAC_SAMPLE`), so its numbers leave out
what the two commands above include and are not comparable with FFmpeg's `bench:` line.

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
context states for every QP and initialization table equal the spec's initialization applied to ours. Contexts 460
to 1023 (4:4:4's Cb and Cr residual blocks and the coded_block_flag of 8x8 blocks) repeat the luma contexts of their
block category; all of them were compared, with the residual contexts below 460, against the tables of JM, the ITU's
reference software (3740 values, all four initialization tables).
The AAC Huffman codebooks were cross-checked the same way against the PacketVideo AAC decoder
from Android (Apache-2.0). For spectral band replication (HE-AAC), whose specification text (ISO/IEC 14496-3 4.6.18,
and 3GPP TS 26.404) is out of the sandbox's reach, the same decoder's SBR sources stand in for it as JM did for MBAFF:
read for the syntax and the clauses its comments cite, its Huffman codebooks enumerated from its decoding trees, its
precomputed band tables used to check the spec's formulas; the arithmetic is written from the formulas, in double
precision, and no code was copied. AAC's noise substitution uses the Numerical Recipes random generator
with the seed FFmpeg uses (the spec leaves the generator open), so noise bands match FFmpeg too.
For MBAFF (the neighbour table 6-4, the field scans, the field CABAC context maps, the order of the
deblocking filter's field edges and the co-located field or frame of direct prediction) the ITU's reference decoder, JM (royalty-free, from the ITU), stood
in for the specification's text where that was out of reach; no code was copied.
