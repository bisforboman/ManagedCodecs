# H.264: notes for working on the H.264 decoder, its samples (libx264, OpenH264, JM) and their scripts

The repository's CLAUDE.md (process, core idea, cross-cutting gotchas) applies too.

## Samples: which encoder writes what

libx264 never writes ref list modification, I_PCM, B partitions below 8x8, MMCO other than 1 (which needs b-pyramid),
explicit weights in B slices, cabac_init_idc other than 0, a second_chroma_qp_index_offset other than the first,
scaling lists in the SPS or a Cr list of its own. A second encoder covers some: OpenH264
(`tools/make-openh264-samples.sh`, its streams committed in `samples/openh264/`) writes long-term references, MMCO 4
and 6, POC type 2, SVC, and frame_num gaps once temporal layers are dropped. A third, JM (`tools/make-jm-samples.sh`,
committed in `samples/jm/`), writes field pictures (PAFF), alone or mixed with frame pictures or MBAFF frames, also in
4:2:2 and 4:0:0, bit depths 9, 12 and 14, I_PCM (heavy noise, QP 14 to 15), cabac_init_idc 1 and 2
(`ContextInitMethod=0 FixedModelNumber=N`), Cb and Cr QP offsets of their own (`CbQPOffset`, `CrQPOffset`), B
partitions below 8x8 (by default) and scaling lists in the SPS, PPS or both (`QmatrixFile`,
`ScalingMatrixPresentFlag`, `ScalingListPresentFlagN`); `tools/h264-add-weights.py` weights its field pictures
afterwards, `tools/h264-add-mmco5.py` puts memory management operation 5 into a field picture; separate colour
planes, which FFmpeg neither decodes nor muxes, go to `samples/jm-ref/` with JM's decoder's references, muxed by
`tools/mux-h264.py` (JM handles them in frames only, crashing on fields or MBAFF: interlaced ones are three 4:0:0
encodes spliced by `tools/h264-splice-planes.py`, referenced by FFmpeg's gray decodes stacked)
(`tools/make-jm-samples.sh <JM tree> <names>` makes only those samples).
`tests/ManagedCodecs.Tests/H264/ParameterSetRewriter.cs` rewrites a sample's SPS and PPS for tests of what no encoder
writes, and `tests/ManagedCodecs.Tests/H264/CabacEncoder.cs` (a CABAC encoder and bit writer) builds such streams.

## Gotchas (each cost time once)

- Comparing CABAC with a reference decoder: OpenH264 builds in scratch (`make USE_ASM=No h264dec`), and a trace of
  every bin as (state, MPS, value) in both decoders, diffed, shows the first syntax element that differs.
- FFmpeg (6.1.1 and master, h264_refs.c MMCO_LONG) loses a long-term field pair's first field when the second field
  takes the same LongTermFrameIdx with MMCO 6 (`remove_long(h, j, 0)` clears both fields' reference bits, then only
  the current field's are set): it is no oracle for long-term marking in field pictures ("Missing reference picture"
  is the sign); JM's decoder is (`jm-paff-ltr*`, `samples/jm-ref/`, from a copy of JM's encoder patched to write it).
- libx264 (core 164) fails to parse `cqm8iy`/`cqm8py` in `-x264-params` ("Error parsing option", and encodes on
  with flat 8x8 lists); give scaling lists in a `cqmfile` instead, and check what was really sent.
- OpenH264's `h264enc` ignores `-cabac 1` in its default Baseline layer config (add `-dprofile 0 77`), takes
  bitrates in kbit/s, and skips frames under a tight bitrate without leaving frame_num gaps.
- MBAFF: with CABAC, end_of_slice_flag comes only after the bottom macroblock of a pair (7.3.4). A skipped top
  macroblock's pair kind comes with the bottom one (CAVLC: the bit after mb_skip_run; CABAC: after the bottom one's
  skip flag), else it is inferred from the left, then the upper pair in the same slice: mark the macroblock as in
  the slice before inferring. Frame-to-field vertical motion halves with truncation towards zero (`/ 2`, not `>> 1`).
  libx264 with `constrained-intra=1` and interlacing writes intra modes FFmpeg and JM reject: not a test source. `ffmpeg -debug
  mb_type` marks field pairs with '=': the oracle for whether a sample mixes field and frame pairs (scrolling
  content comes out all field). The ITU PDF is not reachable from the cloud sandbox; JM (the ITU reference software,
  on GitHub) is, builds with `make -C ldecod CFLAGS="-fcommon -O2"`, and traces every syntax element; printing each
  CABAC bin's (state, MPS, range) in both decoders finds the first divergence.
- Field pictures in MP4 are one packet a field: FFmpeg's `-f rawvideo` output from such a file repeats frames to keep
  the packet rate (compare with `-f framemd5` or add `-fps_mode passthrough`); its frame takes the first field's pts.
  JM's encoder: `make -C lencod CFLAGS="-fcommon -O2"`, `PicInterlace=1` (fields) or `2` (adaptive), about a minute
  for 10 frames at 320x256; it crashes with `WeightedPrediction=1` or `WeightedBiprediction=2` and field pictures,
  with `SetFirstAsLongTerm=1` and field pictures (operation 3 for the second field where 6 belongs: `ltr-fields`
  patches a copy; MBAFF frames take it, and `PicInterlace=2` with the IDR picture coded
  as a frame: `IsLongTerm` is read only by temporal direct and implicit weights, so a long-term sample needs B
  pictures),
  and refuses `PicInterlace=2` with `MbInterlace=2` (`MbInterlace=1` works, every pair a field pair, with
  `ReferenceReorder=0 PocMemoryManagement=0`); in 4:0:0 it exits 100 after writing the stream. In field pictures
  without num_ref_idx_active_override_flag the active count is the PPS default as it is (FFmpeg and JM; not
  doubled). With `PicInterlace=2` it codes a clean testsrc2 source's frames
  all as frames in 4:2:2 (`YUVFormat=2`): check `field_pic_flag` in `trace_headers`, and add noise. Remuxing a raw stream with B pictures gives pts = dts in
  decoding order (FFmpeg then drops frames): `make-jm-samples.sh` sets pts from pic_order_cnt_lsb with `setts`.
- H.264 sample code is generic over the sample type (`PictureDecoder<TPixel>`, byte for 8-bit, ushort deeper): the
  byte SIMD paths are reached through `typeof(TPixel) == typeof(byte)` branches, which the JIT folds per
  instantiation, and clip bounds come from `Samples.Max<TPixel>` (a constant for byte; a runtime `(1 << bitDepth) - 1`
  cost intra-heavy streams about 5%).
- FFmpeg decodes monochrome H.264 to 4:2:0 with flat chroma (128, or 512 at 10 bits): references for gray samples
  come from `-vf extractplanes=y` (make-samples.sh, by `gray` in the name), the decoder's output is one plane. Not
  `-pix_fmt gray`: it rescales a limited-range stream (JM's, no VUI) to full range; libx264's gray is full range.
- Deeper than 10 bits: JM codes macroblocks as I_PCM below about QP -6 at 14 bits (so its streams never reach CAVLC's
  level_prefix 16), segfaults with `EnableIPCM=0`, and writes 4:0:0 with 8-bit chroma, which FFmpeg refuses ("Different
  chroma and luma bit depth"; it cannot even mux the stream): `tools/h264-chroma-depth.py` sets the unused chroma depth
  to the luma's, and FFmpeg then decodes 4:0:0 at 12 and 14 bits. 16-bit SIMD lanes hold sums of eight 12-bit samples, not 14-bit ones.
- Counting macroblock kinds (I_PCM, sub-8x8 partitions) per picture: FFmpeg's `-debug mb_type` grids come out after
  later frames' headers, so they can't be matched to picture types; count in the decoder with a temporary counter.
- H.264's `BitReader` reads zero bits past the end of a NAL unit, so a CABAC slice cut short decodes on through zeros
  to the end of the picture without a syntax error; only the engine's consumed position (`Cabac.PastEnd`) shows it.
  Damage for concealment tests: cut a NAL unit (always detected), not byte flips (often still parse).
- Frame threading (`H264Decoder.Threads`): a picture's worker may read only what an earlier picture has reported
  done (`Picture.WaitForRow`, `WaitForMotion`), and never marking state (the front end marks later pictures
  meanwhile; read `MarkSnapshot`). Rows (pair rows of MBAFF frames and field pictures, a field picture's per field)
  are final one row after they are deblocked (the next row's top edge filters
  their bottom). A race passes the threaded conformance tests by luck unless the random delays (`RowDelay`: before
  each picture and after each row; `MatchesFfmpegWithDelayedThreads`) make pictures overtake, and with them motion
  compensation checks that each reference row it read is final (`CheckRowsFinal`, "Read reference row N before it was
  final"), not only that the frame came out right: check a new wait by
  breaking it and seeing that test fail, and list it in `tools/mutation/mutations.py` (two waits are exceptions,
  covered in practice by the motion compensation waits: the co-located motion wait and ListsDamaged's; that file says
  why). A frame's second field runs on a decoder of its own alongside its first: it waits for
  the first's `Begin` (which resets both fields' slots), and its frame is copied out only once the first is done.
- Measuring frame threading: perf samples only threads that run, so blocked time is invisible to it; time the waits
  (a Stopwatch around `Picture.Wait`, logging waiter, reference, row and duration, in a scratch copy) and the front
  end's wait for the oldest picture. mcdec's loops each start a fresh decoder on an IDR picture, whose followers
  trail its slower intra rows: a third of the 720p sample's waits are that.
