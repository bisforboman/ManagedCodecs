# Why dynamic PGO's warm-up lasts about 1,000 frames (2026-11-07)

A measurement, no code change (#347). README (Startup and the JIT) found that under the runtime's defaults (tiered
compilation, dynamic PGO) a 1,000-frame run of `h264-high-default.mp4` takes far longer than with `TieredPGO` off.
The questions: which methods stay in tier 0 or instrumented code, why, and what the library can change by itself.

**Answer.** It is the compile queue, not a few methods. Under the defaults the JIT does 1.8 s of work, 4.1 times
tiering off's, and the tier-1 part goes through one background thread, nearly saturated from the third loop to
about frame 1,000. Each hot method is compiled three times (tier 0, instrumented tier 0, tier 1), and PGO's tier-1
compiles take longer (it inlines more). Per-picture methods pass the 30-call threshold in the first loop but wait in
the queue behind the ~1,000 methods that passed it during the first picture. `[MethodImpl(AggressiveOptimization)]`
on the slice and picture drivers was not a consistent gain (4 to 6% faster on one stream, 4% slower on two), so it is
not kept. `TieredPGO=false` in the packages' `buildTransitive` props would change the whole app: that waits for the
owner's decision (#347).

## Method

A scratch driver (not committed) decodes a file N times in one process under the runtime's defaults. It records:

- each loop's end time;
- `JitInfo.GetCompilationTime()` and `GetCompiledMethodCount()` after each loop;
- each method's compiles, from an in-process `EventListener` on `Microsoft-Windows-DotNETRuntime` (JIT keyword):
  `MethodJittingStarted` to `MethodLoadVerbose`, with the tier from `MethodFlags` (bits 7 to 9).

perf with a perf map (docs/benchmarking.md) splits samples by thread and by tier. The map tags symbols
`[QuickJitted]`, `[InstrumentedTier]` and `[OptimizedTier1]`. All runs are on a 4-core cloud VM (runtime 10.0), not
`compare.yml`'s runners.

## The compile queue

JIT time (all threads) and compiles so far, `h264-high-default.mp4` (50 frames a loop):

| After loop | 1 | 5 | 10 | 20 | 50 | 100 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Defaults | 0.22 s (1,270) | 0.50 (2,158) | 0.72 (2,316) | 1.10 (3,209) | 1.60 (4,001) | 1.82 (4,229) |
| `TieredPGO=0` | 0.18 (1,270) | 0.37 (1,592) | 0.59 (2,290) | 0.66 (2,524) | 0.82 (2,784) | 0.83 (2,792) |
| Tiering off | 0.44 (447) | 0.44 | 0.44 | 0.44 | 0.44 | 0.44 |

- **The background thread is the bottleneck.** Between loops 10 and 20 it compiled for 383 ms of the 390 ms wall
  time.
- **PGO's tier-1 compiles cost more.** They take 0.84 s for our code with PGO against 0.47 s without. The largest:
  `FilterMacroblock420` about 40 ms; `DecodeInterMacroblock`, `SpatialDirect`, `DirectMotion`, `ReadResidual` and
  `DecodeSliceDataCabac` 20 to 24 ms each.
- **Per-picture methods wait late in the queue.** `SliceHeader.Parse` and `DecodeSliceDataCabac` reach instrumented
  tier 0 in loop 14 (frame ~700) and tier 1 in loops 27 and 31 (frames ~1,350 and ~1,550). With `TieredPGO=0`, one
  pass fewer, they reach tier 1 in loops 15 and 16. `DecodeInterMacroblock` reaches tier 1 in loop 10 (loop 5 without
  PGO).
- **The second loop compiles nothing.** JIT time is 222.6 ms after loops 1 and 2 alike: that is the runtime's
  call-counting delay (100 ms, extended while new tier-0 methods keep appearing).
- **OSR doesn't apply at 320x240.** A patchpoint's counter starts at 1,000 iterations per call, and a 320x240 picture
  has 300 macroblocks, so the slice and deblocking loops stay in tier 0 until they tier up normally. The run has 4 OSR
  compiles in all (`Cabac.InitContexts`, `NalUnit.Unescape`, the driver's `Main`).
- **Instrumented code has an extra cost.** Over 20 loops, 13.6% of the main thread's samples are in libcoreclr
  under the defaults, against 6.3% with PGO off. Most of that is one small helper reaching thread-local storage
  (`__tls_get_addr`), most likely the class-probe sampling helper.
- **The tier-0 time is spread out.** It covers hundreds of methods, none above 0.6% of the samples.

## AggressiveOptimization on the drivers: tried and dropped

The attribute went on `DecodeSliceData`, `DecodeSliceDataCabac`, `DeblockRows` and `DeblockMbaff`. Whole-process time,
median of 5 interleaved rounds (3 for 720p), order alternating:

| Stream | Frames | Defaults | + AO | `TieredPGO=0` | `TieredPGO=0` + AO |
| --- | ---: | ---: | ---: | ---: | ---: |
| `h264-high-default.mp4` (CABAC, 320x240) | 50 | 0.404 s | 0.387 | 0.333 | 0.341 |
| | 1,000 | 1.592 | 1.489 | 1.258 | 1.217 |
| | 5,000 | 4.090 | 3.905 | 3.702 | 3.712 |
| `h264-p-refs.mp4` (CAVLC, 320x240) | 50 | 0.289 | 0.288 | 0.257 | 0.248 |
| | 1,000 | 1.051 | 1.097 | 0.774 | 0.790 |
| | 5,000 | 2.232 | 2.160 | 1.956 | 1.933 |
| `h264-bench-720p.mp4` (CAVLC, 720p) | 50 | 0.888 | 0.914 | 0.709 | 0.715 |
| | 1,000 | 5.009 | 4.900 | 5.026 | 4.931 |
| | 5,000 | 21.41 | 22.33 | 22.58 | 23.17 |

- It is 4 to 6% faster on the CABAC stream, 4% slower on the CAVLC one at 1,000 frames, and 4% slower at 720p over
  5,000 frames. There the slices are long enough for OSR, and the attribute only gives up PGO in the drivers.
- Splitting long loops out of rarely called methods doesn't apply. The drivers are called every picture, and their
  loop bodies are already calls (`DecodeMacroblock`, `FilterMacroblock420`).
- Putting the attribute on more methods would turn the decoder into tiering off, and lose PGO's steady gain on long
  and large streams (#346).

## TieredPGO off, by picture size

From the table:

- At 320x240, `TieredPGO=0` takes 21% less time at 1,000 frames and 9 to 12% less at 5,000.
- At 720p it takes 20% less at 50 frames, the same at 1,000, and 5% more at 5,000, where PGO has paid back its
  warm-up.

So where PGO pays back depends on picture size: about 1,000 frames at 720p, more than 5,000 at 320x240. An app
that sets `<TieredPGO>false</TieredPGO>` itself (README's advice) keeps that choice. Setting it from the packages'
`buildTransitive` props would make the choice for every app and every other library in it: that waits for the
owner's decision (#347).

## On GitHub's hosted runners (2026-11-07)

`compare.yml`, medians of 7 interleaved runs, order alternating. It compared two unmerged scratch branches of mcdec,
one with the runtime's defaults and one with tiering on and `TieredPGO=false`, with `tools/bench.py` limited to these
rows. Whole-process time, in seconds, and the defaults' time over `TieredPGO=false`'s:

| Stream | Frames | x64: `TieredPGO=0` | x64: defaults | x64 ratio | ARM64: `TieredPGO=0` | ARM64: defaults | ARM64 ratio |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `h264-high-default.mp4` (CABAC, 320x240) | 50 | 0.173 | 0.198 | 1.145 | 0.276 | 0.299 | 1.083 |
| | 1,000 | 0.782 | 1.126 | 1.440 | 1.008 | 1.298 | 1.288 |
| | 5,000 | 2.165 | 2.540 | 1.174 | 2.985 | 3.225 | 1.081 |
| `h264-p-refs.mp4` (CAVLC, 320x240) | 50 | 0.125 | 0.141 | 1.128 | 0.191 | 0.207 | 1.084 |
| | 1,000 | 0.535 | 0.648 | 1.211 | 0.733 | 0.864 | 1.179 |
| | 5,000 | 1.225 | 1.385 | 1.129 | 1.795 | 2.015 | 1.123 |
| `h264-bench-720p.mp4` (CAVLC, 720p) | 50 | 0.452 | 0.612 | 1.354 | 0.626 | 0.708 | 1.131 |
| | 1,000 | 2.872 | 2.937 | 1.023 | 4.442 | 4.313 | 0.971 |
| | 5,000 | 12.02 | 11.26 | 0.937 | 20.23 | 19.18 | 0.948 |

The runners agree with the cloud VM:

- **320x240:** the defaults are behind at every length on both machines, by 8 to 44%. The gap is widest at 1,000 frames
  (1.29 to 1.44 on CABAC), during the queue's backlog.
- **720p:** the defaults are 13 to 35% behind at 50 frames, about even at 1,000, and 5 to 6% ahead at 5,000.
- **Steady:** by the last loop of 5,000 frames, the defaults' frame is 0.99 (x64) and 0.91 (ARM64) of
  `TieredPGO=0`'s on the CABAC stream, 1.02 to 1.04 on the CAVLC one, and 0.91 to 0.95 at 720p. At 320x240 the PGO code
  is only just as fast when the run ends, so its warm-up is never paid back within 5,000 frames.

So `TieredPGO=false` is the better default up to a few thousand frames at 320x240, and up to about 1,000 at 720p.
Setting it from the packages' `buildTransitive` props is still the owner's decision (#347).
