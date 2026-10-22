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
| `src/ManagedCodecs.H264` | `H264Decoder`: Constrained Baseline, Main, High, High 10, High 4:2:2 and High 4:4:4 Predictive profile H.264 (CAVLC, CABAC, I/P/B, weighted prediction, 8x8 transform, scaling matrices, deblocking, 4:2:0, 4:2:2 and 4:4:4, 8 and 10 bits, lossless, progressive and interlaced), bit-exact |
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

Each frame owns its samples. `new H264Decoder(video) { BorrowFrames = true }` saves the copy that takes (3 to 11% of
the decoding time at 720p, the most with B frames): its frames point into the decoder's own pictures, valid until the
next `Decode` or `Flush` call and not to be written. Their planes are the decoder's macroblock-aligned buffers, so
read them through `GetRow`, or with the frame's `Offsets` and `Strides`. `mcdec --borrow-frames` times it. 10-bit
video comes out as `PixelFormat.Yuv420P10`, `Yuv422P10` or `Yuv444P10`, a little-endian 16-bit word a sample (strides and offsets
count bytes), and always as copies: the decoder keeps those samples as `ushort`, which a frame's byte planes cannot
borrow.

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
line shows the steady speed without them). A snapshot, made that way (2026-10-22, FFmpeg 6.1, single-threaded on a
4-core AVX2 (and AVX-512) Xeon cloud VM, median of five interleaved runs; µs per frame, audio frames are 1024 samples):

| Sample | Loops | ManagedCodecs | FFmpeg |
| --- | ---: | ---: | ---: |
| `testsrc-420.avi` (320x240 MJPEG 4:2:0) | 100 | 413 | 400 |
| `testsrc-444.avi` (MJPEG 4:4:4) | 100 | 559 | 471 |
| `noise-q1.avi` (MJPEG, noise at the highest quality) | 100 | 1040 | 1291 |
| `h264-p-refs.mp4` (320x240 H.264, P frames) | 40 | 690 | 226 |
| `h264-bench-720p.mp4` (1280x720 H.264) | 10 | 7992 | 2474 |
| `h264-main-p-refs.mp4` (320x240 H.264 Main, CABAC) | 40 | 862 | 380 |
| `h264-b-pyramid.mp4` (320x240 H.264 Main, CABAC, B frames) | 20 | 1589 | 515 |
| `h264-wp-default.mp4` (320x240 H.264, libx264's Main defaults: B frames, weighted prediction) | 20 | 1918 | 408 |
| `h264-high-default.mp4` (320x240 H.264, libx264's High defaults: also the 8x8 transform) | 20 | 1355 | 403 |
| `h264-high-b.mp4` (320x240 H.264 High, B frames, all partitions) | 40 | 1679 | 544 |
| `h264-10bit-b.mp4` (the same at 10 bits, High 10) | 40 | 1711 | 609 |
| `h264-high-motion.mp4` (320x240 H.264 High, fast motion and noise, QP 22) | 20 | 5815 | 3055 |
| `h264-10bit-motion.mp4` (the same at 10 bits, QP 34, which is QP 22's step size) | 20 | 5919 | 3200 |
| `aac-stereo-48k.mp4` (AAC, stereo) | 100 | 55 | 76 |
| `aac-5.1.mp4` (AAC, 5.1) | 100 | 182 | 94 |

The short MJPEG and AAC runs are dominated by .NET's startup and JIT and moved the most (testsrc-420.avi took
between 263 and 604 µs). At 10 bits the ratio to FFmpeg is about what it is at 8: 2.8 against 3.1 times on the B
frame pair, 1.85 against 1.9 times on the motion pair; the 10-bit streams cost us 2% more time than their 8-bit
counterparts, FFmpeg 5 to 12% more.

A 1280x720 libx264 default stream (High, CABAC, B frames; `testsrc2` with noise, 2 s) took 3.3 times FFmpeg's time
on the same VM (10958 against 3338 µs). On 2026-10-21's VM it took 3.0 times (9674 against 3178 µs), down from 4.3
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
   Not checked against FFmpeg there: explicit weighted prediction (libx264 writes none for
   interlaced video, and JM's encoder crashes when asked for it, or for implicit weights, in
   field pictures), field pictures next to MBAFF frames (JM refuses to mix them), and
   constrained intra prediction next to a left pair of the other kind with
   one intra and one inter macroblock (libx264's output for it is not conforming: FFmpeg and JM
   both reject the prediction modes it uses), and a frame macroblock over a co-located field pair
   whose two fields are equally far in order count (the bottom one is taken, as in JM).
   Field pictures come from JM, the ITU reference encoder: field pairs with CABAC and CAVLC, slices,
   POC types 0, 1 and 2, constrained intra prediction and the 8x8 transform, B field pairs with
   spatial and temporal direct prediction, and frames switching between frame and field coding
   (B fields over frames and B frames over fields). Not reached by JM's streams: a B frame after
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
   Produced by neither, so not checked against FFmpeg: I_PCM (decoded from hand-built streams,
   CAVLC and CABAC), memory management operations 2, 3 and 5 (unit tests),
   B sub-macroblock partitions smaller than 8x8, explicit weights for bi-predicted blocks (libx264 weights B frames
   implicitly), cabac_init_idc 1 and 2 (only their tables are checked, against libavc), a Cr QP
   offset other than Cb's (second_chroma_qp_index_offset), and of scaling matrices: lists in the
   SPS, fall-back rule B, useDefaultScalingMatrixFlag and a Cr list of its own (unit tests decode
   samples with rewritten parameter sets). Still open: separate colour planes, bit depths other than 8 and 10,
   error concealment, more speed (about 3.2 to 3.3x FFmpeg's time at 720p, with
   libx264's default High output too, and about the same ratio at 10 bits as at 8; 2x on high-bitrate intra content)
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
