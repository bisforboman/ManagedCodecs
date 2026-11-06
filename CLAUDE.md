# ManagedCodecs

Pure managed C# media decoders (MJPEG, H.264, AAC LC and HE-AAC; AVI, MP4, Matroska/WebM and ADTS demuxing; thumbnails), no native
binaries: Native AOT, WebAssembly and Unity too. What each decodes and what is open: README.md, not copied here.

| Topic | Where |
|---|---|
| What each package decodes, what is open | README.md (Scope, Status, Open) |
| The projects and main scripts (`tools/`, `benchmarks/ManagedCodecs.Benchmarks`: BenchmarkDotNet) | README.md, Layout |
| What each feature is checked against, which encoder made its samples | docs/conformance.md |
| Timing a change, profiling, counting instructions | docs/benchmarking.md |
| Design decisions | docs/decisions/ |

## Core idea

Correct first, then fast. Every decoder is checked against FFmpeg sample by sample (frames by MD5, packets by
timestamps and MD5, audio by value), and every fast path (SIMD, FFT, direct reads) keeps a scalar or by-the-definition
reference that a test compares it with on random input. Implement from the specs (ITU-T T.81, ITU-T H.264,
ISO/IEC 14496-3 and -12), never by porting FFmpeg's LGPL code; README's License section lists the exceptions (the MJPEG
IDCT's arithmetic, tables cross-checked against Apache-2.0 decoders).

## Working on this repository

- **PR pipeline**: work on a branch, open a PR, and turn auto-merge on at once (needs "Allow auto-merge" in the
  settings). Not for drafts, nor for a PR the owner must decide on (its body says so; it waits for the answer), nor
  release PRs: the owner merges those (nuget.org versions can't be deleted; the `release` environment waits for the
  owner too). Never push to main. Required checks: `build`, `build-arm64`, `wasm`, `aot`, `unity` (ci.yml) and
  `mutation` (mutation.yml, on docs-only PRs too).
- **Releases**: a PR a consumer would notice adds a line under CHANGELOG.md's "## Unreleased". A release PR renames
  it "## X.Y.Z – date" (a public API change only in a new minor version, a patch only fixes) and runs
  `tools/ship-public-api.py` (Unshipped into Shipped); `release.yml` then tests, packs every package at that one
  version, publishes (trusted publishing) and tags. After it has published, a PR of its own sets
  `PackageValidationBaselineVersion` (Directory.Build.props) to it (the release PR can't: its version isn't on
  nuget.org yet; until then a member the release added could be removed unnoticed) and deletes any
  `CompatibilitySuppressions.xml` that only covered breaks up to it.
- **Public API**: every public change is an API decision, and a breaking one waits for the next minor version
  (CHANGELOG says so under "Unreleased"). Each src/ package lists its API in `PublicAPI.Shipped.txt` (last release)
  and `PublicAPI.Unshipped.txt` (since, a removal as `*REMOVED*<line>`); an unlisted member fails the build (RS0016,
  RS0017). `dotnet format analyzers src/<project> --diagnostics RS0016 --severity info` adds it; run it on
  `src/ManagedCodecs.Unity` too, which compiles every package's sources (`tools/unity-public-api.py` checks). Package
  validation (ci.yml's `Pack`) fails on a break against `PackageValidationBaselineVersion`; a deliberate one (new
  minor only) is accepted with `dotnet pack -p:ApiCompatGenerateSuppressionFile=true`, committing the
  `CompatibilitySuppressions.xml` it writes.
- **Files an open PR changes**: before editing a file, check the open PRs; if one changes it, wait for it to merge and
  branch from main after it.
- **Worktrees**: `git worktree add .claude/worktrees/<name> -b <branch> origin/main` (ignored): one per session working
  at the same time, so a commit can't land on another's branch.
- **Advisor**: when one is available, call it before design decisions or recommendations, before splitting work
  across agents or sessions, and before declaring a larger task done.
- **Asking the user**: any input (a choice, an approval, whether to go on) goes through the multiple-choice prompt
  (AskUserQuestion), never a question at the end of a reply. So does what to do next: once a PR merges, or whenever a
  reply would end waiting for "what's next", prompt at once, the advisor's recommendation first; a stop the user chose
  ends with that prompt too, never with a list of what is left.
- **Before pushing**: `tools/make-samples.sh` (once; needs ffmpeg with libx264 and ImageMagick) and
  `dotnet test ManagedCodecs.slnx`; for SIMD changes also with `DOTNET_EnableHWIntrinsic=0` (CI runs both), and
  `tools/aot-check.sh` and `tools/aot-check.sh x86-64-v3` (Native AOT against the JIT on every sample, a minute each;
  CI's `aot`). CI's `wasm` and `unity` jobs run `tools/wasm-check.sh` and `tools/unity-check.sh` (the Unity build on
  Mono 6.12+ against net10.0, every sample).
- **Cloud sessions**: `.claude/hooks/session-start.sh` installs what is missing of the .NET 10 SDK, ffmpeg and
  ImageMagick (Ubuntu's packages), restores the tools and the solution and sets `core.hooksPath`; it leaves
  `tools/make-samples.sh` (about 6 minutes there) to you and says so while `samples/` has none.
- **New feature, new sample**: add it to tools/make-samples.sh with a comment saying what it isolates, and check that
  it really exercises the feature (the AAC decoder's `Statistics`, `ffmpeg -bsf:v trace_headers`). Encoders often
  never emit a tool: which do, and the scripts that rewrite streams, are in the H.264 and AAC CLAUDE.md files. Cover
  the rest with unit tests and say so in the docs.
- **Mutation checks**: `tools/mutation/mutate.py` breaks each guard in `tools/mutation/mutations.py` and needs a test
  to fail. A new guard of that kind (a frame threading wait, code that reproduces FFmpeg rather than the spec, a rule
  only some streams reach) gets an entry; a survivor means a missing test, not a mutation to drop. `--only <text>`
  runs some; it undoes each with `git checkout`, so run it in a worktree with the mutated files committed.
- **Code style**: StyleBro (analyzers on build, rules in `.editorconfig`). The pre-commit hook formats staged .cs files
  (`git config core.hooksPath .githooks` once per clone); by hand `dotnet tool run bro format ManagedCodecs.slnx`, CI
  with `--verify-no-changes`. The tool (.config/dotnet-tools.json) and the analyzers (Directory.Packages.props, where
  every package version is) move together, by hand while StyleBro is a prerelease; `upstream.yml` says when.
- **Timing**: docs/benchmarking.md. Above all: time two builds interleaved, alternating which goes first (an A/A test
  with one always first once showed a 10% "regression"); a gain larger than the changed functions' share of the frame
  is noise, however consistent.
- **Other workflows**: `realworld.yml` (nightly: real films and MJPEG against FFmpeg, `tools/realworld.py`),
  `benchmark.yml` (weekly), `compare.yml` and `profile.yml` (on demand), `upstream.yml` (weekly issues: a newer
  StyleBro, a new release of an encoder behind the committed samples), `vulnerable-packages.yml` (weekly: NuGet
  packages with advisories, direct or transitive, `tools/vulnerable-packages.py`; one issue per streak; every build
  already fails on them, NU1901-NU1904 being errors: docs/decisions/2026-11-06-vulnerable-packages.md), `codeql.yml`
  (code scanning, not required).

## Gotchas beside the code

Read the CLAUDE.md beside the code you work on, also for a codec's samples or tools without opening its source:

- src/ManagedCodecs.H264: which encoder writes what (libx264, OpenH264, JM) and the scripts that rewrite streams;
  CABAC, MBAFF, field pictures, high bit depths, monochrome, concealment, frame threading.
- src/ManagedCodecs.Aac: which encoder writes what (FFmpeg's, FDK); edit lists and priming, channel layouts, SBR's
  precision.
- src/ManagedCodecs.Mjpeg: interlaced MJPEG, gray samples, damaged images (FFmpeg as the oracle), the packet-size rule,
  the aspect ratio ffprobe reports.
- src/ManagedCodecs.Containers: MP4 timestamps and edit lists, fragmented MP4's packet durations, what FFmpeg's
  Matroska muxer never writes and what ffprobe fills in.
- tools/ManagedCodecs.Fuzz: fuzzing with AFL++ and SharpFuzz, reproducing findings.
- tools/ManagedCodecs.Wasm: the WebAssembly build and its runtime's bugs.

## Gotchas (each cost time once)

- `ffmpeg -i` turns decoded frames by an MP4 track's rotation (autorotate): frame references take `-noautorotate`
  (make-samples.sh).
- A worktree has no generated samples: link them with `ln -s <main checkout>/samples/*.* samples/`, without `-f`
  (`ln -sf samples/*` once replaced the tracked `samples/README.md` and linked directories into themselves). A sample
  generated after the worktree has no link (its theory row reads "no data"), and references generated inside a
  worktree (a new `.audioframes.csv`) go when it is removed: generate them in the main checkout too, and link them.
  A new reference suffix needs its .gitignore line in the same commit, or `git add -A` in a worktree commits the
  links, which dangle in CI and fail make-samples.sh.
- The cloud sandbox reaches download.blender.org but not archive.org or test-videos.co.uk (CI reaches all three).
- Temporary instrumentation (coverage counters, traces): commit the real work first, or `git checkout <file>` (or
  reversing a `git diff` against HEAD) throws away the file's other uncommitted changes with it.
- make-samples.sh names references by the sample's stem: an `.aac` and an `.mp4` of one stem overwrite each other's
  `.packets.framemd5` and `.f32` (name a remux `-remux.mp4`).
- `ffmpeg ... -f framemd5` moves timestamps to start at 0; packet references need `-copyts`. Decoded-frame
  references are in the encoder's time base (1/25 for MJPEG), not the stream's: compare times as rationals.
- framemd5 lines can carry side data after the hash (`S=1, size, md5` on AAC's first packet): the hash is field 6.
- A `stackalloc` span can't be captured by a local function or lambda, and a span variable declared without an
  initializer can't later hold stack memory: initialize it from the stack buffer.
- Whole-vector loads read past the last needed byte: give buffers slack and check bounds against the furthest load (a
  chroma row read from x + 1 needs 17 bytes).
- Generated samples differ between machines (libx264's `h264-mbaff-p-motion` on ARM64 and x64, `h264-main-slices`
  even on two x64s). Bit-exact checks don't notice (each runner makes its own references), but a test with a floor
  on the content (a concealment PSNR) does: give it a committed sample (`samples/jm/`, `samples/openh264/`).
- ARM64 (CI's `build-arm64`): FFmpeg's default JPEG IDCT there is NEON, which rounds unlike its C one; references
  take `-idct simple` (the same as the default on x64). An `Sse2.`/`Avx2.` call reached behind only
  `Vector128.IsHardwareAccelerated` throws PlatformNotSupportedException on ARM64: dispatch inside the function.
- xunit theory rows filter by `DisplayName~<sample>`, not `FullyQualifiedName` (`dotnet test --filter`).
