#!/usr/bin/env bash
# Fuzzes one target of tools/ManagedCodecs.Fuzz with AFL++ through SharpFuzz, for a number of minutes.
#
# Usage: tools/fuzz.sh <file|h264|mjpeg|aac|thumbnail> <minutes> [work dir]
#   Needs afl-fuzz (apt install afl++) and the SharpFuzz tool (dotnet tool install --global SharpFuzz.CommandLine).
#   Findings end up in <work dir>/<target>/out/default/crashes and hangs; reproduce one with
#   dotnet tools/ManagedCodecs.Fuzz/bin/Debug/net10.0/ManagedCodecs.Fuzz.dll <target> <file>,
#   and once fixed, keep it in tests/ManagedCodecs.Tests/Fuzz/Findings/<target>/ (MalformedInputTests replays them).
set -euo pipefail

target=${1:?usage: tools/fuzz.sh <file|h264|mjpeg|aac|thumbnail> <minutes> [work dir]}
minutes=${2:?usage: tools/fuzz.sh <target> <minutes> [work dir]}
cd "$(dirname "$0")/.."
work=$(mkdir -p "${3:-fuzz-work}" && cd "${3:-fuzz-work}" && pwd)

# A build of its own, so instrumenting it leaves the solution's bin/ as it is.
build=$work/build
dotnet publish tools/ManagedCodecs.Fuzz -c Release -o "$build" -v q > /dev/null

# Seeds before instrumenting: instrumented code writes to AFL's shared memory and crashes without afl-fuzz around it.
if [[ ! -d $work/seeds ]]; then
  dotnet "$build/ManagedCodecs.Fuzz.dll" --seeds samples "$work/seeds"
fi

for library in Core Containers Mjpeg H264 Aac Thumbnails; do
  sharpfuzz "$build/ManagedCodecs.$library.dll" > /dev/null
done

# This sandbox's (and CI's) core_pattern pipes crashes to a handler, and the CPU governor can't be set: AFL++ checks
# both and stops unless told not to. A .NET process is not an instrumented binary AFL++ recognizes, hence the rest.
export AFL_SKIP_BIN_CHECK=1 AFL_SKIP_CPUFREQ=1 AFL_I_DONT_CARE_ABOUT_MISSING_CRASHES=1 AFL_NO_UI=1
mkdir -p "$work/$target"
afl-fuzz -i "$work/seeds/$target" -o "$work/$target/out" -m none -t 5000 -V $((minutes * 60)) \
  -- dotnet "$build/ManagedCodecs.Fuzz.dll" "$target"

echo "Crashes: $(find "$work/$target/out" -path '*crashes/id*' | wc -l), hangs: $(find "$work/$target/out" -path '*hangs/id*' | wc -l)"
