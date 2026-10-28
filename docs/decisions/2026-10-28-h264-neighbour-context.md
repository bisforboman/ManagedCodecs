# H.264: a neighbour context a macroblock (2026-10-28)

A design, decided (below), no code yet. It follows `docs/notes/h264-speed-gap-2026-10.md`, whose second
candidate this is: the largest share of the decoder's time still left in one mechanism, and a change large enough to
be designed before it is written.

## The problem

Every syntax element and prediction that depends on a neighbouring 4x4 block finds it through the generic lookup of
6.4.12: `Neighbor(mbAddr, xN, yN)` (in MBAFF frames and field pictures `AffNeighbor`, Table 6-4), then reads the
picture-wide arrays (`_totalCoeff`, `_mvX`/`_mvY`, `_refIdx`, `_absMvd`, `_kind`, `_cbp`, `_dcCoded`, `_intra4x4Mode`)
at the address it returns. Each caller does that for itself, block by block: nC for each CAVLC block (two lookups),
motion vector prediction for each partition (three or four through `MotionNeighbor`), each CABAC ctxIdxInc
(coded_block_flag per block, coded_block_pattern per 8x8 block, mvd per partition and component, ref_idx, mb_type,
mb_skip_flag, transform_size_8x8_flag), the spatial direct mode, intra 4x4 mode prediction, intra availability.
The same few neighbours are found again and again, and in MBAFF frames each lookup is Table 6-4's case analysis.

Measured on main after #171 (callgrind on the Native AOT build, steady state; perf on the JIT build, the functions'
whole self time, so upper bounds):

| Stream | `Neighbor` calls a frame | `MotionNeighbor` calls | Context-related share of the time (perf) |
| --- | ---: | ---: | --- |
| `h264-bench-720p.mp4` (CAVLC, P) | 30.8 k (8.6 a macroblock) | 14.3 k | 7.8%: MV prediction 3.9, nC 1.9, motion storage 1.8 |
| 720p libx264 default (CABAC, B) | 69.4 k (19 a macroblock) | 24.0 k | 16.6%: CABAC ctxIdxInc 8.0, MV prediction 4.6, motion storage 2.5, direct 0.8 |
| `h264-high-default.mp4` (320x240) | not counted | not counted | 15.4%: CABAC ctxIdxInc 7.5, MV prediction 3.7, motion storage 1.9, direct 1.1 |
| `h264-mbaff-default.mp4` | not counted | not counted | 21.8%: Table 6-4 lookups 10.2, CABAC ctxIdxInc 5.7, MV prediction 2.7 |

By instructions the lookups themselves (`Neighbor`'s own, `MotionNeighbor`'s own, the ctxIdxInc conditions') are
about 3.9 M of the CAVLC stream's 50.3 M a frame (8%) and about 8 M of the CABAC stream's 71.4 M (11%). FFmpeg fills
a per-macroblock cache once (its `fill_decode_caches` and `fill_decode_neighbors`, 0.8 M instructions a 720p frame
together) and reads it at fixed offsets everywhere else. (The idea is the spec's own: 6.4.11 derives the same A and B
for every block of a macroblock; nothing of FFmpeg's code is used, CLAUDE.md, Core idea.)

## The design

A `NeighbourContext` a `PictureDecoder` (one macroblock at a time; each frame-threading worker has its own decoder),
filled when a macroblock starts (after mb_field_decoding_flag is known in MBAFF frames), and read instead of the
lookups:

- **Neighbouring macroblocks.** A, B, C and D (6.4.11.1) with their availability (decoded, in this slice; the
  deblocking filter's any-slice case keeps its own path). In MBAFF frames and field pictures the left neighbour can be
  two macroblocks (a frame macroblock next to a field pair and the other way round, Table 6-4): the fill resolves
  each of the current macroblock's four luma block rows (and its chroma rows) to (macroblock, row in it) once.
- **Block grids with a border.** For each per-4x4-block quantity, a 6 x 5 grid of the current macroblock's 4 x 4
  blocks with the left neighbour column, the upper neighbour row, and C's first column (index `(by + 1) * 6 + bx + 1`).
  The fill copies the neighbours' edge blocks in and marks unavailable entries. Quantities: luma TotalCoeff (each
  component in 4:4:4), chroma TotalCoeff (a grid of the chroma block layout, 4:2:0 and 4:2:2), mvX and mvY and refIdx
  per list (refIdx already in the current macroblock's terms: MBAFF's halved or doubled index and vertical motion,
  8.4.1.3.1, applied once at the fill), absMvd per list and component, intra 4x4/8x8 modes.
- **Macroblock-level flags of A and B.** Kind (skip, intra, I_PCM), coded_block_pattern, DC coded flags, intra chroma
  prediction mode, transform_size_8x8_flag, field flag: what the ctxIdxInc derivations of 9.3.3.1.1 read.
- **The current macroblock writes as it decodes.** Its partitions' motion and references, its blocks' TotalCoeff and
  absMvd go into the grids as they are decoded (a later partition's MV prediction reads an earlier one's there), and
  into the picture-wide arrays as now, which stay the store for later macroblocks, the deblocking filter, co-located
  motion of later pictures and concealment.

What stays as it is: the deblocking filter (it reads the picture-wide arrays and its file has another session's
change pending; it can read a context of its own later), co-located motion (other pictures), concealment (it fills
the picture-wide arrays of macroblocks no slice decoded; a context filled from those arrays sees them as it sees any
macroblock).

Why this is not the cache that was tried and reverted (docs/conformance.md, Performance notes): that one kept the
lookups and cached each macroblock's A and B locations on first use, a cost added to every call; this one replaces
the lookups, with one fill a macroblock and plain reads after. And it is filled from the picture-wide arrays at each
macroblock's start, so nothing in it can go stale across macroblocks (the concern about concealment filling a
macroblock that pairs below it saw as missing does not arise).

## Decisions

Asked with the multiple-choice prompt; the owner took each proposal (2026-10-28).

1. **Layout. Decided:** the bordered 6 x 5 grids above, one array a quantity in the decoder, reused for every
   macroblock. The alternative (small per-neighbour structs: A's right column, B's bottom row) reads fewer bytes but
   needs a branch on "inside the macroblock or not" at every read, which the grid avoids.
2. **Checking it. Decided:** keep today's derivations as the reference (CLAUDE.md, Core idea: a fast path keeps a
   by-the-definition one). A test-only switch (`VerifyNeighbourContext`) makes the decoder compute every nC, motion
   vector predictor and ctxIdxInc both ways and throw on a difference; one conformance theory runs every sample with it
   (progressive, MBAFF, field pictures, slices, constrained intra prediction, 4:2:2, 4:4:4, monochrome, concealment
   tests). Bit-exactness against FFmpeg stays the final check, as for everything.
3. **Scope. Decided:** every chroma format and bit depth from the first PR (the context holds no samples, so
   `TPixel` does not enter); progressive frames first, MBAFF frames and field pictures in a PR of their own (their fill
   is Table 6-4 and the larger part of the risk), the deblocking filter later.
4. **Order. Decided:** the four pull requests below, each measured as #165 to #171 were (callgrind on the AOT build
   for instructions; perf with the order alternating, normalized by untouched functions, for time), each merged before
   the next starts.

## Work, one pull request each

1. **The context for progressive frames: neighbours, nC, motion vector prediction.** `NeighbourContext`, its fill,
   CAVLC nC and `PredictMv`/`MotionNeighbor` and the spatial direct mode's neighbours from it, intra 4x4 mode
   prediction and intra availability; the verification switch and its theory. MBAFF frames and field pictures keep
   the lookups (the fill is skipped there). Done when every sample decodes bit-exact with the switch on, and the
   720p CAVLC stream's `Neighbor` and `MotionNeighbor` calls are gone. Expected: most of MV prediction's 3.9% and
   nC's 1.9% on that stream, less the fill: about 3 to 4% of its time.
2. **CABAC's ctxIdxInc from the context.** coded_block_flag, coded_block_pattern, mvd (absMvd), ref_idx, mb_type,
   mb_skip_flag, transform_size_8x8_flag, intra_chroma_pred_mode. Done as above, on the CABAC samples. Expected: the
   larger part of the 7.5 to 8% CABAC ctxIdxInc group (its functions also decode the bins, which stay): about 4 to 5%
   of a CABAC stream's time.
3. **MBAFF frames and field pictures.** The pair-aware fill: Table 6-4 once a macroblock per neighbour row instead of
   about 33 times (docs/conformance.md counted them), the frame/field motion scaling at the fill. Done when the MBAFF
   and PAFF samples (libx264's and JM's) decode bit-exact with the switch on. Expected: most of the 10.2% Table 6-4
   share of `h264-mbaff-default.mp4`, about 6 to 8%.
4. **The deblocking filter's bS from a context** (after the other session's change to `PictureDecoder.Deblock.cs` has
   merged): edge strengths from the grids instead of the picture-wide arrays. Measured first; it may not pay, since the
   filter already compares motion a macroblock edge at a time.

Ceilings, by CLAUDE.md's rule that a gain larger than the changed functions' share is noise: 7.8% (CAVLC 720p),
16.6% and 15.4% (CABAC), 21.8% (MBAFF), the shares above. The estimates (3 to 4%, then 4 to 5% more on CABAC streams,
6 to 8% on MBAFF) assume the fill costs about what FFmpeg's does relative to the decode, and that the reads become
plain array reads at fixed offsets.

## Risks

- **Table 6-4.** The MBAFF fill is where a subtle mistake would hide; the verification switch compares every
  derived value on every MBAFF and PAFF sample, which today's tests cannot (they compare only the output).
- **Constrained intra prediction and I_PCM.** Their availability and nC rules (an inter neighbour unavailable for
  intra prediction; I_PCM's nC of 16; P_Skip's 0) are marked at the fill, once; the samples that exercise them
  (`h264-constrained-intra`, JM's I_PCM samples) are in the verification theory.
- **Slices.** Availability is per slice; the multi-slice samples and the concealment tests (slices lost) cover it.
- **Frame threading.** None of this reads other pictures, so no wait changes and no mutation entries; the delayed-
  thread tests run as before.
