# AAC LC by stage: 5.1 against stereo, and the IMDCT's first stage and post-twiddle (2026-11-07)

Issue #339. Two questions: does the FFT or the spectral decoding explain why AAC LC 5.1 runs at 1.25 to 1.33 times
FFmpeg's steady time while stereo runs at 0.5 (README, Benchmarking)? And what do fusing the IMDCT's bit reversal with
its first FFT stage and vectorizing its post-twiddle gain?

**Answer.** Neither of our stages explains the gap. Per channel, our IMDCT costs the same in both streams (about 5.9 µs
a channel-frame). Spectral decoding costs 1.7 times as much a channel in 5.1 (4.3 against 2.5 µs), because 5.1 carries
42.7 kbit/s a channel against stereo's 25. The rest is FFmpeg's side: its time a channel is twice as high in stereo
(29 µs against 13.7), a cost per frame that 5.1 shares among six channels. The change (#339) takes 12 to 15% off AAC
LC's steady frame on this VM, and 3% off 5.1 HE-AAC. That is well above the issue's estimate of 2 to 3%, but inside the
IMDCT's share of the frame.

## Method

- Machine: the 4-core cloud VM (Xeon @ 2.30 GHz, AVX-512, no hardware counters), .NET SDK 10.0.114, FFmpeg 6.1.1
  (Ubuntu's). mcdec Release builds of main (7028129) and of the branch.
- Stages: `perf record -e cpu-clock -F 4000` of `mcdec <sample> --loops 1000` (docs/benchmarking.md), with the
  per-symbol shares grouped into stages. **IMDCT FFT** is `Imdct+Plan::Fft` and `BitReverse`. **IMDCT twiddles and
  fold** is `Imdct::Inverse`: the pre-twiddle, the post-twiddle, the fold and the zeroing of its stack buffers.
  **Spectral decoding** is `Huffman::*`, `ReadSpectralData`, `ReadScalefactors`, `ReadSectionData`, `Dequantize` and
  `InverseQuantize`. **Window and output** is `Vectors::*` and `Filterbank::*`. Shares are of the decoder's own code
  (72 to 81% of the samples; the rest is the runtime, the JIT and the kernel). Microseconds are those shares of the
  steady frame.
- Timing: `tools/bench.py --base`, 15 runs interleaved with the order alternating, medians. Against FFmpeg: 7 runs.

## Main by stage

| Stage, share of decoder code | `aac-5.1.mp4` | `aac-stereo-48k.mp4` |
| --- | ---: | ---: |
| Spectral decoding | 36.8% | 24.2% |
| IMDCT twiddles and fold | 27.1% | 30.0% |
| IMDCT FFT | 25.6% | 28.9% |
| Window, overlap, output | 7.5% | 11.3% |
| Stereo, TNS, other | 1.9% | 3.3% |
| Demuxing | 1.0% | 2.2% |

| Steady time, µs | `aac-5.1.mp4` | `aac-stereo-48k.mp4` |
| --- | ---: | ---: |
| mcdec, a frame | 68 | 20 |
| mcdec, a channel-frame | 11.3 | 10 |
| of which IMDCT | 5.9 | 5.9 |
| of which spectral decoding | 4.3 | 2.5 |
| FFmpeg, a frame (whole process) | 82 | 58 |
| FFmpeg, a channel-frame | 13.7 | 29 |

The IMDCT is 52 to 59% of the decoder's code on x64, far above README's 11% for "the FFT" (that is
`profile.yml`'s `Plan::Fft` alone on a hosted runner). Here `Plan::Fft` alone was 12 to 13% of all samples and the
scalar `BitReverse` pass another 2.6 to 3.6%. The twiddles and fold took as long as the FFT: the post-twiddle was
scalar, with a bounds check on every access, and every call zeroed 12 KB of stack.

## The change

- `Plan.Transform` reads the pre-twiddle's output from its own buffer and writes the bit-reversed first stage
  (half-span 1, twiddle 1) straight into the FFT's buffers (`ReorderFirstStage`). For an even j below h = length / 2
  with a = reverse(j), the first stage's sum x[j] + x[j + h] lands at a and its difference at a + 1, and j + 1's at
  a + h and a + h + 1. So two j at a time are read from contiguous source and zipped into place, with no gather and
  no branch. The vector stages then start at half-span 2. Half-span 2 already ran in 128-bit vectors under AVX2: its
  `else if` falls through to the Vector128 path, contrary to the issue. Its twiddle still comes from the table
  (`Math.Cos(pi / 2)` is not 0).
- The post-twiddle runs two l at a time from both ends: u[2l .. 2l + 3] is l's real part zipped with its mirror's
  negated imaginary part, reversed.
- `Inverse` is `[SkipLocalsInit]`: each of its stack buffers is written whole before it is read.

**Exactness.** The vector `Inverse` matches the scalar `InverseScalar` to the bit (`VectorImdct_MatchesScalarExactly`).
The FFT matches `FftScalar`, the reference that keeps the separate bit reversal and multiplies by 1 - 0i, apart from
the sign of a zero (`VectorFft_MatchesScalarExactly`, now with zeros in every other trial). The decoded output of
all 54 AAC and HE-AAC samples hashes the same as main's (`mcdec --hash`).

## Timing

| Steady, branch / main | `aac-stereo-48k` | `aac-5.1` | `heaac/heaac-5.1` | `heaac/heaacv2-stereo` |
| --- | ---: | ---: | ---: | ---: |
| The change | 0.851 | 0.884 | 0.970 | 0.995 |
| `[SkipLocalsInit]` alone | 0.964 | 0.994 | | |

Is it real? The IMDCT was 52 to 59% of the decoder's code, so a 12 to 15% gain is inside the ceiling. I profiled
both builds twice, alternating, and measured the IMDCT's samples against spectral decoding's, which the change
leaves alone. The IMDCT's time fell by 36% in 5.1 (1.38 to 0.88 times spectral decoding) and by 32% in stereo (2.33
to 1.58), which accounts for the wall clock's gain. HE-AAC gains less because the QMF banks dominate its frame.

On the hosted runners (`compare.yml` run 42374598337, medians of 7 interleaved runs), steady time, branch / main:

| Runner | `aac-stereo-48k` | `aac-5.1` | `heaac/heaac-5.1` | `heaac/heaacv2-stereo` |
| --- | ---: | ---: | ---: | ---: |
| x64 (EPYC 7763) | 0.768 | 0.809 | 0.971 | 0.983 |
| ARM64 | 0.831 | 0.846 | 0.970 | 0.990 |

The runners gain more than the cloud VM: 19 to 23% off AAC LC on x64 and 15 to 17% on ARM64.

## Again on main after #343 and #355

Main is now at c954bff: the IMDCT is in single precision (#343) and spectral decoding uses packed tables (#355). Same
VM and method, two perf runs each.

| Stage, share of decoder code | `aac-5.1.mp4` | `aac-stereo-48k.mp4` |
| --- | ---: | ---: |
| Spectral decoding | 42.3% | 28.6% |
| of which `Dequantize` and `InverseQuantize` | 21% | 11% |
| IMDCT FFT (`Imdct+Plan::*`) | 22.5% | 24.8% |
| IMDCT twiddles and fold (`InverseVector`) | 19.5% | 23.3% |
| Window, overlap, output | 12.2% | 16.0% |
| Stereo, TNS, other | 2.5% | 3.5% |

The steady frame is now 39 to 44 µs for 5.1 and 12.6 for stereo, against 60 and 17 after #354. Outside the decoder's
code (26 to 38% of the samples), the time is mostly startup: coreclr and the JIT take 11.6%, and 1000 loops take only
7% longer than their steady time alone would. The rest is `memset` (2.2 to 2.4%, mostly `Array.Clear` of each
channel's 1024 doubles) and page faults.

The largest single stage with no issue yet was inverse quantization. It is one table lookup and one multiply a
coefficient, but its call and two data-dependent branches cost 16% of all of 5.1's samples: #373. After it, the
IMDCT's two halves and the window remain, each already worked on.

## Tried and dropped: the post-twiddle and the fold in one pass

After #343, `Imdct.InverseVector` writes the post-twiddle's two halves to buffers (`re` in place and `q`), interleaves
them into the DCT-IV's `u`, then reads `u` back for the three loops of the fold. I tried computing each block of eight of
u's values in registers instead: four l's real parts and their mirror's negated imaginary parts, zipped. Each block
was scaled once and stored straight to its two places in the output, one of them reversed, with no `u` or `q`. The
output was the same to the bit (`VectorImdct_MatchesScalarExactly`, and the hashes of all 54 AAC and HE-AAC samples).

The wall clock could not see it. Over 15 interleaved runs, steady time branch / main was 1.020 for `aac-stereo-48k`,
0.992 for `aac-5.1` and 0.984 for `heaac/heaac-5.1`. By perf (three runs of each build, alternating), `InverseVector`'s
samples over those of functions it does not touch fell by 4% in 5.1 (medians 0.277 and 0.265) and by about 7% in
stereo, where the runs overlap. That is about 0.6% of a frame, below what this method resolves. Likely because the
buffers it removed (5 KB of a long block) stay in the L1 cache, where a store and a reload cost little (not measured).

## Spectral reading after #355 and #376: where its time goes

On Elephants Dream's AAC (192 kbit/s stereo, `tools/realworld.py`'s film, the audio remuxed alone), with #376's
dequantization, `ReadSpectralData` (with `Huffman.ReadSpectrum` inlined) is 23% of all samples. That is 30% of the
decoder's code, ahead of `Imdct.InverseVector` (13%) and `Dequantize` (9%).

Callgrind with `--branch-sim=yes` on a Native AOT build (60 s of the film) puts it at:
- 22% of the decode's instructions;
- 41% of its mispredicted conditional branches, about 0.3 a codeword.

Mapping the addresses with `--dump-instr=yes` and `objdump` (`nm mcdec.dbg` gives the function's offset) shows:
- **The cursor's refill check** (`_used > 25`) is the most mispredicted branch, wrong 27% of the time.
- **Each band's loop exit** comes second.
- **The cursor's fields** stay on the stack rather than in registers. `Cursor.Position` in the cold error paths and
  `Commit(in Cursor)` take its address, and the one large method that inlines all four loops runs out of registers.

There are about 270 codewords a channel-frame, at about 80 instructions each, so instructions, not mispredicts, are
most of the cost. Refill mispredicts can account for at most about 2 cycles of the roughly 40 a codeword takes.

**Tried and dropped: each band's loop as its own method, its cursor a local copy.**
- The cold paths took the cursor by value.
- The JIT then kept `_start` and `_used` in registers.
- The film's steady time over 8 interleaved runs was 17.9 µs a frame against 18.1, which is noise.
- By perf (three runs each, alternating), spectral reading's samples over those of the stages it does not touch moved
  from 0.564 to 0.548 (medians), 3% of the stage. That is well under a point of the frame.
- Store forwarding makes the cursor's memory round trips cheap; they were not the cost.

Taking the cursor's address out of the cold paths alone was slower: 20.0 µs against 18.2. Passing the 24-byte struct
by value goes through a stack copy, and it did not free registers in the large method.

What is left is the work per codeword, about 80 instructions:
- the second-level test;
- two bounds-checked table and array accesses;
- each value's sign extracted with shifts and masks;
- the refill.

A gain there needs a leaner entry layout. For example, entries could carry each value's sign-bit position, and two
codewords could be read per refill (57 bits cover two of any book's, escapes aside). By the counts that is perhaps a
quarter of the stage, about 5 to 7% of a film's AAC frame. That is not tried here.

## Tried and dropped: two codewords a reload, and a table of codewords with their signs

These are the two changes the section above ended on, tried on main after #376 (391d721) with Elephants Dream's AAC.
- **Two codewords a reload.** `Cursor.Refill()` reloads the cache unconditionally before every two pairs, so the
  first peek's check, mispredicted 27% of the time, is always false. 57 bits cover two codewords with their signs, at
  most 20 bits each.
- **A table of codewords with their signs.** Books 7 to 11 get a second table of 2^12 entries indexed by the codeword
  and its sign bits together. It gives both values already signed and the total length in one lookup. Escapes and
  codewords whose signs run past 12 bits fall back to the packed table.

Both kept the output (the film's SHA-256 and `SpectrumHuffmanTests`). The measure was spectral reading's perf samples
over those of the stages it does not touch (the IMDCT, `Dequantize`, `Vectors`, `ApplyStereo`), four runs of each
build, alternating:

| Round | main | reload | reload and signed table |
| --- | ---: | ---: | ---: |
| First (two builds) | 0.570 | 0.538 | |
| Second (three builds) | 0.552 | 0.562 | 0.577 |

The first round's gain did not reproduce. The steady frame stayed at 19.2 to 20.1 µs in every build. The signed
table may cost more in cache than it saves in instructions; that is not measured. So the work per codeword stays as
#355 left it. A further try would need counts first (callgrind on an AOT build, which resolves what perf here cannot),
not the wall clock.
