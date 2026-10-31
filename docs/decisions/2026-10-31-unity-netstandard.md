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
- **Runtime features**: static abstract interface members (2 files, the zip interleaves of `VectorZip`, vector code
  only) and one `[InlineArray]` (a fixed buffer or an array does the same).
- **Library APIs**: polyfills cover them. A trial build of ManagedCodecs.Core for netstandard2.1, with the vector file
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

1. **The build**: `src/ManagedCodecs.Unity`, the `#if NET` guards, the polyfills, the InlineArray change. Then the
   real speed of the scalar build on CoreCLR and on Mono (mcdec's samples, measured as in README's Benchmarking),
   written into README before anything ships. If it is far off the proxy, that comes back to the owner first.
2. **Tests**: the conformance tests against the netstandard2.1 assembly in ci.yml, and the Mono smoke job.
3. **Package**: packing, `release.yml`, the package README, CHANGELOG.

## Decided

Asked with the multiple-choice prompt; the owner took each proposal (2026-10-31).

- **Sharing the sources. Decided:** one linked assembly, `ManagedCodecs.Unity.dll`, from the existing sources with
  `#if NET` around the vector paths. The alternative was multi-targeting each project (net10.0;netstandard2.1), which
  puts both builds into every package rather than a separate one.
- **CI. Decided:** the conformance tests against the netstandard2.1 assembly on CoreCLR, and a Mono smoke job. The
  alternative was CoreCLR only.
- **Name. Decided:** `ManagedCodecs.Unity`. The alternative was `ManagedCodecs.NetStandard`, which names the target
  rather than its user.
- **Speed. Decided:** measure the real scalar build on CoreCLR and Mono in the first pull request and put the result
  in README before the package ships. The alternative was to ship with the proxy's range.
