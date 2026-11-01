# ManagedCodecs

Pure managed C# media codecs: no native binaries, Native AOT friendly, and running on WebAssembly too. Decoders for
MJPEG, H.264 (Constrained Baseline up to High 4:4:4 Predictive), AAC LC and HE-AAC, with AVI, MP4 and raw AAC (ADTS)
demuxing, every one checked against FFmpeg sample by sample: video **bit-exact**, AAC LC within 2^-17 of FFmpeg's float
output. H.264 decodes
on several threads if asked, and every decoder has an async API.

## Scope

What this is for: decoding MJPEG, H.264 and AAC in a .NET process without native FFmpeg: no binaries to ship or
match to a platform, Native AOT, x64, ARM64 and WebAssembly from one package, and output that is FFmpeg's (video bit
for bit, AAC within 2^-17) and the same on every one of them. The decoders cover what platform decoders often do not:
H.264 up to High 4:4:4 Predictive, 9 to 14 bits, lossless, interlaced (MBAFF and field pictures), separate colour
planes; HE-AAC with parametric stereo.

What it is not: FFmpeg's speed on one thread. In the steady state (Benchmarking, GitHub's runners) MJPEG is about
FFmpeg's time (0.9 to 1.25), AAC LC under it in stereo (0.5 to 0.6), HE-AAC 1.2 to 1.9 times and H.264 1.4 to 3 times
it; FFmpeg's inner loops are hand-written assembly. H.264 decodes on several threads if asked (`Threads`).

In a browser (WebAssembly, below): the same output, on one thread, about 1.3 to 2.5 times the native time compiled
ahead of time. For mainstream 8-bit 4:2:0 H.264 and AAC LC a browser's own decoder (WebCodecs, often hardware) will be
faster; this library is for what that does not decode or does not give the same everywhere (the profiles and bit
depths above, interlaced video, MJPEG, results that must match FFmpeg's), and for a .NET app that wants no JavaScript
in between. That comparison is not measured here.

## Layout

| Project | What it holds |
| --- | --- |
| `src/ManagedCodecs.Core` | `VideoFrame`, `PixelFormat`, `Packet`, `Rational`, `IDemuxer`, `IVideoDecoder` |
| `src/ManagedCodecs.Containers` | `AviDemuxer`, `Mp4Demuxer`, `AdtsDemuxer` (raw .aac), and `Demuxer.Open` to pick one by content |
| `src/ManagedCodecs.Mjpeg` | `MjpegDecoder`: baseline/extended Huffman JPEG, interlaced AVI1, bit-exact with FFmpeg |
| `src/ManagedCodecs.H264` | `H264Decoder`: Constrained Baseline, Main, High, High 10, High 4:2:2 and High 4:4:4 Predictive profile H.264 (CAVLC, CABAC, I/P/B, weighted prediction, 8x8 transform, scaling matrices, deblocking, monochrome, 4:2:0, 4:2:2 and 4:4:4, also as separate colour planes, 8 to 14 bits, lossless, progressive and interlaced), bit-exact |
| `src/ManagedCodecs.Thumbnails` | `VideoProbe`: a video file's duration, displayed size, codecs and rotation as ffprobe reports them, without decoding; `VideoThumbnail`: a frame as a small upright RGBA image, PNG or JPEG; the `ManagedCodecs.Thumbnails` package (see Probing a video, Thumbnails; docs/decisions/2026-10-31-thumbnails.md) |
| `src/ManagedCodecs.Unity` | The netstandard2.1 build for Unity (Mono, IL2CPP): every decoder's sources (and Thumbnails') in one assembly, without the vector paths, 1 to 3.4 times slower on CoreCLR; the `ManagedCodecs.Unity` package (see Unity) |
| `src/ManagedCodecs.Aac` | `AacDecoder`: AAC LC (mono to 7.1, M/S, intensity, PNS, TNS), within 3e-7 of FFmpeg's float output; HE-AAC (spectral band replication) |
| `src/Shared` | Source files linked into several codec projects (the VLC decoder) |
| `tools/ManagedCodecs.Cli` | `mcdec`: decodes a file and times it, to compare with `ffmpeg -benchmark` (`--threads N` for H.264 frame threading and HE-AAC's channels) |
| `tools/ManagedCodecs.MonoGame` | `mcplay`: a MonoGame sample that plays a file into a texture, for in-game video (its README; issue #254) |
| `tools/ManagedCodecs.Unity.Sample` | A Unity `MonoBehaviour` that plays a video into a `Texture2D` and an `AudioClip` with the `ManagedCodecs.Unity` package (mcplay's decoding and colour conversion, linked); CI compiles it against stubs of UnityEngine, Unity has not run it |
| `tools/make-samples.sh` | Generates the test samples and FFmpeg's references |
| `tools/compare.sh` | Times mcdec and FFmpeg on the same file |
| `tools/realworld.py` | Decodes real public films with mcdec and FFmpeg and compares them (nightly, `realworld.yml`) |
| `tools/mutation/mutate.py` | Breaks each guard in `tools/mutation/mutations.py` and checks that a test fails (every pull request, `mutation.yml`) |
| `tools/upstream-issue.sh` | Files `upstream.yml`'s weekly issues: a new StyleBro, or a new release of an encoder the committed samples come from |
| `tools/ManagedCodecs.Fuzz`, `tools/fuzz.sh` | Fuzzing targets for SharpFuzz and AFL++ (see Malformed input) |
| `tools/ManagedCodecs.Wasm`, `tools/wasm-check.sh` | The decoders on WebAssembly, checked against the native build (see WebAssembly) |
| `tools/ManagedCodecs.Thumbnails.Blazor` | A Blazor WebAssembly page that probes and thumbnails a video picked in the browser with `ManagedCodecs.Thumbnails`, nothing uploaded (its README has the timings); CI publishes it |
| `tools/aot-check.sh` | mcdec published with Native AOT, checked against the JIT build on every sample (see Native AOT) |
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

- **MJPEG**: baseline and extended Huffman JPEG in 4:2:0, 4:2:2, 4:4:4, 4:1:1, 4:4:0 and gray, restart markers, missing Huffman
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
  docs/conformance.md). Parametric stereo (HE-AAC v2): 10, 20 and 34 bands, mixing procedures Ra and Rb,
  the phase parameters (IPD and OPD), a mono stream decoded to stereo; within 5e-4 of FFmpeg's output, 7e-6 on average. A frame's channels
  through SBR on several threads (`Threads`).
- **Containers**: AVI (video), MP4 (video and audio tracks, fragmented files too, edit lists of several edits)
  and raw AAC (ADTS), with packets and timestamps as FFmpeg's demuxers give them.
- **Throughout**: an async API, malformed input that ends in `InvalidDataException` or `NotSupportedException`
  (fuzzed), and the same output on x64, ARM64 and WebAssembly, and under Native AOT (CI checks all of them).

### Open

- AAC: SBR's speed (5.1 HE-AAC at about 0.38 ms a frame on GitHub's ARM64 runner once the JIT has warmed up, about
  110 times real time, 1.6 times FFmpeg's time on one thread; Benchmarking).
- MJPEG: files from real cameras (the samples are all FFmpeg's encoder's, some cut or padded as cameras do,
  docs/conformance.md), and packets holding one field (FFmpeg decodes none).
- H.264: luma and chroma bit depths that differ are rejected on purpose (no mainstream
  encoder or decoder, FFmpeg included, handles them, and they would need a bit depth for each plane in the frame
  API); without concealment, a
  lost reference fails pictures up to the next IDR picture, not an MMCO 5 one (by design: the MMCO 5 picture itself
  would have to be decoded from lists that can no longer be trusted, so its marking never applies).
- H.264 speed: 1.4 to 3.0 times FFmpeg's time on one thread in the steady state, interlaced video included (Benchmarking); at 720p
  every class sits at 2.2 to 2.8 times (CAVLC and CABAC, P and intra-only, High), so the gap is general rather than
  one class's (the CAVLC residual reader's compiled code is clean). MBAFF frames and field pictures find the pairs
  around a macroblock once (#177: 2.9% off `h264-mbaff-default.mp4`'s time, 4.0% off `jm-paff-b.mp4`'s), but each
  neighbour lookup still runs Table 6-4's case analysis on (xN, yN) for itself, where progressive pictures read a
  table made once a picture (docs/conformance.md, Performance notes; docs/decisions/2026-10-28-h264-neighbour-context.md).
  With 4 threads a 720p P chain takes 0.52 of one thread's time over 50 frames and 0.41 over 250 (one IDR picture
  each, which decodes in 2.4 times a P picture's time with the P pictures trailing it: short GOPs gain less; medians
  of five interleaved runs), on 4 cores without SMT where four single-threaded decodes run side by side about 7%
  slower. Workers wait for references' rows about 18% of the time, and each picture takes about 30% more CPU than
  alone at 720p, 60% at 320x240 (perf stat's task clock). GitHub's ARM64 runner has hardware counters (2026-10-29):
  with 4 threads the instructions rise only 2 to 3.5%, so it is no extra work; about half the extra CPU was the
  kernel's, from 8 to 14 context switches a picture as pictures blocked on their references' rows one macroblock row at
  a time, and the rest is a lower IPC (2.57 to 2.37 at 720p) and clock with four cores busy. A wait that has to block
  now waits for 32 rows more, and a report wakes only waits it satisfies: context switches fell 35 to 50% and the CPU
  time 2 to 5% on both runners, the wall time unchanged. (The x64 runner's 4 vCPUs are 2 cores with SMT, so its
  threads share cores.) Decoding a picture's rows on several threads would mostly shorten the
  IDR picture's lead: about a tenth of a 50-frame GOP's time, little of a 250-frame one's.

## Getting started

The packages, on nuget.org ([CHANGELOG.md](CHANGELOG.md) lists the versions): `ManagedCodecs` for everything, or one of
`ManagedCodecs.Containers`, `.Mjpeg`, `.H264` and `.Aac` (each with `ManagedCodecs.Core`). They share internals, so
each depends on the others at exactly its own version, and NuGet reports a mix (NU1107 or NU1608). From 0.1.0 on, the public API changes only in a new minor version
(0.2.0), with a "Breaking changes" entry in the changelog; a patch version only fixes.

```
dotnet add package ManagedCodecs
```

For Unity (Mono, IL2CPP), `ManagedCodecs.Unity` instead (see Unity, below).
For a video's facts and thumbnail in a web back end or a browser, `ManagedCodecs.Thumbnails` (see Probing a video and
Thumbnails, below).

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

Each frame says how it was coded in `PictureType` (I, P or B), the `pict_type` that `ffprobe -show_frames` prints: in
H.264 the type of the picture's first slice, of its first field for a frame of two field pictures; I for every MJPEG
frame. Whether a frame is a keyframe is the packet's `IsKeyframe`.

Interlacing as FFmpeg reports it, for a deinterlacer to go by: `IsInterlaced` and `IsTopFieldFirst` are each frame's
`interlaced_frame` and `top_field_first` (`ffprobe -show_frames`), `IsCodedAsFields` says it was two field pictures
(PAFF), and `FieldOrder` is the order FFmpeg's parser gives its picture (tt, bb, progressive); `H264Decoder.FieldOrder`
is the stream's, as `ffprobe -show_streams` prints it for an MP4 file (its first picture's). In H.264 they follow the
picture timing SEI where the stream has one, else how the frame was coded and its fields' order counts
(docs/conformance.md, Interlacing); MJPEG frames are never flagged, as in FFmpeg.

What the samples mean, for a converter to RGB or a player to go by: `ColorRange` (limited or full), `ColorPrimaries`,
`ColorTransfer` (PQ and HLG among them) and `ColorMatrix` (the YCbCr to RGB coefficients), with ITU-T H.273's values
as FFmpeg's `color_range`, `color_primaries`, `color_transfer` and `color_space` have them; `ChromaLocation`; and
`SampleAspectRatio` (a frame is shown `Width` times it wide; 0:1 when unknown). In H.264 they are the SPS's VUI as
FFmpeg reads it (docs/conformance.md, Colour and aspect ratio). Nothing is guessed: most H.264 streams leave the
matrix unspecified, which usually means BT.601 below 720 lines and BT.709 from 720, in limited range. MJPEG frames are
BT.601 (`Bt470BG`) with centred chroma, full range (limited where FFmpeg's encoder said so), and take the JFIF
density's aspect ratio, as FFmpeg gives them (docs/conformance.md, MJPEG). An MP4 file's `VideoStreamInfo` has the
container's say, before anything is decoded: `ColorRange`, `ColorPrimaries`, `ColorTransfer` and `ColorMatrix` from
'colr', `SampleAspectRatio` from 'pasp', and `Rotation` (0, 90, 180 or 270 degrees counter-clockwise, from the track
and movie matrices: phone video) for a player to turn the frames by, which the decoders do not. A decoder made from
the stream gives its frames the container's colour where the bitstream says none, and the container's ratio where it
has one, as ffprobe shows FFmpeg's frames. An AVI file's has the sample aspect ratio of its 'vprp' (OpenDML's video
properties, which FFmpeg's muxer writes), and nothing else of these.

An MP4 file's tracks can be read one at a time: `new Mp4Demuxer(stream, MediaTypes.Video)` leaves the audio track out
(no sample table, no packets, `Audio` null). Every demuxer's `ReadPacketInfo()` lists the packets without their data,
each one's size, keyframe and discard flags and timestamps: a duration, a packet list or a bitrate graph needs nothing
more. MP4's comes from the sample tables and ADTS's from the frames found on opening, neither reading the file; AVI's
walks the 'movi' list reading each chunk's 8-byte header and skipping its data.

```csharp
using var demuxer = new Mp4Demuxer(File.OpenRead("video.mp4"), MediaTypes.Video);
long bytes = demuxer.ReadPacketInfo().Sum(p => (long)p.Size);
```

`Seek(pts, streamIndex)` moves where the next read starts: at the keyframe at or before `pts` (in that stream's time
base), the other stream at the same instant. The packets are those a read from the start gives from there, so decode
them with a new decoder (or one flushed) and drop the frames shown before the time, as FFmpeg's `-ss` does. MP4 seeks
in every file, fragmented and edited ones too, by its sample tables; AVI in MJPEG, every frame of which is a keyframe
(other codecs in AVI need the 'idx1' index, not read yet, and throw `NotSupportedException`, as raw AAC does).

```csharp
using var demuxer = Demuxer.Open("video.mp4");
var video = demuxer.Video!;
long pts = 10 * video.TimeBase.Denominator / video.TimeBase.Numerator; // 10 s
demuxer.Seek(pts, video.StreamIndex);
var decoder = new H264Decoder(video);
var frame = demuxer.ReadPackets().Where(p => p.StreamIndex == video.StreamIndex)
    .SelectMany(p => decoder.Decode(p)).First(f => f.Pts >= pts);
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
damage stays, the picture's other slices are decoded, and each missing macroblock is predicted from a reference with
the motion of the neighbour whose prediction matches its edges best; a lost picture's frame copies the latest reference. Every frame that arrives comes out; the damaged
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
`file`) too many reference list modifications, SPS crop offsets and a first_mb_in_slice that overflowed `int`. An
hour each of `mjpeg` and `aac` after that (17 and 18 million inputs) found nothing. Field pictures' long-term marking (after 0.2.0): 40
minutes of `h264` seeded with the long-term field samples alone (4.4 million inputs, with and without concealment)
found nothing, and `ReferenceListTests` marks random field pictures with random memory management operations and
checks the lists after each.

### Probing a video

`ManagedCodecs.Thumbnails`'s `VideoProbe.Read(stream)` gives an MP4 or AVI file's facts without decoding a frame:
`Duration` as ffprobe's `format=duration`, the coded and the displayed size (stretched by the pixels' aspect ratio,
wider above 1 and taller below, then turned by the rotation), codec, frame rate, rotation and the audio's codec, rate
and channels. Each is checked against ffprobe on every sample (VideoProbeTests). The stream must be seekable (an MP4
file's index can follow its data): buffer an upload first. A codec this library does not decode throws
`NotSupportedException`, a file without video `InvalidDataException`; AVI's audio is not read, so an AVI lasts as long
as its video. The duration comes from the streams' `Duration` (`VideoStreamInfo`, `AudioStreamInfo`), which the
demuxers give as FFmpeg's stream duration: MP4's edits' length, capped at the samples they take, else the track's,
AVI's stream header length.

```csharp
var info = VideoProbe.Read("upload.mp4");
Console.WriteLine($"{info.Duration} {info.DisplayWidth}x{info.DisplayHeight} {info.Codec}");
```

### Thumbnails

`VideoThumbnail.Create(stream, options)` takes one frame of an MP4 or AVI file (H.264 or MJPEG) and gives a
`Thumbnail`: its RGBA pixels upright, `Width`, `Height`, the frame's `Time`, `SavePng`/`ToPng` (a PNG written here,
deflated by the BCL: no image library, native code or FFmpeg) and `SaveJpeg`/`ToJpeg` (a baseline JPEG written here,
4:2:0, `quality` 1 to 100 as libjpeg's, 85 by default; docs/decisions/2026-11-01-thumbnails-jpeg.md). For WebP, hand
`Pixels` to SkiaSharp or ImageSharp.

- **Which frame:** the one shown at `At` from the file's start as ffmpeg's `-ss` takes it (the time in the stream's
  time base to the nearest unit, then the first frame at or after that), decoded from the keyframe before it
  (`IDemuxer.Seek`; AVI's H.264, which has no index to seek by, from the start). By default 10% into the video, at most
  10 s. `SkipDark` steps keyframe by keyframe past frames whose mean luma is under 10% of white (up to 10, then the
  brightest). Damaged streams decode on with concealment, and an undamaged frame is taken where there is one.
- **Its size:** the frame as shown (aspect ratio and rotation applied), scaled down by area to fit `MaxWidth` x
  `MaxHeight` (320 x 320 by default), never above its decoded size.
- **Its colours:** converted at full size by the frame's matrix and range, as FFmpeg's scaler takes them where the
  stream says nothing (BT.601, limited range; MJPEG full range), or by `Matrix` and `Range` from the options. The
  primaries and transfer are not converted: HDR comes out dim and flat. Within 0 to 3 levels of
  `ffmpeg -vf format=rgba,scale=w:h:flags=area` on every sample (docs/conformance.md).
- **Uploads nobody vetted:** a file declaring frames over `MaxPixels` (16,777,216 by default; in its container, its
  SPS or a JPEG's header) is refused with `NotSupportedException` before they are decoded, and `CancellationToken`
  stops the decoding. Managed code rules out the memory corruption FFmpeg's parsers keep yielding; it doesn't rule out
  a hostile file being slow, which is what the limits are for (the `thumbnail` fuzz target, tools/fuzz.sh, covers this
  path).

```csharp
await using var buffer = new MemoryStream();
await request.Body.CopyToAsync(buffer); // MP4's index can follow its data: the stream must be seekable
buffer.Position = 0;

var thumbnail = await VideoThumbnail.CreateAsync(buffer, new ThumbnailOptions { MaxWidth = 480 }, cancellationToken);
thumbnail.SavePng(response.Body); // or thumbnail.SaveJpeg(response.Body, quality: 85)
```

### Unity

`ManagedCodecs.Unity` is the same sources built for .NET Standard 2.1, which Unity's runtimes (Mono and IL2CPP, Unity
2021.2 or later) load: one assembly, `ManagedCodecs.Unity.dll`, with every decoder and demuxer and the Thumbnails package's `VideoProbe`
and `VideoThumbnail`, and
`System.Runtime.CompilerServices.Unsafe` as its one dependency. .NET Standard 2.1 has no
`System.Runtime.Intrinsics`, so the vector paths are left out (`#if NET`) and their scalar references run. The
output is the same to the bit: CI runs the whole test suite against this assembly on CoreCLR, and decodes every sample
on Mono 6.12 (`tools/unity-check.sh`), comparing the output, and each video file's probe and thumbnail, with the
net10.0 build's. IL2CPP is not tested; it needs a
Unity install. On Mono 6.12 (one thread, GitHub's x64 runner) it takes 4 to 19 times the net10.0 build's time:
H.264 4 to 22 ms a 320x240 frame and 80 ms a 720p one (12.5 frames a second), MJPEG 1 to 3.4 ms a 320x240 frame,
AAC 0.5 to 1.5% of a core in real time and HE-AAC 2 to 7%; so audio, MJPEG and small cutscenes, not HD H.264 on one
thread. On CoreCLR the scalar build takes 1 to 3.4 times the other packages' time
(docs/decisions/2026-10-31-unity-netstandard.md has both tables). WebGL builds have no threads,
so `H264Decoder.Threads` stays at 1 there. The package holds the same types as the others, so a project references one
or the other, never both. Its nuget.org page is `src/ManagedCodecs.Unity/PACKAGE.md`. A sample `MonoBehaviour` playing a video with it is in `tools/ManagedCodecs.Unity.Sample` (compiled in CI
against stubs of UnityEngine; not yet run in Unity).

### WebAssembly

The libraries run unchanged on `browser-wasm`. `tools/wasm-check.sh [aot]` decodes every sample there, under
Node, and natively (`mcdec --hash`, a SHA-256 of each file's decoded frames and audio samples), and fails unless the
output is the same. All 342 sample files agree, interpreted or compiled ahead of time, and CI's `wasm` job runs both
on every pull request (until 0.2.2 it ran only the AOT build, and the interpreter's H.264 output was wrong from 0.2.0
on, #206). The native build is the one checked against FFmpeg, so the wasm output matches
FFmpeg as well. It does the same for `ManagedCodecs.Thumbnails` (`mcdec --thumbnail-hash`: each video file's probe
facts and a small thumbnail's size, time and pixels), and all 362 MP4 and AVI files agree there too.
`tools/ManagedCodecs.Wasm` needs the `wasm-tools` workload (`dotnet workload install wasm-tools`), so it is not part
of `ManagedCodecs.slnx`. `tools/ManagedCodecs.Thumbnails.Blazor` is a browser page using the thumbnails package.

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
`--base <mcdec.dll>`, or a Native AOT publish of mcdec: `--base` takes the native executable too), five runs a side interleaved with the order alternating, and takes the medians; one more row
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
AAC `aac-stereo-48k.mp4` and `aac-5.1.mp4`, HE-AAC `heaac/heaac-5.1.mp4` and `heaac/heaacv2-stereo.mp4`. On GitHub's
4-core runners (the benchmark run of 2026-10-30 at 0.4.0, x64 on a Xeon Platinum 8370C; FFmpeg 6.1.1), each sample
comes out two ways: mcdec's whole process over FFmpeg's, .NET's startup and JIT included, and its last loop alone
(steady state, what a long file costs a frame) over FFmpeg's, whose startup is a few percent. The short samples'
whole-process ratios are mostly startup: on the 50-frame H.264 clips it is about 45% of mcdec's time. Whole process,
then steady state, x64 and ARM64: MJPEG 0.94 to 1.10 and 1.06 to 1.38, then 0.89 to 1.01 and 1.01 to 1.25; AAC 0.84
to 0.95 (stereo) and 1.7 to 1.8 (5.1), then 0.50 to 0.57 and 1.25 to 1.33; HE-AAC 1.6 to 2.2, then 1.2 to 1.9; H.264
1.5 to 3.6, then 1.4 to 3.0 (the 720p sample 2.27 and 2.05, libx264's default streams 1.8 to 2.2, high motion 1.4 to
2.0, 10 bits with B frames 3.0 on x64 and 2.1 on ARM64, MBAFF and field pictures 2.0 to 2.6). Four threads take the
720p sample to 0.55 of one thread's time on x64 and 0.39 on ARM64. Builds timed against each other in one
`compare.yml` run: 0.2.5's H.264 work (#216 to #223) took 2.7% off the 720p sample's steady frame on x64 and 0.8% on
ARM64 against 0.2.4; 0.4.0's CAVLC residual reader (#232) 3.2% on x64 and 2.4% on ARM64, CABAC unchanged; ARM64's
zip interleaves (#235, after this run) 2.4 to 3.4% off MJPEG on ARM64.

On ARM64 (128-bit vectors) the gaps that were this code's own are closed: the MJPEG IDCT's 128-bit butterflies are
inlined (#200, about 3%), and SBR's and parametric stereo's last 256-bit-only loops have 128-bit paths (#201, about
4% on HE-AAC); `profile.yml` (perf on both runners) found them. What is left is FFmpeg's: its ARM64 JPEG IDCT is
NEON assembly (about 16% faster than its C one on the same runner), and AAC's FFT is about 20% of an ARM64 frame
against 11% on x64, likely because ours works on 2 doubles a vector where FFmpeg's works on 4 floats (not
confirmed; fusing two FFT stages a pass gained only 1 to 2% and was dropped). H.264's ARM64 profile is as flat as
x64's. The QMF banks' real-input transforms (2026-10-28) took 14 to 16% off 5.1 HE-AAC and 8 to 10% off
HE-AAC v2 on both runners (`compare.yml` against the code before them). The runners' CPUs differ from run to run (an
EPYC 7763 on 2026-10-25 and in the first run of 2026-10-29, a 9V74 on 2026-10-28, a 9V45 in the second run of
2026-10-29 and in the first run of 2026-10-30, a Xeon 6973P-C in the third run of 2026-10-29, a Xeon Platinum 8370C in the second run of 2026-10-30, which moved the x64 ratios by up to a third with the code unchanged): a ratio is a snapshot, and only builds timed against each other in one run
(`compare.yml`) show a change. A VM's speed
moves from hour to hour, and FFmpeg's and ours do not move together (unchanged code's ratios once moved by -14 to +16%
between two snapshots of a day): only comparisons made on one machine at one time count.

### Startup and the JIT

mcdec turns tiered compilation off (its project file), so each method is compiled fully optimized on its first call
and short runs aren't timed on unoptimized code; that is also what the ratios above measure. An app has the runtime's
defaults, tiered compilation and dynamic PGO, which start faster but cost a short file more. `h264-high-default.mp4`
(50 frames, 320x240) decoded by one process, on a 4-core cloud VM (2026-10-29; median of 3, process wall time):

| Runtime settings | 1 loop (50 frames) | 20 loops | 100 loops | A frame, steady |
| --- | ---: | ---: | ---: | ---: |
| Tiered compilation off (mcdec) | 0.60 s | 1.44 s | 5.1 s | about 850 µs |
| Defaults: tiered compilation, dynamic PGO | 0.47 s | 2.46 s | 5.5 s | about 735 µs |
| Tiered compilation, `TieredPGO` off | 0.43 s | 1.52 s | 5.0 s | about 810 µs |
| ReadyToRun (`--instruction-set:x86-64-v3`), tiering off | 0.41 s | about 1.56 s | | |

Dynamic PGO runs instrumented code first and pays that back only on long files (about 9% less a frame once it is done,
but still behind after 5,000 frames here). For short files, turn it off in the app's project:

```xml
<TieredPGO>false</TieredPGO>
```

ReadyToRun, set when the app publishes (it compiles these packages too), saves about 0.2 s a process. Give crossgen
the instruction set, or the vector paths it leaves out run slower with tiering off:

```xml
<PublishReadyToRun>true</PublishReadyToRun>
<PublishReadyToRunCrossgen2ExtraArgs>--instruction-set:x86-64-v3</PublishReadyToRunCrossgen2ExtraArgs>
```

Most of the H.264 decoder is still compiled at run time even then (267 methods, 115 of them
`PictureDecoder<byte>`'s), which is why the gain is small. Native AOT (below) compiles nothing at run time.

### Native AOT

On x64, a Native AOT publish compiles for the instruction set it is given, not the machine it runs on. Under its
default, `Avx2.IsSupported` and `Vector256.IsHardwareAccelerated` read false, so the MJPEG IDCT's AVX2 path and AAC's
256-bit ones are left out (SSSE3 and SSE4 are still used, checked at run time). The portable
`Vector128.Shuffle` is a software call there rather than one pshufb, so the transposes and the deblocking's lane
spreads use SSE2's unpacks and SSSE3's pshufb directly (`VectorZip`): before that the call took a third of MJPEG's time
(1.6 to 1.9 times the JIT's) and 6% of 720p H.264's. For the 256-bit paths, set the instruction set in the
publishing project:

```xml
<IlcInstructionSet>x86-64-v3</IlcInstructionSet>   <!-- AVX2: Intel since 2013 (Haswell), AMD since 2015 -->
```

(`native` for the build machine's own). A v3 binary refuses to start on a CPU without AVX2. `tools/aot-check.sh
[x86-64-v3]` publishes mcdec with Native AOT, warnings as errors, and fails unless it decodes every sample as the JIT
build does; CI's `aot` job runs it for both targets on every pull request. ARM64 needs nothing: its
vector instructions are part of the baseline. `mcdec` published both ways (2026-10-27, 4-core cloud VM, `last loop`
per frame, medians of 5 interleaved runs, against the JIT build of the same code):

| Sample | JIT (µs) | AOT, default | AOT, `x86-64-v3` |
| --- | ---: | ---: | ---: |
| `testsrc-420.avi` (MJPEG) | 344 | 1.21x | 1.10x |
| `noise-q1.avi` (MJPEG) | 1260 | 1.12x | 1.06x |
| `h264-p-refs.mp4` | 622 | 0.99x | 1.04x |
| `h264-bench-720p.mp4` | 11977 | 0.97x | 1.06x |
| `h264-high-b.mp4` | 1252 | 1.12x | 1.20x |
| `h264-10bit-b.mp4` | 1911 | 1.01x | 1.01x |
| `h264-mbaff-default.mp4` | 1588 | 1.13x | 1.11x |
| `aac-5.1.mp4` | 126 | 1.33x | 1.06x |
| `heaac/heaac-5.1.mp4` | 639 | 1.32x | 1.00x |

The default target leaves AAC and HE-AAC about a third slower (their 256-bit paths); H.264 and MJPEG are within 0 to
20% of the JIT either way, and perf profiles of the v3 and JIT builds of 720p H.264 have the same shape, no function's
share more than a point apart (mcdec's JIT build has tiered compilation and so dynamic PGO off, so that is not the
difference). `OptimizationPreference=Speed` changed nothing beyond the noise. Startup is AOT's own gain: in bench.py's
whole-process times (taken before the shuffle fix) the short H.264 and AAC LC runs took less under AOT than under the
JIT.

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

Two things the specifications leave to others. T.81 does not fix the IDCT's arithmetic, and output bit-exact with
FFmpeg's needs its integer arithmetic: `SimpleIdct` implements the same arithmetic independently (its constants are
cos(k pi / 16) sqrt(2) 2^14 rounded, its rows keep 11 fractional bits), no code copied. And constant tables are
error-prone to type in: each was taken from one source and checked against another. The H.264 CAVLC tables, kept
in the spec's own layout, were cross-checked against h264bsd (Apache-2.0) by enumerating its decoder.
The CABAC tables (context initialization, rangeTabLPS, state transitions) are the spec's values,
transcribed from OpenH264 (BSD-2-Clause) and verified against libavc (Apache-2.0): its precomputed
context states for every QP and initialization table equal the spec's initialization applied to ours. Contexts 460
to 1023 (4:4:4's Cb and Cr residual blocks and the coded_block_flag of 8x8 blocks) repeat the luma contexts of their
block category; all of them were compared, with the residual contexts below 460, against the tables of JM, the ITU's
reference software (3740 values, all four initialization tables).
The AAC Huffman codebooks were cross-checked the same way against the PacketVideo AAC decoder
from Android (Apache-2.0). Parametric stereo's phase codebooks (IPD, OPD), which that decoder doesn't read, are the spec's (Annex 8.B), read
from the FDK encoder's code tables and equal to the codes FFmpeg's code lengths give in canonical order; its 34-band tables (the hybrid filters' prototypes, the groups and their parameter
bands, the sub-subbands' centre frequencies) are libxaac's (Apache-2.0), which equal FFmpeg's. For spectral band replication (HE-AAC), whose specification text (ISO/IEC 14496-3 4.6.18,
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

[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), packed into every package, has the notices of the projects whose
tables were transcribed (OpenH264's CABAC tables; libxaac's QMF window, SBR noise table and 34-band tables) and lists
the ones only consulted.
