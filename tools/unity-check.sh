#!/usr/bin/env bash
# The Unity build (src/ManagedCodecs.Unity, netstandard2.1, the scalar paths) on Mono, the runtime family Unity's is
# built on: decodes every sample with tools/ManagedCodecs.Cli.Unity under mono and with the net10.0 mcdec
# (mcdec --hash), fails unless both give the same output frame for frame, and prints each file's time on Mono over
# the net10.0 build's (docs/decisions/2026-10-31-unity-netstandard.md). The net10.0 build is the one the conformance
# tests check against FFmpeg, so this carries their result over to Mono. Then the same for the Thumbnails package
# (mcdec --thumbnail-hash on the AVI and MP4 samples).
#
# Usage: tools/unity-check.sh
#   Needs mono (6.12 or later: Ubuntu's 6.8 cannot load netstandard2.1 assemblies) and samples/ (tools/make-samples.sh).
set -euo pipefail
cd "$(dirname "$0")/.."

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

dotnet build tools/ManagedCodecs.Cli -c Release -v q -nologo
dotnet build tools/ManagedCodecs.Cli.Unity -c Release -v q -nologo -warnaserror

files=$(ls samples/*.avi samples/*.mp4 samples/*.aac samples/heaac/*.mp4 samples/heaac/*.aac samples/jm/*.mp4 samples/openh264/*.mp4)

# shellcheck disable=SC2086
dotnet tools/ManagedCodecs.Cli/bin/Release/net10.0/mcdec.dll --hash $files > "$work/net.txt" 2> "$work/net-stderr.txt" ||
  { cat "$work/net.txt" "$work/net-stderr.txt" >&2; echo "The net10.0 run failed (above: its output up to the failing file)." >&2; exit 1; }
# shellcheck disable=SC2086
mono tools/ManagedCodecs.Cli.Unity/bin/Release/netstandard2.1/mcdec.dll --hash $files > "$work/mono.txt" 2> "$work/mono-stderr.txt" ||
  { cat "$work/mono.txt" "$work/mono-stderr.txt" >&2; echo "The Mono run failed (above: its output up to the failing file)." >&2; exit 1; }

if ! diff "$work/net.txt" "$work/mono.txt"; then
  echo "The Unity build on Mono differs from the net10.0 build (above: < net10.0, > Mono)." >&2
  exit 1
fi

# The Thumbnails package's probe and thumbnail of every container file (ThumbnailOutput.Line: the RGBA's hash).
containers=$(echo "$files" | tr ' ' '\n' | grep -v '\.aac$')
# shellcheck disable=SC2086
dotnet tools/ManagedCodecs.Cli/bin/Release/net10.0/mcdec.dll --thumbnail-hash $containers > "$work/net-thumb.txt" 2>&1 ||
  { cat "$work/net-thumb.txt" >&2; echo "The net10.0 thumbnail run failed (above: its output up to the failing file)." >&2; exit 1; }
# shellcheck disable=SC2086
mono tools/ManagedCodecs.Cli.Unity/bin/Release/netstandard2.1/mcdec.dll --thumbnail-hash $containers > "$work/mono-thumb.txt" 2>&1 ||
  { cat "$work/mono-thumb.txt" >&2; echo "The Mono thumbnail run failed (above: its output up to the failing file)." >&2; exit 1; }
if ! diff "$work/net-thumb.txt" "$work/mono-thumb.txt"; then
  echo "The Unity build's thumbnails on Mono differ from the net10.0 build's (above: < net10.0, > Mono)." >&2
  exit 1
fi

echo "The Unity build on Mono ($(mono --version | head -1)) and the net10.0 build agree on all $(wc -l < "$work/net.txt") files and $(wc -l < "$work/net-thumb.txt") thumbnails."
echo
echo "| File | net10.0 (ms a decode) | Mono (ms a decode) | Mono / net10.0 |"
echo "| --- | ---: | ---: | ---: |"
join -t: <(sort "$work/net-stderr.txt") <(sort "$work/mono-stderr.txt") | sed 's/ ms a decode//g' |
  awk -F: '$2 > 0 { printf "| %s | %.1f | %.1f | %.2f |\n", $1, $2, $3, $3 / $2 }'
