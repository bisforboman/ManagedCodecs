# Conformance notes

What each decoder and demuxer is checked against, which encoder produced the samples for each feature, and what
is not checked, feature by feature. The README's Status section has the summary and the list of what is still open.

## MJPEG

Baseline (and extended Huffman) JPEG, bit-exact against FFmpeg: 4:2:0, 4:2:2, 4:4:4, gray, restart markers, missing
DHT. Interlaced AVI1 frames too (two JPEG fields per packet, recognized as FFmpeg does by being less than 3/4 of the
stream's height, so the decoder needs `new MjpegDecoder(video)`), woven with the first field on the odd rows, the
order FFmpeg takes for AVI, which does not tell it (`FirstFieldOnTop` gives the other; the AVI1 segment's field
byte is ignored, as by FFmpeg). Packets of one field are not decoded (FFmpeg decodes none either).

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

ISO BMFF (`moov`, `trak`, `stbl` sample tables): packets, timestamps and
codec private data match FFmpeg, for video and audio tracks, fragmented files (moof) included. Audio is trimmed by the edit list as FFmpeg trims it: priming
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
  end of the edits, longer than 1024 if need be.
- An empty edit after a real one plays the media from its start for its duration, FFmpeg reading its media time, -1,
  as a time like any other (the spec has it as a gap).

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
PicNum) and the field B lists' list 1 swap have unit tests; other memory management operations
in field pictures and a field without its pair throw NotSupportedException.

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
and 10 bits; I_PCM in 4:4:4 from hand-built streams. Separate colour planes are rejected. x264 before
build 151 derived the context of an 8x8 block's coded_block_flag in 4:4:4 against the spec (a
neighbour without 8x8 transforms counted as missing); FFmpeg decodes such streams by the encoder's
version, this decoder by the spec, so they come out differently.

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

Monochrome (chroma_format_idc 0) comes out as `Gray8` or `Gray10`, one plane; it is checked on
libx264's gray output (High and High 10): no intra_chroma_pred_mode, the coded_block_pattern
without a chroma part, no chroma weights in pred_weight_table, with CABAC and CAVLC, P and B
frames, explicit weights, cropping, MBAFF, 10 bits and lossless; and on JM's field pictures
(PAFF) in 4:0:0, alone and mixed with frame pictures, with B pictures and CAVLC; I_PCM from
hand-built streams. FFmpeg's decoder outputs such streams as 4:2:0 with flat chroma, so the
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

### Performance notes

A profile of a
720p libx264 default stream (2026-10-22) is flat: CABAC residual decoding takes 14%, deblocking 21%
in pieces of at most 2.5% (bS, transposes, the filter), nothing else over 5%. Output planes are allocated
without zeroing (callgrind: 0.4% fewer instructions on `h264-high-b.mp4`, 2.6% on `testsrc-420.avi`). Looked
at and left: dropping the whole-picture clears in `Begin` (skipped, direct and intra macroblocks rely on the
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

## AAC

AAC LC: channel configurations 1 to 7 and program config elements (1 to 8
channels), all window sequences and shapes, M/S, intensity, PNS, TNS. Both decoders are floating
point, so the tests compare values: they require 2^-17 (about 7.6e-6) and measure at most 3e-7.
Implemented but never produced by FFmpeg's encoder, so untested against it: pulse data and
correlated noise. Priming before the edit list's start is trimmed (`Packet.SkipSamples`); like FFmpeg, the
padding in the last packet is not.

## HE-AAC (spectral band replication)

Samples from the Fraunhofer FDK encoder, committed in `samples/heaac/` (`tools/make-heaac-samples.sh`): stereo at
44.1 kHz, mono at 48 kHz, a coupled low-bitrate pair at 32 kHz, 5.1, ADTS (SBR signalled only by its data), HE-AAC v2
(parametric stereo), downsampled SBR, and a stream with the header's second part (the encoder writes it only with settings other than
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
The QMF banks (32-band analysis, 64-band synthesis) run as 64- and 128-point FFTs, compared on random input with the
spec's sums (kept in the tests as `QmfAnalysisReference` and `QmfSynthesisReference`). Analysis followed by synthesis with
the upper 32 bands empty, SBR's output where it adds nothing, gives the core's signal at twice the rate: unit gain
across the core's band to the window's ripple (about 1e-3; the bank is nearly, not perfectly, reconstructing), a delay
of 578 output samples, and no fractional delay (unit tests; reading samples in the wrong order, the window's odd taps
or a kernel offset by one breaks them). The kernels' cos and sin are computed without Math.Cos and Math.Sin, whose
last bits differ between .NET and WebAssembly's runtime, which the HF generator's prediction can magnify.

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
AAC ELD; a copy of the tree with its HE-AAC default changed writes it, and with parametric stereo it stays dual-rate, so
no sample has both (the code path is the same; untested). The 32-band synthesis bank is the full one's kernel at every
other n, exp(i pi/128 (k + 0.5)(4n - 255)), with every other window coefficient: after the analysis bank it gives the
signal back with a whole-sample delay of 289 and a gain of 1 within 6e-3 (`QmfTests`, which also check it against its
sums). Reading the kernel as exp(i pi/64 (k + 0.5)(2n - 127)) instead delays it a quarter of a sample more, and misses
FFmpeg's output by 2e-2 on average, 14 to 17% of the signal at 3 to 6 kHz, below the high band. The sample agrees with FFmpeg to 3e-4 at most,
6e-6 on average, with noise under its tones, as FFmpeg's single precision in the HF generator needs: without noise, the
11 kHz tone that fills much of this narrower band left 7e-5 on average, in the frames where computing the prediction
in single precision moves this decoder's output (log correlation 0.91); with a third as much, 4e-3 at most on x64 and
1e-2 against FFmpeg's ARM64 build, past the bound.

Parametric stereo (HE-AAC v2, `PsProcessor`) follows the spec's baseline decoder: the hybrid analysis filters on the
three lowest QMF bands (six slots of look-ahead from the SBR low band), decorrelation by a fractional delay and three
all-pass links (plain delays above QMF band 23), transient ducking, mixing procedure Ra with the matrices ramped
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
checked against libxaac's. 10-band parameters map to the 20 bins as band b / 2 (no sample sends 10 bands); 34 bands
and procedure Rb (icc_mode 3 to 5) throw NotSupportedException, and the phase parameters (IPD, OPD) are ignored, as
the baseline decoder does. Its data is read (`PsData`): every
frame of the v2 sample to the end of its extension, less than a byte of padding left (the FDK encoder sends 20 bands,
fine IID quantization, ICC in 20 bands, no IPD or OPD; reading the envelopes' IID and ICC interleaved instead of IID
first fails it on the third frame). Its codebooks are enumerated from PacketVideo's decoding trees and checked to be
complete prefix codes. Time deltas across a change between 10 and 20 bands take the previous values at the other
resolution as PacketVideo does (no sample changes resolution); 34 bands are refused, and the extension (IPD, OPD) is
skipped, as the spec's baseline decoder skips it.
## ADTS (raw AAC)

Raw AAC files (ADTS, `AdtsDemuxer`) demux packet by packet as FFmpeg's
demuxer does (whole frames, header included; ID3v2 tags skipped, junk between frames skipped as FFmpeg
resynchronizes) and decode as FFmpeg decodes them; frames with a CRC (which FFmpeg's muxer never writes) from
rewritten samples. Not supported there: several raw data blocks in a frame, channel configuration 0.
