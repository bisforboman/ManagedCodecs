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
  body and waits for the answer. Don't push to main directly; once branch protection is on it requires `build`
  (ci.yml) and `mutation` (mutation.yml, which reports on docs-only PRs too).
- **Releases**: CHANGELOG.md's "## Unreleased" section collects what is merged and not released (add a line there in a
  PR a consumer would notice); a release is a PR that renames it to "## X.Y.Z – date" (from 0.1.0: a public API change
  only in a new minor version, a patch only fixes), and `release.yml` then tests, packs every package at that version, publishes to nuget.org (trusted
  publishing) and tags it. Every package has the one version. The release PR also runs `tools/ship-public-api.py`
  (each package's PublicAPI.Unshipped.txt into its Shipped) and sets `PackageValidationBaselineVersion`
  (Directory.Build.props) to the last version on nuget.org, deleting any `CompatibilitySuppressions.xml` that only
  covered breaks up to it.
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
  `dotnet test ManagedCodecs.slnx`; for SIMD changes also with `DOTNET_EnableHWIntrinsic=0` (CI runs both).
- **New feature, new sample**: add it to tools/make-samples.sh with a comment saying what it isolates, and check
  that it really exercises the feature (the AAC decoder's `Statistics`, `ffmpeg -bsf:v trace_headers` for H.264)
  before relying on it. Encoders often never emit a tool (libx264: no ref list modification, I_PCM, B partitions
  below 8x8, MMCO other than 1 (which needs b-pyramid), explicit weights in B slices, cabac_init_idc other
  than 0, a second_chroma_qp_index_offset other than the first, scaling lists in the SPS or a Cr list of its own;
  FFmpeg's AAC: no pulse data (`tools/make-aac-pulse.py` writes a stream with it), no SBR or PS: HE-AAC comes from the FDK encoder, `tools/make-heaac-samples.sh`, its
  streams committed in `samples/heaac/`). A second encoder covers some: OpenH264 (`tools/make-openh264-samples.sh`, its
  streams committed in `samples/openh264/`) writes long-term references, MMCO 4 and 6, POC type 2, SVC, and
  frame_num gaps once temporal layers are dropped. A third, JM (`tools/make-jm-samples.sh`, committed in
  `samples/jm/`), writes field pictures (PAFF), alone or mixed with frame pictures or MBAFF frames, also in 4:2:2
  and 4:0:0, bit depths 9, 12 and 14, I_PCM (heavy noise, QP 14 to 15), cabac_init_idc 1 and 2
  (`ContextInitMethod=0 FixedModelNumber=N`), Cb and Cr QP offsets of their own (`CbQPOffset`, `CrQPOffset`), B
  partitions below 8x8 (by default) and scaling lists in the SPS, PPS or both (`QmatrixFile`,
  `ScalingMatrixPresentFlag`, `ScalingListPresentFlagN`); `tools/h264-add-weights.py` weights its field pictures afterwards, `tools/h264-add-mmco5.py` puts memory management operation 5 into a field picture; separate colour planes, which FFmpeg neither
  decodes nor muxes, go to `samples/jm-ref/` with JM's decoder's references, muxed by `tools/mux-h264.py` (JM handles them in
  frames only, crashing on fields or MBAFF: interlaced ones are three 4:0:0 encodes spliced by
  `tools/h264-splice-planes.py`, referenced by FFmpeg's gray decodes stacked)
  (`tools/make-jm-samples.sh <JM tree> <names>` makes only those samples). Cover
  the rest with unit tests and say so in the docs.
  `tests/ManagedCodecs.Tests/H264/ParameterSetRewriter.cs` rewrites a sample's SPS and PPS for such tests.
  `tests/ManagedCodecs.Tests/H264/CabacEncoder.cs` (a CABAC encoder and bit writer) builds such H.264 streams.
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

## Gotchas (each cost time once)

- A worktree has no generated samples: link them with `ln -s <main checkout>/samples/*.* samples/`, without `-f`
  (`ln -sf samples/*` once replaced the tracked `samples/README.md` with a link, and linked directories into themselves).
  A worktree made before a sample was generated has no link to it: a new theory row then reads "no data". References
  generated inside a worktree (a new `.audioframes.csv`) go with it when it is removed: generate them in the main
  checkout's `samples/` too, and link them, or the next worktree reads "no data" for them.
- The cloud sandbox reaches download.blender.org but not archive.org or test-videos.co.uk (CI reaches all three).
- A dts after its pts (a 'ctts' whose last two entries are more negative than the shift; docs/conformance.md, MP4):
  FFmpeg reports "Invalid timestamps" (`-v debug`) and keeps it for H.264, but fills a dts in from the pts for a codec
  without reordering (MJPEG, audio), which Mp4Demuxer doesn't. Its command line then drops the frame whose pts goes
  back, so such a sample has packet references only.
- Temporary instrumentation (coverage counters, traces): commit the real work first. `git checkout <file>` to
  remove the instrumentation otherwise also throws away the file's other uncommitted changes, and so does
  reversing a `git diff` taken against HEAD.
- Comparing CABAC with a reference decoder: OpenH264 builds in scratch (`make USE_ASM=No h264dec`), and a trace of
  every bin as (state, MPS, value) in both decoders, diffed, shows the first syntax element that differs.

- make-samples.sh names references by the sample's stem: an `.aac` and an `.mp4` of the same stem overwrite each
  other's `.packets.framemd5` and `.f32` (name a remux `-remux.mp4`).
- FFmpeg (6.1.1 and master, h264_refs.c MMCO_LONG) loses a long-term field pair's first field when the second field
  takes the same LongTermFrameIdx with MMCO 6 (`remove_long(h, j, 0)` clears both fields' reference bits, then only
  the current field's are set): it is no oracle for long-term marking in field pictures ("Missing reference picture"
  is the sign); JM's decoder is (`jm-paff-ltr*`, `samples/jm-ref/`, from a copy of JM's encoder patched to write it).
- Short samples are mostly JIT: `jm-paff-b` (10 frames) took 3960 µs a frame at 20 loops and 2177 at 100. Time
  them at 100 loops or more. Inlining a hot callee moves its perf share into its callers rather than removing it:
  judge such a change by the share of a function it leaves alone (`ResidualBlock`), which rises if the total fell.
- perf shares move by about a point between runs of one build (the HF generator read 6.7, 7.6 and 8.0% of 5.1 HE-AAC
  on three runs): profile the builds before and after back to back, and take a difference under a point as noise.
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
- libx264 (core 164) fails to parse `cqm8iy`/`cqm8py` in `-x264-params` ("Error parsing option", and encodes on
  with flat 8x8 lists); give scaling lists in a `cqmfile` instead, and check what was really sent.
- OpenH264's `h264enc` ignores `-cabac 1` in its default Baseline layer config (add `-dprofile 0 77`), takes
  bitrates in kbit/s, and skips frames under a tight bitrate without leaving frame_num gaps.
- FFmpeg trims MP4 audio by the edit list asymmetrically: exactly media_time samples at the start (skip-samples side
  data, across packets), but at the end it drops whole packets that start past the edit, cuts the last one's
  duration (rounded to nearest) and still decodes all its samples. With several edits (docs/conformance.md, MP4) a
  later audio edit starts at the next whole packet, every edit is preceded by discarded packets (a second of audio;
  video from the keyframe before), and an empty edit after a real one replays the media's start. `ffmpeg -c copy -f
  framemd5` rewrites decode timestamps that go back (an edit's discarded packets): compare packets with
  `ffprobe -show_packets -show_data_hash MD5` (make-samples.sh's `.packets.csv`).
- MBAFF: with CABAC, end_of_slice_flag comes only after the bottom macroblock of a pair (7.3.4). A skipped top
  macroblock's pair kind comes with the bottom one (CAVLC: the bit after mb_skip_run; CABAC: after the bottom one's
  skip flag), else it is inferred from the left, then the upper pair in the same slice: mark the macroblock as in
  the slice before inferring. Frame-to-field vertical motion halves with truncation towards zero (`/ 2`, not `>> 1`).
  libx264 with `constrained-intra=1` and interlacing writes intra modes FFmpeg and JM reject: not a test source. `ffmpeg -debug
  mb_type` marks field pairs with '=': the oracle for whether a sample mixes field and frame pairs (scrolling
  content comes out all field). The ITU PDF is not reachable from the cloud sandbox; JM (the ITU reference software,
  on GitHub) is, builds with `make -C ldecod CFLAGS="-fcommon -O2"`, and traces every syntax element; printing each
  CABAC bin's (state, MPS, range) in both decoders finds the first divergence.
- Field pictures in MP4 are one packet a field: FFmpeg's `-f rawvideo` output from such a file repeats frames to keep
  the packet rate (compare with `-f framemd5` or add `-fps_mode passthrough`); its frame takes the first field's pts.
  JM's encoder: `make -C lencod CFLAGS="-fcommon -O2"`, `PicInterlace=1` (fields) or `2` (adaptive), about a minute
  for 10 frames at 320x256; it crashes with `WeightedPrediction=1` or `WeightedBiprediction=2` and field pictures,
  with `SetFirstAsLongTerm=1` and field pictures (operation 3 for the second field where 6 belongs: `ltr-fields`
  patches a copy; MBAFF frames take it, and `PicInterlace=2` with the IDR picture coded
  as a frame: `IsLongTerm` is read only by temporal direct and implicit weights, so a long-term sample needs B
  pictures),
  and refuses `PicInterlace=2` with `MbInterlace=2` (`MbInterlace=1` works, every pair a field pair, with
  `ReferenceReorder=0 PocMemoryManagement=0`); in 4:0:0 it exits 100 after writing the stream. In field pictures
  without num_ref_idx_active_override_flag the active count is the PPS default as it is (FFmpeg and JM; not
  doubled). With `PicInterlace=2` it codes a clean testsrc2 source's frames
  all as frames in 4:2:2 (`YUVFormat=2`): check `field_pic_flag` in `trace_headers`, and add noise. Remuxing a raw stream with B pictures gives pts = dts in
  decoding order (FFmpeg then drops frames): `make-jm-samples.sh` sets pts from pic_order_cnt_lsb with `setts`.
- Interlaced MJPEG: FFmpeg's encoder refuses interlacing (make-samples.sh encodes fields as images and pairs them
  into AVI1 packets in Python). Its decoder takes an image of less than 3/4 of the stream's height (integer division)
  as a field, ignores the AVI1 field byte, and puts the first field on the odd rows unless the decoder's field order
  is tt, tb or bt (`-field_order` as an input option shows it); one field a packet it does not decode at all.
- H.264 sample code is generic over the sample type (`PictureDecoder<TPixel>`, byte for 8-bit, ushort deeper): the
  byte SIMD paths are reached through `typeof(TPixel) == typeof(byte)` branches, which the JIT folds per
  instantiation, and clip bounds come from `Samples.Max<TPixel>` (a constant for byte; a runtime `(1 << bitDepth) - 1`
  cost intra-heavy streams about 5%).
- FFmpeg decodes monochrome H.264 to 4:2:0 with flat chroma (128, or 512 at 10 bits): references for gray samples
  come from `-vf extractplanes=y` (make-samples.sh, by `gray` in the name), the decoder's output is one plane. Not
  `-pix_fmt gray`: it rescales a limited-range stream (JM's, no VUI) to full range; libx264's gray is full range.
  xunit theory rows filter by `DisplayName~<sample>`, not `FullyQualifiedName`.
- Deeper than 10 bits: JM codes macroblocks as I_PCM below about QP -6 at 14 bits (so its streams never reach CAVLC's
  level_prefix 16), segfaults with `EnableIPCM=0`, and writes 4:0:0 with 8-bit chroma, which FFmpeg refuses ("Different
  chroma and luma bit depth"; it cannot even mux the stream): `tools/h264-chroma-depth.py` sets the unused chroma depth
  to the luma's, and FFmpeg then decodes 4:0:0 at 12 and 14 bits. 16-bit SIMD lanes hold sums of eight 12-bit samples, not 14-bit ones.
- ARM64 (CI's `build-arm64`): FFmpeg's default JPEG IDCT there is NEON, which rounds unlike its C one; references
  take `-idct simple` (the same as the default on x64). An `Sse2.`/`Avx2.` call reached behind only
  `Vector128.IsHardwareAccelerated` throws PlatformNotSupportedException on ARM64: dispatch inside the function.
- Counting macroblock kinds (I_PCM, sub-8x8 partitions) per picture: FFmpeg's `-debug mb_type` grids come out after
  later frames' headers, so they can't be matched to picture types; count in the decoder with a temporary counter.
- Fuzzing (`tools/fuzz.sh`): SharpFuzz-instrumented assemblies crash (access violation) without afl-fuzz around
  them, so seeds and reproductions run from an uninstrumented build (fuzz.sh writes the seeds before instrumenting;
  reproduce with `dotnet tools/ManagedCodecs.Fuzz/bin/Debug/net10.0/ManagedCodecs.Fuzz.dll <target> <files>`, which
  runs H.264 a second time concealing errors, as the fuzzing does: a finding that only shows concealing was once
  "rejected" there, and AFL had saved it as killed by signal 2). The
  sandbox needs `AFL_SKIP_BIN_CHECK=1 AFL_SKIP_CPUFREQ=1 AFL_I_DONT_CARE_ABOUT_MISSING_CRASHES=1` (fuzz.sh sets them),
  and the `sharpfuzz` global tool lives in `~/.dotnet/tools`, which is not on PATH by default. Many crash files are
  one bug: group them by the top frame of the stack before fixing.
- H.264's `BitReader` reads zero bits past the end of a NAL unit, so a CABAC slice cut short decodes on through zeros
  to the end of the picture without a syntax error; only the engine's consumed position (`Cabac.PastEnd`) shows it.
  Damage for concealment tests: cut a NAL unit (always detected), not byte flips (often still parse).
- WebAssembly (`tools/ManagedCodecs.Wasm`, `tools/wasm-check.sh`): a browser-wasm build needs the `wasm-tools`
  workload even without AOT (NETSDK1147), hence the project is outside the solution. `dotnet new wasmconsole` needs
  a NuGet search host the sandbox blocks: the project is written by hand. The browser has no MD5 (SHA-256 works),
  Emscripten's file system doesn't see the host's (files come in through `[JSImport]` from main.mjs), and the runtime
  prints `MONO_WASM:` notes on stdout under Node (it can't fetch its symbol file). `dotnet publish` puts the app in
  `bin/Release/net10.0/browser-wasm/AppBundle`, whatever `-o` says.
- Frame threading (`H264Decoder.Threads`): a picture's worker may read only what an earlier picture has reported
  done (`Picture.WaitForRow`, `WaitForMotion`), and never marking state (the front end marks later pictures
  meanwhile; read `MarkSnapshot`). Rows (pair rows of MBAFF frames and field pictures, a field picture's per field)
  are final one row after they are deblocked (the next row's top edge filters
  their bottom). A race passes the threaded conformance tests by luck unless the random delays (`RowDelay`: before
  each picture and after each row; `MatchesFfmpegWithDelayedThreads`) make pictures overtake, and with them motion
  compensation checks that each reference row it read is final (`CheckRowsFinal`, "Read reference row N before it was
  final"), not only that the frame came out right: check a new wait by
  breaking it and seeing that test fail, and list it in `tools/mutation/mutations.py` (two waits are exceptions,
  covered in practice by the motion compensation waits: the co-located motion wait and ListsDamaged's; that file says
  why). A frame's second field runs on a decoder of its own alongside its first: it waits for
  the first's `Begin` (which resets both fields' slots), and its frame is copied out only once the first is done.
- Hardware counters (`perf stat -e cycles,instructions`): GitHub's ARM64 runner has them, the x64 runner and the cloud
  VM do not (`<not supported>`), and the x64 runner's 4 vCPUs are 2 cores with SMT; `perf record -e context-switches -g`
  works everywhere and shows where threads block.
- Measuring frame threading: perf samples only threads that run, so blocked time is invisible to it; time the waits
  (a Stopwatch around `Picture.Wait`, logging waiter, reference, row and duration, in a scratch copy) and the front
  end's wait for the oldest picture. mcdec's loops each start a fresh decoder on an IDR picture, whose followers
  trail its slower intra rows: a third of the 720p sample's waits are that.
- In fragmented MP4, FFmpeg's packet durations are not the file's: its demuxer leaves them unset and libavformat
  fills in the codec frame size (1024 for AAC). Compare with FFmpeg's next dts instead (Mp4ConformanceTests does).
- HE-AAC: FFmpeg's SBR is single precision, and the HF generator's prediction is nearly singular for a subband holding
  one sinusoid, so its output moves by up to about 5e-3 of the peak there (this decoder's does too in float): compare
  with a loose largest and a tight mean difference, and check a suspected bug by whether it moves the mean. Math.Cos and
  Math.Sin differ in their last bit between .NET and the WebAssembly runtime, which that prediction can magnify into the
  output (wasm-check.sh fails): tables that feed SBR are computed in arithmetic alone (`QmfTrig`). Math.Pow too: `Math.Pow(x, 4.0 / 3)` differs in the
  last bit at 7 of AAC's 8192 dequantization values, which only a stream reaching one shows (an unseeded noise sample
  made `wasm` fail now and then); `FourThirdsPower` and `QuarterPowerOfTwo` replace it. The FDK encoder
  writes the SBR header's second part only with non-default settings (a patched copy, `make-heaac-samples.sh`), and
  downsampled SBR only for ELD (another patched copy; with PS also `sbrEncoder_IsSingleRatePossible`, and a build
  without assertions, `-DCMAKE_BUILD_TYPE=Release`: its PS analysis asserts at one rate). The downsampled synthesis kernel is
  the full one at even n, (4n - 255): read as (2n - 127) it lags a quarter of a sample and misses FFmpeg by 14 to 17% at
  3 to 6 kHz, which looks like a core bug. FFmpeg's parametric stereo (the oracle where the spec's text is out of reach): the phases' band count follows
  the last header that enabled IID (none before one), a frame is in 34 bands when either IID or ICC is, and its
  per-envelope slots keep imaginary parts above the phase bands that later frames interpolate towards. A one-line
  `fprintf` in a scratch FFmpeg build (`git clone -b n6.1.1` from GitHub, `./configure --disable-everything
  --enable-decoder=aac --enable-demuxer=mov ...`, a few minutes) settles such questions faster than reading.
