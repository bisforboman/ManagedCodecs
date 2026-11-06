# Security analyzers on every build, as errors (2026-11-06)

Decided by the owner, with the multiple-choice prompt; built in the same pull request.

## Question

The vulnerable-package gate (2026-11-06-vulnerable-packages.md) keeps packages with known advisories out, but
nothing in the build scanned the repository's own code for security bugs. The decoders read untrusted files, so the
code that parses them is what matters most. CodeQL (codeql.yml) already scans ManagedCodecs, but as code scanning,
not in the build.

The third question, asked after the two in 2026-11-06-vulnerable-packages.md, covering the owner's five
repositories: "Should I also add scanning of your own code?" Options:

1. "Security analyzers, all 5 (Recommended)": the .NET SDK's Security rules (CA2xxx, CA3xxx, CA5xxx), all of them,
   as errors, in all five repositories.
2. "CodeQL on StanzaSharp, stylebro" (public repositories: free).
3. "CodeQL on Svarta" (private: needs paid GitHub Code Security).
4. "Not now".

## Answer

**The security analyzers in all five repositories, and CodeQL on StanzaSharp and stylebro.** ManagedCodecs already
has CodeQL; this adds the analyzers. Here:

- Directory.Build.props sets `AnalysisModeSecurity` to `All`: the SDK adds its global config enabling every Security
  rule. It also sets the SDK's private property
  `_GlobalAnalyzerConfigFileName_MicrosoftCodeAnalysisNetAnalyzersSecurity_WarnAsErrorSuffix` to `_warnaserror`,
  which picks that config's variant with every rule at error: what `CodeAnalysisTreatWarningsAsErrors` would pick, but
  for Security only (that property would also make about 27 default rules of other categories errors). Every build
  fails on a finding, local, CI, pack and release, not only those with `-warnaserror`.
- `dotnet_analyzer_diagnostic.category-Security.severity = error` in .editorconfig doesn't work (Svärta tried it
  first): the SDK's global config sets each rule's severity by id, and a rule's own severity wins over its category's.
- The SDK runs its analyzers on net5.0 and later only, and sets `AnalysisLevel` only there. The two netstandard2.1
  projects (src/ManagedCodecs.Unity, which compiles every package's sources, and tools/ManagedCodecs.Cli.Unity, the
  harness unity-check.sh runs under Mono) would have had none: Directory.Build.props also sets `EnableNETAnalyzers`
  and `AnalysisLevelSecurity` (`latest`, what net10.0 projects get anyway). With `AnalysisLevel` still unset there,
  that brings the Security rules and no other category's. Checked: a temporary `MD5.Create()` in Core failed the plain
  (no `-warnaserror`) build of ManagedCodecs.Core and of ManagedCodecs.Unity with CA5351.
- Since the suffix is the SDK's, not a documented property, the target `CheckSecurityAnalyzers`
  (Directory.Build.targets) fails the build unless the config in use exists and ends in
  `_all_warnaserror.globalconfig`. It runs after the SDK's target that picks the config, or after `CoreCompile` where
  that target isn't imported, so a project with the analyzers off fails too. Checked: clearing the suffix
  (`-p:..._WarnAsErrorSuffix=`) fails with the message, and so does `-p:EnableNETAnalyzers=false` on the Unity build.
- Every build CI runs goes through MSBuild with this props file: the solution (src, tests, the Cli, Godot, MonoGame,
  Unity sample, fuzz and benchmark projects), the Unity build and its Mono harness (unity-check.sh), the wasm publish
  (wasm-check.sh, with or without AOT compilation), the Blazor sample and the Native AOT publish (aot-check.sh). Unity
  itself compiles the package's DLL, not its sources, so nothing builds outside the analyzers.
- A real weakness is fixed, the owner deciding how; a false positive is suppressed where it is, as narrowly as
  possible (`[SuppressMessage]` with a justification, or one path-scoped .editorconfig section), never by a rule off
  for the whole repository.

## Findings when it was turned on

Two rules, none a weakness:

- **CA5394 (insecure randomness), in the H.264 decoder**: `H264Decoder.Run` and `PictureDecoder.MacroblockDone`
  sleep a random time when `RowDelay` is set, which only tests do, to shake frame threading's timing. Suppressed on
  those two methods.
- **CA5394, in the tests** (about 280 places in 17 files): seeded `System.Random` for the random inputs that compare
  each fast path with its reference, and for the corrupted streams of the malformed-input tests. Suppressed for
  `tests/**.cs` in .editorconfig.
- **CA5351 (broken cryptographic algorithm, MD5)**: `FrameMd5` (tests), `OutputDump` (mcdec's `--hash`, what
  realworld.py compares) and `Md5Polyfill` (the Mono harness): MD5 as FFmpeg's framemd5 uses it, a checksum to
  compare frames with FFmpeg's, not security. Suppressed on the three classes.

Nothing in the decoders, demuxers or thumbnail code was reported.

## Consequences

- An SDK with new Security rules may fail the build after an update; its findings are triaged like these.
- The analyzers find patterns (weak cryptography, unsafe deserialization, XML and process handling), not the bugs a
  decoder is most likely to have (unbounded allocations from sizes in a file, out-of-range reads): those stay with
  the fuzzing (tools/ManagedCodecs.Fuzz), the malformed-input tests and CodeQL.
