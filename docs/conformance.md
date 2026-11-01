# Conformance notes

What each decoder and demuxer is checked against, which encoder produced the samples for each feature, and what
is not checked, feature by feature. The README's Status section has the summary and the list of what is still open.

## MJPEG

Baseline (and extended Huffman) JPEG, bit-exact against FFmpeg: 4:2:0, 4:2:2, 4:4:4, gray, restart markers, missing
DHT. 4:1:1 and 4:4:0 (cameras' layouts, which FFmpeg's encoder doesn't write) on ImageMagick's images: luma 4x1
against chroma 1x1, 1x2 against 1x1 and 2x2 against 2x1, at 322x238 so the quarter-wide chroma rounds up; FFmpeg
gives yuvj411p and yuvj440p, `PixelFormat.Yuv411P` and `Yuv440P` here. Interlaced AVI1 frames too (two JPEG fields per packet, recognized as FFmpeg does by being less than 3/4 of the
stream's height, so the decoder needs `new MjpegDecoder(video)`), woven with the first field on the odd rows, the
order FFmpeg takes for AVI, which does not tell it (`FirstFieldOnTop` gives the other; the AVI1 segment's field
byte is ignored, as by FFmpeg). Packets of one field are not decoded (FFmpeg decodes none either).

Each frame's colour properties and aspect ratio are compared with `ffprobe -show_frames` frame by frame (`name.color`,
as for H.264). T.81 leaves them open; JFIF implies BT.601 YCbCr in full range with centred chroma. FFmpeg's decoder
(6.1.1), found with patched samples where its encoder writes nothing to tell:

- Every frame is `Bt470BG` (BT.601) with `Center` chroma, gray ones too; primaries and transfer unspecified.
- YCbCr is full range, unless a "CS=ITU601" comment came (the whole comment, to a terminating zero), which FFmpeg's
  encoder writes for limited range (`mjpeg-limited-range`; FFmpeg decodes it to yuv420p, tv). Gray is unspecified
  (`gray.avi`, ImageMagick's; FFmpeg's encoder writes no gray).
- The aspect ratio is the JFIF header's Xdensity:Ydensity whatever its units (300 by 150 dots an inch gave 2:1),
  unknown with a 0 in either or without JFIF (`mjpeg-sar`, 2:1).
- Both are kept for the rest of the stream: a stream whose first image alone had the comment, or a JFIF density of
  3:2, gave tv, or 3:2, for every frame; a later JFIF replaces the ratio. `MjpegColorTests` covers these.

As for H.264, ffprobe's ratio prefers the container's (AVI's 'vprp', MP4's 'pasp'), and so does a decoder made from
the stream (`MjpegDecoder(VideoStreamInfo)`): FFmpeg's muxers write it from the same source as the JFIF density, so
`mjpeg-vprp-conflict` patches the 'vprp' (tools/avi-vprp.py; a 16:9 picture, 4:3 samples at 320x240, over JFIF's 2:1)
and its frames read 4:3. `AviDemuxer` reads 'vprp' as FFmpeg's avidec does: the picture's ratio (dwFrameAspectRatio,
Y in the low word) over the frame's (dwFrameWidthInPixels over dwFrameHeightInLines), none with a zero anywhere or a
chunk of 36 bytes or fewer (`AviDemuxerTests`; `DisplayTests` compares every AVI with ffprobe's stream).

Damaged images, as cameras and capture cards write them, decode as FFmpeg's decoder (6.1.1) decodes them, found by
measuring its output, which T.81 does not cover (src/ManagedCodecs.Mjpeg/CLAUDE.md has how):

- Bytes before the SOI marker are skipped; data that ends without an EOI marker ends there (`mjpeg-junk`).
- A scan cut short is decoded up to the MCU after which more bits were read than its data holds (the zeros past it,
  FFmpeg's "overread"), that MCU included. After the data come the 0xFF of the marker that ends it, and that marker's
  code too where a 0xFF fill byte came before it ("FF FF D9"); a lone 0xFF ending the packet is not data. A code that
  is not valid stops the scan at its block. The frame is `IsDamaged`, the rest of its scan 0 (`mjpeg-cut`,
  `-cut-eoi`, `-cut-stuffed`, `-cut-ff-eoi`, `-cut-stuffed-eoi`, each cut frame between clean ones).
- A packet of less than a quarter byte for each 8x8 block of the image (its size rounded up to whole blocks; what
  comes before SOI counts) is refused with InvalidDataException, as FFmpeg gives no frame for it (measured at gray
  322x238 and 640x480, 4:2:0 1280x720 and 1282x722, 4:4:4 1280x720). An interlaced packet whose first field is cut
  short (so no second field follows) is refused too, where FFmpeg gives none.

Checked beyond the samples by cutting frames of seven layouts (4:2:2, 4:4:4, an odd size, restart markers, the Annex K
tables, gray, noise) at 14 places each, with and without an EOI after the cut: 362 of 364 frames as FFmpeg's, and 27 of
28 frames damaged mid-scan. Where they differ:

- FFmpeg leaves what a cut scan does not reach as its frame buffer was: 0 in a buffer it has not used before, an
  earlier frame's samples in a reused one, which depends on its buffer pool. The decoder leaves 0, so the samples put
  each cut frame second, where FFmpeg's buffer is new.
- A cut ending on the 0xFF of a stuffed pair with an EOI after it, in the two layouts with the Annex K tables
  (testsrc-rst and -stdhuff; testsrc-420's, mjpeg-cut-ff-eoi, matches): what FFmpeg reads past such data.
- Damage mid-scan matches only up to the first code that is not valid: FFmpeg decodes on through some of them (its
  tables' own reading), and after an error in a scan with restart markers it starts again at some of the later ones.
  `MjpegDecoderTests` covers such a frame (damaged, the part before the damage as the undamaged frame's).
- After an interlaced packet it refuses for a cut first field, FFmpeg pairs fields across packets, so its next frame
  is not that packet's two fields; the decoder's is.

## Real films

Every sample above is a test pattern encoded to isolate a feature. `tools/realworld.py` (nightly, `realworld.yml`, a job
per film) decodes real ones with `mcdec --dump` and with FFmpeg 6.1 and compares them: each frame's MD5 (`-f framemd5`)
and the audio as floats (`-f f32le`, within 2^-17). The films, pinned by SHA-256, from download.blender.org:

- Sintel's trailer: 1920x1080 H.264 High, 52 s, AAC LC from FAAC 1.26.1 (its encoder delay, signalled only by its
  name, see AAC).
- Elephants Dream, the whole film: 480x270 H.264 Main in an Apple MOV, 11 minutes, with negative composition offsets
  and the AAC configuration in a QuickTime `wave` box (see MP4 demuxing).
- Tears of Steel, the whole film: 1280x534 H.264 Main in a MOV, 12 minutes of live action; its MP3 audio is not
  decoded, so only the video is compared.
- Caminandes 2: 1920x1080 H.264 High from MainConcept, 2.5 minutes, CABAC with B pictures and explicit weights in P
  slices, four PPS, AAC LC at 48 kHz.
- Big Buck Bunny, the whole film at 320x180: H.264 Constrained Baseline muxed by FFmpeg in 2008 (Lavf52), 10
  minutes, deblocking off, AAC LC at 48 kHz.
- Big Buck Bunny, the whole film at 640x360: H.264 Constrained Baseline from Apple's encoder, 10 minutes, two slices a
  picture, deblocking off, and 640x359 in its sample entry (see H.264, The container's picture size); AAC LC at
  44.1 kHz.

And four cameras' own MJPEG from archive.org (each licence on its item's page; the files are only downloaded), where
every MJPEG sample above is FFmpeg's or ImageMagick's encoder:

- Three digital cameras' AVIs, a Canon PowerShot's among them: 320x240 at 15 and 640x480 at 30 frames a second, 31
  to 97 s, 4:2:2, with 8- and 16-bit PCM audio, which is not decoded (nor read by `VideoProbe`, so not probed).
- A Nikon Coolpix's QuickTime file: 320x240 MJPEG (`jpeg`) at 15 frames a second, 33 s, starting with a `pnot`
  preview box and no `ftyp`, which `Demuxer.Open` didn't recognise until it was added.

All four matched FFmpeg frame for frame at once (3978 frames); none uses 4:1:1 or 4:4:0, which the samples above
cover (`mjpeg-411`, `mjpeg-440`).

The first run found the three differences those sections describe; the H.264 video itself matched on all three. Of
the three films added later, two matched at once; the third's frames were FFmpeg's but for its last row, which FFmpeg
crops by the container's size.

## SIMD and ARM64

MJPEG: AVX2 IDCT, DC-only blocks, single-lookup coefficient decoding, buffer reuse, and for machines
without AVX2 (ARM64 among them) an IDCT in cross-platform `Vector128` operations, so the code ARM64 runs is
tested on x64 too (against the scalar IDCT on random blocks, and against FFmpeg with `DOTNET_EnableAVX2=0`, a CI
step): with AVX2 off, 43 and 47% fewer instructions than the scalar IDCT on `testsrc-420.avi` and `noise-q1.avi`
(callgrind), 16 and 18% less time. CI runs every test on ARM64 too (`build-arm64`, GitHub's ubuntu-24.04-arm
runner), against references made there; the H.264 deblocking filter's transposes have a `Vector128` version
for it as well (x64 keeps SSE2's), and that job found 8-bit H.264 throwing PlatformNotSupportedException on
ARM64 before (the chroma filter called SSE2's transposes unchecked).
Decoding straight into the output frame: opt-in, `MjpegDecoder.ZeroCopy` (6% fewer instructions).

## MP4 demuxing

`VideoProbe` (ManagedCodecs.Thumbnails) is checked against ffprobe on every MP4 and AVI sample with H.264 or MJPEG
video (make-samples.sh's name.probe.json): `format=duration` within 1.5 µs, width and height, sample aspect ratio,
rotation, average frame rate and the audio stream's rate and channels. The duration is the streams' container
durations as FFmpeg reads them: MP4's edits' length, but no longer than the samples they take (JM's field-picture
files edit to the last field's pts, their samples lasting half a frame), else 'mdhd''s, or the samples' when longer (a
fragmented file's 'mdhd' counts only the samples in 'moov'); AVI's 'strh' length, and AVI's audio, which the demuxer
does not expose, is not counted (avi-dropped-frame's MP3 outlasts its video). H.264's size is the decoder's frame size
from the SPS, which can crop the container's.

The thumbnails' pixels (`RgbaImage`, ManagedCodecs.Thumbnails) are each frame converted to RGBA at its own size (the
matrix and range the frame says; none said, BT.601 and limited range, as FFmpeg's scaler takes them, but full range for
MJPEG, as FFmpeg converts its grey frames; the identity matrix as G, B and R planes, FFmpeg's `gbrp`, h264-444-gbr from
libx264rgb; the nearest chroma sample), scaled down by area and rotated.
`RgbaImageTests` checks the conversion within one level of its definition in double precision (every pixel format,
random samples and matrices), the scaling exactly against the overlap mean summed pixel by pixel, the rotation against
the turned coordinates, and the three together against FFmpeg's first frame of every sample at 3/8 of its size as shown
(make-samples.sh's name.thumb.rgba: `-vf format=rgba,scale=W:H:flags=area+accurate_rnd+full_chroma_int+full_chroma_inp`,
autorotate on). FFmpeg comes out 0 to 3 levels below ours and at most 1 above (2 on ARM64, whose NEON code reduces
the deep samples to 8 bits rounding up more often): its fast YUV to RGB path rounds down
(green most), and its area scaler is within one level of the exact mean once its flags keep RGB from going through YUV
of its own (without them, 10 levels at colour edges). It is not the reference everywhere: from 9 to 14 bits its direct
conversion to RGBA was up to 87 levels from the definition and from its own 8-bit route, so those references reduce to
8 bits first (within 3), and at an odd width or height it leaves the fast path for one that interpolates chroma (up to
194 levels apart at an edge): those two samples, the container crops, have no FFmpeg reference, and the odd sizes are
checked against the definition only. So does 4:1:1 and 4:4:0 at any size (mjpeg-411, mjpeg-440 and mjpeg-440-2x2,
6 levels apart at the first pixel that differed), likewise checked against the definition only.

`VideoThumbnail.Create` is checked end to end against the same FFmpeg chain and band: at `At` zero on every sample
whose pixels are square (FFmpeg's chain scales the coded size, `Create` the size as shown), and at times into six
(name.atNNNN.thumb.rgba, `ffmpeg -ss <t> -i`: between keyframes, on one, a fragmented file that starts late, past an
empty edit, MJPEG, a fade-in). FFmpeg rescales the time into the stream's time base to the nearest unit and takes the
first frame at or after that, which `Create` does too: where the time base is a frame's that is the frame nearest the
time, not the next (testsrc-420.at610, a quarter frame past 600 ms in AVI's 1/25: the frame at 600; Sintel's trailer
at 5.2208 s in 1/24, below: the frame at 5.2083). `SkipDark` is checked on
h264-fade-in (a keyframe every 0.2 s: the frame at 0.4 s is the first at 10% of white's mean luma). The PNG files are
read back by a reader the tests have from the PNG spec (chunk CRCs bit by bit, inflate, the five filters undone) and,
where ffmpeg is installed (CI), by FFmpeg's PNG decoder, to the byte. The fuzzer's `thumbnail` target probes and
thumbnails whole files, and `MalformedInputTests` replays every container finding through it.

On WebAssembly, `tools/wasm-check.sh` compares each video sample's probe facts and a thumbnail (160 x 160 at most,
`SkipDark`; its pixels hashed, not its PNG, whose deflated bytes are the runtime's zlib's) with the native build's,
interpreted and compiled ahead of time: all 362 agree. `tools/realworld.py` probes and thumbnails the films too: the
facts against ffprobe (duration, size, rotation, average frame rate, audio rate and channels), the thumbnail at the
size `Create` chose against FFmpeg's chain at that size and the same default time, in the same band; Big Buck Bunny
640x359, an odd height, is probed but its thumbnail not compared (FFmpeg's general path, as above).

`VideoStreamInfo`'s colour properties, aspect ratio and rotation are the container's, as FFmpeg's mov demuxer reads
them (6.1.1): the visual sample entry's 'colr' ('nclx', its full range flag clear meaning limited; QuickTime's 'nclc',
without a range; ICC profiles ignored; a later 'colr' replacing an earlier one's values; code points FFmpeg does not
name unspecified) and 'pasp' (in lowest terms; a zero unknown), and the rotation of the track's matrix ('tkhd') times
the movie's ('mvhd'), which FFmpeg multiplies too (a 90-degree 'mvhd' alone gave ffprobe's rotation 90). ffprobe's
stream shows the container's ratio and the rotation (`name.display`, `DisplayTests`; `h264-rotate-90`, `-180`, `-270`
from `-display_rotation`, which ffprobe prints as 90, -180 and -90: here 90, 180, 270); its stream colour is the
decoder's once it has decoded frames, so 'colr' is checked through the frames (`h264-colr-only`, `mjpeg-colr`).
`Mp4DemuxerTests` builds what no muxer at hand writes: 'nclc', a profile between two 'colr's, undefined code points,
boxes cut short, a zero or huge 'pasp', scaled, mirrored and 45-degree matrices. A mirrored matrix (FFmpeg's
`-display_hflip`) is 0 here, where ffprobe prints its rotation: turning such a frame without mirroring it would be
wrong too. FFmpeg's command line turns decoded frames by the rotation (the references take `-noautorotate`); the
decoders here do not.

ISO BMFF (`moov`, `trak`, `stbl` sample tables): packets, timestamps and
codec private data match FFmpeg, for video and audio tracks, fragmented files (moof) included. On every MP4 sample,
`ReadPacketInfo` is checked to be `ReadPackets`'s packets without their data, with no read of the file, and a demuxer of
one track (`MediaTypes.Video` or `Audio`) to give that track's description and packets as one of both does. AVI's
and ADTS's `ReadPacketInfo` are checked the same way on their samples: ADTS's with no read of the file, AVI's reading
only chunk headers, 8 bytes each. `Seek` is checked on every MP4 and AVI sample (MJPEG), at each keyframe's time, one
tick before and after it, between keyframes, before the first and past the last, in both streams: each stream's
packets after it are the tail of a read from the start, from the last keyframe whose pts is at or before the time
(`SeekTests`). The frame then shown at a time matches FFmpeg's accurate seek (`ffmpeg -seek_timestamp 1 -ss t -i ...
-frames:v 1`, name.seekNNNN.framemd5) between keyframes, on one, in a fragmented file that starts late, past an empty
edit and in MJPEG (`SeekFrameTests`). Audio is trimmed by the edit list as FFmpeg trims it: priming
skipped sample by sample, packets after the edit's end left out.

Edit lists of several edits, which FFmpeg's muxer never writes, come from rewritten samples (`tools/mp4-edit-list.py`,
in `make-samples.sh`): two audio edits, the second from inside a packet; an empty edit between two; two video edits
starting and ending inside a GOP, with B frames; a video edit that ends a second before the track; and both tracks of
a file edited. Their packets are compared with `ffprobe -show_packets` (timestamps, durations, keyframe and discard
flags, MD5), which shows the demuxer's own; `ffmpeg -c copy -f framemd5` keeps decode timestamps going forward, so
it rewrites those of the packets an edit goes back for. FFmpeg's reading, which these follow:

- Video: an edit is decoded from the keyframe before its first frame, and on to the second keyframe after its end (the
  first without composition offsets: B frames after the first keyframe can still belong to the edit); the frames
  shown outside it are discarded. The frames come out exactly as the edit says.
- Audio: a second of packets before an edit is decoded and discarded, which primes the decoder (the overlap of the
  filterbank, the noise generator's state). The first edit starts inside the packet its media time falls in (skip
  samples), a later one at the next whole packet, so up to 1023 samples late; each edit takes the whole packet its
  end falls in. Packet durations run up to the next packet's dts where that comes later, and the last one's to the
  end of the edits, longer than 1024 if need be, but not past the media's end (an edit whose duration, rounded up in
  the movie's timescale, runs past it: aac-fdk-pce-remux).
- An empty edit after a real one plays the media from its start for its duration, FFmpeg reading its media time, -1,
  as a time like any other (the spec has it as a gap).
- Where the first pts shown (of a frame not discarded), less the empty edits before the first real one, comes after 0,
  every time of the track moves back so that it is 0 (FFmpeg's `mov_fix_index`): an edit whose media time is before
  the first composition offset, which FFmpeg's muxer never writes (it sets the first offset as the media time), but
  other muxers do (Caminandes: Gran Dillama, media time 0 and a first offset of 1000; issue #252).
  `h264-edit-zero`, `h264-edit-gap` (past a discarded first frame, which doesn't count) and `h264-edit-empty-zero`.

Negative composition offsets: in the `moov`'s own `ctts` (`h264-negative-ctts`, `-movflags negative_cts_offsets`
unfragmented), FFmpeg moves every dts back by the most negative offset (of all entries but the last two), so the pts
keep their times and an edit list's end is taken in them; in fragments (`fmp4-negative-cts`), every pts forward by it.
An offset in the last two entries more negative than that puts a pts before its dts, which FFmpeg keeps for H.264
(`h264-negative-ctts-last`, packets only: FFmpeg's command line drops that frame as its pts goes back). Not matched:
for a codec without reordering (MJPEG, audio), which no encoder gives a 'ctts', FFmpeg's generic demuxing makes the dts
the pts (Mp4DemuxerTests checks the shift on such a table, not that dts).
Elephants Dream (an Apple-made MOV, offsets down to -600 of a second's 600) had its last 24 frames discarded as past
its edit's end until the first was read that way. QuickTime files (`aac-quicktime`, FFmpeg's `-f mov`) put the AAC
`esds` in a `wave` box of the sound description.

## H.264

### Profiles and tools

Bit-exact with
FFmpeg on libx264 samples covering intra and inter prediction, all partitions, multiple
references and slices, cropping and constrained intra, with CAVLC and with CABAC (Main profile),
and B frames: every B macroblock type, spatial and temporal direct prediction, bi-prediction, B
pictures as references (with memory management operation 1); explicit and implicit weighted
prediction, so libx264's default Main profile output decodes; and High profile's 8x8 transform
with Intra 8x8 prediction, so libx264's default output decodes too, and scaling matrices (the
default ones and custom lists). Frames come out in display order.

Each frame's `VideoFrame.PictureType` is FFmpeg's `pict_type`, compared with `ffprobe -show_frames` frame by frame on
every sample FFmpeg decodes (`name.picttypes`, in the conformance tests, with threads too). FFmpeg takes the type of
the picture's first slice (`h264_frame_start`; the spec gives a picture no type of its own), so a frame of two field
pictures has its first field's type (I and P fields give I; JM's PAFF samples hold such pairs), and a picture whose
slices differ in type has its first slice's: no encoder used here mixes them, so `H264PictureTypeTests` builds such a
stream (ffprobe gives it I, I, P too). SI and SP, which FFmpeg reports too, would come from the Extended profile's
switching slices, which the decoder rejects. A concealed frame has the type of the slices that arrived.

### Interlacing

Each frame's `VideoFrame.IsInterlaced` and `IsTopFieldFirst` are FFmpeg's `interlaced_frame` and `top_field_first`,
compared with `ffprobe -show_frames` frame by frame on every sample FFmpeg decodes (`name.interlacing`, in the
conformance tests, with threads too), and `H264Decoder.FieldOrder` with the stream's `field_order`. The spec leaves
both to the application, so these are FFmpeg's rules (`h264_export_frame_props` in h264_slice.c and `parse_nal_units`
in h264_parser.c, the same in 6.1.1, which CI runs, and 8.1.3 on every sample):

- Interlaced: where the SPS has pic_struct_present_flag and the picture timing SEI came, pic_struct 1 and 2 (a field)
  are, 3 and 4 are for a field pair or an MBAFF frame and otherwise keep the previous frame's flag (soft telecine; true
  at the start and after a change of picture size), and 0 and 5 to 8 are not; a clock timestamp's ct_type 0 or 1
  (progressive, interlaced) overrides that for pic_struct up to 4. Without the SEI, a field pair or an MBAFF frame is
  interlaced, whatever its macroblock pairs are.
- Top field first: the top field's order count below the bottom's where they differ, else pic_struct 3 or 5 where the
  SEI came, else whether the frame is interlaced. FFmpeg compares a first field's order count as decoded, before
  memory_management_control_operation 5 rebases it to 0, so a frame whose first (top) field has that operation is
  bottom field first (`jm-paff-mmco5`).
- The SEI is the last one of the picture's packet; a first field's holds for its second field, where FFmpeg sets the
  frame's flags (no sample's field pictures carry one: `H264InterlacingTests` adds it to JM's, as ffprobe reads that
  stream too).
- `VideoFrame.FieldOrder` is the parser's: tt for pic_struct 3 or 5, bb for 4 or 6, progressive for any other where
  the SEI came, whatever the order counts say (libx264 writes pic_struct 3 for bottom field first frames: tt to the
  parser, `top_field_first` 0 to the decoder; `h264-mbaff-bff`); else the order counts' order, progressive where they
  are equal. A field picture right after the other field of the same frame_num gets that one's order, any other field
  unknown; a frame of two field pictures takes its second field's. ffprobe prints only the stream's, which for an MP4
  file is its first picture's: FFmpeg's stream analysis reads one packet there, so a stream that starts with a field
  picture (JM's) is unknown (a raw H.264 file, which FFmpeg reads on, gives the second field's). The frames' orders are
  checked against a build of FFmpeg's parser printing every packet's (`H264InterlacingTests`). The parser forgets the
  SEI at each packet: after a field whose second field was lost, a frame picture without an SEI of its own would get
  the field's here, and none from FFmpeg's parser.

libx264 writes pic_struct 0 and 3 and no clock timestamps, so `tools/h264-set-pic-struct.py` rewrites its SEI with every
pic_struct and some ct_type values, on MBAFF and on progressive frames (`h264-mbaff-pic-struct`, `h264-pic-struct`);
`h264-mbaff-hrd` has the HRD delays ahead of pic_struct, and the `-nosei` samples have the SEI removed. MJPEG frames
are never interlaced or top field first, as ffprobe reports for every MJPEG sample, the interlaced AVI1 ones too, and
their field order is unknown.

### Colour and aspect ratio

Each frame's `ColorRange`, `ColorPrimaries`, `ColorTransfer`, `ColorMatrix`, `ChromaLocation` and `SampleAspectRatio`
are compared with `ffprobe -show_frames`'s `color_range`, `color_primaries`, `color_transfer`, `color_space`,
`chroma_location` and `sample_aspect_ratio` frame by frame on every H.264 sample FFmpeg decodes (`name.color`, in the
conformance tests, with threads too). libx264 writes the colour description only when asked: `h264-color-bt709`
(limited range), `h264-color-bt2020-pq` (full range, PQ, chroma_sample_loc_type 2), `h264-color-hlg-sar` (SMPTE 170M,
HLG, Table E-1's 16:11) and `h264-sar-extended` (Extended_SAR 64:45), each checked with `trace_headers`; libx264's
other streams send a VUI with a 1:1 ratio and nothing else, OpenH264's a VUI without a ratio, JM's none. What no
encoder writes (reserved and undefined code points, an Extended_SAR of 0, a ratio that would show the frame as
nothing) `VideoSignalTests` covers through the SPS parser. The spec (E.2.1) gives the meaning; FFmpeg's reading
(`ff_h2645_decode_common_vui_params`, `ff_set_sar`, 6.1.1) adds:

- A VUI without chroma_loc_info means left, whatever the chroma format; without a VUI the location is unspecified.
- A code point FFmpeg does not name is unspecified (primaries above 12 but 22, transfer above 18, matrix above 14: 6.1
  has no IPT-C2 or YCgCo-R); the reserved 0 and 3 it names are kept.
- A ratio is checked against the frame (`av_image_check_sar`): one under which the shown width (or, above 1:1, height)
  rounds down to nothing is unknown.

ffprobe's `sample_aspect_ratio` for a frame is not quite the decoder's: it prints `av_guess_sample_aspect_ratio`,
which takes the container's ratio ('pasp' in MP4) where there is one, else the frame's, either in lowest terms (an
SPS patched to 128:90 under a 'pasp' of 3:2 printed 3:2; FFmpeg's decoder itself keeps 128:90). The frames here give
the container's ratio where the decoder was given one (`H264Decoder(VideoStreamInfo)`), else the SPS's in lowest
terms, so they show what ffprobe shows; `h264-pasp-conflict` ('pasp' 4:3 over a VUI of 64:45, tools/mp4-pasp.py, as
FFmpeg's muxer copies the VUI's) checks that. The container's colour ('colr') is what FFmpeg's decoder starts from:
where the VUI sends a video signal type, its range wins, where it sends a colour description, its primaries, transfer
and matrix (`h264-colr-conflict`, a BT.2020 'colr' over a BT.709 VUI, gives BT.709), and otherwise the frames take the
container's (`h264-colr-only`).

An SPS that leaves out the range or the colour description keeps what an earlier SPS sent, as FFmpeg's codec context
does (`h264_init_ps` writes the range only under video_signal_type_present_flag and the three codes only under
colour_description_present_flag), and those still win over the container's; the spec's reading would be the
container's or unspecified, the VUI describing its own coded video sequence. The chroma location and aspect ratio are
each SPS's own. No sample changes its colour between SPSs, so `VideoSignalTests.LaterSps_KeepsWhatItLeavesOut` builds
them (the owner's choice, 2026-10-31).

### The container's picture size

FFmpeg's decoder crops a frame further to the size the container gives (MP4's sample entry), where that only takes
columns and rows off the right and bottom inside the last macroblock: `init_dimensions` in h264_slice.c takes it with
no SPS cropping at the top or left, a size no larger than the SPS's visible area, and the same once each is rounded up
to a multiple of 16, and otherwise ignores it for the rest of the stream. Apple's encoder writes such files (Big Buck
Bunny at 640x360 stores 640x359; Real films). `H264Decoder(VideoStreamInfo)` does the same (the span constructor has
no container size). `tools/mp4-video-size.py` rewrites a sample entry's size: h264-container-crop (317x233 from
320x240, odd, so 4:2:0 chroma rounds up) and h264-container-crop-422-10bit (319x239) are cropped,
h264-container-crop-misaligned (320x224, another multiple of 16) and h264-container-crop-sps-top (the SPS crops the
top, `h264_metadata=crop_top=2`) are not. Probed beforehand on patched copies: 320x225 is taken, 320x224, 304x240 and
anything larger are not. Not tested: a stream whose SPS changes from one that takes it to one that doesn't, after
which neither does again.

### Interlaced video

MBAFF frames of I, P and B slices (macroblock pairs coded as frame or field
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
PicNum) and the field B lists' list 1 swap have unit tests; a field without its pair throws
NotSupportedException. Memory management operation 5 in a field picture, which no encoder at hand
writes: `tools/h264-add-mmco5.py` puts it into the first field of a JM stream's first non-IDR I
frame and rebases the frame_num and pic_order_cnt_lsb of the slices after it. With P fields and
one reference frame the lists after that frame hold its fields alone, as the encoder had them, so
JM's decoder gives exactly the encoder's reconstruction and FFmpeg the same: `jm-paff-mmco5`
(CABAC) and `jm-paff-mmco5-cavlc` fail when the second field is not paired with frame_num 0, or
operation 5 does not unmark the references or reset the frame's frame_num. The field's own order
count, rebased to 0 (which changes no output order in these P-only streams), has a unit test. Long-term references
used by field pictures: `jm-paff-b-adaptive-temporal-ltr` codes its IDR picture as a frame marked
long-term and later pictures as fields, so the P and B field lists carry its fields after the
short-term ones (parity alternating) and temporal direct asks whether they are long-term; the
sample fails when either is broken. Field pictures marked long-term: JM's encoder crashes writing
an IDR field pair marked long-term (for the second field it writes operation 3, naming the first
field, where operation 6 belongs), so `make-jm-samples.sh` builds a copy with that one value
changed. FFmpeg (6.1.1, and its master) drops the first field's mark when the second field takes
the same LongTermFrameIdx, so its output is wrong from the second frame on; JM's decoder gives
exactly the encoder's reconstruction (the script checks it), and its frames are the reference
(`samples/jm-ref/`): `jm-paff-ltr` (B fields, CABAC), `jm-paff-ltr-cavlc` (P fields) and
`jm-paff-ltr-adaptive` (fields and frames, noisy) predict from the long-term pair throughout, and
fail when the IDR field's long_term_reference_flag or the second field's operation 6 is not
applied as the spec says. Operations 2, 3 and 4 in field pictures (by LongTermPicNum of either
parity; an index given to a field of a frame whose other field holds it; both fields above the
maximum) have unit tests. `jm-paff-ltr-temporal` adds temporal direct over the long-term fields:
there JM's encoder reconstructs three of its B frames unlike JM's decoder, which this decoder
agrees with frame for frame, so the encoder is taken to be the one wrong and its check skipped.

### OpenH264 and rewritten libx264 streams

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

### 4:2:2

High 4:2:2 (8-bit) is checked on libx264's 4:2:2 output: 8x16 chroma blocks
with the 2x4 chroma DC transform (at QP_C + 3), CAVLC's and CABAC's chroma DC coding of 8
levels, chroma intra prediction over 16 rows, chroma motion with quarter-sample vertical
vectors and the chroma deblocking edges of 4:2:2 (including those that 8x8 luma transforms
skip), with CAVLC and CABAC, the 4x4 and 8x8 transforms, P and B frames, weights, constrained
intra prediction and cropping; I_PCM in 4:2:2 from hand-built streams. Interlaced 4:2:2 too:
libx264's MBAFF frames (intra prediction from 16 chroma rows of neighbours, and chroma lines
across mixed pair edges taking their own luma line's bS) and JM's field pairs, also mixed with
frame pictures and B pictures.

### 10 bits

10-bit video (High 10 and High 4:2:2 at 10 bits) is checked on libx264's output too: QP_Y from
-12 (QpBdOffset) with dequantization at QP'Y and chroma QPs below 0, Clip1 to 1023, deblocking
thresholds and weighted-prediction offsets scaled by 4, CAVLC level prefixes of 16 and more,
at QP_Y -12 on noise, P and B frames, weights, constrained intra prediction, cropping, 4:2:2
and MBAFF; I_PCM at 10 bits from hand-built streams. Motion compensation and the deblocking
filter have vector paths for 10-bit samples too (32-bit lanes for the 6-tap filter, 16-bit
ones for deblocking), each checked against its reference on random 10-bit input.

### 9, 12 and 14 bits

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

### 4:4:4

4:4:4 (High 4:4:4 Predictive, 8 and 10 bits) is checked on libx264's 4:4:4 output: Cb and Cr
coded as luma (Intra16x16 DC and AC, 4x4 and 8x8 blocks, CABAC contexts 460 to 1023 and the
coded_block_flag that 8x8 blocks have only in 4:4:4, Table 9-4's 4:4:4 coded_block_pattern),
predicted with the luma intra modes and the luma 6-tap filter, dequantized at their own QP'C
with their own scaling lists (12 in the PPS), and deblocked with the luma filter at QP_C on the
luma edges, with CAVLC and CABAC, P and B frames, constrained intra prediction, cropping, MBAFF
and 10 bits; I_PCM in 4:4:4 from hand-built streams. x264 before
build 151 derived the context of an 8x8 block's coded_block_flag in 4:4:4 against the spec (a
neighbour without 8x8 transforms counted as missing); FFmpeg decodes such streams by the encoder's
version, this decoder by the spec, so they come out differently.

### Separate colour planes

4:4:4 with separate_colour_plane_flag codes each colour plane as a monochrome picture of its own
(ChromaArrayType 0): its own slices (colour_plane_id), macroblocks, motion and deblocking, all as
luma. FFmpeg decodes none of it (nor muxes it), so it is the one feature checked against JM's
decoder instead (`samples/jm-ref/`, from `tools/make-jm-samples.sh`, muxed by
`tools/mux-h264.py`): CABAC with B pictures (direct prediction reads the co-located motion of
the same plane), CAVLC, scaling lists in the SPS and PPS (each plane takes its own
lists, iYCbCr being colour_plane_id, 8.5.9), 10 bits, and chroma QP offsets in the PPS, which no
plane applies (all three dequantize and deblock at QP_Y). The samples fail when a plane predicts
from another plane's references, and the scaling-list and QP-offset ones when their rule is broken.
With threads, a picture is reported done to the pictures that predict from it once all three
planes are, not row by row.

Interlaced video with separate colour planes (field pictures, MBAFF) neither JM's encoder nor its
decoder handles (both crash), so those samples are spliced: `tools/h264-splice-planes.py` joins
three 4:0:0 JM encodes of one source's planes, with the same settings and so the same slice
headers, into one stream (the SPS rewritten to separate colour planes in High 4:4:4, each slice
given its colour_plane_id). Each plane decodes as its monochrome stream did, so the reference is
FFmpeg's three monochrome decodes, stacked (the method checked first on a progressive splice,
which JM's decoder decodes to the same frames). `jm-planes-paff-cavlc` (field pictures, CAVLC, P),
`jm-planes-paff` (CABAC, B pictures, temporal direct: the co-located field is the plane's) and
`jm-planes-mbaff`. They fail when a plane's field reads the frame's fields instead of its own.
`jm-planes-mbaff-ltr` (MBAFF, B pictures, temporal direct, the IDR picture marked long-term) fails
when a plane's field is not marked as the frame's field is: temporal direct asks whether a field
macroblock's reference field is long-term. JM's encoder crashes with long-term references in field
pictures (and with implicit weights in this sample), so a unit test (ReferenceListTests) also checks
the marks of a plane's fields directly.

### Lossless

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

### Monochrome

Monochrome (chroma_format_idc 0) comes out as `Gray8` to `Gray14`, one plane; it is checked on
libx264's gray output (High and High 10): no intra_chroma_pred_mode, the coded_block_pattern
without a chroma part, no chroma weights in pred_weight_table, with CABAC and CAVLC, P and B
frames, explicit weights, cropping, MBAFF, 10 bits and lossless; and on JM's field pictures
(PAFF) in 4:0:0, alone and mixed with frame pictures, with B pictures and CAVLC; at 12 and 14 bits on JM's
progressive streams with B pictures (`jm-gray-12bit`, `jm-gray-14bit`, the 14-bit one at QP -6); I_PCM from
hand-built streams. JM writes bit_depth_chroma 8 in 4:0:0 whatever the luma's depth, which no plane uses (7.4.2.1.1)
but which FFmpeg refuses, so it cannot even mux those streams: `tools/h264-chroma-depth.py` gives the SPS the luma's
depth for chroma too, FFmpeg then decodes them (JM's own decoder gives the same frames, all 10 of each), and
`H264MonochromeDepthTests` decodes them with JM's 8-bit chroma depth put back, to the same frames. FFmpeg's decoder outputs such streams as 4:2:0 with flat chroma, so the
references take its Y plane alone (`-vf extractplanes=y`, which leaves the samples as they are).

### What only JM writes, and what no encoder writes

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
with rewritten parameter sets) stay as a second check.

### Error concealment

`H264Decoder.ConcealErrors` (off by default). A slice whose data fails (a syntax element out of range, or the data
ending inside a macroblock) keeps the macroblocks it decoded; the failing one and the rest of the slice are marked
missing at once, so the rows below them are neither deblocked nor reported to other pictures before they are filled.
The picture's other slices are decoded, then every missing macroblock becomes an inter macroblock with no coefficients
and the slice's QP, in a slice of its own. Its motion is guessed by boundary matching (Lam, Reibman and Liu, "Recovery
of lost or erroneously received motion vectors", ICASSP 1993): the candidates are zero motion from list 0 entry 0 of the
picture's first P or B slice (an intra picture: the latest reference; none, after a damaged IDR picture, and for now an
intra second field, whose latest reference is its own frame: mid-grey, and no guess) and the motion of each 4x4 block of
an available inter neighbour (left, above, right, below; decoded, or concealed before it) along their shared edge, list
0's or else list 1's; each predicts the macroblock's luma, and the one whose outer samples differ least from the
neighbours' samples next to them is taken (the earlier on a tie, zero motion first). Missing macroblocks are filled in
address order, so the guess does not depend on threads. In MBAFF frames the edges are the frame samples next to the
macroblock (a field macroblock's own field above and below it), each owned by the macroblock that holds it, and a
neighbour of the other kind has its motion converted as motion vector prediction converts it (8.4.1.3.1: a field's
vertical vector doubled for a frame macroblock, a frame's halved towards zero for a field macroblock, which takes the
reference frame's field of its own parity). A missing pair is a frame pair: inferring field pairs from the neighbours
was worse on the test streams (progressive sources, of which one field holds half the rows), and adding the line
between the fields to a vector from the other parity changed nothing measurable. Deblocking, co-located motion and later direct
prediction then read it as any other. A lost picture (a frame_num gap; its packet missing, or its slice headers
damaged) is the frame inferred for the
gap (8.2.5.2), which then copies the latest reference once that is done, with no motion; a slice whose reference lists
cannot be built (no reference at all, or a list modification naming a lost one) is not decoded and its macroblocks are
concealed. The frame is flagged `VideoFrame.IsDamaged`, as is every picture whose reference lists hold a damaged
picture (checked once those are done: a lost picture's place shifts the lists even where no motion names it), up to the
next IDR picture. A field whose second field was lost (an unpaired field; without concealment it throws) gives its frame
once it is decoded: the missing field's rows are copies of the present field's next to them (line doubling, so spatial,
unlike the macroblocks' copies from a reference), and the missing field is reported done before the next picture starts,
so that pictures predicting from the frame do not wait for it; the frame carries its own field's timestamp. A lost
bottom field followed by the next frame's top field with the same frame_num, both non-reference, would pair them as one
frame (7.4.1.2.4 says they are a pair): that needs consecutive non-reference frames, which JM's samples do not have.
Nothing of FFmpeg's (error_resilience.c guesses motion its own way) is ported. JM codes an intra frame's second
field as P, so no sample has an intra second field to test the grey case's alternative (its first field) with.

Without concealment, a gap the SPS does not allow fails every picture up to the next IDR picture: the lost picture's
memory_management_control_operation went with it (libx264's B pyramid un-marks references that way), and the lists
after it named the wrong pictures without an error (1 or 2 wrong frames a lost picture in libx264's B samples before).
Gaps the SPS allows (OpenH264 with temporal layers dropped) decode as before.

Concealed frames match no reference, so `H264ConcealmentTests` checks the rest against the clean decode (bit-exact
with FFmpeg by the conformance tests): one packet loses the second half of its last NAL unit (CAVLC and CABAC with four
slices a picture, a B pyramid, MBAFF, 4:4:4, JM's field pictures; an IDR picture and an inter one), every frame comes
out, the unflagged ones are bit-exact, the flagged ones within a loose PSNR floor, and three threads with random delays
give the same frames and flags; lost packets likewise (an IDR picture, an inter one, a B picture other B pictures
predict from, OpenH264's long-term references, a JM field pair, and one field of a pair: reference and non-reference,
top and bottom, whose frames come out from one field). Without concealment no frame comes out wrong. The malformed-input sweep and the fuzz
target run concealing too. Measured against FFmpeg on the same damage (the last NAL unit of
every Nth packet cut to half, the damaged stream given to FFmpeg as Annex B; mean and worst PSNR of the flagged frames
against the clean decode, in dB; 2026-10-29):

| Sample, N | Zero motion | Guessed | FFmpeg |
| --- | --- | --- | --- |
| `h264-high-b.mp4`, 10 | 23.6, 18.1 | 26.3, 22.8 | 27.6, 20.2 |
| `h264-high-b.mp4`, 4 | 21.6, 16.9 | 25.6, 19.6 | 20.8, 17.1 |
| `h264-high-motion.mp4`, 10 | 27.6, 14.9 | 30.3, 17.7 | 26.2, 17.2 |
| `h264-bench-720p.mp4`, 10 | 26.3, 22.1 | 28.3, 21.7 | 29.7, 22.7 |
| `h264-bench-720p.mp4`, 4 | 23.9, 19.7 | 26.6, 21.4 | 28.5, 21.9 |
| `h264-main-slices.mp4`, 10 | 22.3, 20.1 | 37.0, 25.2 | 31.1, 27.4 |
| `h264-b-pyramid.mp4`, 6 | 20.9, 14.3 | 27.4, 21.1 | 18.9, 15.4 |
| `h264-p-refs.mp4`, 6 | 26.9, 20.5 | 27.4, 19.7 | 25.0, 19.1 |
| `h264-mbaff-p-motion.mp4`, 10 | 29.8, 27.1 | 29.2, 26.2 | 28.1, 22.9 |
| `h264-mbaff-p-motion.mp4`, 4 | 26.6, 24.3 | 26.2, 22.0 | 24.7, 22.3 |
| `h264-mbaff-b-pyramid.mp4`, 6 | 25.2, 20.0 | 26.1, 19.3 | 23.5, 18.0 |
| `h264-mbaff-b-temporal.mp4`, 6 | 25.9, 21.8 | 26.3, 22.1 | 21.3, 17.9 |
| `h264-mbaff-default.mp4`, 10 | 26.7, 21.3 | 24.8, 21.3 | 22.6, 19.7 |

In MBAFF frames (2026-10-30) the guess gains less, and loses on two streams cut in packet after packet. Cut once, one
packet at a time over the whole stream, its mean over all cuts is 1.0 to 2.4 dB above zero motion on `p-motion`,
`b-temporal` and `p`, and within 0.2 dB on `b-pyramid` and `default`. Cut in the middle, `p-motion` gains 5.4 dB in
4:2:0 and 6.1 dB in 4:2:2 on x64, less on ARM64, whose libx264 encodes these streams differently; the test takes cuts of
JM's committed streams instead (`jm-paff-mbaff` 20.1 to 24.0 dB, `jm-planes-mbaff` 23.3 to 27.5 dB, the first only
with the motion converted between field and frame macroblocks).

`H264ConcealmentTests.CutPacket_MotionGuessed` keeps one cut of each of four committed streams (OpenH264's slices, JM's
CABAC, two of JM's MBAFF) above floors that zero motion falls 3 to 13 dB below; libx264's samples, encoded on each
machine, differ between machines, so no floor rests on them. Damage that still parses is not detected and spreads unflagged: with 8 bytes flipped in one of 6
packets of each of 21 samples, 13 samples had at least one such packet (CAVLC and CABAC alike). Cut slice data was detected in every case: a conforming
CABAC slice ends inside its data (9.3.4.5 writes the stop bit last), so consuming bits past it is damage.

### Performance notes

Where the time goes after the day's speed PRs (stage timers in a scratch copy, 2026-10-24 evening, one thread,
`DOTNET_TieredCompilation=0`; shares of each picture's decoding, the timers nested so they overlap): on
`h264-bench-720p.mp4` motion compensation 29%, deblocking 22% (30% before #81 and #84), inter residuals 22% (16 of it
parsing), intra macroblocks 8%, the rest (macroblock headers, motion vector prediction and storage) about 18%; on
`h264-high-b.mp4` residual parsing 24%, deblocking 18%, intra macroblocks 18%, motion compensation 15%; on the noisy
`h264-high-motion.mp4` residual parsing 61%. Copying frames out is 1 to 4%. So the next gains are in
CABAC residual parsing for detailed content and motion compensation's memory reads at 720p (the chroma half of it
waits on its reference reads, #85), nothing above about a quarter on its own. `ConcealErrors` costs nothing measurable
on clean input: `mcdec --conceal` against without, 6 interleaved pairs each, +0.8% on one thread and +1.1% on four
on the 720p stream, -3.7% on `h264-high-b.mp4` with four, all within the pairs' spread. The libx264-default streams, furthest
behind FFmpeg (then 3.4 and 3.6 times its time, now 3.1), are mostly skipped and direct macroblocks with weighted prediction:
skipped macroblocks (their motion compensation included) take 20% there, and weighted prediction took 7%. It is now
in vectors for 8-bit samples (16-bit lanes from one list, 32-bit for two; the by-the-definition loop stays for deeper
samples and as the reference `VectorWeights_MatchReference` checks), 4% by the same timers. A perf profile of the
optimized code by function (CLAUDE.md, Timing) then ranked the rest of `h264-high-default.mp4`: the neighbouring
macroblock lookup (6%, two divisions a call; now a table made with the decoder), storing motion and re-checking the
uniform motion of direct macroblocks (4.6%, now 3%), and the deblocking filter's tC0 vector built for every edge (1%,
now a table). What is left there: CABAC residual decoding (about 11%, its engine already in locals), motion
compensation's full-sample copies (2 to 4%, one vector load and store a row: their reference reads) and pieces of 1
to 2% each. In MBAFF frames and field pictures Table 6-4's lookup (`AffNeighbor`) is 9.3% (`h264-mbaff-default`) and
7.1% (`jm-paff-b`) by perf. A table of each pair's neighbouring pairs, made with the decoder, took its division and
position arithmetic out and moved that by about a point, within the noise, and the wall clock not at all (both
against an A/A run, reverted): the cost is the number of calls, not each one. Caching each pair's neighbours'
availability instead would have to be redone when concealment fills a macroblock that pairs below it saw as missing.
Counted, the lookups are about 33 a macroblock in `h264-mbaff-default` (fewer than 8 for each luma A and B location,
the left column's and the top row's, the rest inside the macroblock or chroma, C, D and the deblocking filter's): a
cache of the current macroblock's luma A and B locations, filled on first use and keyed on its address, field flag and
slice, saved about 8 of them but cost more than it saved (the lookups' share rose from 9.6 to 11.6%, and from 7.0 to
8.4% in `jm-paff-b`; reverted). Left: the lookups' callers each asking for what they need in fewer calls.
What did move it (#177, 2026-10-28): splitting Table 6-4 into its two halves, finding the pairs around the macroblock
(A to D, their availability in the slice and whether each is a field pair) once a macroblock (`FindAffPairs`) and the
case analysis on (xN, yN) per lookup (`AffNeighbor(in AffPairs, ...)`), the static lookup kept as the reference that
`PairsFoundOnce_MatchTheLookup` compares it with: -5.4% of `h264-mbaff-default`'s instructions and -6.0% of
`jm-paff-b`'s by callgrind, -2.9% and -4.0% of their time by perf
(docs/decisions/2026-10-28-h264-neighbour-context.md). The per-lookup case analysis remains.

A profile of a
720p libx264 default stream (2026-10-22) is flat: CABAC residual decoding takes 14%, deblocking 21%
in pieces of at most 2.5% (bS, transposes, the filter), nothing else over 5%. (That profile counted the JIT's
unoptimized fallback under valgrind, CLAUDE.md, Timing.) Stage timers (2026-10-24, wall clock) put deblocking at
30% of `h264-bench-720p.mp4` and 22% of `h264-high-b.mp4`, three quarters of it the edge filters; 8-bit edges are now
filtered in registers (rows loaded whole or transposed in registers, bS and tC0 spread to the lanes by shuffles, no
gather buffer), about twice as fast an edge. The 16-bit path still gathers into a buffer. The bS derivation was most
of the rest (about 250 to 290 ns a macroblock on the 720p stream, 84% of whose macroblocks have uniform motion and no
coefficients); an edge between two such macroblocks now takes one motion comparison for its four segments, which
halves that (about 140 ns). Output planes come from a
pool and are allocated without zeroing. Two earlier cuts, timed afterwards (2026-10-24) by a Stopwatch
microbenchmark of the old and new code on the decoder's array layout, scaled by calls per frame: `StoreMotion`
writing a row and an 8x8 partition at a time takes about 45 ns a call instead of 115 (about 5,600 calls a frame on
the 720p libx264 default stream: about 3.5% of its frame), and skipping the bS of edges 1 and 3 in 8x8-transform
macroblocks saves about 500 ns where it applies (about 1% there, nothing on `h264-bench-720p.mp4`). Chroma motion
compensation is about 3% of that stream's time in the filter and about twice that with the reference reads (an
in-decoder timer; 80% of its blocks are 8x8, 45% whole-sample copies): each row is now filtered across once and
reused for the row below it, behind one bounds check a block, and the copies are unchecked too, 52 to 65 ns an 8x8
block down to 35 to 40 and 8x16 (4:2:2) about 1.5 times as fast (microbenchmark), about 0.5% of the stream. Looked
at and left: prefetching a partition's chroma rows while its luma is predicted (`Sse.Prefetch0`): the chroma
calls took about 95 ns instead of 116, but a timer around all of motion compensation, the prefetches included,
showed no gain; dropping the whole-picture clears in `Begin` (skipped, direct and intra macroblocks rely on the
zeros in `_absMvd` and the motion arrays, so each path would clear its own macroblock instead, about as many
bytes), one transpose for all four vertical luma edges (`FilterEdge` already skips the transposes of edges whose
bS are all 0, which a whole-macroblock transpose would not). Frames are now deblocked a macroblock row behind
decoding (no measurable change in time, so cache locality matters little with a large L2), so that a reference's
upper rows become final while the rest is still being decoded: frame threading (`Threads`, under Decoding a file)
builds on it. MBAFF frames and field pictures are deblocked and report their progress by pair rows (32 frame rows)
the same way, a field picture's per field: with 4 threads, libx264's MBAFF samples decode 1.8 to 2.2 times as fast
as with one (1.3 to 1.4 before). A frame's second field runs alongside its first, on a decoder of its own, once its
first field has begun (which resets both fields' state): before, it waited for its first to finish, and JM's field
samples decoded slower with threads (1.2 times one thread's time with 4; now 0.79). Each picture's thread copies its
frame out too (6% faster with 4 threads). A profile of a 10 s 720p P chain with 4 threads: each picture's decoding
takes about 28% longer than with one thread, which four single-threaded decodes at once do not show (so it is the
references written on other cores, not the machine), threads wait for references' rows about a fifth of their
time, and are idle (no picture to decode) about as long. More threads than cores fill some of that: 6 threads on
4 cores were 2 to 16% faster than 4. Output frames given back with `Dispose` are pooled
(gen2 collections over 5 loops of `h264-bench-720p.mp4`: 33 down to 3). Vector 4x4 and 8x8 inverse transforms
(#42) were reverted: callgrind counted 1.4% more instructions per loop of `h264-bench-720p.mp4` with them and 0.9%
more on `h264-high-b.mp4`, and 24 interleaved runs of the builds before and after showed no difference; the 9 and
10% first reported were the VM's noise. Those instruction counts and profile shares were of the JIT's unoptimized
fallback under valgrind (CLAUDE.md, Timing); by wall clock the conclusion holds: the vector transforms are 2.5 to
2.8 times faster per block (about 20 against 50 ns for a 4x4, 100 against 280 for an 8x8), but at 503 4x4 and 22
8x8 blocks a frame of `h264-high-b.mp4`, and 4625 and 11 of `h264-bench-720p.mp4`, that is about 1.5% of a frame,
below what interleaved runs resolve.

Against FFmpeg stage by stage (2026-10-30): perf profiles of mcdec and of an FFmpeg 6.1.1 built with only its H.264
decoder and its symbols (`--disable-stripping --enable-debug=2`), one thread, 30 loops of `h264-bench-720p.mp4`, each
decoder's functions put into the same stages. mcdec took about 2.5 times FFmpeg's time; by stage, as shares of each
decoder's own decoding time (the last column is mcdec's time over FFmpeg's for that stage):

| Stage | mcdec | FFmpeg | Time ratio |
|---|---|---|---|
| Macroblock layer (CAVLC parsing, motion vector prediction and storage) | 39% | 37% | 2.6 |
| Deblocking, bS included | 18% | 19% | 2.4 |
| Luma motion compensation | 20% | 23% | 2.1 |
| Chroma motion compensation | 9% | 6% | 3.5 |
| Inverse transforms and reconstruction | 10% | 7% | 3.3 |
| Clearing (memset) | 3% | 1% | about 10 |

(FFmpeg inlines its motion vector prediction into `ff_h264_decode_mb_cavlc`, so parsing and the macroblock's motion are
one row.) The gap is general: no stage holds a large part of it. The two above the average are chroma motion
compensation (Cb and Cr each repeat the block's setup, and the filter takes two multiplies a row where FFmpeg's
`pmaddubsw` takes one) and the transforms (32-bit coefficients where FFmpeg's 8-bit path uses 16). Each partition's
reference picture was a `Picture` reference stored in an array, a write barrier each (0.7 to 0.8% by perf): it is now
a byte, an index into a table of the pictures the current picture refers to, each slice keeping its reference lists'
indices, and a macroblock's four compare as one `uint` in the bS derivation; the barrier's share fell to about 0.13%
and nothing rose in its place (two pairs of back-to-back profiles), about 0.7% of the frame. Cb and Cr of a block
now share its setup (`PredictChromaPair`: position, fractions, bounds, the choice of window; a bi-predicted block's
two components side by side in each list's scratch block), and with SSSE3 each row is filtered across by one load,
a shuffle pairing every sample with its right-hand neighbour and one multiply-add (`pmaddubsw`), as FFmpeg's does;
without SSSE3 (ARM64) the two widened multiplies stay. Chroma motion compensation and the block copies took 12.5
to 13.2% of `h264-bench-720p.mp4` afterwards against 14.5 to 15.0% (two pairs of back-to-back profiles, the filter
itself 2.9% against 3.6 to 4.1%), about 1.5 to 2% of the frame; `h264-high-b.mp4`, where chroma is under 3%, did not
move. The 8-bit 4x4 inverse transform now runs both passes in 16-bit lanes with SSE2 (two rows or
columns a vector, transposed by unpacks, the rounding as 32 added to the DC, the clip the saturating pack), as
FFmpeg's does, for coefficients under 2^11, from which every sum fits 16 bits; larger ones take the 32-bit
transform: 8.5.12 bounds a conforming 8-bit stream's values to 2^15, but JM's encoder with scaling matrices exceeds it
(`jm-planes-qm` failed with 16 bits throughout). `Dequantize` multiplies four coefficients a vector. The transforms
took 4.4 to 4.5% of `h264-bench-720p.mp4` afterwards against 5.0 to 5.25% (the 4x4 add alone about 1.0 against
2.4%), about 0.7% of the frame; on `h264-high-b.mp4` within the runs' spread. The CAVLC residual reader (`ReadResidualBlock`, 15% of
that stream) spends its time evenly (perf's annotation of the jitted code: no instruction over 4%), much of it
reloading the bit reader's position, a field of the reader, after every store; it now reads through a cursor whose
data, length and position are locals (`BitReader.Cursor`), the position written back once a block: 15.4 against
16.0% on average over five pairs of back-to-back profiles, lower in every pair, about 0.6% of the frame. A P_Skip
macroblock's motion vector prediction read neighbours A and B twice, once for the skip test of 8.4.1.1 and again in
the prediction; it now hands them over: motion vector prediction and `DecodeSkip` took 4.1 against 4.8% (five pairs,
lower in each), about 0.6% of the frame. A per-macroblock cache of the neighbours' motion, as FFmpeg's
`fill_decode_caches`, would cost more to fill than it saves where 84% of macroblocks are one partition. The
macroblock layer's header fields (mb_skip_run, mb_type, coded_block_pattern, mb_qp_delta, ref_idx, mvd) are read
one or two a method, too few for a cursor to pay; `ReadUe` and `ReadSe` were calls, their throw and long-code branch
keeping the JIT from inlining them, and are now inlined with codes of 16 or more leading zeros out of line: the
parse functions with the bit reader took 5.5 against 6.15% (five pairs, lower in each), about 0.6% of the frame. The
8-bit 8x8 inverse transform runs in 16-bit lanes too, a row a vector, for coefficients under 2^9 (one pass of the
8-point transform multiplies the largest magnitude by at most 7.375, both by 54.4; 90 to 96% of the 8x8 blocks of
`h264-high-b`, `-default` and `-p` are under it, the rest take the 32-bit transform): the transforms took 2.7 against
3.1% of `h264-high-default.mp4` (four pairs, lower in each), about 0.4% of the frame. The first version, its eight
vectors in a stack span, gained half that: the vectors belong in locals the JIT keeps in registers. Timed together
against 0.2.4 on GitHub's runners (`compare.yml`, medians of 7 interleaved runs), these eight changes took 2.7% off
the 720p sample's steady frame on x64 and 0.8% on ARM64, the other samples within about 1.5% either way: the
per-change perf shares above, added up, promised about 4.5%. A share that falls in one function is not all saved
time (part moves to the memory system or the next function), and a cloud VM's profile is not a runner's.

## AAC

AAC LC: channel configurations 1 to 7 and program config elements (1 to 8
channels), all window sequences and shapes, M/S, intensity, PNS, TNS. Both decoders are floating
point, so the tests compare values: they require 2^-17 (about 7.6e-6) and measure at most 3e-7.
Pulse data, which FFmpeg's encoder never writes, comes from a stream written by its syntax (`aac-pulse`,
`tools/make-aac-pulse.py`: one to four pulses a frame, some on zero quantized values), decoded by FFmpeg as the
reference. M/S set in a band both
channels of a pair fill with noise (which FFmpeg's encoder never writes, the FDK encoder does: aac-fdk-pns-ms) is the
spec's correlated noise, the right channel reusing the left's random vector; FFmpeg gives the right channel noise of
its own, and so does this decoder, on purpose (correlated, the sample was up to 3e-2 off).
Priming before the edit list's start is trimmed (`Packet.SkipSamples`); like FFmpeg, the
padding in the last packet is not. FAAC signals its encoder delay nowhere but in its name, "libfaac" and a version as text in its first
frame's fill element: FFmpeg then drops the first 1024 samples, and `Decode(Packet)` does too, unless the container says
what to skip (aac-faac-delay: FFmpeg's encoder name rewritten to read as FAAC's, `tools/aac-encoder-name.py`; Blender's
Sintel trailer, from FAAC 1.26.1, matches FFmpeg once it is dropped).

A program config element (channel configuration 0) comes out in FFmpeg's order. When it places every channel on a
speaker (a front centre first and front pairs from the centre outward, as the spec lists them; a side pair; back pairs
from front to back and a back centre after them, as the FDK encoder writes them and FFmpeg reads them; an LFE), FFmpeg
names the layout and outputs WAVE order:
aac-fdk-pce (the FDK encoder's 5.1, as configuration 6) and aac-pce-front (two front pairs, the outer one first).
FFmpeg's own encoder lists the front pair before the centre and codes the LFE as a side channel, and FFmpeg outputs
those layouts as listed (aac-pce-8ch, aac-adts-pce). The rule was checked against FFmpeg on all 28 layouts its encoder
writes with `-aac_pce 1` (up to 16 channels) and on the FDK encoder's PCEs for 1 to 8 channels (configuration 0 forced in a copy of
its tree; the 7.1 rear-surround and front-centre modes write one anyway); `ProgramConfigTests` holds the shapes,
and the ones no encoder at hand writes, which keep the listed order.

## HE-AAC (spectral band replication)

Samples from the Fraunhofer FDK encoder, committed in `samples/heaac/` (`tools/make-heaac-samples.sh`): stereo at
44.1 kHz, mono at 48 kHz, a coupled low-bitrate pair at 32 kHz, 5.1, ADTS (SBR signalled only by its data), HE-AAC v2
(parametric stereo), downsampled SBR, both together, and a stream with the header's second part (the encoder writes it only with settings other than
its defaults, so a patched copy makes it: 3 limiter bands an octave, -3 dB limiter gains, no frequency interpolation,
gains smoothed over time; without it, the other samples use 2 bands, 0 dB, interpolation and no smoothing). The config's signalling, both
backward-compatible (a sync extension after the core's config) and hierarchical (object type 5 first, a unit test),
and SBR data in the fill element after each SCE and CPE (none after the LFE). Every frame of every sample is read to
its last bit (an SBR payload leaves only its padding to a whole byte; changing how one field is read breaks that on
5 of the 6 samples), with every frame class, coupling, added sinusoids and parametric stereo data occurring. The
frequency tables follow the spec's formulas; its start and stop bands match the PacketVideo decoder's precomputed
tables, and the tables derived from every header a 44.1 kHz stream can send hold together (a unit test). The FDK
encoder's headers use bs_freq_scale 2 and 3 (3 in the low-bitrate pair): scale 1, and the linear master table of 0, are
checked only for consistency.
The QMF banks (32-band analysis, 64-band synthesis) run on complex FFTs of 32 (the analysis's real input packed two
values a point; the synthesis as two DCT-IVs), each also compared with the DFT route of 64 and 128 points it replaced,
and on random input with the spec's sums (kept in the tests as `QmfAnalysisReference` and `QmfSynthesisReference`). Analysis followed by synthesis with
the upper 32 bands empty, SBR's output where it adds nothing, gives the core's signal at twice the rate: unit gain
across the core's band to the window's ripple (about 1e-3; the bank is nearly, not perfectly, reconstructing), a delay
of 578 output samples, and no fractional delay (unit tests; reading samples in the wrong order, the window's odd taps
or a kernel offset by one breaks them). The kernels' cos and sin are computed without Math.Cos and Math.Sin, whose
last bits differ between .NET and WebAssembly's runtime, which the HF generator's prediction can magnify.
The vector paths (the QMF FFTs and rotations, the HF generator's covariances and patch filter, the gains, noise and
sinusoids added to the high band, the parametric stereo all-pass links) do each lane's arithmetic in the scalar loop's
order, without fused multiply-adds, and add noise under a mask rather than as zero, so a negative zero stays: each is
compared bit for bit with its scalar loop on random input (`VectorFft_MatchesScalarExactly`,
`VectorRotations_MatchScalarExactly`, `SbrCovariances_MatchScalar`, `SbrFilterPatch_MatchesFormula`,
`SbrApplyGains_MatchesScalar`, `PsAllPassRow_MatchesScalar`), and each speed change left every sample's output the same
to the bit.

Decoded output (`SbrConformanceTests`) against FFmpeg's (name.sbr.f32: at the SBR rate; MP4 without the 2048 samples of
encoder delay its iTunSMPB tag gives, which the demuxer reads as FFmpeg's does when there is no edit list; mono in
stereo, as FFmpeg's decoder makes it, with both channels equal; this decoder keeps a mono stream without
parametric stereo mono, one channel and an MP4 track reporting one, by the owner's choice, and the test compares that
channel with FFmpeg's left one). heaac-edit.mp4 is the 44.1 kHz sample remuxed by
FFmpeg, whose muxer writes that delay as an edit list instead, in the SBR rate's timescale: an MP4 track whose config
signals SBR reports the SBR rate, as FFmpeg's does, so the edit's samples count the decoder's output (at the core's rate
half of them would be skipped). The 5.1 sample's LFE, which has no SBR data and so
goes through the QMF banks alone, agrees to 2e-7: the banks, the 578-sample delay and the 6 time slots the high band
lags the analysis by are right. The SBR channels agree to about 1e-6 in most frames but differ by up to about 5e-3 of
the signal's peak in some: FFmpeg's single precision, not a different formula. The HF generator's prediction solves a
2x2 system per subband that is nearly singular where the subband holds one sinusoid, as the samples' test tones make
it; computing the coefficients in single precision moves this decoder's output by as much, in the same frames (the
two differences' logarithms correlate 0.86 to 0.93 over the frames), and adding a little noise to the samples' tones
shrinks both. So the test bounds the largest difference at 1e-2 of the channel's peak and the mean one at 1e-4 (the
samples measure 1e-6 to 2e-5). Mutations show what that catches: 16 of 19 (smoothing weights reversed, noise added
under a sinusoid, the previous frame's transient ignored, unsmoothed chirp factors, sinusoids' alternating sign,
the noise table's step, no limiter, no boost, frequency interpolation inverted, the sinusoid band flag, sinusoids
starting an envelope late, balances not doubled, noise floor time deltas, the limiter's band merging, the second noise
floor) fail it; not caught: epsilon 1 in the gains (it matters only in near-silent bands), the prediction's 1e-6
relaxation, and dropping a last patch of fewer than 3 subbands (no sample's band layout leaves one). The samples are
checked to use what they are named for: filtering by the HF generator, cut gains, transient envelopes, sinusoids, and
in the header sample smoothing, no interpolation and its limiter settings.

Downsampled SBR (the config gives SBR the core's own rate, 44.1 kHz in the sample's sync extension) runs as for twice
that rate, its frequency tables too (the sample's band runs from QMF band 9 to 23 of the 88.2 kHz grid), and
synthesizes only the lower 32 subbands, at the core's rate: 1024 samples a frame. The FDK encoder chooses it only for
AAC ELD; a copy of the tree with its HE-AAC default changed writes it. With parametric stereo the encoder also refuses
one rate (`sbrEncoder_IsSingleRatePossible`); a copy with that check changed too writes `heaacv2-downsampled`, built
without assertions (its PS analysis fails one in `schur_div` at one rate). That analysis isn't made for it and moves
the tones (sidebands 43 Hz apart around each, as FDK's own decoder hears them too), but the stream is valid: PS on the
32-band synthesis agrees with FFmpeg to 4.2e-7 at most, 4e-8 on average. The 32-band synthesis bank is the full one's kernel at every
other n, exp(i pi/128 (k + 0.5)(4n - 255)), with every other window coefficient: after the analysis bank it gives the
signal back with a whole-sample delay of 289 and a gain of 1 within 6e-3 (`QmfTests`, which also check it against its
sums). Its kernel, a quarter of a sample off the full bank's, is no DCT-IV, so it keeps its DFT of 64 points where
the full bank now takes two DCT-IVs of 64 (each an FFT of 32; `QmfTests` compare that with the DFT of 128 it replaced,
to 1e-12 of the peak). Reading the kernel as exp(i pi/64 (k + 0.5)(2n - 127)) instead delays it a quarter of a sample more, and misses
FFmpeg's output by 2e-2 on average, 14 to 17% of the signal at 3 to 6 kHz, below the high band. The sample agrees with FFmpeg to 3e-4 at most,
6e-6 on average, with noise under its tones, as FFmpeg's single precision in the HF generator needs: without noise, the
11 kHz tone that fills much of this narrower band left 7e-5 on average, in the frames where computing the prediction
in single precision moves this decoder's output (log correlation 0.91); with a third as much, 4e-3 at most on x64 and
1e-2 against FFmpeg's ARM64 build, past the bound.

Parametric stereo (HE-AAC v2, `PsProcessor`) follows the spec's baseline decoder, and FFmpeg's for the phase
parameters: the hybrid analysis filters on the
three lowest QMF bands (six slots of look-ahead from the SBR low band), decorrelation by a fractional delay and three
all-pass links (plain delays above QMF band 23), transient ducking, mixing procedure Ra or Rb with the matrices ramped
linearly over each envelope, and the hybrid synthesis; the right channel gets a QMF synthesis bank of its own. A mono
stream with it decodes to stereo (`Channels` is 2, from the config's object type 29 or sync extension, or, signalled
only by its data, once the first access unit is decoded), and an MP4 track signalling it reports two channels, as
FFmpeg's does (the ADTS demuxer can't tell and reports one; no sample is implicitly signalled v2). The v2 sample agrees
with FFmpeg's output to 3e-4, 3e-6 on average, with left and right differing by up to 1 (the stereo image is really
made). FFmpeg's PS runs in single precision too, but nothing here magnifies it as the HF generator does, so the mean is
held to 2e-5: a decay slope of 0.06 instead of 0.05 gives 1e-4 (within the SBR bound), a link's fraction 0.36 instead
of 0.347 7e-4, no transient ducking 2e-4 (and 8e-2 at most). The cos and sin of data-dependent angles (`PsMath`, a
two-part reduction by pi/2 and a series, checked against Math on random input) and the scale factors and ICC angles
(literals, matching libxaac's float tables to 6e-8) keep the output the same on WebAssembly; the phase factors are
checked against libxaac's. 10-band parameters map to the 20 bins as band b / 2 (no sample sends 10 bands). Mixing
procedure Rb (icc_mode 3 to 5) comes from a patched FDK encoder that writes its own parameters with icc_mode + 3
(`heaacv2-rb`, tools/make-heaac-samples.sh; every frame mixed by Rb, `PsRbFrames`), and matches FFmpeg as the Ra sample
does once rho is taken as at least 0.05, as FFmpeg takes it (without, up to 0.3 off; whether the spec says so is not
checked here, its text being out of reach). The phase parameters (IPD and OPD, ps_extension 0) come from another
patched FDK encoder (`heaacv2-ipd`): its writer for them, never enabled, sends phases of its own (non-decreasing over
the bands, moving from frame to frame), once three of its bugs are fixed (it writes the IID's 10 or 20 bands where the
phases have 5 or 11, leaves the enable bit out of the extension's size, and writes the extension only under a PS
header). They are applied as FFmpeg applies them (the spec's text out of reach): each of the lowest 11 bands' OPD and
IPD smoothed over the last three envelopes (unit vectors weighted 1/4, 1/2 and 1, normalized), h11 and h21 turned by
the OPD and h12 and h22 by the OPD less the IPD, the imaginary parts ramped as the real ones, from the previous
envelope's negated in the two sub-subbands of negative frequency (FFmpeg negates the start, not the end), and the band
count kept from the last header that enabled IID (FFmpeg's; the encoder's first phases come under a header without
IID, where FFmpeg reads none, `PsTests`). The sample agrees to 5e-4, 7e-6 on average; ignoring the phases, it is off
by its whole peak (0.6), and each of those rules moves it (`tools/mutation/mutations.py`). The codebooks are the FDK
encoder's code tables, which give FFmpeg's code lengths in canonical order.

34 bands (iid_mode or icc_mode 2 or 5) come from a third patched FDK encoder (`heaacv2-34`): it analyses 20 bands and
turns modes 2 and 5 into 10, so a copy widens its band arrays, writes 34 bands for those modes and maps each envelope's
20 values up by the decoder's 20-to-34 map, its header cycling every 8 frames through IID and ICC in (20, 20), (34, 34),
(34, 20) and (20, 34) bands; `heaacv2-34-ipd` adds the phases (17 bands in 34, 11 mapped to 17 where only ICC is in 34).
They are processed as FFmpeg processes them: a frame in 34 bands when either parameter is in 34 (the other mapped up:
10 to 34 by repeating, 20 to 34 by repeating with two bands averaged, rounded towards zero, `PsTests`), the five lowest
QMF bands split by a 12-band, an 8-band (a prototype of its own) and three 4-band complex filters, 50 groups over 34
parameter bands, the all-pass decay from QMF band 5; and a switch between 20 and 34 clearing the decorrelator (its
transient detector and every delay line), moving the last matrices to the new bands (20 to 34 repeated and averaged, 34
to 20 averaged) and clearing the phases' history. Two FFmpeg behaviours only the phases show: in 34 bands the phases'
imaginary parts start negated in groups 9 to 13 (libxaac negates 9 to 11, 5e-3 off on average), and above the phase
bands FFmpeg interpolates towards whatever its per-envelope slot last held (in 20 bands after 34, bands 11 to 16's
34-band values; zeroing them was up to 0.2 off); FFmpeg starts each frame from the slot the last frame ended on, which
equals the last envelope here unless a frame without phases changes the envelope count (no sample does). Both samples
agree to 3e-4, 5e-6 on average, with FFmpeg's 48 frames in 34 bands and 4 switches (`Ps34Frames`,
`PsLayoutSwitches`). The 34-band prototypes, groups, parameter bands and the phase factors of the centre frequencies
equal libxaac's tables (Apache-2.0). Its data is read (`PsData`): every
frame of the v2 sample to the end of its extension, less than a byte of padding left (the FDK encoder sends 20 bands,
fine IID quantization, ICC in 20 bands, no IPD or OPD; reading the envelopes' IID and ICC interleaved instead of IID
first fails it on the third frame). Its codebooks are enumerated from PacketVideo's decoding trees and checked to be
complete prefix codes. Time deltas across a change between 10 and 20 bands take the previous values at the other
resolution as PacketVideo does (no sample changes resolution; the phases the same between 5 and 11 bands, the 11th
from 0), and to or from 34 bands take band b as it was, as FFmpeg does (the 34-band samples send frequency deltas across
a change); extensions other than 0 are skipped.
## ADTS (raw AAC)

Raw AAC files (ADTS, `AdtsDemuxer`) demux packet by packet as FFmpeg's
demuxer does (whole frames, header included; ID3v2 tags skipped, junk between frames skipped as FFmpeg
resynchronizes) and decode as FFmpeg decodes them; frames with a CRC (which FFmpeg's muxer never writes) from
rewritten samples. Channel configuration 0: the AudioSpecificConfig is the stream's header fields and the program config
element that starts the first frame (FFmpeg writes it there only, the FDK encoder in every tenth frame), its byte
alignment redone at its new place, byte for byte what FFmpeg's ADTS-to-MP4 filter writes (aac-adts-pce, whose element
carries a comment, and aac-fdk-pce, both remuxed); a stream whose first frame has none is refused. Frames of several
raw data blocks (up to four; FFmpeg's muxer and the FDK encoder write one, FFmpeg's demuxer refuses more) are one packet
of 1024 samples a block, decoded block after block into one frame; with CRCs, the header's comes after the positions of
the blocks but the first, and each block has its own. No encoder at hand writes them, so the tests join a sample's
frames, 1 to 4 under one header, with and without CRCs, AAC LC and HE-AAC (the SBR state runs on across blocks), and
require the same samples as the frames one by one.
