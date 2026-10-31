# Fuzzing: notes for working on fuzzing (`tools/fuzz.sh`)

The repository's CLAUDE.md (process, core idea, cross-cutting gotchas) applies too.

## Gotchas (each cost time once)

- Fuzzing (`tools/fuzz.sh`): SharpFuzz-instrumented assemblies crash (access violation) without afl-fuzz around
  them, so seeds and reproductions run from an uninstrumented build (fuzz.sh writes the seeds before instrumenting;
  reproduce with `dotnet tools/ManagedCodecs.Fuzz/bin/Debug/net10.0/ManagedCodecs.Fuzz.dll <target> <files>`, which
  runs H.264 a second time concealing errors, as the fuzzing does: a finding that only shows concealing was once
  "rejected" there, and AFL had saved it as killed by signal 2). The
  sandbox needs `AFL_SKIP_BIN_CHECK=1 AFL_SKIP_CPUFREQ=1 AFL_I_DONT_CARE_ABOUT_MISSING_CRASHES=1` (fuzz.sh sets them),
  and the `sharpfuzz` global tool lives in `~/.dotnet/tools`, which is not on PATH by default. Many crash files are
  one bug: group them by the top frame of the stack before fixing.
