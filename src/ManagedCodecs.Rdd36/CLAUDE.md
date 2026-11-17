# RDD 36: notes for working on the SMPTE RDD 36 decoder and its samples

The repository's CLAUDE.md (process, core idea, cross-cutting gotchas) applies too. Issue #461 has the plan (slice 1
is this: 4:2:2, progressive, 10 bits, from MOV; then interlaced, 4:4:4 and alpha, 12 bits; MXF; speed) and
docs/decisions/2026-11-15-rdd36-decoder.md the decisions.

## Rules of this work (the owner's, issue #461)

- Written from the RDD 36:2022 PDF only (https://pub.smpte.org/doc/rdd36/; `rdd36-2022.pdf` is served on its own
  beside the zip). Never download, open, read, port, compare against or commit the reference decoder the zip
  `rdd36-20220909-pub.zip` holds (`rdd36a-2022-referencedecoder.zip`): its licence forbids it. Don't commit SMPTE PDFs.
- No other implementation's source is read to find out how to decode (FFmpeg's `proresdec`, oxideav-prores, others).
  FFmpeg is the oracle for output, by measurement only.
- Names: neutral IDs (`ManagedCodecs.Rdd36`, `Rdd36Decoder`, `CodecIds.Rdd36`). The trademark appears only where the
  owner decided: once in README and in the package description, with its notice. Not in type, namespace or ID names.
- Write nothing that states or implies the licensing, patents or name are cleared, and no legal conclusions.

## Gotchas (each cost time once)

- The inverse transform's arithmetic is FFmpeg's C one, found by measuring (the decision record has how): two passes,
  constants cos(k pi / 16) sqrt(2) 2^14 rounded but W3 = 19265 and W4 = 2^14, rows >> 15 with 2^14 added, columns
  >> 18 with 2^17, then + 512, clamped to 4..1019. An "obvious" variant is off by one on about 1.5% of dense blocks:
  only blocks of one row or one column (one pass at a time) pinned the constants down.
- FFmpeg's C and x64 SIMD transforms agree on every sample, but not on coefficients beyond what pictures give (|level *
  weight * qScale| past about 2^14): there they differ from each other, so nothing is bit-exact and the decoder's
  16-bit wrap is its own definition. The references take `-cpuflags 0` (the C one).
- Neither FFmpeg encoder writes: a quantization_index above 68 (prores_ks's `-qscale:v` stops at 64) or above 128,
  stuffing, zero bytes after a component's data, bitstream version 1 in 4:2:2, aspect_ratio_information, header bytes
  past the syntax elements. `tools/rdd36-rewrite.py` rewrites a stream for these (requantizing towards zero, the
  bytes saved going into stuffing), so FFmpeg's decoder still gives the reference.
- FFmpeg's decoder refuses what RDD 36 allows: a slice header longer than its elements ("invalid plane data size"),
  and set reserved bits (it reads the byte before bitstream_version as part of the version, and the picture header's
  reserved bits as part of the slice size). Those are in `Rdd36DecoderTests` only, on frames from `Rdd36FrameWriter`.
- prores_aw writes transfer_characteristic 0 (unknown in RDD 36) for SMPTE 170M's, beside a 'colr' that says 6.
  FFmpeg's frame shows "reserved"; the decoder takes the container's there (`rdd36-aw-colour`).
- FFmpeg's 4:2:2 pixel formats round odd sizes down, so no encoded sample has an odd width or height: the edges come
  from 330x250 (not a multiple of 16) and from `Rdd36FrameWriter` (17x33, 9x7, 2x2).
- FFmpeg 6.1's encoder named `prores` is `prores_aw` (byte for byte the same output); `rdd36-prores` is kept for a
  later FFmpeg where it might not be.
- The encoders write the sample entry's fourcc by profile ('apco' Proxy, 'apcs' LT, 'apcn' 422, 'apch' HQ); the
  decoder needs nothing of the sample entry, only its 'colr' and 'pasp' (read for every visual sample entry).
