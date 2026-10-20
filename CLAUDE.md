# ManagedCodecs

Pure managed C# media decoders: no native binaries, Native AOT friendly. MJPEG (bit-exact with FFmpeg), H.264
Constrained Baseline and Main (CAVLC and CABAC, I/P/B, weighted prediction, bit-exact), AAC LC (within 2^-17 of FFmpeg's float output, measured 3e-7), AVI and MP4
demuxing. README.md has the layout, the roadmap and what is not supported yet.

## Core idea

Correct first, then fast. Every decoder is checked against FFmpeg sample by sample (frames by MD5, packets by
timestamps and MD5, audio by value), and every fast path (SIMD, FFT, direct reads) keeps a scalar or by-the-definition
reference that a test compares it with on random input. Implement from the specs (ITU-T T.81, ITU-T H.264,
ISO/IEC 14496-3 and -12), never by porting FFmpeg's LGPL code; README's license note lists the exceptions (the MJPEG
IDCT's arithmetic, tables cross-checked against Apache-2.0 decoders).

## Working on this repository

- **PR pipeline**: work on a branch, push, open a PR, and turn auto-merge on at once (as in ShotDetector; needs
  "Allow auto-merge" in the repository settings). Drafts excepted, and a PR the owner must decide on says so in its
  body and waits for the answer. Don't push to main directly; once branch protection is on it requires `build`
  (ci.yml).
- **Files an open PR changes**: before editing a file, check the open PRs for one that changes it; if one does, wait
  for it to merge and branch from main after it, rather than editing the same lines in parallel.
- **Worktrees**: `git worktree add .claude/worktrees/<name> -b <branch> origin/main` (ignored). Sessions working at
  the same time each use their own worktree, never one checkout, so a commit can't land on another's branch.
- **Advisor**: when an advisor tool is available, call it before design decisions or recommendations, before
  splitting work across agents or sessions, and before declaring a larger task done.
- **Asking the user**: whenever you need input from the user (a choice, an approval, whether to go on), ask with
  the multiple-choice prompt (AskUserQuestion), not with a question at the end of a reply.
- **Before pushing**: `tools/make-samples.sh` (once; needs ffmpeg with libx264 and ImageMagick) and
  `dotnet test ManagedCodecs.slnx`; for SIMD changes also with `DOTNET_EnableHWIntrinsic=0` (CI runs both).
- **New feature, new sample**: add it to tools/make-samples.sh with a comment saying what it isolates, and check
  that it really exercises the feature (the AAC decoder's `Statistics`, `ffmpeg -bsf:v trace_headers` for H.264)
  before relying on it. Encoders often never emit a tool (libx264: no ref list modification, I_PCM, B partitions
  below 8x8, MMCO other than 1 (which needs b-pyramid), explicit weights in B slices or cabac_init_idc other
  than 0; FFmpeg's AAC: no pulse data); cover those with unit tests and say so in the docs.
  `tests/ManagedCodecs.Tests/H264/CabacEncoder.cs` (a CABAC encoder and bit writer) builds such H.264 streams.
- **Code style**: StyleBro (analyzers on build; rules in `.editorconfig`, chosen by `stylebro-migrate init` from this
  code's own conventions). The pre-commit hook formats the staged .cs files (`git config core.hooksPath .githooks`
  once per clone); by hand: `dotnet tool run stylebro-migrate format ManagedCodecs.slnx`. CI checks it with
  `--verify-no-changes`. The tool's version (.config/dotnet-tools.json) and the analyzers' (Directory.Packages.props)
  move together, by hand while StyleBro is a prerelease. Package versions are central (Directory.Packages.props).
- **Timing**: `tools/compare.sh <file> <loops>` runs `mcdec` (tools/ManagedCodecs.Cli) and
  `ffmpeg -benchmark -threads 1` on the same file (README, Benchmarking). Cloud VMs are noisy: compare before/after
  on one machine, repeat runs, and don't trust one number.

## Gotchas (each cost time once)

- Temporary instrumentation (coverage counters, traces): commit the real work first. `git checkout <file>` to
  remove the instrumentation otherwise also throws away the file's other uncommitted changes, and so does
  reversing a `git diff` taken against HEAD.
- Comparing CABAC with a reference decoder: OpenH264 builds in scratch (`make USE_ASM=No h264dec`), and a trace of
  every bin as (state, MPS, value) in both decoders, diffed, shows the first syntax element that differs.

- `ffmpeg ... -f framemd5` moves timestamps to start at 0; packet references need `-copyts`. Decoded-frame
  references are in the encoder's time base (1/25 for MJPEG), not the stream's: compare times as rationals.
- framemd5 lines can carry side data after the hash (`S=1, size, md5` on AAC's first packet): the hash is field 6.
- FFmpeg's `-ignore_editlist 1` gives every decoded AAC sample, priming included; without it the first 1024 go.
- Tables shared between sample rates (AAC's 44.1 and 48 kHz band offsets) are one array: never find a rate index by
  the array's identity.
- FFmpeg reads AAC channel configuration 7 as 7.1 (FL FR FC LFE BL BR SL SR), not the spec's "outside front" pair.
- A `stackalloc` span can't be captured by a local function or lambda, and a span variable declared without an
  initializer can't later hold stack memory: initialize it from the stack buffer.
- Whole-vector loads read past the last needed byte: give buffers slack, and check bounds against the furthest load
  (a chroma row read from x + 1 needs 17 bytes, not 16).
