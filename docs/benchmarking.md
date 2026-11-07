# Measuring speed

How to time a change and find where the time goes, and the traps that cost time once. The tools themselves
(`mcdec`, `tools/compare.sh`, `tools/bench.py`, `benchmark.yml`, `compare.yml`, BenchmarkDotNet) and the current
ratios are in README's [Benchmarking](../README.md#benchmarking) section; why mcdec turns tiered compilation off is in
its [Startup and the JIT](../README.md#startup-and-the-jit).

## Wall clock

- Judge decoding by the "steady" columns (mcdec's last loop), not the whole-process ratio, which on short clips is
  mostly .NET's startup and JIT (README). The steady columns are noisier on short clips: compare medians.
- Short samples are mostly JIT and warm-up: time them at 100 loops or more (`jm-paff-b`, 10 frames, once took nearly
  twice as long a frame at 20 loops as at 100).
- Cloud VMs are noisy: compare before and after on one machine, repeat runs, and don't trust one number.
  `compare.yml` (dispatch it on the branch) times a branch against its base on hosted x64 and ARM64 runners, quieter
  than a cloud VM. `benchmark.yml` keeps the history on the `benchmarks` branch: a ratio moving there across runs is a
  regression, one run's number is not.
- Comparing two builds: interleave their runs and alternate which goes first. With the same one first in every pair,
  an A/A test of one build against itself once showed a 10% "regression". `tools/bench.py --base <mcdec.dll>` does
  this.
- mcdec already runs with tiered compilation off (its project file). `DOTNET_TieredCompilation=0` only matters for
  another host (a scratch console, a test), where JIT tiering otherwise adds to the noise.
- Changes of 1 to 2% are below the noise: count instructions instead (below).

## Is the gain real?

- A gain larger than the changed functions' share of the frame is noise, however consistent: check that ceiling
  before believing an A/B. Two streams agreeing on -9 and -10% for the vector inverse transforms were noise, and the
  change was reverted; timed by wall clock later, the transforms are 2.5 to 2.8 times faster per block but about 2% of
  a frame, so about 1.5% in all (docs/conformance.md, H.264, Performance notes).
- Inlining a hot callee moves its perf share into its callers rather than removing it: judge such a change by the
  share of a function it leaves alone (`ResidualBlock`), which rises if the total fell.
- perf shares move by about a point between runs of one build (the HF generator read 6.7, 7.6 and 8.0% of 5.1 HE-AAC
  on three runs): profile the builds before and after back to back, and take a difference under a point as noise.
- Don't rank hot spots by `dotnet-trace`'s thread-time sampling: its samples land at safe points, and it put 23 to 48%
  of H.264 decoding in `Picture.ReportProgress`'s lock, whose removal changed nothing. Stopwatch timers around a
  decoder's stages (in a scratch copy, never pushed) and microbenchmarks of the hot functions ranked them instead.

## Where the time goes: perf

`perf` (apt's linux-tools-generic; run `/usr/lib/linux-tools-*/perf`) samples optimized code by function:

```
DOTNET_PerfMapEnabled=1 DOTNET_EnableWriteXorExecute=0 perf record -e cpu-clock -F 4000 \
  dotnet tools/ManagedCodecs.Cli/bin/Release/net10.0/mcdec.dll <file> --loops 100
perf report --no-children --sort symbol
```

Without `EnableWriteXorExecute=0` the JIT's code shows as bare addresses. A function's share falling between builds
confirms a gain the wall clock can't separate from noise. With `-g` (frame pointers work for JIT code) and
`DOTNET_ReadyToRun=0` (so CoreLib's helpers such as `SpanHelpers.Memmove` have names), samples can be assigned to a
decoder's stages (docs/notes/h264-speed-gap-2026-10.md, Method); `tools/h264-stages.py` does that for mcdec and
FFmpeg alike, by perf or by callgrind's counts (docs/notes/h264-stages-2026-11.md has the last results).

`profile.yml` runs perf on the hosted x64 and ARM64 runners on one sample: the only profile of ARM64 code. On x64,
`DOTNET_EnableAVX2=0` runs the 128-bit paths ARM64 takes. Its `tiering: pgo` profiles the runtime's defaults (tiered
compilation, dynamic PGO) instead of mcdec's tiering off, with `delay` (ms) leaving the warm-up out. Its `event:
instructions` samples by retired instructions on the ARM64 runner, which has hardware counters. Every run reports
`perf stat`'s instruction and cycle totals there. That compares the two settings function by function without a
cloud VM's noise (#346).

Hardware counters (`perf stat -e cycles,instructions`): GitHub's ARM64 runner has them, the x64 runner and the cloud VM
do not (`<not supported>`), and the x64 runner's 4 vCPUs are 2 cores with SMT. `perf record -e context-switches -g`
works everywhere and shows where threads block.

## How many instructions: callgrind

Callgrind repeats to about 1e-6, so it resolves what the wall clock can't. It does not see cache effects. Take loops N
minus loops 1 for the steady decode.

Use a **Native AOT build of mcdec**: its code is compiled before valgrind sees it, so callgrind counts optimized code,
and its speed is the JIT build's (docs/notes/h264-speed-gap-2026-10.md, Method):

```
dotnet publish tools/ManagedCodecs.Cli -c Release -r linux-x64 -p:PublishAot=true -p:IlcInstructionSet=x86-64-v3 -o <dir>
valgrind --tool=callgrind <dir>/mcdec <file> --loops N
```

About a minute for 150 frames of 720p. The AOT compiler inlines a little differently from the JIT, so counts per
function differ; totals per stage do not depend on it. `--dump-instr=yes` with `objdump -d` of the binary classifies
the executed instructions (vector, divisions, calls, branches).

**A change's gain by AOT count can be larger than under the JIT**, which is what mcdec and most users run. Twice so
far the count promised a gain the JIT build did not show: `StoreMotion`'s row stores (-0.7% of the AOT instructions,
nothing by perf) and motion vector prediction in frames (-1.27%, about -0.1% by perf;
docs/decisions/2026-11-04-h264-motion-prediction-frames.md). There the JIT's code for the changed functions was
already leaner than the AOT compiler's (`MotionNeighbor` 2.45% of the JIT build's time, 4.8% of the AOT build's
instructions). So count to find and to shape a change, but before keeping one that rests on a count, check it under
the JIT. perf both builds on one stream, six runs each with the order alternating. For each run, divide the changed
functions' samples (or the total) by those of functions the change does not touch (the motion compensation kernels,
the deblocking filters), and compare the medians. That ratio resolves a point or two, which the wall clock here
cannot. #312 passed it (1.9% of the frame by perf, 2.1% by count).

What the count does see in both builds: a static array read from a hot method costs a class-initialization check and
a load at every use, because mcdec compiles each method once, at its first call (tiered compilation off), often before
the class's static constructor has run, and Native AOT checks the same way. Constant tables written as
`static ReadOnlySpan<byte> X => [...]` don't pay it (#313, `MpsNext` and `LpsNext`: 2.8% of a CABAC frame by count, about
1.7% by perf).
`DOTNET_ReadyToRun=0 DOTNET_JitDisasm=<method>` shows the JIT's code, checks included.
Counting them in all of a decoder (`DOTNET_JitDisasm='ManagedCodecs.H264.*:*' DOTNET_JitStdOutFile=<file>`, the
`CORINFO_HELP_GET*STATIC_BASE` calls by method) found few left in H.264 by 2026-11 (issue #346): `Cavlc.ReadResidualBlock`
(six), CABAC's context bases and the deblocking filter's tC0 rows, now constant data or initialized before the first
slice. That took about 0.15% (High, CABAC) to 0.5% (MBAFF) of the AOT build's instructions and less than the JIT build's
noise: they do not explain dynamic PGO's gain. A class whose static constructor is run up front (`PictureDecoder`'s,
`Cavlc`'s) gets no checks from the JIT, but still does from Native AOT, unless the compiler preinitializes it.

Not the JIT build: under valgrind (seen on runtime 10.0.13) the JIT's full-optimization compiles fail and fall back to
MinOpts, where every span index is a call (the QMF FFT's profile, all MinOpts, was 7 times the native instruction
count). Counts and shares taken that way (`valgrind --tool=callgrind --smc-check=all dotnet <mcdec.dll> ...`) are of
unoptimized code; `DOTNET_JitDisasmSummary=1 DOTNET_JitStdOutFile=<file>` under valgrind lists each method's level.
Cachegrind on the JIT build still gives useful miss counts: MinOpts adds stack traffic, but reads and writes the
pictures and tables as optimized code does.
