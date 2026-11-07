# MJPEG: the entropy decoder's share, its bit buffer in locals (2026-11-07)

#342. How much of an MJPEG frame is entropy decoding, and what keeping the bit reader's state in registers gave.

## The entropy decoding's share

perf on main before the change (x64 cloud VM, mcdec, `--loops 100`, the whole process), the Huffman decoding being
inlined into `MjpegDecoder.DecodeBlock` (which also clears and dequantizes the block):

| | `noise-q1.avi` | `testsrc-420.avi` |
| --- | ---: | ---: |
| `DecodeBlock` | 57.1% | 33.3% |
| `JpegBitReader.Fill` | 10.5% | 6.6% |
| `HuffmanTable.Decode` (the combined table's misses) | 5.1% | 4.2% |
| Entropy decoding in all | about 73% | about 44% |
| `SimpleIdct` (`PutAvx2`, `PutDcOnly`, `CheckDestination`) | 10.7% | 16.1% |
| `SpanHelpers.Fill` (flat blocks' rows, from `PutDcOnly`) | – | 5.6% |
| Building the Huffman tables (`AddCoefficientEntries`, the constructor) | 0.2% | about 1% |

`noise-q1.avi` (noise at the highest quality) is nearly all coefficients; on `testsrc-420.avi` the transform weighs
more, and so do its flat blocks: `PutDcOnly` fills each of a block's eight rows through `Span.Fill`, a call to
`SpanHelpers.Fill` each (a call graph, `perf record -g`, attributed them: 3.9% from `PutDcOnly`, 1.7% from it inlined
into `DecodeScan`).

### Reusing the Huffman tables: tried and dropped (#359)

The first reading of this profile put `SpanHelpers.Fill` (5.6%) with the tables' construction, since FFmpeg's encoder
writes a DHT in every frame, and #359 proposed reusing the last table of a slot when a DHT repeats it. The call graph
showed otherwise: building the tables is about 1% of `testsrc-420.avi`'s profile. And FFmpeg's encoder writes optimal
tables, computed for each frame (its `-huffman optimal` default): of the 200 tables in `testsrc-420.avi`, 39 repeat the
one before them in their slot (20 of 100 in `noise-q1.avi`, 17 of 100 in `testsrc-444.avi`). A cache would save about
a fifth of 1%, below anything perf or the wall clock resolves here, so it was not made. Only streams whose tables
never change (cameras with the Annex K tables, as `testsrc-stdhuff.avi`: one DHT repeated) would gain, and there by
the same 1% at most.

## The change

- **The reader's buffer and count in locals.** `DecodeBlock` copies them out of `JpegBitReader` at a block's start
  and back at its end; `HuffmanTable.DecodeAc` and `DecodeDc` (inlined) take them by reference, and the refill
  (`JpegBitReader.Fill(ulong, int)`, not inlined) takes and returns values, so no address escapes and the JIT keeps
  them in registers, as #312 did for CAVLC and the CABAC engine. A refill always takes whole bytes to more than 56
  bits, so the caller works out the new count itself (`FilledCount`).
- **One lookup on a miss.** The 10-bit combined table gave a whole coefficient or nothing; a miss decoded the symbol
  again with a second `Peek(16)` and the 9-bit table. Where the code fits in 10 bits but its magnitude bits do not
  (high quality: large sizes after short codes), the entry now gives the symbol and the code's length; only codes
  longer than 10 bits take MAXCODE, from length 11. The 9-bit table is gone, and `HuffmanTable.Decode` is T.81's
  Figure F.16 bit by bit, the reference `HuffmanTableTests` checks both decoders against on random tables and data.

## Results

Bit-exact (every MJPEG reference). perf, six runs of each build, the order alternating: the changed functions'
samples (`DecodeBlock`, `JpegBitReader`, `HuffmanTable`) over the untouched `SimpleIdct`'s:

| Sample | Before (median) | After | Changed functions | Their share | Frame |
| --- | ---: | ---: | ---: | ---: | ---: |
| `noise-q1.avi` | 6.99 | 6.28 | -10.2% | about 73% | about -7% |
| `testsrc-420.avi` | 2.87 | 2.51 | -12.5% | about 48% | about -6% |

The six runs of each build don't overlap on either sample. By wall clock (`tools/bench.py --base`, 15 interleaved
runs, whole process): `testsrc-420.avi` 0.915, `testsrc-444.avi` 0.929, `noise-q1.avi` 0.920 of the base; steady 0.927,
1.045 and 0.924, the steady column being the noisier on short clips (docs/benchmarking.md).

## A flat block's rows as 8-byte stores (2026-11-07)

`SimpleIdct.PutDcOnly` wrote a flat block's eight rows with eight `Span.Fill` calls (the 5.6% above); each row is now
one unaligned 8-byte store, in both builds (#361 had made it so in the Unity build, for Mono). Bit-exact. perf, six
runs of each build, the order alternating, on `testsrc-420.avi`: `PutDcOnly` and `SpanHelpers.Fill` over the
untouched `PutAvx2` and `DecodeBlock`, medians 0.21 before and 0.066 after (no run overlapping), about 7% of the
profile. By wall clock (`tools/bench.py --base`, 15 interleaved runs, whole process): `testsrc-420.avi` 0.896,
`testsrc-444.avi` 0.864 (its flat blocks about 11% of the profile before), `noise-q1.avi` 1.013 (few flat blocks).

## Where the time goes after #357 and #369, and the refill's fast path inlined (2026-11-07)

perf on main after #369 (`--loops 100`, the whole process, `-g` and `DOTNET_ReadyToRun=0`):

| | `noise-q1.avi` | `testsrc-420.avi` |
| --- | ---: | ---: |
| `DecodeBlock` (the Huffman decoding inlined, the clear, dequantization) | 56.8% | 34.1% |
| `JpegBitReader.Fill` | 9.0% | 5.9% |
| `SimpleIdct.PutAvx2` | 9.7% | 12.9% |
| `SimpleIdct.PutDcOnly` | – | 2.3% |
| Building the Huffman tables | 0.3% | 1.0% |

Against FFmpeg (`tools/bench.py`, 7 runs, this cloud VM's x64): mcdec's steady time is 0.57 of FFmpeg's on
`testsrc-420.avi`, 0.63 on `testsrc-444.avi` and 0.66 on `noise-q1.avi` (whole process 0.69 to 0.76).

The refill was a call every 56 bits or so. Its fast path (eight bytes without a 0xFF) is now inlined into the
decoders (`JpegBitReader.Fill(ulong, int)`), the rest (stuffing, markers, the end of the data) a call as before
(`FillSlow`). Bit-exact. perf, six runs of each build, the order alternating, `DecodeBlock`, `JpegBitReader` and
`HuffmanTable` over `SimpleIdct`: medians 6.32 before and 6.01 after on `noise-q1.avi` (-4.9%, about 3.5% of the
frame), 2.68 and 2.58 on `testsrc-420.avi` (-3.5%, about 1.6%), the runs overlapping a little. By wall clock
(`tools/bench.py --base`, 15 interleaved runs): whole process 0.962, 0.961 and 0.952 of the base on `testsrc-420.avi`,
`testsrc-444.avi` and `noise-q1.avi`, steady 0.944, 0.958 and 0.923.

What is left is the decoding itself, a code and its magnitude bits at a time, and the transform; neither has a
lever of the size of the three above in sight.
