#!/usr/bin/env bash
# Publishes mcdec with Native AOT (with warnings as errors, so a trimming or AOT warning from the libraries fails it),
# decodes every sample with it and with the JIT build (mcdec --hash), and fails unless both give the same output,
# frame for frame. The JIT build is the one the conformance tests check against FFmpeg, so this carries their result
# over to Native AOT, which README and the packages promise.
#
# Usage: tools/aot-check.sh [instruction set]
#   Needs clang (the AOT linker) and samples/ (tools/make-samples.sh). The instruction set (x64 only) is given to
#   IlcInstructionSet, as an app would (x86-64-v3 for the AVX2 paths); without one, the default target, which leaves
#   the 256-bit paths out and reaches the SSE and VectorZip ones (README, Native AOT).
set -euo pipefail
cd "$(dirname "$0")/.."

case "$(uname -m)" in
  x86_64) rid=linux-x64 ;;
  aarch64 | arm64) rid=linux-arm64 ;;
  *) echo "Unsupported machine $(uname -m)" >&2; exit 1 ;;
esac

isa=()
if [[ -n ${1:-} ]]; then isa=(-p:IlcInstructionSet="$1"); fi

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

dotnet build tools/ManagedCodecs.Cli -c Release -v q -nologo
dotnet publish tools/ManagedCodecs.Cli -c Release -r "$rid" -v q -nologo -warnaserror -p:PublishAot=true "${isa[@]}" -o "$work/aot"

files=$(ls samples/*.avi samples/*.mp4 samples/*.aac samples/heaac/*.mp4 samples/heaac/*.aac samples/jm/*.mp4 samples/openh264/*.mp4)

# shellcheck disable=SC2086
dotnet tools/ManagedCodecs.Cli/bin/Release/net10.0/mcdec.dll --hash $files > "$work/jit.txt" 2> "$work/jit-stderr.txt" ||
  { cat "$work/jit.txt" "$work/jit-stderr.txt" >&2; echo "The JIT run failed (above: its output up to the failing file)." >&2; exit 1; }
# shellcheck disable=SC2086
"$work/aot/mcdec" --hash $files > "$work/aot.txt" 2> "$work/aot-stderr.txt" ||
  { cat "$work/aot.txt" "$work/aot-stderr.txt" >&2; echo "The Native AOT run failed (above: its output up to the failing file)." >&2; exit 1; }

if diff "$work/jit.txt" "$work/aot.txt"; then
  echo "Native AOT (${1:-default target}) and JIT output agree on all $(wc -l < "$work/jit.txt") files."
else
  echo "Native AOT (${1:-default target}) output differs from the JIT output (lines above: < JIT, > AOT)." >&2
  exit 1
fi
