# Changelog

Versions are git tags (`vX.Y.Z`); each one publishes every package (ManagedCodecs.Core, .Containers, .Mjpeg, .H264,
.Aac and the ManagedCodecs meta-package) to nuget.org at that version, and a GitHub release with this file's section
as its notes. `release.yml` releases on the push to main that brings a version heading not yet tagged, so a release
is a pull request that turns "Unreleased" into "## X.Y.Z – date" and runs `tools/ship-public-api.py`. Until the API
is frozen the versions are previews (`0.1.0-preview.N`), and any of them can change the public API; the packages
share internals, so use one version of them all.

## Unreleased

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
