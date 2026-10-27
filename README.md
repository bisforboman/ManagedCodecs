# ManagedCodecs

Pure managed C# media codecs: no native binaries, Native AOT friendly, and running on WebAssembly too. Decoders for
MJPEG, H.264 (Constrained Baseline up to High 4:4:4 Predictive), AAC LC and HE-AAC, with AVI, MP4 and raw AAC (ADTS)
demuxing, every one checked against FFmpeg sample by sample: video **bit-exact**, AAC LC within 2^-17 of FFmpeg's float
output. H.264 decodes
on several threads if asked, and every decoder has an async API.

## Layout

| Project | What it holds |
| --- | --- |
| `src/ManagedCodecs.Core` | `VideoFrame`, `PixelFormat`, `Packet`, `Rational`, `IDemuxer`, `IVideoDecoder` |
| `src/ManagedCodecs.Containers` | `AviDemuxer`, `Mp4Demuxer`, `AdtsDemuxer` (raw .aac), and `Demuxer.Open` to pick one by content |
| `src/ManagedCodecs.Mjpeg` | `MjpegDecoder`: baseline/extended Huffman JPEG, interlaced AVI1, bit-exact with FFmpeg |
| `src/ManagedCodecs.H264` | `H264Decoder`: Constrained Baseline, Main, High, High 10, High 4:2:2 and High 4:4:4 Predictive profile H.264 (CAVLC, CABAC, I/P/B, weighted prediction, 8x8 transform, scaling matrices, deblocking, monochrome, 4:2:0, 4:2:2 and 4:4:4, also as separate colour planes, 8 to 14 bits, lossless, progressive and interlaced), bit-exact |
| `src/ManagedCodecs.Aac` | `AacDecoder`: AAC LC (mono to 7.1, M/S, intensity, PNS, TNS), within 3e-7 of FFmpeg's float output; HE-AAC (spectral band replication) |
| `src/Shared` | Source files linked into several codec projects (the VLC decoder) |
| `tools/ManagedCodecs.Cli` | `mcdec`: decodes a file and times it, to compare with `ffmpeg -benchmark` (`--threads N` for H.264 frame threading and HE-AAC's channels) |
| `tools/make-samples.sh` | Generates the test samples and FFmpeg's references |
| `tools/compare.sh` | Times mcdec and FFmpeg on the same file |
| `tools/realworld.py` | Decodes real public films with mcdec and FFmpeg and compares them (nightly, `realworld.yml`) |
| `tools/mutation/mutate.py` | Breaks each guard in `tools/mutation/mutations.py` and checks that a test fails (every pull request, `mutation.yml`) |
| `tools/upstream-issue.sh` | Files `upstream.yml`'s weekly issues: a new StyleBro, or a new release of an encoder the committed samples come from |
| `tools/ManagedCodecs.Fuzz`, `tools/fuzz.sh` | Fuzzing targets for SharpFuzz and AFL++ (see Malformed input) |
| `tools/ManagedCodecs.Wasm`, `tools/wasm-check.sh` | The decoders on WebAssembly, checked against the native build (see WebAssembly) |
| `tests/ManagedCodecs.Tests` | Unit tests and FFmpeg conformance tests (framemd5 for frames and packets) |
| `benchmarks/ManagedCodecs.Benchmarks` | BenchmarkDotNet decode benchmark |

## Status

Every decoder is checked against FFmpeg, on samples generated to isolate one feature each; where an encoder never
writes a feature, unit tests and hand-built or rewritten streams cover it. [docs/conformance.md](docs/conformance.md)
says feature by feature which encoder made the samples (libx264, OpenH264, JM) and what is not checked. Every night
the [real-world check](https://github.com/bisforboman/ManagedCodecs/actions/workflows/realworld.yml)
(`tools/realworld.py`) decodes six of Blender's open movies, real H.264 and AAC from real encoders, against FFmpeg:
every frame identical, the audio within 2^-17. On every pull request (docs-only ones skip its steps) the
[mutation check](https://github.com/bisforboman/ManagedCodecs/actions/workflows/mutation.yml)
(`tools/mutation/mutate.py`) breaks, one at a time, the guards a passing test could miss (the frame threading waits,
and code that reproduces FFmpeg's reading where it differs from the spec), and needs a test to fail for each.

- **MJPEG**: baseline and extended Huffman JPEG in 4:2:0, 4:2:2, 4:4:4 and gray, restart markers, missing Huffman
  tables, interlaced AVI1 frames; bit-exact. A vector IDCT (AVX2, and `Vector128` for ARM64 and WebAssembly), and
  output straight into the frame's planes (`ZeroCopy`).
- **H.264**: Constrained Baseline to High 4:4:4 Predictive. CAVLC and CABAC; I, P and B pictures; weighted
  prediction, the 8x8 transform, scaling matrices; MBAFF frames and field pictures; monochrome, 4:2:0, 4:2:2 and
  4:4:4, also as separate colour planes (progressive and interlaced); 8, 9, 10, 12 and 14 bits; lossless. Bit-exact on
  streams from libx264, OpenH264 and JM (separate colour planes against JM's decoder or, interlaced, against three
  monochrome streams spliced into one, as FFmpeg decodes none). Frame threading
  (`Threads`), and concealment of damaged slices (`ConcealErrors`, Malformed input).
- **AAC LC**: mono to 7.1 and program config elements (in MP4 and ADTS, in FFmpeg's channel order), every window
  sequence and shape, M/S and intensity stereo,
  PNS, TNS; within 2^-17 of FFmpeg's float output (measured: 3e-7). Encoder priming is trimmed by the MP4 edit list,
  or by an iTunSMPB tag without one.
- **HE-AAC** (spectral band replication, at twice the core's rate, or downsampled at the core's own): signalled in the
  config or only by its data, mono to 5.1, coupled pairs; within 1e-6 of FFmpeg's float output where its single precision is not the limit (see
  docs/conformance.md). Parametric stereo (HE-AAC v2): the baseline decoder (10 and 20 bands, mixing procedure Ra, no
  IPD or OPD), a mono stream decoded to stereo; within 3e-4 of FFmpeg's output, 3e-6 on average. A frame's channels
  through SBR on several threads (`Threads`).
- **Containers**: AVI (video), MP4 (video and audio tracks, fragmented files too, edit lists of several edits)
  and raw AAC (ADTS), with packets and timestamps as FFmpeg's demuxers give them.
- **Throughout**: an async API, malformed input that ends in `InvalidDataException` or `NotSupportedException`
  (fuzzed), and the same output on x64, ARM64 and WebAssembly (CI checks all three).

### Open

- AAC: parametric stereo with 34 bands or mixing procedure Rb (no encoder at hand sends them, nor downsampled SBR with
  parametric stereo, which decodes but is untested); SBR's speed (5.1 HE-AAC at about 0.41 ms a frame, 100 times
  real time, 2.2 times FFmpeg's time on one thread; Benchmarking).
- MJPEG: files from real cameras (the samples are all FFmpeg's), and packets holding one field (FFmpeg decodes none).
- H.264: long-term marking of field pictures, and memory management operations other than 1 in them (rejected:
  JM's encoder cannot write them and FFmpeg mishandles one, docs/conformance.md); luma and chroma bit depths that differ are rejected on purpose (no mainstream
  encoder or decoder, FFmpeg included, handles them, and they would need a bit depth for each plane in the frame
  API); guessing motion for concealed macroblocks (they copy their place in a reference); without concealment, a
  lost reference fails pictures up to the next IDR picture, not an MMCO 5 one (by design: the MMCO 5 picture itself
  would have to be decoded from lists that can no longer be trusted, so its marking never applies).
- H.264 speed: 1.85 to 3.29 times FFmpeg's time on one thread, interlaced video included (Benchmarking); at 720p
  every class sits at 2.2 to 2.8 times (CAVLC and CABAC, P and intra-only, High), so the gap is general rather than
  one class's (the CAVLC residual reader's compiled code is clean). MBAFF frames still look neighbours up through
  Table 6-4 call by call (about 9% of their time, field pictures 7%), where progressive pictures read a table made
  once a picture; neither a table of neighbouring pairs nor a cache of each macroblock's A and B locations moved it
  (docs/conformance.md, Performance notes).
  With 4 threads a 720p P chain takes 0.52 of one thread's time over 50 frames and 0.41 over 250 (one IDR picture
  each, which decodes in 2.4 times a P picture's time with the P pictures trailing it: short GOPs gain less; medians
  of five interleaved runs), on 4 cores without SMT where four single-threaded decodes run side by side about 7%
  slower. Workers wait for references' rows about 18% of the time, and each picture takes about 30% more CPU than
  alone at 720p, 60% at 320x240 (perf stat's task clock): spread over every hot function, the ones reading
  references most (in one profile), and not waiting (locks and progress
  reports are under 0.2% of the CPU). It is not yet explained; this VM has no hardware counters to tell cache misses
  from the rest. Decoding a picture's rows on several threads would mostly shorten the
  IDR picture's lead: about a tenth of a 50-frame GOP's time, little of a 250-frame one's.

## Getting started

The packages, on nuget.org ([CHANGELOG.md](CHANGELOG.md) lists the versions): `ManagedCodecs` for everything, or one of
`ManagedCodecs.Containers`, `.Mjpeg`, `.H264` and `.Aac` (each with `ManagedCodecs.Core`). They share internals, so
keep them at one version. Until the API is frozen they are previews, `0.1.0-preview.N`, and any of them can change the
public API.

```
dotnet add package ManagedCodecs --prerelease
```

Building from source requires the .NET 10 SDK. The tests also need samples made with FFmpeg (with libx264) and ImageMagick:

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
using ManagedCodecs;            // IVideoDecoder, CodecIds, packets and frames
using ManagedCodecs.Containers; // Demuxer, Mp4Demuxer, AviDemuxer, AdtsDemuxer
using ManagedCodecs.H264;
using ManagedCodecs.Mjpeg;

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
not a picture already started. The demuxers' `ReadPacketsAsync` awaits every read of the file (`Open(path)` opens it
without `FileOptions.Asynchronous`, so the runtime reads on the thread pool; pass your own stream for overlapped I/O).

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
H.264 or MJPEG frame's planes back to the decoder's pool for a later frame, which saves allocating and
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
thread's time). Each decoder slot keeps a thread of its own for its pictures (it ends after `Flush`, or two seconds
without a picture), and the front end blocks rather than spins while it waits: a new thread a picture, and the
spinning, had cost 12% of the CPU and 22% of the time of 320x240 video at 4 threads (within the noise at 720p). The
default, 1, decodes on the calling thread, which WebAssembly in a browser needs; it costs 0.3% more
instructions than before frame threading existed (callgrind).

`AacDecoder.Decode(packet)` returns one `AudioFrame`: the packet's 1024 samples per channel, less the
`packet.SkipSamples` at its start. The MP4 demuxer sets those from the edit list, so encoder priming is dropped and the
output matches FFmpeg's default (`Decode(packet.Data.Span)` gives all 1024).

`new AacDecoder(audio) { Threads = 4 }` puts up to four channels of an HE-AAC frame through the filterbank and SBR at
once, on the thread pool; the samples are the same as with one thread (`ThreadsGiveTheSameSamples`). On a 4-core cloud
VM, `heaac-5.1.mp4` took 305 µs a frame against 403 with one thread (about 1.3 times as fast; medians of 12 interleaved
`mcdec --threads N` runs, last loop); stereo no faster: a channel's 70 µs of work is little more than handing it over.
AAC LC and parametric stereo decode on the calling thread either way.

An edit list can also leave packets out, repeat them, or need some decoded only to be able to decode others: the MP4
demuxer gives every packet FFmpeg's does, those to be decoded and not shown marked `Packet.Discard` (the frames from
the keyframe before an edit's start, audio before it, which primes the decoder). Each decoder decodes such a packet and
returns nothing for it, no frame or an empty `AudioFrame`, so playing every packet gives what FFmpeg plays.

### Malformed input

Damaged, truncated or hostile files make the demuxers and decoders throw `InvalidDataException` (or
`NotSupportedException` for a valid stream using something not implemented), never another exception, and they
neither hang nor allocate without bound: an MJPEG image over 2^26 samples is refused, so is an H.264 frame larger
than any level allows (139,264 macroblocks, Table A-1), and an AVI chunk running past the end of the file is an error
rather than an allocation. A truncated file throws where the data runs
out (FFmpeg stops quietly instead); packets already returned stay valid. A damaged H.264 picture fails its `Decode`
call, and so does every later picture predicted from it (none comes out predicted from other pictures in its place),
until an IDR picture; frames other pictures finished meanwhile still come out, from the next call. So does every
picture after a lost reference picture (a gap in frame_num the SPS does not allow), up to the next IDR picture: the
lost picture's reference marking went with it. Slice data that ends inside a macroblock (cut short) counts as damaged:
CABAC would otherwise decode the zeros past its end.

With `H264Decoder.ConcealErrors` the decoder conceals damaged slice data instead: what a slice decoded before the
damage stays, the picture's other slices are decoded, and each missing macroblock copies its place in a reference,
with zero motion; a lost picture's frame copies the latest reference. Every frame that arrives comes out; the damaged
ones, and the ones predicted from them until an IDR picture, have `VideoFrame.IsDamaged` set (docs/conformance.md,
Error concealment). Damage that still parses is not detected, as in any decoder; damaged slice headers and parameter
sets still throw (the picture is then lost, which the next one conceals).

`tools/fuzz.sh <file|h264|mjpeg|aac> <minutes>` checks this with AFL++ through SharpFuzz (`file` demuxes a whole file
and decodes every track; the others feed one decoder its configuration and packets), with seeds taken from
`samples/`. Inputs that once broke the contract are kept in `tests/ManagedCodecs.Tests/Fuzz/Findings/<target>/`, and
`MalformedInputTests` replays them, along with a fixed-seed sweep of truncations and byte flips over a few samples.
The sweep and the first 20-minute runs of each target found sample sizes and durations over `int`, AVI chunks past
the end of the file, a zero image width or channel count, an `mb_type` code over `int` and huge MJPEG images, all
fixed; a later round (after 0.1.0-preview.2) a field picture's packet going on with a frame's slice, and an H.264
frame of 16384x14336 whose concealment took 30 s a picture; a longer one concealing errors (two hours of `h264` and
`file`) too many reference list modifications, SPS crop offsets and a first_mb_in_slice that overflowed `int`.

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
would, so H.264 frames reuse the planes of earlier ones.

`tools/bench.py` times the samples below that way, each against FFmpeg (or one build of mcdec against another, with
`--base <mcdec.dll>`), five runs a side interleaved with the order alternating, and takes the medians; one more row
times the 720p sample with 4 threads against 1. The [weekly benchmark](https://github.com/bisforboman/ManagedCodecs/actions/workflows/benchmark.yml)
(`.github/workflows/benchmark.yml`, also on demand) runs it on GitHub's x64 and ARM64 runners with Ubuntu's FFmpeg 6.1
and puts the tables in each run's summary; each run's record goes to the `benchmarks` branch, whose
[history](https://github.com/bisforboman/ManagedCodecs/tree/benchmarks) has every run's ratios.
[`compare.yml`](https://github.com/bisforboman/ManagedCodecs/actions/workflows/compare.yml) times a branch against its
base on the same two runners, before a performance change is kept (`gh workflow run compare.yml --ref <branch>`).

The samples: MJPEG `testsrc-420.avi`, `testsrc-444.avi` and `noise-q1.avi`; H.264 `h264-p-refs.mp4`,
`h264-bench-720p.mp4`, `h264-main-p-refs.mp4` (CABAC), `h264-b-pyramid.mp4`, `h264-wp-default.mp4` and
`h264-high-default.mp4` (libx264's Main and High defaults), `h264-high-b.mp4`, `h264-10bit-b.mp4`,
`h264-high-motion.mp4`, `h264-10bit-motion.mp4`, `h264-mbaff-default.mp4` and `jm/jm-paff-b.mp4` (field pictures);
AAC `aac-stereo-48k.mp4` and `aac-5.1.mp4`, HE-AAC `heaac/heaac-5.1.mp4` and `heaac/heaacv2-stereo.mp4`. On a 4-core
cloud VM (2026-10-25, FFmpeg 6.1.1), MJPEG took 0.8 to 1.0 times FFmpeg's time, AAC 0.75 (stereo) to 1.5 (5.1), HE-AAC
1.6 to 2.3 and H.264 1.85 to 3.3: less on noisier content, whose residual decoding weighs more, and at 10 bits about
the 8-bit ratio. The short MJPEG and AAC runs are mostly .NET's startup and JIT: without them (`last loop`),
`aac-5.1.mp4` takes about 90 µs a frame, `heaac-5.1.mp4` about 410, `heaacv2-stereo.mp4` about 140. A VM's speed
moves from hour to hour, and FFmpeg's and ours do not move together (unchanged code's ratios once moved by -14 to +16%
between two snapshots of a day): only comparisons made on one machine at one time count.

Cloud VMs are noisy (single runs differed by up to a third), so compare before and after on one machine. For
statistics on the decoder alone there is BenchmarkDotNet, `dotnet run -c Release --project
benchmarks/ManagedCodecs.Benchmarks` (`--filter *H264*` or `*Aac*` for one codec). It decodes packets already read
into memory, from `samples/testsrc-420.avi`, `samples/h264-bench-720p.mp4` and `samples/aac-5.1.mp4` (or the files
in `MANAGEDCODECS_SAMPLE`, `MANAGEDCODECS_H264_SAMPLE` and `MANAGEDCODECS_AAC_SAMPLE`), so its numbers leave out
what the two commands above include and are not comparable with FFmpeg's `bench:` line.

## License

MIT ([LICENSE](LICENSE)).

FFmpeg is LGPL-2.1-or-later (some parts GPL). Code ported from FFmpeg's sources must keep that
license. Implementing from the public specifications (ITU-T T.81 for JPEG, ITU-T H.264,
ISO/IEC 14496-12 for MP4) and using FFmpeg only as a reference for test output is what lets
this code be MIT. Codec patents (e.g. H.264, AAC) apply regardless of language.

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
precision, and no code was copied; where libxaac (Apache-2.0) has the same processes in floating point, it was read
too. The QMF banks' 640-tap window is libxaac's (in the spec's order), whose even taps equal the PacketVideo decoder's
own analysis table to the digits it gives; so is the HF adjustment's noise table, whose real parts equal PacketVideo's
digit for digit. AAC's noise substitution uses the Numerical Recipes random generator
with the seed FFmpeg uses (the spec leaves the generator open), so noise bands match FFmpeg too.
For MBAFF (the neighbour table 6-4, the field scans, the field CABAC context maps, the order of the
deblocking filter's field edges and the co-located field or frame of direct prediction) the ITU's reference decoder, JM (royalty-free, from the ITU), stood
in for the specification's text where that was out of reach; no code was copied.
