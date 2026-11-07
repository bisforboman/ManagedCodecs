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
| Building the Huffman tables (`SpanHelpers.Fill`, `AddCoefficientEntries`) | 0.2% | 7.7% |

`noise-q1.avi` (noise at the highest quality) is nearly all coefficients; on `testsrc-420.avi` the transform and the
tables weigh more. FFmpeg's encoder writes a DHT in every frame, and each one builds its four tables again: about 6
to 8% of `testsrc-420.avi`'s profile, which a cache of the last tables by their DHT bytes would save (not done here).

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
