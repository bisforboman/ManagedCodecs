# MXF: the picture descriptor's colour, and what is left of issue #469 (2026-11-15)

A plan, decided on by the owner (Decided, below), with the colour slice built in the same pull request (Outcome).
Issue #469's five slices are
on main (#473 to #523: partitions, header metadata, frame- and clip-wrapped essence, D-10's sound, the start timecode,
files cut short or still being written). This record lists what `MxfDemuxer` still doesn't do, measured against
FFmpeg 6.1.1, and proposes the next slice. ProRes and VC-3 decoding stay out (#461: Apple's reply, 2027-01-19).

## What is left

| Gap | Today | FFmpeg's demuxer | Can it be checked here? |
|---|---|---|---|
| **Colour in the picture descriptor** | Not read: `VideoStreamInfo`'s `ColorRange`, `ColorPrimaries`, `ColorTransfer` and `ColorMatrix` stay unspecified | Reads all four (`ffprobe -nofind_stream_info`) | Yes: FFmpeg's muxer writes them |
| **10-bit 4:2:2 intra H.264** (the issue's "AVC-Intra-like" sample) | No sample | Reads it | Yes |
| **Sound tracks after the first** | One `Audio`, the first sound track. Broadcast MXF usually carries one mono track a channel (4, 8 or 16), so a player gets channel 1 alone | A stream a track | Yes, but it is an API decision for every demuxer (MP4, Matroska and AVI give one `Audio` too) |
| **Files from other writers** (bmx, cameras, Avid, Omneon) | None tested | | Not from the cloud sandbox: see below |
| **A clip-wrapped file cut short whose index survives** | Refused | No file shows it | No |
| **ProRes, VC-3 frames** | Packets by MD5 | | Held (#461) |

## Colour: measured (FFmpeg 6.1.1)

H.264 without colour in its VUI, remuxed with `-c copy -color_primaries ... -color_trc ... -colorspace ...
-color_range ...` (the way make-samples.sh's `h264-colr-*.mp4` set MP4's 'colr'):

| File | Header (`-nofind_stream_info`) | Stream and frames (`-show_frames`) |
|---|---|---|
| Descriptor says BT.2020, HLG, limited; VUI says nothing | tv, bt2020nc, arib-std-b67, bt2020 | the descriptor's |
| Descriptor says BT.2020, PQ, limited; VUI says BT.709, full | tv, bt2020nc, smpte2084, bt2020 | the VUI's: pc, bt709, bt709, bt709 |
| Nothing set | unknown, all four | |
| ProRes with BT.709 | tv, bt709, bt709, bt709 | (not decoded here) |

So the rule is 0.5.0's (docs/decisions/2026-10-31-colour-metadata.md): the bitstream's where it says something, else
the container's. Some values don't come back as written: a transfer of smpte170m reads back bt709 and a matrix of
smpte170m reads back bt470bg (one label for both in SMPTE's register, RP 224), and gamma22 reads back bt470m.

Where the values are (ST 377-1 Annex G, the generic picture and CDCI descriptors; the labels in SMPTE's registers,
BSD-3-Clause as the ones `MxfLabels` already holds, each checked against ST 377-1 and ST 2067-21's lists):

- **Primaries, transfer, matrix:** three label properties, Color Primaries (tag 3219), Transfer Characteristic
  (Capture Gamma, 3210) and Coding Equations (321A), each a 16-byte UL naming one of H.273's code points.
- **Range:** no label. The CDCI descriptor's Black Ref Level (3304), White Ref Level (3305) and Color Range (3306)
  with the Component Depth: 16, 235 and 225 at 8 bits (scaled by depth) are limited range; 0, 255 and 256 full.
  Which combinations FFmpeg takes for which, and what it does with the rest, is to measure while building
  (hand-made descriptors through `tools/mxf-rewrite.py`), not to read out of its source.

## Proposed slice: colour (no new public API)

- `MxfHeader` reads the three labels and the reference levels; `MxfDemuxer.Video` carries them in `VideoStreamInfo`'s
  existing properties. A label the table doesn't know stays `Unspecified`.
- Decoded H.264 frames then take the descriptor's colour where the VUI is silent, as MP4's 'colr' does today (the
  same code path; checked by the `.color` references).
- **Samples** (make-samples.sh, each with a comment): `mxf-h264-colour-hlg` (descriptor only), `mxf-h264-colour-conflict`
  (descriptor against VUI), a full-range one, and the existing ProRes and DNxHR samples for descriptors FFmpeg writes
  by default. Unit tests for every label in the table, written into hand-made descriptors, since FFmpeg's muxer
  merges some (above).
- **Checks:** `MxfHeaderTests` against `.headers.json` (add the four fields to the ffprobe line), the frames'
  colour against `.color`. A CHANGELOG line under "Unreleased" (a consumer would notice: HDR and full-range MXF
  were shown as unspecified). No `PublicAPI.Unshipped.txt` change, so it can ship in a patch.
- With it, `mxf-h264-422-10`: 10-bit 4:2:2 intra-only H.264 (`-profile:v high422 -x264-params keyint=1`), the issue's
  third sample line, packets and frames against FFmpeg's.

## Files from other writers

- **Reach:** archive.org's search and metadata now answer from the cloud sandbox, but its downloads redirect to data
  hosts (`ia800905.us.archive.org` and the like) that the sandbox's proxy refuses. CI reaches them. github.com/ebu/bmx
  and samples.ffmpeg.org are refused; Ubuntu has no bmx package.
- **Found** (metadata only, nothing downloaded):
  - `mxfburger`: 18 files of 12 to 22 MB, Public Domain Mark, 9 s each, mono to 7.1 at 16 and 24 bits. The writer and
    the essence are unknown until one is read (the names suggest a cinema package's track files: JPEG 2000, which this
    library only names).
  - NASA's `jsc-pao-video-collection`: broadcast MXF from a playout server, public domain as a US government work, but
    2.6 GB and up a file. A nightly check could read the first megabytes by HTTP range (the header metadata, and
    packets under the cut-short rules), not whole files.
- **How:** `realworld.yml` (nightly, `tools/realworld.py`) is where real films are already compared with FFmpeg. MXF
  files pinned there by SHA-1 would run only in CI: written blind from here, fixed from the nightly's logs.
  The alternative is that the owner allows one data host for this sandbox, so the first file can be read here.

## Sound tracks after the first

Not MXF's alone: every demuxer has one `Audio`. For MXF it matters most (mono tracks a channel). Two shapes are
possible: a list of tracks with one selected, across all demuxers (new public API, a minor version), or MXF folding
its mono tracks into one multichannel stream (no new API, but packets FFmpeg never gives, so no oracle). Proposed: an
issue of its own, not a slice of #469.

## Decided (the owner, 2026-11-15)

1. **Next slice:** colour and the 10-bit 4:2:2 sample.
2. **Files from other writers:** pinned in `realworld.yml` afterwards, worked from the nightly's logs.
3. **Sound tracks after the first:** an issue of its own.
4. **Issue #469:** stays open, with a status comment after each pull request.

## Outcome (colour)

- **Labels:** the SMPTE registers answer from the cloud sandbox (registry.smpte-ra.org), so the three lists are from
  there: 04.01.01.01, then 01 (transfer), 02 (coding equations) or 03 (primaries), then the value. What FFmpeg 6.1
  reads for each was measured by patching the value into a sample: it agrees with the registers' names but for
  BT.470's transfer (gamma 2.2 to it, where the register says BT.470) and the two it doesn't know, sRGB and ICtCp,
  which are read by the registers.
- **Range:** FFmpeg's rule, measured on 33 combinations of the levels and the depth: limited for 16, 235 and 225
  scaled to the component depth, full for 0 (or no black level), the largest value and a colour range of that or one
  more, else unspecified. `MxfHeader.RangeOf`, with mutation entries (it reproduces FFmpeg, not a rule in ST 377-1).
- **What FFmpeg's muxer writes unasked:** limited range's levels for ProRes, DNxHR, D-10 and interlaced H.264, so
  those samples now say limited range; progressive H.264 and MPEG-2 get nothing.
- **No new public API:** `VideoStreamInfo`'s properties were there. The four samples are in make-samples.sh, with
  name.color references for the MXF H.264 samples.
