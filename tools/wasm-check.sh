#!/usr/bin/env bash
# Decodes every sample on WebAssembly (tools/ManagedCodecs.Wasm, run by Node) and natively (mcdec --hash), and fails
# unless both give the same output, frame for frame. The native build is the one the conformance tests check against
# FFmpeg, so this carries their result over to browser-wasm.
#
# Usage: tools/wasm-check.sh [aot]
#   Needs Node and the wasm-tools workload (dotnet workload install wasm-tools), and samples/ (tools/make-samples.sh).
#   aot: compile ahead of time to WebAssembly, as a web app shipping the decoders would (two minutes more to build);
#   without it the runtime's interpreter runs them.
set -euo pipefail
cd "$(dirname "$0")/.."

aot=false
if [[ ${1:-} == aot ]]; then aot=true; fi

dotnet build tools/ManagedCodecs.Cli -c Release -v q -nologo
dotnet publish tools/ManagedCodecs.Wasm -c Release -v q -nologo -warnaserror -p:RunAOTCompilation=$aot
bundle=tools/ManagedCodecs.Wasm/bin/Release/net10.0/browser-wasm/AppBundle

files=$(ls samples/*.avi samples/*.mp4 samples/*.aac samples/heaac/*.mp4 samples/heaac/*.aac samples/jm/*.mp4 samples/openh264/*.mp4)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# shellcheck disable=SC2086
dotnet tools/ManagedCodecs.Cli/bin/Release/net10.0/mcdec.dll --hash $files > "$work/native.txt" 2> "$work/native-stderr.txt" ||
  { cat "$work/native.txt" "$work/native-stderr.txt" >&2; echo "The native run failed (above: its output up to the failing file)." >&2; exit 1; }
# Timings go to stderr. The runtime's own notes (under Node, that it can't load its symbol file) come on stdout,
# starting MONO_WASM: they are left out of the comparison.
# shellcheck disable=SC2086
node "$bundle/main.mjs" $files > "$work/wasm-stdout.txt" 2> "$work/wasm-stderr.txt" ||
  { cat "$work/wasm-stdout.txt" "$work/wasm-stderr.txt" >&2; echo "The WebAssembly run failed (above: its output up to the failing file)." >&2; exit 1; }
grep -v '^MONO_WASM:' "$work/wasm-stdout.txt" > "$work/wasm.txt" || true
head -1 "$work/wasm-stderr.txt"

if diff "$work/native.txt" "$work/wasm.txt"; then
  echo "WebAssembly (AOT: $aot) and native output agree on all $(wc -l < "$work/native.txt") files."
else
  echo "WebAssembly (AOT: $aot) output differs from the native output (lines above: < native, > wasm)." >&2
  exit 1
fi

# The Thumbnails package the same way: each MP4 and AVI file's probe and thumbnail (ThumbnailOutput), the pixels
# hashed, against the native ones.
videos=$(echo "$files" | tr ' ' '\n' | grep -E '\.(mp4|avi)$')
# shellcheck disable=SC2086
dotnet tools/ManagedCodecs.Cli/bin/Release/net10.0/mcdec.dll --thumbnail-hash $videos > "$work/native-thumbnails.txt"
# shellcheck disable=SC2086
node "$bundle/main.mjs" --thumbnails $videos > "$work/wasm-thumbnails-stdout.txt" 2> "$work/wasm-thumbnails-stderr.txt" ||
  { cat "$work/wasm-thumbnails-stdout.txt" "$work/wasm-thumbnails-stderr.txt" >&2; echo "The WebAssembly thumbnail run failed." >&2; exit 1; }
grep -v '^MONO_WASM:' "$work/wasm-thumbnails-stdout.txt" > "$work/wasm-thumbnails.txt" || true
if diff "$work/native-thumbnails.txt" "$work/wasm-thumbnails.txt"; then
  echo "WebAssembly (AOT: $aot) and native thumbnails agree on all $(wc -l < "$work/native-thumbnails.txt") files."
else
  echo "WebAssembly (AOT: $aot) thumbnails differ from the native ones (lines above: < native, > wasm)." >&2
  exit 1
fi

# The fuzz findings kept in tests/ManagedCodecs.Tests/Fuzz/Findings, the error paths the samples don't reach, through
# their targets (Targets.Outcome: accepted, rejected, or the exception that escaped) on WebAssembly and natively. Compiled
# ahead of time, exception filters there once read their methods' arguments wrong, and an MP4 finding brought the
# runtime down (tests/ManagedCodecs.Tests/ExceptionFilterTests.cs).
dotnet build tools/ManagedCodecs.Fuzz -c Release -v q -nologo > /dev/null
findings=tests/ManagedCodecs.Tests/Fuzz/Findings
for dir in "$findings"/*/; do
  target=$(basename "$dir")
  # shellcheck disable=SC2086
  dotnet tools/ManagedCodecs.Fuzz/bin/Release/net10.0/ManagedCodecs.Fuzz.dll --outcomes "$target" "$dir"* >> "$work/native-findings.txt"
  # shellcheck disable=SC2086
  node "$bundle/main.mjs" --outcomes "$target" "$dir"* >> "$work/wasm-findings-stdout.txt" 2>> "$work/wasm-findings-stderr.txt" ||
    { cat "$work/wasm-findings-stdout.txt" "$work/wasm-findings-stderr.txt" >&2; echo "The WebAssembly run of the $target findings failed." >&2; exit 1; }
done
grep -v '^MONO_WASM:' "$work/wasm-findings-stdout.txt" > "$work/wasm-findings.txt" || true
if diff "$work/native-findings.txt" "$work/wasm-findings.txt"; then
  echo "WebAssembly (AOT: $aot) and native outcomes agree on all $(wc -l < "$work/native-findings.txt") fuzz findings."
else
  echo "WebAssembly (AOT: $aot) outcomes differ from the native ones on the fuzz findings (< native, > wasm)." >&2
  exit 1
fi
