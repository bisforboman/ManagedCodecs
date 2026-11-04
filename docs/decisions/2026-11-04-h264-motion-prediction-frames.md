# H.264: motion vector prediction in frames without the generic lookup (2026-11-04)

**Proposed, not decided.** A design for the owner to decide (Decisions, below), before any code. It follows
`docs/notes/h264-speed-gap-2026-10.md` and the closed record `2026-10-28-h264-neighbour-context.md`, and it is meant
to be different from what that record tried and dropped.

## The problem

After #312 and #313, motion bookkeeping is the largest piece of a CABAC frame not yet worked on. Counted on the 720p
libx264 default stream (CABAC, B; callgrind on the Native AOT build, loops 3 minus loops 1, main at 3f8b0d4):

| Function | Instructions a frame | Share | Calls a frame | A call |
| --- | ---: | ---: | ---: | ---: |
| `MotionNeighbor` | 2.87 M | 4.8% | 23,381 | 123 |
| `StoreMotion` | 1.66 M | 2.8% | – | – |
| `PredictMv` (the overload not inlined) | 0.51 M | 0.8% | – | – |

Inside `MotionNeighbor`, about 57 instructions a call are the generic neighbour lookup (`Neighbor`, inlined): which of
A to D holds (xN, yN), whether that macroblock exists, and whether it is in the current slice (two bounds-checked loads
and a compare). The other 66 are the block's coordinates, three bounds-checked reads (`_refIdx`, `_mvX`, `_mvY`),
the MBAFF check, and building the returned `Motion`. 95% of the calls (22,306 of 23,381) cross a macroblock edge,
which is why reading neighbours inside the macroblock directly moved nothing (the notes, "Motion bookkeeping: not
kept").

The lookup's first half is the same for every call of a macroblock: A, B, C and D and whether each is available. A
macroblock's partitions call `MotionNeighbor` 3 or 4 times each, for each list, so that answer is worked out about 6
times a macroblock in P pictures and more in B pictures.

## What was tried, and why this differs

- **The neighbour context (closed, 2026-10-28)**: grids of the neighbours' motion, filled once a macroblock and kept
  in step with the picture-wide arrays. It lost (+3.9% and +4.0% instructions) because every value was stored twice
  and every read checked what was filled. This design copies no values. The picture-wide arrays stay the only store,
  and what is computed once a macroblock is four availability flags and four macroblock addresses.
- **Local changes (2026-10-28)**: no tuple, P_Skip's A and B passed on, row stores in `StoreMotion`. Each was under
  0.1% under the JIT, or slower. They kept the per-call lookup. This removes it.

## The design

For frames (not MBAFF, not field pictures, which keep `AffNeighbor`):

- **Once a macroblock**, when its motion decoding starts: the addresses of A, B, C and D (`NeighborMb`'s table, as
  now) and whether each is available for prediction (decoded and in this slice), in four fields of the slice state.
  C's availability for a partition also depends on decoding order inside the macroblock (6.4.11.7); that stays per
  partition, from the partition's position, without a lookup.
- **Per call**, a frames-only `MotionNeighborFrame(list, mbAddr, x, y, currentBlock)`. Inside the macroblock it reads
  the current macroblock's blocks, as now. Across an edge it reads A's right column, B's bottom row, C's or D's corner
  block, at offsets fixed by (x, y) mod 16, from the flags above. The reads go through one checked reference a
  macroblock rather than three bounds checks a read, where the JIT's code shows it pays.
- **The reference stays.** `MotionNeighbor` remains the derivation by the definition, used in MBAFF frames and field
  pictures, and a test compares the two at every location of random pictures of macroblocks. The test varies slice
  boundaries, intra macroblocks, partitions that leave a list unused, and partition decoding order for C, the way
  `PairsFoundOnce_MatchTheLookup` checks the MBAFF pairs. Bit-exactness against FFmpeg on every sample stays the
  final check.

Spatial direct (8.4.1.2.2) reads A, B and C of the whole macroblock through `PredictMv`, so it takes the same path.
`StoreMotion` is left as it is: its row stores were tried, and gained nothing under the JIT.

## Expected gain, and the ceiling

The ceiling is `MotionNeighbor`'s share: 4.8% of the CABAC stream, and 3.4% of the CAVLC 720p stream's instructions
(callgrind, before #312). If a call drops from 123 instructions to about 40 (the reads and the `Motion`, with the
lookup gone), the CABAC stream gains about 1.9 M instructions a frame, about 3%. The record above estimated 3 to 4%
for its context and measured +3.9%, so this estimate is held loosely. The first step is to measure, before the code is
tidied: a rough version on frames only, counted by callgrind and timed by perf with the order alternating. It is kept
only if the JIT build gains, not the AOT count alone (the `StoreMotion` experiment gained 0.7% of AOT instructions and
nothing under the JIT).

## Decisions

1. **Whether to try it at all.** The record of 2026-10-28 decided against grids for frames, and this is not a grid,
   but it is the same territory. Proposed: one measured attempt, dropped if the JIT build does not gain at least 1% on
   the 720p streams.
2. **Unchecked reads.** Proposed: a bounds check once a macroblock and `Unsafe` reads after it, as CABAC's significance
   map does (#188), with the reference test above as the guard. The alternative is checked reads, giving up part of
   the gain.
3. **Scope.** Proposed: frames only. MBAFF frames and field pictures keep `AffNeighbor` with the pairs found once
   (#177).
