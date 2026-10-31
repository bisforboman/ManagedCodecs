# In-game video for C# engines (2026-10-31)

A look at issue #254, decided (below). The idea: one managed H.264 and
AAC decoder that plays the same on every platform a C# game ships to, where Unity's `VideoPlayer` uses each platform's
own decoder and MonoGame and FNA have little video support at all.

## What it would cost, engine by engine

The library targets net10.0 only and leans on what that runtime gives: `System.Runtime.Intrinsics` (`Vector128`,
`Vector256`, `AdvSimd`, `Sse2`) for every fast path, `InlineArray`, spans throughout, static abstract interface
members (the zip interleaves), Native AOT.

- **MonoGame and FNA**: both run on modern .NET, so a game project that targets net10.0 can reference the packages as
  they are. Nothing to port; the work is a sample that decodes into a `Texture2D` each frame (YUV to RGBA, or a shader
  that does the conversion, which is cheaper) and keeps audio in step through `DynamicSoundEffectInstance`.
- **Godot (C#)**: Godot's .NET projects are ordinary SDK-style projects on modern .NET, so the same holds if the
  project's target framework is raised to net10.0 (to be checked against the Godot version in use). Godot's C# can't
  export to the web, which removes the strongest "same everywhere" argument there.
- **Unity**: scripts run on Mono or IL2CPP with the .NET Standard 2.1 / .NET Framework API profile. The library would
  need a second target, netstandard2.1, without `System.Runtime.Intrinsics` (not there at all) and without the
  C# features that need the newer runtime: every SIMD path would need its scalar reference as the netstandard2.1 code,
  which already exists for each (the repository's rule), but would run several times slower. A large change for a
  slow result. Unity's planned move to CoreCLR would remove the problem; until a Unity release ships it, Unity is out.
- **Stride**: on modern .NET, like MonoGame.

## Is it fast enough

Measured with mcdec on Caminandes: Gran Dillama (1920x1080, 24 fps, H.264 High and AAC; the cloud VM, 4 vCPUs of a
2.1 GHz Xeon), video and audio decoded, frames dropped once decoded:

| Threads | Wall time | Frames a second | CPU time |
| ---: | ---: | ---: | ---: |
| 1 | 60.9 s | 57.5 | 58.7 s |
| 2 | 35.0 s | 100 | 62.5 s |
| 4 | 25.1 s | 140 | 75.8 s |

So 1080p30 takes about half of one core on one thread (30 / 57.5), with room on a phone-class CPU only at lower
resolutions. A hardware decoder costs almost nothing in CPU and battery; a game spends its frame budget elsewhere. A
managed decoder wins where the same output on every platform matters more than that cost: cutscenes on desktop and
console where the platform's decoder differs, tools and editors, deterministic replays.

## Decided

The owner (2026-10-31), against the recommendation to keep it parked:

- **What to do now. Decided:** build the MonoGame sample now: an MP4 played into a texture each frame, YUV converted
  to RGBA on the CPU and uploaded with `Texture2D.SetData` (a shader would be cheaper, but MonoGame's effect compiler,
  mgfxc, needs Wine on Linux, so it comes later if the CPU conversion is the bottleneck), audio through
  `DynamicSoundEffectInstance`, on modern .NET with no change to the packages; it builds in
  CI (a window and a GPU are not there, so it runs only on a developer's machine). The recommendation was to leave
  #254 parked with these numbers.
- **Unity. Decided:** multi-target, as a separate package: a netstandard2.1 build of the decoders with the scalar
  paths only (the references every fast path already has), for Mono and IL2CPP, slower than the net10.0 build and
  said so. Its own decision record first, as it touches every project: how the sources are shared (one project with
  two targets and `#if` around the vector paths, or linked files), which C# features the scalar code may use, how CI
  tests it (the conformance tests on netstandard2.1 through a net-framework-free test host), and the package name.
  The recommendation was no netstandard2.1 target until Unity ships its CoreCLR runtime. Its record: docs/decisions/2026-10-31-unity-netstandard.md (decided).
