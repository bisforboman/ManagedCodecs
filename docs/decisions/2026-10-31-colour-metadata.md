# Colour and geometry metadata for 0.5.0 (2026-10-31)

A design, decided (below); no code yet. It comes from the review of the repository's decisions
(2026-10-31): the frames carry no colour information, so a consumer cannot convert them to RGB correctly, and this is
the gap in the API most likely to matter next.

## The problem

A decoded frame says its layout (`PixelFormat`) and nothing about what its samples mean:

- **Range.** MJPEG output is full range (JFIF: 0 to 255), H.264 output is usually limited range (16 to 235 for luma,
  16 to 240 for chroma) unless video_full_range_flag says otherwise. Both come out as `PixelFormat.Yuv420P`; FFmpeg
  gives the first as `yuvj420p` with `color_range=pc` and the second as `yuv420p` with `color_range=tv`. A converter
  that guesses wrongly crushes or washes out every frame.
- **Matrix, primaries and transfer.** BT.601 or BT.709 (or BT.2020, or PQ and HLG for HDR) decide the YCbCr-to-RGB
  coefficients and what the RGB means. H.264 sends them in the VUI (colour_primaries, transfer_characteristics,
  matrix_coefficients, E.2.1); `ParameterSets.ReadVui` reads past them today. MP4 can carry them in a 'colr' box
  ('nclx'); JFIF implies BT.601 (FFmpeg: `color_space=bt470bg`).
- **Chroma location.** H.264's chroma_sample_loc_type (VUI), else left for 4:2:0 (FFmpeg: `chroma_location=left`);
  JPEG's is centre. A scaler that upsamples chroma needs it.
- **Sample aspect ratio.** Anamorphic video (DVD and broadcast sources: 720x576 shown at 16:9) is drawn the wrong
  shape without it. H.264's aspect_ratio_idc and sar_width/sar_height (VUI, Table E-1), MP4's 'pasp', JFIF's density
  when its units are 0.
- **Rotation.** Phone video is stored landscape with a display matrix in MP4's 'tkhd' (90, 180, 270 degrees);
  FFmpeg reports it as display matrix side data and rotates by default when transcoding.

All of it is metadata: no sample changes, so nothing in the bit-exact checks moves.

## The design

New public API, so a minor version (0.5.0); nothing removed or changed, so code built against 0.4 keeps working.

- **Enums with ITU-T H.273's code points**, the values H.264's VUI and MP4's 'nclx' send and FFmpeg's `AVColor*` use:
  `ColorRange` (Unspecified, Limited, Full), `ColorPrimaries`, `ColorTransfer`, `ColorMatrix` (each with H.273's
  names and numbers, Unspecified = 2 as there; Unknown values kept as their number, cast to the enum), and
  `ChromaLocation` (FFmpeg's `AVChromaLocation` values, which are chroma_sample_loc_type + 1).
- **`VideoFrame`**: `ColorRange`, `ColorPrimaries`, `ColorTransfer`, `ColorMatrix`, `ChromaLocation` and
  `SampleAspectRatio` (a `Rational`, 0:1 when unknown, as FFmpeg prints it), each `{ get; internal set; }` as the
  interlacing properties are. What `ffprobe -show_frames`
  prints for the frame, which is the oracle: the bitstream's where it says something, else the container's.
- **`VideoStreamInfo`**: the container's: `ColorRange` and the three H.273 values from 'colr' ('nclx'; 'nclc' too,
  QuickTime's, without the range), `SampleAspectRatio` from 'pasp', and `Rotation` (degrees counter-clockwise, 0, 90,
  180 or 270, from 'tkhd''s matrix; a matrix that is not a rotation by a multiple of 90 degrees, or mirrored, reads as
  0 and is left to a later version). As init-only properties after the positional ones, so the record's constructor
  and `Deconstruct` stay as they are (adding a positional parameter would break both). AVI has none of these.
- **Not a new `PixelFormat` for full range** (FFmpeg's deprecated `yuvj*` formats): range is a property beside the
  layout, as in FFmpeg's current API, so the formats stay one per layout.
- **Not guessed.** Unspecified stays Unspecified: FFmpeg does not infer BT.601 or BT.709 from the picture size, and a
  guess would make this decoder's metadata differ from the oracle's. README says what a consumer usually assumes
  (BT.601 below 720 lines, BT.709 from 720, limited range for H.264).

## Checking it

- **Oracle**: `ffprobe -show_frames -show_entries frame=color_range,color_space,color_primaries,color_transfer,
  chroma_location,sample_aspect_ratio` and `-show_streams` (stream level, and the rotation from its side data),
  stored beside each sample as make-samples.sh stores the packet references, and compared for every frame of every
  sample by the conformance tests.
- **Samples**: libx264 writes the VUI from `-color_range`, `-colorspace`, `-color_primaries`, `-color_trc` and
  `-aspect` (checked with FFmpeg 6.1.1: the frame reads `color_range=tv`, `bt709` three times, `chroma_location=left`,
  `sample_aspect_ratio=16:9`); one sample in full range and BT.2020 with PQ, one anamorphic, one rotated
  (`-display_rotation 90` as an input option), one MP4 whose 'colr' and VUI disagree (which wins is then FFmpeg's
  answer, not ours). JM's samples have no VUI (Unspecified throughout), the real films whatever their encoders sent.
- **Parsing**: VUI and the new boxes are fuzzed (`tools/fuzz.sh h264` and `file`) and their bounds unit tested; a
  rotation from a damaged matrix is 0, never an exception.

## Work, one pull request each

1. **The API and H.264's VUI**: the enums, `VideoFrame`'s properties, `ReadVui` keeping what it reads, the frame
   filled from the active SPS; ffprobe references in make-samples.sh and their conformance theory.
2. **MJPEG**: full range, BT.601 matrix, centre chroma, SAR from JFIF's density (FFmpeg's rules, `ffprobe` as the
   oracle; Adobe APP14 files too).
3. **MP4**: 'colr', 'pasp' and 'tkhd''s rotation into `VideoStreamInfo`, and into the frames where the bitstream says
   nothing (as FFmpeg's decoder takes the container's values as defaults).

Each adds to PublicAPI.Unshipped.txt and CHANGELOG's Unreleased under Added; the release that carries them is 0.5.0.

## Decisions

Asked with the multiple-choice prompt; the owner took each proposal (2026-10-31).

- **Spelling of the names. Decided:** `Color` (`ColorRange`, `ColorMatrix`, ...), .NET's convention
  (`System.Drawing.Color`, `ConsoleColor`) and FFmpeg's, though the docs here write "colour". The alternative is
  `Colour` throughout.
- **Where the values live. Decided:** on `VideoFrame` (what the decoder knows, which can change at a new SPS) and on
  `VideoStreamInfo` (what the container says, known before decoding). The alternative is frames only, simpler, but a
  player then cannot size its window before the first frame.
- **Rotation. Decided:** in, on `VideoStreamInfo` only (FFmpeg's frames carry it as side data from the container,
  not from H.264), multiples of 90 degrees only. The alternative is leaving it out of 0.5.0.
- **Separate properties or one value. Decided:** separate properties on the frame (as the interlacing ones are). The
  alternative is one `ColorInfo` record struct on the frame and the stream, which groups them but makes a consumer
  write `frame.Color.Range`.
