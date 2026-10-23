# ManagedCodecs

Pure managed C# media codecs: no native binaries, Native AOT friendly.
The first goal is a baseline MJPEG decoder whose output is **bit-exact** with FFmpeg,
then the same approach for H.264 + AAC in MP4.

## Layout

| Project | What it holds |
| --- | --- |
| `src/ManagedCodecs.Core` | `VideoFrame`, `PixelFormat`, `Packet`, `Rational`, `IDemuxer`, `IVideoDecoder` |
| `src/ManagedCodecs.Containers` | `AviDemuxer`, `Mp4Demuxer`, and `Demuxer.Open` to pick one by content |
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
take 0.63 of one thread's time and 4 threads 0.47 (`mcdec --threads N`). The default, 1, decodes on the calling thread,
which WebAssembly in a browser needs; it costs 0.3% more instructions than before frame threading existed (callgrind).

`AacDecoder.Decode(packet)` returns one `AudioFrame`: the packet's 1024 samples per channel, less the
`packet.SkipSamples` at its start. The MP4 demuxer sets those from the edit list, so encoder priming is dropped and the
output matches FFmpeg's default (`Decode(packet.Data.Span)` gives all 1024).

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
output is the same. All 272 sample files agree, interpreted or compiled ahead of time, and CI's `wasm` job runs the
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
would, so H.264 frames reuse the planes of earlier ones. A snapshot, made that way (2026-10-22, after the output frame
pool, FFmpeg 6.1.1, single-threaded on a 4-core AVX2 (and AVX-512) Xeon cloud VM, median of five interleaved runs;
µs per frame, audio frames are 1024 samples):

| Sample | Loops | ManagedCodecs | FFmpeg |
| --- | ---: | ---: | ---: |
| `testsrc-420.avi` (320x240 MJPEG 4:2:0) | 100 | 294 | 358 |
| `testsrc-444.avi` (MJPEG 4:4:4) | 100 | 539 | 508 |
| `noise-q1.avi` (MJPEG, noise at the highest quality) | 100 | 1074 | 1284 |
| `h264-p-refs.mp4` (320x240 H.264, P frames) | 40 | 682 | 246 |
| `h264-bench-720p.mp4` (1280x720 H.264) | 10 | 7656 | 2412 |
| `h264-main-p-refs.mp4` (320x240 H.264 Main, CABAC) | 40 | 864 | 360 |
| `h264-b-pyramid.mp4` (320x240 H.264 Main, CABAC, B frames) | 20 | 1505 | 506 |
| `h264-wp-default.mp4` (320x240 H.264, libx264's Main defaults: B frames, weighted prediction) | 20 | 1258 | 374 |
| `h264-high-default.mp4` (320x240 H.264, libx264's High defaults: also the 8x8 transform) | 20 | 1269 | 368 |
| `h264-high-b.mp4` (320x240 H.264 High, B frames, all partitions) | 40 | 1645 | 594 |
| `h264-10bit-b.mp4` (the same at 10 bits, High 10) | 40 | 1947 | 628 |
| `h264-high-motion.mp4` (320x240 H.264 High, fast motion and noise, QP 22) | 20 | 5906 | 3199 |
| `h264-10bit-motion.mp4` (the same at 10 bits, QP 34, which is QP 22's step size) | 20 | 6299 | 3285 |
| `aac-stereo-48k.mp4` (AAC, stereo) | 100 | 56 | 65 |
| `aac-5.1.mp4` (AAC, 5.1) | 100 | 169 | 88 |

The short MJPEG and AAC runs are dominated by .NET's startup and JIT and move the most from run to run
(testsrc-420.avi took between 263 and 604 µs in the previous snapshot, 274 to 319 in this one). At 10 bits the ratio
to FFmpeg is close to the 8-bit one: 3.1 against 2.8 times on the B frame pair, 1.9 against 1.85 times on the
motion pair; the 10-bit streams took us 7 to 18% more time than their 8-bit counterparts, FFmpeg 3 to 6% more. The
10-bit rows read 6 to 14% slower than in the previous snapshot while callgrind counts 1.5% fewer instructions for
`h264-10bit-b.mp4` since then: the VM's day, not the decoder.

A 1280x720 libx264 default stream (High, CABAC, B frames; `testsrc2` with noise, 2 s) took 3.3 times FFmpeg's time
in the previous snapshot (10958 against 3338 µs). One with heavier noise (`testsrc2=size=1280x720:rate=25,noise=alls=10:allf=t`,
2 s, libx264's defaults), whose residual decoding weighs more, took 2.5 times in this one (33624 against 13680 µs). On 2026-10-21's VM the first took 3.0 times (9674 against 3178 µs), down from 4.3
times before the direct prediction and motion compensation changes of that day, and `h264-bench-720p.mp4` took 8554
against 2448 µs (7066 against 2352 the day before): a VM's speed moves from day to day, which is why only
same-machine comparisons count.

Cloud VMs are noisy (single runs differed by up to a third), so compare before and after on one machine. For
statistics on the decoder alone there is BenchmarkDotNet, `dotnet run -c Release --project
benchmarks/ManagedCodecs.Benchmarks` (`--filter *H264*` or `*Aac*` for one codec). It decodes packets already read
into memory, from `samples/testsrc-420.avi`, `samples/h264-bench-720p.mp4` and `samples/aac-5.1.mp4` (or the files
in `MANAGEDCODECS_SAMPLE`, `MANAGEDCODECS_H264_SAMPLE` and `MANAGEDCODECS_AAC_SAMPLE`), so its numbers leave out
what the two commands above include and are not comparable with FFmpeg's `bench:` line.

## Roadmap

1. ~~MJPEG baseline decoder, bit-exact against FFmpeg (4:2:0, 4:2:2, 4:4:4, gray, restart markers, missing DHT)~~ done;
   interlaced AVI1 frames too (two JPEG fields per packet, recognized as FFmpeg does by being less than 3/4 of the
   stream's height, so the decoder needs `new MjpegDecoder(video)`), woven with the first field on the odd rows, the
   order FFmpeg takes for AVI, which does not tell it (`FirstFieldOnTop` gives the other; the AVI1 segment's field
   byte is ignored, as by FFmpeg). Still open: real camera files, and packets of one field (FFmpeg decodes none)
2. SIMD: `Span`, `Vector128`/`Vector256` IDCT and colour paths; measure the ratio to FFmpeg.
   Done so far: AVX2 IDCT, DC-only blocks, single-lookup coefficient decoding, buffer reuse, and for machines
   without AVX2 (ARM64 among them) an IDCT in cross-platform `Vector128` operations, so the code ARM64 runs is
   tested on x64 too (against the scalar IDCT on random blocks, and against FFmpeg with `DOTNET_EnableAVX2=0`, a CI
   step): with AVX2 off, 43 and 47% fewer instructions than the scalar IDCT on `testsrc-420.avi` and `noise-q1.avi`
   (callgrind), 16 and 18% less time. CI runs every test on ARM64 too (`build-arm64`, GitHub's ubuntu-24.04-arm
   runner), against references made there; the H.264 deblocking filter's transposes have a `Vector128` version
   for it as well (x64 keeps SSE2's), and that job found 8-bit H.264 throwing PlatformNotSupportedException on
   ARM64 before (the chroma filter called SSE2's transposes unchecked).
   Decoding straight into the output frame: opt-in, `MjpegDecoder.ZeroCopy` (6% fewer instructions)
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
   Interlaced video: MBAFF frames of I, P and B slices (macroblock pairs coded as frame or field
   pairs, mixed in any way; skipped pairs whose kind is inferred; field macroblocks predicting
   from either field of a reference frame; spatial and temporal direct prediction from
   co-located pairs of either kind, and implicit weights, with field order counts), with CAVLC
   and CABAC, 4x4 and 8x8 transforms, slices and the deblocking filter, so libx264's interlaced
   output decodes; and progressive frames from interlaced-capable parameter sets. Field pictures
   (PAFF) of I, P and B slices: each field coded as a picture of its own, paired into a frame
   (the second field predicting from the first), with field reference lists, field order counts
   and field marking, mixed with frame pictures from one frame to the next, and direct prediction
   from a co-located field, from the frame macroblocks over a field's rows, or for a frame from
   the nearer field of a frame coded as fields.
   Not checked against FFmpeg there:
   constrained intra prediction next to a left pair of the other kind with
   one intra and one inter macroblock (libx264's output for it is not conforming: FFmpeg and JM
   both reject the prediction modes it uses), and a frame macroblock over a co-located field pair
   whose two fields are equally far in order count (the bottom one is taken, as in JM).
   Field pictures come from JM, the ITU reference encoder: field pairs with CABAC and CAVLC, slices,
   POC types 0, 1 and 2, constrained intra prediction and the 8x8 transform, B field pairs with
   spatial and temporal direct prediction, and frames switching between frame and field coding
   (B fields over frames and B frames over fields), MBAFF frames (all field pairs) next to field
   pictures, and field pictures in 4:0:0. Weighted prediction in them is added to JM's streams
   afterwards (its encoder crashes when asked to weight field pictures): `tools/h264-add-weights.py`
   sets weighted_bipred_idc 2 for implicit weights, or puts a pred_weight_table with weights and
   offsets of their own for each reference index in every P and B slice header; JM's decoder
   decodes each rewritten stream exactly as FFmpeg does. So explicit weights in P and B fields
   (CAVLC and CABAC, B over frames and fields), next to MBAFF frames, and implicit weights from
   field order counts are checked too. Not reached by JM's streams: a B frame after
   its co-located field pair in display order (so the bottom field is the nearer one), and a B
   frame over a field pair partitioned below 16x16. Field marking that JM's streams never reach
   (the second field of a frame not sliding the window, memory management operation 1 by field
   PicNum) and the field B lists' list 1 swap have unit tests; other memory management operations
   in field pictures and a field without its pair throw NotSupportedException.
   Streams from a second encoder, OpenH264, are checked against FFmpeg too: long-term references
   with reference list modification and memory management operations 1, 4 and 6, POC type 2,
   non-reference P frames, slices by size, deblocking that stops at slice edges, changing and
   repeated parameter sets, the base layer of an SVC stream (prefix NAL units and enhancement
   slices skipped), and gaps in frame_num from dropped temporal layers. The frames inferred for
   such gaps (8.2.5.2) take their places in the default reference lists and the sliding window;
   no encoder names a reference by such a position, so hand-built streams test that.
   Direct prediction without direct_8x8_inference_flag, which libx264 never writes, is checked
   on two libx264 B streams with the flag cleared in their SPS (Main profile, where it changes
   only how direct motion is derived, and FFmpeg decodes the same rewritten stream).
   High 4:2:2 (8-bit) is checked on libx264's 4:2:2 output: 8x16 chroma blocks
   with the 2x4 chroma DC transform (at QP_C + 3), CAVLC's and CABAC's chroma DC coding of 8
   levels, chroma intra prediction over 16 rows, chroma motion with quarter-sample vertical
   vectors and the chroma deblocking edges of 4:2:2 (including those that 8x8 luma transforms
   skip), with CAVLC and CABAC, the 4x4 and 8x8 transforms, P and B frames, weights, constrained
   intra prediction and cropping; I_PCM in 4:2:2 from hand-built streams. Interlaced 4:2:2 too:
   libx264's MBAFF frames (intra prediction from 16 chroma rows of neighbours, and chroma lines
   across mixed pair edges taking their own luma line's bS) and JM's field pairs, also mixed with
   frame pictures and B pictures.
   10-bit video (High 10 and High 4:2:2 at 10 bits) is checked on libx264's output too: QP_Y from
   -12 (QpBdOffset) with dequantization at QP'Y and chroma QPs below 0, Clip1 to 1023, deblocking
   thresholds and weighted-prediction offsets scaled by 4, CAVLC level prefixes of 16 and more,
   at QP_Y -12 on noise, P and B frames, weights, constrained intra prediction, cropping, 4:2:2
   and MBAFF; I_PCM at 10 bits from hand-built streams. Motion compensation and the deblocking
   filter have vector paths for 10-bit samples too (32-bit lanes for the 6-tap filter, 16-bit
   ones for deblocking), each checked against its reference on random 10-bit input.
   9, 12 and 14 bits (High 10 and High 4:4:4 Predictive) are checked on JM's output (libx264 writes
   only 8 and 10): P and B frames with the 8x8 transform, explicit weights in P and B at 12 bits
   (bi-predicted blocks too), CAVLC at 12 and 14 bits, 4:2:2 at
   12 and 4:4:4 at 14, at QPs down to -6 (CAVLC's level_prefix up to 15); I_PCM from hand-built streams, and CAVLC's level_prefix of 16 and more (which only levels
   past about 2048 need, and which JM never writes: it codes such macroblocks as I_PCM) from unit
   tests. Frames come out as `Yuv420P9`, `Yuv422P12`, `Gray14` and the like (FFmpeg's yuv420p9le
   and so on). Deblocking takes its 16-bit lanes up to 12 bits (the strong filter sums eight
   samples, 8 x 4095 + 4 at most) and the scalar filter at 14. 11 and 13 bits are rejected: FFmpeg
   has no pixel format for them, so nothing to check them against. Monochrome at 9 to 14 bits is
   not checked (JM writes it with 8-bit chroma, which FFmpeg refuses), nor are luma and chroma
   bit depths that differ (FFmpeg does not decode them; this decoder rejects them).
   4:4:4 (High 4:4:4 Predictive, 8 and 10 bits) is checked on libx264's 4:4:4 output: Cb and Cr
   coded as luma (Intra16x16 DC and AC, 4x4 and 8x8 blocks, CABAC contexts 460 to 1023 and the
   coded_block_flag that 8x8 blocks have only in 4:4:4, Table 9-4's 4:4:4 coded_block_pattern),
   predicted with the luma intra modes and the luma 6-tap filter, dequantized at their own QP'C
   with their own scaling lists (12 in the PPS), and deblocked with the luma filter at QP_C on the
   luma edges, with CAVLC and CABAC, P and B frames, constrained intra prediction, cropping, MBAFF
   and 10 bits; I_PCM in 4:4:4 from hand-built streams. Separate colour planes are rejected. x264 before
   build 151 derived the context of an 8x8 block's coded_block_flag in 4:4:4 against the spec (a
   neighbour without 8x8 transforms counted as missing); FFmpeg decodes such streams by the encoder's
   version, this decoder by the spec, so they come out differently.
   Lossless coding (qpprime_y_zero_transform_bypass_flag with QP'Y 0) is checked on libx264's
   `-qp 0` output: the levels added as the residual with no scaling or transform, Intra_16x16 and
   chroma DC levels put in place, and the residual of intra blocks predicted vertically or
   horizontally summed along that direction first (8.5.15; over the whole macroblock for
   Intra_16x16 and for chroma), in 4:2:0, 4:2:2 and 4:4:4, at 8 and 10 bits (QP_Y -12), with
   CABAC and CAVLC, the 4x4 and 8x8 transforms, P and B frames and MBAFF. Deblocking treats
   lossless macroblocks by their QP_Y, as JM and FFmpeg do (libx264's `-qp 0` puts every
   macroblock at QP 0, so lossless next to lossy macroblocks is not checked). x264 before build 151
   also predicted lossless Intra_8x8 blocks from unfiltered neighbours; FFmpeg follows it there
   too, this decoder the spec.
   Monochrome (chroma_format_idc 0) comes out as `Gray8` or `Gray10`, one plane; it is checked on
   libx264's gray output (High and High 10): no intra_chroma_pred_mode, the coded_block_pattern
   without a chroma part, no chroma weights in pred_weight_table, with CABAC and CAVLC, P and B
   frames, explicit weights, cropping, MBAFF, 10 bits and lossless; and on JM's field pictures
   (PAFF) in 4:0:0, alone and mixed with frame pictures, with B pictures and CAVLC; I_PCM from
   hand-built streams. FFmpeg's decoder outputs such streams as 4:2:0 with flat chroma, so the
   references take its Y plane alone (`-vf extractplanes=y`, which leaves the samples as they are).
   Produced by neither libx264 nor OpenH264, but by JM, so checked against FFmpeg on its streams:
   I_PCM next to coded macroblocks in I and P pictures, with CAVLC and CABAC (also decoded from
   hand-built streams), cabac_init_idc 1 and 2 (JM's adaptive choice takes them in most of its
   streams; two samples fix each), and a Cr QP offset other than Cb's
   (second_chroma_qp_index_offset) in 4:2:0, 4:2:2 and 4:4:4; B sub-macroblock partitions smaller
   than 8x8 (in every JM sample with B pictures, CAVLC and CABAC, frames and fields); and scaling
   matrices as libx264 never sends them: lists in the SPS (fall-back rule A there), in both with
   picture lists absent (rule B), useDefaultScalingMatrixFlag, Cb's and Cr's lists of their own,
   and 4:4:4's 8x8 chroma lists (8 to 11). I_PCM's QP 0 in the deblocking filter
   is not reached: an edge next to it averages 0 with the neighbour's QP, below indexA 16 (alpha 0)
   for any neighbour under QP 32, and JM writes I_PCM only below about QP 16.
   Produced by none of them, so not checked against FFmpeg: memory management operations 2, 3
   and 5 (unit tests; JM writes 1 and OpenH264 1, 4 and 6). The scaling matrix unit tests (samples
   with rewritten parameter sets) stay as a second check. Still open: separate colour planes, luma and chroma bit depths that differ,
   error concealment, more speed (about 3.2x FFmpeg's time on `h264-bench-720p.mp4`, 2.5 to 3.3x on libx264's
   default High output at 720p, less the noisier the source, and about the same ratio at 10 bits as at 8; 2x on
   high-bitrate intra content). A profile of a
   720p libx264 default stream (2026-10-22) is flat: CABAC residual decoding takes 14%, deblocking 21%
   in pieces of at most 2.5% (bS, transposes, the filter), nothing else over 5%. Output planes are allocated
   without zeroing (callgrind: 0.4% fewer instructions on `h264-high-b.mp4`, 2.6% on `testsrc-420.avi`). Looked
   at and left: dropping the whole-picture clears in `Begin` (skipped, direct and intra macroblocks rely on the
   zeros in `_absMvd` and the motion arrays, so each path would clear its own macroblock instead, about as many
   bytes), one transpose for all four vertical luma edges (`FilterEdge` already skips the transposes of edges whose
   bS are all 0, which a whole-macroblock transpose would not). Frames are now deblocked a macroblock row behind
   decoding (no measurable change in time, so cache locality matters little with a large L2), so that a reference's
   upper rows become final while the rest is still being decoded: frame threading (`Threads`, under Decoding a file)
   builds on it. Still open there: MBAFF frames and field pictures report their progress only when done (a picture
   predicting from one waits for all of it), and P pictures in a chain keep 4 threads busy 2.4 cores' worth. Output frames given back with `Dispose` are pooled
   (gen2 collections over 5 loops of `h264-bench-720p.mp4`: 33 down to 3). Vector 4x4 and 8x8 inverse transforms
   (#42) were reverted: callgrind counted 1.4% more instructions per loop of `h264-bench-720p.mp4` with them and 0.9%
   more on `h264-high-b.mp4`, and 24 interleaved runs of the builds before and after showed no difference; the 9 and
   10% first reported were the VM's noise, more than the two functions' 2% (CAVLC) and 7% (CABAC) of the profile
   could have given
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
context states for every QP and initialization table equal the spec's initialization applied to ours. Contexts 460
to 1023 (4:4:4's Cb and Cr residual blocks and the coded_block_flag of 8x8 blocks) repeat the luma contexts of their
block category; all of them were compared, with the residual contexts below 460, against the tables of JM, the ITU's
reference software (3740 values, all four initialization tables).
The AAC Huffman codebooks were cross-checked the same way against the PacketVideo AAC decoder
from Android (Apache-2.0). AAC's noise substitution uses the Numerical Recipes random generator
with the seed FFmpeg uses (the spec leaves the generator open), so noise bands match FFmpeg too.
For MBAFF (the neighbour table 6-4, the field scans, the field CABAC context maps, the order of the
deblocking filter's field edges and the co-located field or frame of direct prediction) the ITU's reference decoder, JM (royalty-free, from the ITU), stood
in for the specification's text where that was out of reach; no code was copied.
