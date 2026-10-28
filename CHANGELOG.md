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

### Added

- H.264: memory management operation 5 in field pictures, which threw `NotSupportedException`.

### Changed

- HE-AAC decodes faster: the full QMF synthesis bank takes two DCT-IVs of 64 points, each by a complex FFT of 32, where
  it took a complex FFT of 128, and the analysis bank a complex FFT of 32 for its 64 real values, where it took one of
  64, both with their twiddles in vectors. About 7%, 4% and 4% faster on 5.1 HE-AAC (synthesis, analysis, twiddles) on
  the cloud VM measured (interleaved runs); the output moves only in its last bits (to 1e-12 of the peak).
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
