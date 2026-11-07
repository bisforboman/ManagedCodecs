# A netstandard2.1 build for Unity (2026-10-31)

The owner's decision in docs/decisions/2026-10-31-game-engines.md (#254): Unity gets a netstandard2.1 build of the
decoders with the scalar paths only, as a separate package, after a decision record of its own. This is that record,
decided (below).

## Why a second build

Unity runs scripts on Mono or IL2CPP with the .NET Standard 2.1 API profile. The library targets net10.0 and uses
what that runtime gives. What it uses that netstandard2.1 lacks (counted in src/ on 2026-10-31, 28,900 lines):

- **SIMD**: `System.Runtime.Intrinsics` is not in netstandard2.1 at all. There are 564 uses of `Vector128`/`Vector256` and
  107 of `Sse2`/`Avx2`/`AdvSimd`/`Ssse3`, in 11 files. Every one of those fast paths has a scalar reference that a test
  compares with it (the repository's rule), so the scalar code exists already.
- **Runtime features**: generic math, the one that matters. H.264's sample code is generic over the sample type
  (`PictureDecoder<TPixel>` and its helpers, `where TPixel : unmanaged, IBinaryInteger<TPixel>`: 26 constraints, 37
  `TPixel.CreateTruncating` and 30 `int.CreateTruncating` calls in Transform, Intra and InterPrediction, Deblock and
  Samples). Generic math needs static abstract interface members, which Mono and IL2CPP cannot run (CS8919, "Target
  runtime doesn't support static abstract members in interfaces"), so no polyfill helps: the scalar H.264 code
  itself must change. The uses are conversions only, no arithmetic on `TPixel`, so the change is mechanical (below).
  The others are small: static abstract members in `VectorZip` (vector code only), one `[InlineArray]` and two ref
  fields (a fixed buffer or an array does the same), the non-generic `TaskCompletionSource` (4 uses).
- **Library APIs**: polyfills cover them (and `SkipLocalsInit`, 54 uses, by an attribute of that name; `Unsafe` comes
  from the System.Runtime.CompilerServices.Unsafe package, a dependency the Unity package then carries). A trial build of ManagedCodecs.Core for netstandard2.1, with the vector file
  left out, failed only on `IsExternalInit` (records, init accessors), `ArgumentOutOfRangeException.ThrowIfNegative`
  and its siblings, `ArgumentNullException.ThrowIfNull` and `GC.AllocateUninitializedArray`. The other projects
  (`ReadExactly`, `Math.Clamp`, `BitOperations`, `System.Threading.Lock`, collection expressions) still need the same
  trial; the language features compile for any target with the current compiler.

## Speed

A proxy, not the real thing: the net10.0 build on CoreCLR with `DOTNET_EnableHWIntrinsic=0` (the cloud VM, last of
20 loops, two runs each):

| Sample | With SIMD | Without | Ratio |
| --- | ---: | ---: | ---: |
| h264-bench-720p (1 I, 49 P) | 5.5 ms a frame | 55 to 61 ms | about 10 |
| h264-b-pyramid (320x240) | 1.05 to 1.11 ms | 3.4 ms | about 3.2 |
| h264-10bit-b (320x240) | 1.0 to 1.5 ms | 3.5 to 5.4 ms | 2.4 to 5.4 |
| heaac-5.1 | 0.50 ms | 0.54 to 0.92 ms | 1.1 to 1.8 |
| mjpeg-interlaced-420 | 0.40 to 0.45 ms | 0.46 to 0.62 ms | 1.1 to 1.4 |

The 720p ratio is probably inflated. With intrinsics off, CoreCLR still runs the `Vector128` code where a path
doesn't check `IsHardwareAccelerated`, now as slow software emulation, and a scalar-only build would not contain it.
Mono's JIT is slower than CoreCLR's on scalar code too, and IL2CPP compiles through C++, which may be faster. So the real
cost is unknown. At the proxy's worst, the 1080p film that decoded at 57.5 frames a second on one thread would decode
at about 6. That rules out large video for Unity and leaves cutscenes at modest sizes, audio, and MJPEG.

## Measured (pull request 1)

The scalar build itself, `ManagedCodecs.Unity.dll`, under a copy of mcdec on CoreCLR (net10.0; one decode each,
`--hash`, on the cloud VM), against the net10.0 build:

| Sample | net10.0 | Unity build | Ratio |
| --- | ---: | ---: | ---: |
| h264-bench-720p (1 I, 49 P) | 355 ms | 1211 ms | 3.4 |
| h264-main-p-refs | 28 ms | 65 ms | 2.3 |
| jm-paff-b | 15 ms | 34 ms | 2.3 |
| h264-mbaff-default | 67 ms | 148 ms | 2.2 |
| h264-b-pyramid | 60 ms | 112 ms | 1.9 |
| h264-10bit-b | 83 ms | 109 ms | 1.3 |
| aac-pns-stereo | 6.0 ms | 7.4 ms | 1.2 |
| mjpeg-interlaced-420 | 65 ms | 74 ms | 1.1 |
| heaac-5.1 | 129 ms | 129 ms | 1.0 |

So 1 to 3.4 times, not the proxy's 10: that one did run software `Vector128`. All 246 samples (libx264, OpenH264, JM,
MJPEG, AAC, HE-AAC) decode to the same SHA-256 as the net10.0 build. Ubuntu's Mono 6.8 cannot run netstandard2.1
assemblies (`MissingMethodException` on `ReadOnlySpan<T>`'s indexer, its facade too old), and the cloud sandbox cannot
reach mono-project.com's 6.12, so the Mono job of pull request 2 measures it on CI.

## Measured (pull request 2): Mono

Mono 6.12 from mono-project.com on GitHub's x64 runner, one thread: the same mcdec on the Unity build under `mono`
against the net10.0 build (ci.yml's `unity` job, `tools/bench.py --runtime mono`; medians of 3 interleaved runs, the
"steady" last loop). Every sample decodes to the same output as the net10.0 build there (`tools/unity-check.sh`).

| Sample | net10.0 (ms a frame) | Mono 6.12 (ms a frame) | Mono / net10.0 |
| --- | ---: | ---: | ---: |
| h264-bench-720p (1280x720, 1 I, 49 P) | 4.22 | 79.8 | 18.9 |
| h264-p-refs (320x240, P frames, CAVLC) | 0.30 | 3.89 | 12.8 |
| h264-b-pyramid (320x240, CABAC, B frames) | 0.72 | 6.45 | 9.0 |
| h264-high-default (320x240, libx264's High defaults) | 0.59 | 5.75 | 9.8 |
| h264-10bit-b (320x240, High 10) | 0.99 | 7.04 | 7.1 |
| h264-mbaff-default (320x240, MBAFF) | 1.05 | 7.53 | 7.2 |
| h264-high-motion (320x240, QP 22, fast motion) | 4.15 | 21.6 | 5.2 |
| testsrc-420.avi (320x240 MJPEG) | 0.23 | 1.05 | 4.5 |
| noise-q1.avi (MJPEG, highest quality) | 0.89 | 3.42 | 3.8 |
| aac-stereo-48k (a frame: 1024 samples, 21 ms) | 0.018 | 0.096 | 5.3 |
| aac-5.1 | 0.065 | 0.33 | 5.0 |
| heaac-5.1 (a frame: 2048 samples, 43 ms) | 0.30 | 2.84 | 9.5 |
| heaacv2-stereo | 0.12 | 0.99 | 8.1 |

Far past the proxy's 10 on 720p H.264, and 5 to 9.5 times for audio, where CoreCLR's scalar build was even: Mono's JIT
is the cost, not the missing vectors. In absolute terms the record's conclusion holds: AAC takes 0.5 to 1.5% of a core
in real time and HE-AAC 2 to 7%, MJPEG and 320x240 H.264 play (1 to 22 ms a frame), and 720p H.264 runs at 12.5 frames
a second on one thread (1080p would be a few). Unity's own Mono fork and IL2CPP (through C++) are not measured.

## Mono's profile (issue #345)

Where Mono 6.12 spends the Unity build's time, and what of it Mono compiles worse than CoreCLR. Measured on GitHub's
x64 runner (Mono 6.12.0.200 from mono-project.com, as ci.yml's `unity` job installs it) by a scratch workflow, not
committed: `perf record -e cpu-clock -F 2000 mono --jitmap mcdec.dll <sample> --loops N` (`--jitmap` writes Mono's perf
map, so the JIT's code has names) and `perf report --sort symbol`, and a console of micro-benchmarks of the patterns
below, built for netstandard2.1 and run under `mono` and `dotnet`. The cloud sandbox cannot run Mono 6.12 (above).

**Before** (main on 2026-11-07), the top of each profile, as shares of the run:

- h264-bench-720p (CAVLC, P frames): motion compensation by the definition, about 70%. The per-sample reference
  interpolation (`PredictLumaReference`, `PredictChromaReference`), whose local function `S` (a clamped read) alone was
  38% for luma and 14% for chroma: Mono compiles local functions as calls taking their closure by reference and inlines
  none of them, where CoreCLR's share of the same code was 58%. Then deblocking's `FilterSamples` 5.6% and
  `get_Tc0` 2.1%.
- h264-b-pyramid (CABAC, B frames): motion compensation about 45%; CABAC's `ResidualBlock` 4.8%, the table properties
  `get_RangeTabLps` 3.7%, `get_MpsNext` 2.5%, `get_LpsNext` 1.3%, and the `BitOperations.LeadingZeroCount` polyfill
  3.3% (a loop a bit, at every renormalization).
- testsrc-420.avi (MJPEG): the scalar IDCT (`Row`, `Column`, `PutScalar`, `PutDcOnly`) about 30%, and
  `Span<short>`'s conversion to `ReadOnlySpan<short>` 8 to 10% by itself.
- heaac-5.1: the QMF banks' FFT (`QmfFft.Transform`) 26%, their windows (`Vectors.SumOfProducts`) 24%,
  `QmfSynthesis.FullTransform` 11%.
- aac-stereo-48k: the IMDCT's FFT 30% and the rest of the IMDCT 15%; Mono's runtime (`mono-sgen`) 14 to 17%.

**The patterns**, each a loop over 4096 values (the console's best of 5, ns a value, one run). The console is crude
(each loop is a lambda over static arrays): the same row moved by up to 2 times between two runs, so only the large
ratios mean anything, and the two columns only compare within themselves.

| Pattern | Mono 6.12 | CoreCLR (.NET 10) |
| --- | ---: | ---: |
| short add by array index / `Span` index / `Unsafe.Add` | 1.91 / 1.93 / 3.22 | 2.66 / 2.41 / 2.09 |
| `Vector<short>` add | 9.39 | 0.99 |
| `typeof(T) == typeof(byte)` in a generic method / `Unsafe.As<T, byte>` there / plain byte code | 2.40 / 0.63 / 2.11 | 1.81 / 1.73 / 1.81 |
| a 32-byte struct to an AggressiveInlining callee by value / by `in` | 1.25 / 0.94 | 2.36 / 2.32 |
| a small AggressiveInlining call / the same written inline | 1.26 / 1.26 | 2.30 / 2.91 |
| a static `ReadOnlySpan<byte>` table property indexed in the loop | 8.46 | 0.32 |
| the same with an AggressiveInlining getter | 7.19 | 2.90 |
| the same property hoisted into a local span / a `static readonly byte[]` | 1.25 / 1.25 | 2.03 / 1.89 |
| a call taking `ReadOnlySpan`, passed a `Span` / passed a `ReadOnlySpan` | 10.9 / 2.19 | 1.57 / 3.14 |
| `span.Slice(i, 4)[0]` | 10.0 | 0.63 |
| the `LeadingZeroCount` polyfill: a loop a bit / a binary search | 13.3 / 4.88 | 4.25 / 4.36 |

So Mono does well what the issue suspected it might not: `typeof(TPixel)` branches fold per instantiation,
`Unsafe.As` and `Unsafe.Add` (from the System.Runtime.CompilerServices.Unsafe package) compile to plain loads, struct
copies cost nothing extra, and AggressiveInlining is honoured. What it does badly is calls CoreCLR inlines: local
functions, `Span<T>`'s conversion to `ReadOnlySpan<T>`, `Slice`, `Fill`, and the getter of a `ReadOnlySpan<byte>`
property over constant data (the form CoreCLR wants for tables, since it folds it to the data's address: hoisting it
into a local costs CoreCLR about 6 times, so the shared code can't simply change).

**`System.Numerics.Vector<T>`** is not accelerated on Mono 6.12's JIT: `Vector.IsHardwareAccelerated` is false and
`Vector<short>` (16 bytes there) adds about 5 times slower than scalar code (software emulation). It is no vector path
for the Unity build on Mono. IL2CPP is not measured: it needs a Unity license (GameCI can run it in CI with one). The
console above, built into a Unity player, would answer both questions for IL2CPP and for Unity's own Mono.

**Fixed** (pull request for #345), each bit-exact (the whole test suite against the Unity build, and
`tools/unity-check.sh` on every sample):

- Motion compensation in the Unity build (`InterPrediction.Scalar.cs`, compiled only there): the block's reference
  samples are read once into a window, clamped, and the 6-tap and bilinear filters run over its rows and columns, as
  the vector paths do, in scalar code; spans are passed as `Span`, not `ReadOnlySpan`. InterPredictionTests compares
  it with the per-sample reference on random blocks, as it does the vector paths.
- The `LeadingZeroCount` and `TrailingZeroCount` polyfills: a binary search instead of a loop a bit (PolyfillTests
  compares them with .NET's on every power of two and 20,000 random values).
- MJPEG's scalar IDCT (`SimpleIdct.PutScalar`, the Unity build's only IDCT and CoreCLR's reference): rows and
  columns by offset rather than slices and a conversion each, the zero tests as ORs; `PutDcOnly` stores a row as one
  8-byte write in the Unity build. The `Span<T>.IndexOfAnyExcept` polyfill loops itself instead of converting.

Mono 6.12, the last loop's time a frame, medians of five interleaved runs of each build (each row's two on one runner;
the H.264 rows on the runner of README's table, the MJPEG row on another):

| Sample | Before (ms) | After (ms) | Before / after |
| --- | ---: | ---: | ---: |
| h264-bench-720p | 79.1 | 38.3 | 2.07 |
| h264-high-motion | 21.3 | 16.4 | 1.30 |
| h264-b-pyramid | 6.56 | 4.29 | 1.53 |
| h264-10bit-b | 7.10 | 4.82 | 1.47 |
| testsrc-420.avi (MJPEG) | 1.18 | 0.81 | 1.45 |

ci.yml's table of the Unity build on Mono against the net10.0 build, after this change (`tools/bench.py --runtime
mono`, on a faster runner, an AMD EPYC 9V45, so not comparable with the table above by time): Mono takes 3.1 to 3.4
times the net10.0 build's time on MJPEG, 3.1 to 8.7 on H.264 (720p 21.9 ms a frame), 4.7 to 4.8 on AAC and 7.2 to 9.1
on HE-AAC; src/ManagedCodecs.Unity/PACKAGE.md has it.

The net10.0 build is unchanged where it runs: ManagedCodecs.H264.dll for net10.0 is the same file byte for byte
(built without debug information, whose id hashes the sources), and the MJPEG change reaches CoreCLR only without
SSSE3 or AVX2 (`PutScalar`), with `PutDcOnly`'s CoreCLR path as it was; `tools/bench.py --base` on the cloud VM (7
interleaved runs) found no change beyond its noise.

**Fixed after** (the next pull request for #345, once #353 and #354 had changed the same files): HE-AAC's scalar
paths. `QmfFft.Transform`'s butterflies go by reference instead of a slice a group, and `Vectors.SumOfProducts` (the QMF
windows) takes four sums at a time with each term's offsets read once; each sum is the same chain as before, so the
output is bit-exact (VectorFft_MatchesScalarExactly and SumOfProducts_MatchesMultiplyAddATerm compare them with the
vector paths and the definition; both builds hash the same on all 23 AAC samples on Mono). CoreCLR reaches them only
without intrinsics. On Mono 6.12, medians of five interleaved runs against main: heaac-5.1 2.38 to 1.71 ms a frame
(1.39 times), heaacv2-stereo 0.78 to 0.60 (1.30 times), AAC LC unchanged (it has no SBR). PACKAGE.md's table predates it.

**Then the table properties** (a third pull request for #345): CABAC's `CabacTables.RangeTabLps`, `Cabac.MpsNext` and
`LpsNext` (read at every bin, in `Decide`), deblocking's `Tc0` (every filtered line) and the context offsets by block
category (`CodedBlockFlagCatOffset`, `SignificantCatOffset`, `AbsLevelCatOffset`, every block) are `static readonly`
arrays in the Unity build: one declaration, the `ReadOnlySpan<byte>` property under `#if NET` and the array otherwise,
so the data is written once; the reads that index stay as they are, and `Decide`'s three `Unsafe.Add` reads have an
indexed form for the Unity build. ManagedCodecs.H264.dll for net10.0 is the same file byte for byte. On Mono 6.12,
medians of five interleaved runs against main: h264-b-pyramid 4.47 to 3.88 ms a frame (1.15 times), h264-main-p-refs
3.18 to 2.86 (1.11), h264-high-default 4.27 to 3.91 (1.09); the CAVLC streams, where only `Tc0` was hot, are unchanged
(h264-bench-720p 39.7 and 39.4, h264-p-refs 2.59 and 2.60); both builds hash the same on all 337 H.264 samples.

**Left** (not fixed here):

- MJPEG's `ZigZag` and CABAC's `ScanPositionInc`, read once a block into a local (one getter call a block, about 1%).
- The remaining `Span`-to-`ReadOnlySpan` conversions and slices in hot code (about 1 to 3% of each profile).
- Unity's own Mono and IL2CPP (above).

## The design

- **One linked assembly**: a new project, `src/ManagedCodecs.Unity`, compiles the sources of Core, Containers, Mjpeg,
  H264 and Aac into one `ManagedCodecs.Unity.dll` for netstandard2.1. The existing projects stay net10.0 only, and
  their packages, public API files and package validation are untouched. In the shared sources, `#if NET` goes around
  every vector path and its dispatch (the scalar reference then runs), and around the `VectorZip` file. The InlineArray
  becomes a type both builds can compile. One `Polyfills.cs`, compiled only into the Unity project, holds the missing
  APIs. A Unity user drops in one DLL (or takes the package through NuGetForUnity), and no `InternalsVisibleTo` is
  needed across packages. The public API is the same types in the one assembly; it gets a `PublicAPI.*.txt` of its own.
- **C# in the shared code**: anything the current compiler accepts for netstandard2.1 with the polyfills, so the
  code's style stays as it is. Runtime-dependent features (static abstract members, `InlineArray`, ref fields) only
  inside `#if NET`. A build of the Unity project in CI enforces it: a new use breaks that build.
- **Tests**: the conformance tests run a second time against the netstandard2.1 assembly on net10.0, through a test
  project property that references `ManagedCodecs.Unity` instead of the five projects. That proves the scalar build
  bit-exact on every sample. A small Mono job (apt's `mono-complete`, the runtime family Unity's is built on) decodes a
  few samples of each codec with a console harness and checks their MD5s against the references. IL2CPP is not tested
  in CI (it needs a Unity install); the README says so.
- **Package**: `ManagedCodecs.Unity` on nuget.org, at the one version every package shares, released by `release.yml`
  with the rest. Its README states the speed (measured, see below) and that it is the scalar build.
- **Threading**: `H264Decoder.Threads` uses `Thread` and `Monitor`, which Mono and IL2CPP have; WebGL builds have no
  threads, so there it must stay at 1 (documented, not enforced).

## Work, one pull request each

0. **H.264 without generic math**, before the rest and in the net10.0 build: the constraint becomes `unmanaged`, and
   the conversions go through `Samples` helpers that dispatch on `typeof(TPixel) == typeof(byte)` with `Unsafe.As`,
   which the JIT folds per instantiation as it folds the byte SIMD paths' checks today. It rewrites the hot path, so
   it is a speed pull request: a draft until `compare.yml` (two runs) shows no regression on x64 and ARM64.
1. **The build**: `src/ManagedCodecs.Unity`, the `#if NET` guards, the polyfills, the InlineArray change. Then the
   real speed of the scalar build on CoreCLR and on Mono (mcdec's samples, measured as in README's Benchmarking),
   written into README before anything ships. If it is far off the proxy, that comes back to the owner first.
2. **Tests**: the conformance tests against the netstandard2.1 assembly in ci.yml, and the Mono smoke job.
3. **Package**: packing, `release.yml`, the package README, CHANGELOG.

## Decided

Asked with the multiple-choice prompt; the owner took each proposal (2026-10-31).

- **Generic math. Decided** (asked once the first trial build of the whole tree found it): rewrite H.264's `TPixel`
  conversions first, as pull request 0. The alternatives were a Unity build without H.264 at first, and parking Unity
  until it ships CoreCLR.
- **Sharing the sources. Decided:** one linked assembly, `ManagedCodecs.Unity.dll`, from the existing sources with
  `#if NET` around the vector paths. The alternative was multi-targeting each project (net10.0;netstandard2.1), which
  puts both builds into every package rather than a separate one.
- **CI. Decided:** the conformance tests against the netstandard2.1 assembly on CoreCLR, and a Mono smoke job. The
  alternative was CoreCLR only.
- **Name. Decided:** `ManagedCodecs.Unity`. The alternative was `ManagedCodecs.NetStandard`, which names the target
  rather than its user.
- **Speed. Decided:** measure the real scalar build on CoreCLR and Mono in the first pull request and put the result
  in README before the package ships. The alternative was to ship with the proxy's range.
- **Shipping at Mono's speed. Decided** (asked once the Mono job measured it, since it is far off the proxy): ship,
  with the measured numbers in the package's page, README and here, and the scope stated (audio, MJPEG and small
  cutscenes; not HD H.264). The alternatives were holding the package for a Mono speed round (on a Mono that isn't
  Unity's), and a package without H.264.
