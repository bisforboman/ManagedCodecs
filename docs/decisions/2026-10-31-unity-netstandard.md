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
