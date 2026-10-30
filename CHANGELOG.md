# Changelog

Versions are git tags (`vX.Y.Z`); each one publishes every package (ManagedCodecs.Core, .Containers, .Mjpeg, .H264,
.Aac and the ManagedCodecs meta-package) to nuget.org at that version, and a GitHub release with this file's section
as its notes. `release.yml` releases on the push to main that brings a version heading not yet tagged, so a release
is a pull request that turns "Unreleased" into "## X.Y.Z – date" and runs `tools/ship-public-api.py`. From 0.1.0 on, the
public API changes only in a new minor version (0.2.0, 0.3.0, ...), with a "Breaking changes" entry; a patch version
(0.1.1) only fixes. Package validation enforces it: every pull request packs the packages and compares them with
the last baseline release on nuget.org (Directory.Build.props). 1.0 is a later decision. The packages
share internals, so each depends on the others at exactly its own version (`[X]`; Directory.Build.targets): NuGet
then reports a mix of versions (NU1107, or NU1608 for a newer one referenced directly) rather than letting it fail at
run time.

## Unreleased

A new public API: the next release is a minor version, 0.4.0.

### Added

- Each decoded frame's interlacing as FFmpeg reports it: `VideoFrame.IsInterlaced` and `IsTopFieldFirst` (ffprobe's
  `interlaced_frame` and `top_field_first`), `FieldOrder` (the order FFmpeg's H.264 parser gives the picture, a new
  `FieldOrder` enum with `AVFieldOrder`'s values) and `IsCodedAsFields` (two field pictures), and
  `H264Decoder.FieldOrder`, the stream's as ffprobe prints it for MP4. H.264 follows the picture timing SEI's
  pic_struct and ct_type where there is one, else MBAFF or field coding and the field order counts; MJPEG frames are
  not flagged, as in FFmpeg (#225).
- `Mp4Demuxer`: a file's tracks can be read one at a time, `new Mp4Demuxer(stream, MediaTypes.Video)` (a new
  `MediaTypes` flags enum); the other track is left out as if the file had none, and builds no sample table and gives
  no packets. And `ReadPacketInfo()`, the packets `ReadPackets` gives without their data (a new `PacketInfo`: the
  size, flags and timestamps), from the sample tables alone, without reading the file or allocating an array a packet,
  for a duration pass, a packet list or a bitrate graph (#211). `Mp4Demuxer(Stream, bool leaveOpen = false)` becomes
  two constructors, `(Stream)` and `(Stream, bool)`, so that the new one can take `leaveOpen` as an optional
  parameter; calls compile and run as before.

### Changed

- H.264 decodes CAVLC streams faster, bit for bit as before: 3.2% off the 720p benchmark stream's steady frame
  (`h264-bench-720p.mp4`, Constrained Baseline) on GitHub's x64 runner, 1.8% off `h264-p-refs.mp4`, the CABAC
  samples unchanged (`compare.yml` against main). The residual reader takes the trailing ones' signs in one read and
  run_before from one lookup.

### Fixed

- `AviDemuxer`: an empty video chunk (a dropped frame) now takes its frame's tick, as in FFmpeg, so the packets after
  it have FFmpeg's pts rather than coming a frame early; it still gives no packet (#226).

## 0.3.0 – 2026-10-30

Each decoded frame's picture type (I, P, B), as FFmpeg reports it. A new public API, hence a minor version; nothing
is removed or changed, so code built against 0.2 keeps working.

### Added

- `VideoFrame.PictureType` and the `PictureType` enum (FFmpeg's `AVPictureType`, the same values): how each decoded
  frame was coded, as `ffprobe -show_frames` prints its `pict_type`. H.264 gives the type of the picture's first slice
  (of its first field, for a frame of two field pictures), MJPEG gives I, and a frame no decoder made None (#221).

## 0.2.5 – 2026-10-30

Faster H.264 decoding, the same output. The public API is that of 0.2.0, unchanged.

### Changed

- H.264 decodes faster on one thread, bit for bit as before: 2.7% on the 720p benchmark stream
  (`h264-bench-720p.mp4`) on GitHub's x64 runner and 0.8% on its ARM64 runner, the other H.264 samples within about
  1.5% either way (`compare.yml` against 0.2.4; the 4.5% first given here added up per-change profile estimates that
  overstated the whole). Each partition's reference picture is a byte handle rather than an object reference, Cb and Cr share one
  setup with a multiply-add chroma filter (SSSE3), 8-bit 4x4 and 8x8 inverse transforms run in 16-bit lanes (SSE2;
  32-bit for coefficients too large for them), CAVLC residual blocks read through a bit cursor in locals, `ReadUe`
  is inlined and a P_Skip macroblock no longer reads its neighbours twice (docs/conformance.md, Performance notes).

## 0.2.4 – 2026-10-30

H.264 concealment guesses motion in MBAFF frames too. The public API is that of 0.2.0, unchanged.

### Changed

- H.264 concealment (`ConcealErrors`) guesses the motion of missing macroblocks in MBAFF frames too, converting a
  neighbour's motion between field and frame macroblocks, where they copied their place in a reference
  (docs/conformance.md has what it gains and where it does not).

## 0.2.3 – 2026-10-29

H.264 right again under the WebAssembly interpreter (wrong from 0.2.0), a better guess for concealed macroblocks, and
less CPU with frame threading. The public API is that of 0.2.0, unchanged.

### Fixed

- H.264 under the WebAssembly interpreter (browser-wasm without AOT, SIMD on, the default): frames came out wrong from
  0.2.0 on, and differently from one decode to the next, as the interpreter narrows the deblocking strengths'
  comparison masks with saturation once it has optimized the function (#206). The masks are now narrowed as signed
  lanes, which every runtime narrows alike; CI's `wasm` job checks the interpreted build too.

### Changed

- H.264 concealment (`ConcealErrors`) guesses the motion of a missing macroblock from its neighbours (the candidate whose
  prediction matches their edges best), where it copied its place in one reference: on streams cut in packet after
  packet, the concealed frames come 0.5 to 15 dB closer to the clean decode (mean PSNR; docs/conformance.md), ahead of
  FFmpeg's on five of the eight measured. MBAFF frames still copy.
- H.264 frame threading spends 2 to 5% less CPU for the same speed: a picture waiting on its reference's rows waits for
  32 rows more once it has to block, and a reference's progress wakes only the waits it satisfies, which halves the
  context switches (GitHub's x64 and ARM64 runners, 4 threads).

## 0.2.2 – 2026-10-29

Faster MJPEG and HE-AAC on ARM64 (and x64 without AVX2). The public API is that of 0.2.0, unchanged.

### Changed

- MJPEG decodes about 3% faster on ARM64 (and x64 without AVX2): the 128-bit IDCT's butterflies are inlined, where
  they were a call a pass with their results through memory. GitHub's ARM64 runner, `compare.yml`; x64 with AVX2 is
  unchanged; the output is the same.
- HE-AAC decodes about 4% faster on ARM64 (and x64 without AVX2): SBR's patch filter, covariances and gains, and
  parametric stereo's all-pass filter have 128-bit paths, where they had only 256-bit ones and ran scalar there.
  GitHub's ARM64 runner, `compare.yml` (5.1 and v2 stereo); x64 with AVX2 runs the same code; the output is the same.

## 0.2.1 – 2026-10-29

Faster HE-AAC (the QMF banks by smaller transforms) and CABAC H.264, and memory management operation 5 in H.264 field
pictures. The public API is that of 0.2.0, unchanged.

### Added

- H.264: memory management operation 5 in field pictures, which threw `NotSupportedException`.

### Changed

- HE-AAC decodes faster: the full QMF synthesis bank takes two DCT-IVs of 64 points, each by a complex FFT of 32, where
  it took a complex FFT of 128, and the analysis bank a complex FFT of 32 for its 64 real values, where it took one of
  64, both with their twiddles in vectors. Together 14 to 16% faster on 5.1 HE-AAC and 8 to 10% on HE-AAC v2, on
  GitHub's x64 and ARM64 runners (`compare.yml`); the output moves only in its last bits (to 1e-12 of the peak).
- H.264 decodes CABAC streams faster: the deblocking filter's edge strengths of bi-predicted blocks in vectors, CABAC's
  context selection reading neighbours inside the macroblock directly, and its significance map without per-bin
  bounds checks. About 3% fewer instructions a frame on CABAC streams with B frames, 1 to 2% faster on the cloud VM
  measured (docs/notes/h264-speed-gap-2026-10.md); CAVLC streams are unchanged; the output is the same.

## 0.2.0 – 2026-10-28

Parametric stereo complete (mixing procedure Rb, phase parameters, 34 bands), long-term references in H.264 field
pictures, and faster H.264 motion compensation. The public API is that of 0.1.0, unchanged.

### Added

- H.264: long-term references marked in field pictures (an IDR field pair marked long-term, memory management
  operations 2, 3, 4 and 6 in field pictures), which threw `NotSupportedException`.
- HE-AAC v2: parametric stereo with mixing procedure Rb (icc_mode 3 to 5), which threw `NotSupportedException`.
- HE-AAC v2: parametric stereo in 34 bands (iid_mode or icc_mode 2 or 5), which threw `NotSupportedException`.
- HE-AAC v2: parametric stereo's phase parameters (IPD and OPD), which were ignored: a stream that sends them decoded
  without error but up to its whole peak off FFmpeg's output.

### Changed

- H.264 decodes faster: motion compensation's common partitions (one reference, bi-predicted, weighted) take direct
  paths to the kernels, which copy by rows at fixed widths. On GitHub's runners, timed in one run against the code
  before these changes: 4% (x64) to 5% (ARM64) faster on the 720p sample, about 3.5% on P-frame streams, 1 to 3% with
  B frames, unchanged at 10 bits; the output is the same.

### Documentation

- Native AOT on x64: set `IlcInstructionSet` (x86-64-v3) in the app, or the AVX2 and 256-bit paths are left out (AAC
  and HE-AAC take about a third longer); README's Native AOT section has the timings.
- HE-AAC v2 with downsampled SBR (parametric stereo at the core's rate) is checked against FFmpeg now, a sample from a
  patched FDK encoder; it was untested.

### Fixed

- Native AOT for the default x64 target: MJPEG decoded at half speed and H.264 6% slower, the portable byte shuffle
  being a software call there; x64 now uses SSE2's unpacks and SSSE3's pshufb directly.

## 0.1.0 – 2026-10-27

The first stable version: the public API of 0.1.0-preview.3, documented, and from here on changed only in a new minor
version (see the top of this file).

### Added

- The packages carry their XML documentation (`lib/net10.0/*.xml`), so IntelliSense shows it; every public member has
  a doc comment, and the build fails on one without (CS1591).
- nuget.org shows a readme for every package (`PACKAGE.md`): what they decode, an example, links to the documentation.

### Changed

- The packages depend on each other at exactly one version (`[0.1.0]`, not `0.1.0` or later): they share internals,
  so a mix of versions could fail at run time, and NuGet now reports one instead (NU1107 or NU1608).

## 0.1.0-preview.3 – 2026-10-27

### Breaking changes

- `H264Decoder(VideoStreamInfo)` crops frames to the container's picture size where it takes only columns and rows
  off the right and bottom within the last macroblock, as FFmpeg's decoder does: an Apple-encoded 640x360 MP4 that
  stores 640x359 now decodes to 640x359. The span constructor is unchanged.

### Fixed

- H.264: a field picture's packet that goes on with a slice of the other field or of a frame is refused (as a packet
  holding more than one picture) rather than decoded into the field, where concealing errors could throw
  `ArgumentOutOfRangeException`; and frames larger than any level allows (over 139,264 macroblocks) are refused with
  `NotSupportedException` rather than decoded in time and memory without bound. Both from fuzzing.
- H.264, from a longer fuzzing round: a slice with more reference list modifications than its list has entries, SPS
  crop offsets that overflow when scaled, and a first_mb_in_slice of 2^31 or more are `InvalidDataException`s
  rather than `ArgumentOutOfRangeException`, `OverflowException` and `IndexOutOfRangeException`.

## 0.1.0-preview.2 – 2026-10-26

The public API before it freezes ([docs/decisions/2026-10-26-api-before-0.1.0.md](docs/decisions/2026-10-26-api-before-0.1.0.md)).

### Breaking changes

- `Mp4Demuxer`, `AviDemuxer` and `AdtsDemuxer` are in `ManagedCodecs.Containers`, beside `Demuxer` (they were in
  `ManagedCodecs.Containers.Mp4`, `.Avi` and `.Adts`): drop those `using` lines.
- No longer public, as implementation details: MJPEG's JPEG parser (`JpegComponent`, `JpegFrameHeader`,
  `JpegMarker`, `JpegMarkerReader`, `JpegSegment`), `PixelFormatExtensions.Of`, `AudioFrame.DropStart`,
  `VideoFramePool`, and `VideoFrame.IsDamaged`'s setter (the decoder sets it).

### New

- `AudioFrame.Pts`: the frame's first sample's time, in the stream's time base (the packet's, moved by the samples
  dropped from its start), as FFmpeg gives it.

## 0.1.0-preview.1 – 2026-10-26

The first preview: what the README's Status section lists, as it stands.

### New

- MJPEG decoding, bit-exact with FFmpeg: baseline and extended Huffman JPEG in 4:2:0, 4:2:2, 4:4:4 and gray, restart
  markers, interlaced AVI1.
- H.264 decoding, bit-exact with FFmpeg: Constrained Baseline, Main, High, High 10, High 4:2:2 and High 4:4:4
  Predictive; CAVLC and CABAC, I, P and B, weighted prediction, the 8x8 transform, scaling matrices, MBAFF and field
  pictures, monochrome to 4:4:4 (also separate colour planes), 8 to 14 bits, lossless; frame threading
  (`H264Decoder.Threads`), error concealment (`ConcealErrors`), borrowed frames.
- AAC decoding: AAC LC from mono to 7.1, within 2^-17 of FFmpeg's output; HE-AAC (spectral band replication and
  parametric stereo).
- AVI, MP4 (QuickTime, fragmented MP4, edit lists as FFmpeg applies them) and ADTS demuxing; `Demuxer.Open` picks one by
  content. Asynchronous reads (`ReadPacketsAsync`).
- Pure managed, no native binaries: Native AOT, ARM64 and WebAssembly (browser-wasm) run the same code.
