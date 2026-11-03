# A Matroska and WebM demuxer (2026-11-03)

Issue #305, filed on the owner's choice. Built in the pull request that closes it.

## Why

shotdetector-web reads WebM (VP9 and AV1, decoded by the browser's WebCodecs) through a Matroska reader of its own
(bisforboman/shotdetector-web, its PR #135). With a demuxer here it can drop that copy, and Matroska files holding
H.264 and AAC, common from screen recorders and OBS, decode with this library's decoders.

## What

- `MatroskaDemuxer : IDemuxer`, as the other demuxers: the constructor `(Stream, bool leaveOpen = false)`, `Open(path)`,
  `Video` and `Audio` (the first track of each kind), `ReadPackets`, `ReadPacketsAsync`, `ReadPacketInfo`, `Seek`,
  `Dispose`. `Demuxer.Open` picks it by the EBML header's magic. No `MediaTypes` constructor (as Mp4Demuxer has): no
  consumer asks for one, and it can be added later without a break.
- Codecs this library decodes map to `CodecIds` (V_MPEG4/ISO/AVC to H264 with the avcC as CodecPrivate, V_MJPEG, A_AAC
  and its old names); the others keep the Codec ID as written (V_VP9, V_AV1, A_OPUS, A_VORBIS), as AVI and MP4 keep
  their FourCCs and sample entry types. No new `CodecIds` constants: a constant names a codec the library handles.
- Opening walks every Cluster once, reading the few header bytes of each block and skipping its data, and keeps an
  index (offset, size, timestamps, flags) of the exposed tracks' frames. That is what the MP4 demuxer has from its
  sample tables: frame counts, `ReadPacketInfo` without reads, and `Seek` to the exact keyframe with the
  `Packet.Sequence` a read from the start gives, which the `IDemuxer.Seek` contract asks for.

## Cues

The issue asked for seeking through Cues. The index makes them unnecessary: it holds every keyframe (Cues usually
list only the video's, often only one a cluster), and a file without Cues (a live recording) seeks the same way. Cues
could make opening cheaper (start reading at a cluster without walking the ones before), but a seek must still give
each packet's `Sequence`, which needs the blocks counted from the start. The cost of the walk: one read of the
stream per 64 KiB window of headers, so a few reads per cluster of large frames. If opening long files over a slow
stream turns out to matter, a lazily built index (clusters walked on first need) is the way, not Cues.

## Decode times

Matroska stores presentation times only. `Packet.Dts` is required, and nothing in this library reads it, so it is
derived: the presentation times sorted, delayed by the fewest frames that keep every dts at or before its pts.
FFmpeg's (libavformat's) delay is the codec's declared reorder depth, which can be larger; matching it would mean
parsing the H.264 SPS in the container package. The tests check the dts against FFmpeg's bound instead
(docs/conformance.md).
