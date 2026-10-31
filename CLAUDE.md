# ManagedCodecs

Pure managed C# media decoders: no native binaries, Native AOT friendly. MJPEG (bit-exact with FFmpeg), H.264
Constrained Baseline, Main and High (CAVLC and CABAC, I/P/B, weighted prediction, 8x8 transform, scaling matrices,
MBAFF, field pictures, monochrome, 4:2:2, 4:4:4 (also separate colour planes), 9 to 14 bits, lossless, bit-exact), AAC LC (within 2^-17 of FFmpeg's float output, measured 3e-7), HE-AAC (SBR and parametric stereo, 10 to 34 bands; FFmpeg's single precision limits the match, docs/conformance.md), AVI, MP4
and ADTS demuxing. README.md has the layout, the status and what is still open;
docs/conformance.md what each feature is checked against.

## Core idea

Correct first, then fast. Every decoder is checked against FFmpeg sample by sample (frames by MD5, packets by
timestamps and MD5, audio by value), and every fast path (SIMD, FFT, direct reads) keeps a scalar or by-the-definition
reference that a test compares it with on random input. Implement from the specs (ITU-T T.81, ITU-T H.264,
ISO/IEC 14496-3 and -12), never by porting FFmpeg's LGPL code; README's License section lists the exceptions (the MJPEG
IDCT's arithmetic, tables cross-checked against Apache-2.0 decoders).

## Working on this repository

- **PR pipeline**: work on a branch, push, open a PR, and turn auto-merge on at once (as in ShotDetector; needs
  "Allow auto-merge" in the repository settings). Drafts excepted, and a PR the owner must decide on says so in its
  body and waits for the answer. Release PRs are excepted too: they never get auto-merge, the owner merges them
  (merging publishes to nuget.org, where a version can be unlisted but never deleted; the `release` environment
  waits for the owner's approval as well). Don't push to main directly; branch protection requires `build`,
  `build-arm64`, `wasm` and `aot` (ci.yml) and `mutation` (mutation.yml, which reports on docs-only PRs too).
- **Releases**: CHANGELOG.md's "## Unreleased" section collects what is merged and not released (add a line there in a
  PR a consumer would notice); a release is a PR that renames it to "## X.Y.Z – date" (from 0.1.0: a public API change
  only in a new minor version, a patch only fixes), and `release.yml` then tests, packs every package at that version, publishes to nuget.org (trusted
  publishing) and tags it. Every package has the one version. The release PR also runs `tools/ship-public-api.py`
  (each package's PublicAPI.Unshipped.txt into its Shipped). Once `release.yml` has published, a PR of its own sets
  `PackageValidationBaselineVersion` (Directory.Build.props) to the new version (the release PR can't: its version
  is not on nuget.org while its checks run), deleting any `CompatibilitySuppressions.xml` that only covered breaks up
  to it; until then a member the release added could be removed unnoticed.
- **Public API**: Microsoft.CodeAnalysis.PublicApiAnalyzers lists every src/ package's public API in its
  `PublicAPI.Shipped.txt` (the last release's) and `PublicAPI.Unshipped.txt` (changes since, a removal as
  `*REMOVED*<line>`); a public member that isn't listed fails the build (RS0016, RS0017). Adding one:
  `dotnet format analyzers src/<project> --diagnostics RS0016 --severity info` writes its lines into Unshipped. Every
  public change is an API decision, and from 0.1.0 a breaking one waits for the next minor version: CHANGELOG says it
  under "Unreleased". Package validation (Directory.Build.props, the `Pack` step in ci.yml) compares each package with
  `PackageValidationBaselineVersion` on nuget.org (the last release) and fails on a break (CP0002 and others); a
  deliberate one, for a new minor version only, is accepted with `dotnet pack -p:ApiCompatGenerateSuppressionFile=true`,
  which writes the project's `CompatibilitySuppressions.xml` into that PR.
- **Files an open PR changes**: before editing a file, check the open PRs for one that changes it; if one does, wait
  for it to merge and branch from main after it, rather than editing the same lines in parallel.
- **Worktrees**: `git worktree add .claude/worktrees/<name> -b <branch> origin/main` (ignored). Sessions working at
  the same time each use their own worktree, never one checkout, so a commit can't land on another's branch.
- **Advisor**: when an advisor tool is available, call it before design decisions or recommendations, before
  splitting work across agents or sessions, and before declaring a larger task done.
- **Asking the user**: whenever you need input from the user (a choice, an approval, whether to go on), ask with
  the multiple-choice prompt (AskUserQuestion), not with a question at the end of a reply. That includes what to do
  next: once a PR merges (or whenever a reply would end waiting for "what's next"), prompt the next choice at once,
  with the advisor's recommendation first, rather than inviting the user to ask; a stop the user chose ends with that
  prompt too, never with a list of what is left.
- **Before pushing**: `tools/make-samples.sh` (once; needs ffmpeg with libx264 and ImageMagick) and
  `dotnet test ManagedCodecs.slnx`; for SIMD changes also with `DOTNET_EnableHWIntrinsic=0` (CI runs both), and
  `tools/aot-check.sh` and `tools/aot-check.sh x86-64-v3` (Native AOT against the JIT on every sample, about a minute
  each; CI's `aot` job).
- **New feature, new sample**: add it to tools/make-samples.sh with a comment saying what it isolates, and check
  that it really exercises the feature (the AAC decoder's `Statistics`, `ffmpeg -bsf:v trace_headers` for H.264)
  before relying on it. Encoders often never emit a tool: which ones do, and the scripts that patch or rewrite
  streams, are in src/ManagedCodecs.H264/CLAUDE.md and src/ManagedCodecs.Aac/CLAUDE.md. Cover the rest with unit
  tests and say so in the docs.
- **Mutation checks**: `tools/mutation/mutate.py` (CI: `mutation.yml`) breaks each guard in
  `tools/mutation/mutations.py` and needs a test to fail. A new guard of that kind (a frame threading wait, code that
  reproduces FFmpeg rather than the spec, a rule only some streams reach) gets an entry; one that survives means a
  missing test, not a mutation to drop. `--only <text>` runs some; it undoes each with `git checkout`, so run it in a
  worktree with the mutated files committed.
- **Code style**: StyleBro (analyzers on build; rules in `.editorconfig`, chosen by `bro init` from this
  code's own conventions). The pre-commit hook formats the staged .cs files (`git config core.hooksPath .githooks`
  once per clone); by hand: `dotnet tool run bro format ManagedCodecs.slnx`. CI checks it with
  `--verify-no-changes`. The tool's version (.config/dotnet-tools.json) and the analyzers' (Directory.Packages.props)
  move together, by hand while StyleBro is a prerelease. Package versions are central (Directory.Packages.props).
- **Timing**: `tools/compare.sh <file> <loops>` runs `mcdec` (tools/ManagedCodecs.Cli) and
  `ffmpeg -benchmark -threads 1` on the same file (README, Benchmarking); `tools/bench.py` runs README's whole list,
  interleaved, and `--base <mcdec.dll>` one build against another. Its first ratio is mcdec's whole process over FFmpeg's; on the 50-frame
  H.264 clips .NET's startup and JIT were about 45% of that (FFmpeg's has almost none), so judge decoding by the
  "steady" columns (mcdec's last loop), noisier on short clips: compare medians. `compare.yml` (dispatch it on the branch) does that on
  hosted x64 and ARM64 runners, quieter than a cloud VM, and `profile.yml` runs perf there on one sample (the only
  profile of ARM64 code; on x64, `DOTNET_EnableAVX2=0` runs the 128-bit paths ARM64 takes); `benchmark.yml` runs weekly and keeps the history (the
  `benchmarks` branch: a ratio moving there across runs is a regression, one run's number is not). Cloud VMs are noisy: compare before/after
  on one machine, repeat runs, and don't trust one number. Comparing two builds: interleave their runs and
  alternate which goes first (with the same one first in every pair, an A/A test of one build against itself once
  showed a 10% "regression"), and `DOTNET_TieredCompilation=0` takes JIT tiering out of the noise.
  Changes of 1 to 2% are below the noise: count instructions instead, `DOTNET_TieredCompilation=0 valgrind
  --tool=callgrind --smc-check=all dotnet <mcdec.dll> <file> --loops N` (repeats to 1e-6; take loops 4 minus loops
  1 for the decode alone; about 40 s for a 320x240 sample). It does not see cache effects. Check first that it
  measures optimized code: under valgrind (runtime 10.0.13) the JIT's full-optimization compiles fail and fall back to
  MinOpts, where every span index is a call (the QMF FFT's profile, all MinOpts, was 7 times the native instruction
  count); `DOTNET_JitDisasmSummary=1 DOTNET_JitStdOutFile=<file>` under valgrind lists each method's level, and
  without FullOpts or Tier1 for the hot ones, time the code instead (a Stopwatch microbenchmark, interleaved A/B).
  `perf` (apt's linux-tools-generic; run `/usr/lib/linux-tools-*/perf`) samples optimized code by function:
  `DOTNET_PerfMapEnabled=1 DOTNET_EnableWriteXorExecute=0 DOTNET_TieredCompilation=0 perf record -e cpu-clock -F 4000
  dotnet <mcdec.dll> <file> --loops 100`, then `perf report --no-children --sort symbol` (without
  `EnableWriteXorExecute=0` the JIT's code shows as bare addresses). A function's share falling between builds confirms
  a gain the wall clock can't separate from noise.
  A gain larger than the changed functions' share of the frame is noise, however consistent: check that ceiling
  before believing an A/B (two streams agreeing on -9 and -10% for the vector inverse transforms were noise and the
  change was reverted; timed by wall clock later, the transforms are 2.5 to 2.8 times faster per block but about 2%
  of a frame, so about 1.5% in all). Nor rank hot spots by `dotnet-trace`'s thread-time sampling: its samples land at
  safe points, and it put 23 to 48% of H.264 decoding in `Picture.ReportProgress`'s lock, whose removal changed
  nothing. Stopwatch timers around a decoder's stages (in a scratch copy, never pushed) and microbenchmarks of the hot
  functions ranked them instead.

## Gotchas beside the code

Codec- and tool-specific notes are in a CLAUDE.md beside the code they concern, read with it:
src/ManagedCodecs.H264 (also for its samples, encoders and scripts: libx264, OpenH264, JM; MBAFF, field pictures,
frame threading), src/ManagedCodecs.Aac (AAC, HE-AAC, the FDK encoder), src/ManagedCodecs.Containers (MP4 edit lists
and timestamps), src/ManagedCodecs.Mjpeg (interlaced MJPEG), tools/ManagedCodecs.Fuzz (fuzzing) and
tools/ManagedCodecs.Wasm (WebAssembly). Working on a codec's samples or tools without opening its source, read its
file first.

## Gotchas (each cost time once)

- `ffmpeg -i` turns decoded frames by an MP4 track's rotation (autorotate, on by default): frame references take
  `-noautorotate` (make-samples.sh), or a rotated sample's frames differ after the first.

- A worktree has no generated samples: link them with `ln -s <main checkout>/samples/*.* samples/`, without `-f`
  (`ln -sf samples/*` once replaced the tracked `samples/README.md` with a link, and linked directories into themselves).
  A worktree made before a sample was generated has no link to it: a new theory row then reads "no data". References
  generated inside a worktree (a new `.audioframes.csv`) go with it when it is removed: generate them in the main
  checkout's `samples/` too, and link them, or the next worktree reads "no data" for them.
- The cloud sandbox reaches download.blender.org but not archive.org or test-videos.co.uk (CI reaches all three).
- Temporary instrumentation (coverage counters, traces): commit the real work first. `git checkout <file>` to
  remove the instrumentation otherwise also throws away the file's other uncommitted changes, and so does
  reversing a `git diff` taken against HEAD.
- make-samples.sh names references by the sample's stem: an `.aac` and an `.mp4` of the same stem overwrite each
  other's `.packets.framemd5` and `.f32` (name a remux `-remux.mp4`).
- Short samples are mostly JIT: `jm-paff-b` (10 frames) took 3960 µs a frame at 20 loops and 2177 at 100. Time
  them at 100 loops or more. Inlining a hot callee moves its perf share into its callers rather than removing it:
  judge such a change by the share of a function it leaves alone (`ResidualBlock`), which rises if the total fell.
- perf shares move by about a point between runs of one build (the HF generator read 6.7, 7.6 and 8.0% of 5.1 HE-AAC
  on three runs): profile the builds before and after back to back, and take a difference under a point as noise.
- `ffmpeg ... -f framemd5` moves timestamps to start at 0; packet references need `-copyts`. Decoded-frame
  references are in the encoder's time base (1/25 for MJPEG), not the stream's: compare times as rationals.
- framemd5 lines can carry side data after the hash (`S=1, size, md5` on AAC's first packet): the hash is field 6.
- A `stackalloc` span can't be captured by a local function or lambda, and a span variable declared without an
  initializer can't later hold stack memory: initialize it from the stack buffer.
- Whole-vector loads read past the last needed byte: give buffers slack, and check bounds against the furthest load
  (a chroma row read from x + 1 needs 17 bytes, not 16).
- Generated samples are not the same bytes everywhere: libx264 encodes `h264-mbaff-p-motion` differently on CI's
  ARM64 runner than on x64, and `h264-main-slices` even between two x64 machines (sha256 compared in a scratch CI
  run). Bit-exact checks don't notice (each runner makes its own references), but a test with a floor on the content
  (a concealment PSNR) does: give it a committed sample (`samples/jm/`, `samples/openh264/`), which decodes and conceals
  to the same MD5 on both architectures.
- ARM64 (CI's `build-arm64`): FFmpeg's default JPEG IDCT there is NEON, which rounds unlike its C one; references
  take `-idct simple` (the same as the default on x64). An `Sse2.`/`Avx2.` call reached behind only
  `Vector128.IsHardwareAccelerated` throws PlatformNotSupportedException on ARM64: dispatch inside the function.
- Hardware counters (`perf stat -e cycles,instructions`): GitHub's ARM64 runner has them, the x64 runner and the cloud
  VM do not (`<not supported>`), and the x64 runner's 4 vCPUs are 2 cores with SMT; `perf record -e context-switches -g`
  works everywhere and shows where threads block.
- xunit theory rows filter by `DisplayName~<sample>`, not `FullyQualifiedName` (`dotnet test --filter`).
