#!/usr/bin/env bash
# Times ManagedCodecs (mcdec) and FFmpeg decoding the same file the same number of times, single-threaded, and prints
# each one's wall time per decoded frame. Both include demuxing and process startup (mcdec's also .NET's JIT), so use
# enough loops for that to wash out. Run from the repository root: tools/compare.sh samples/h264-bench-720p.mp4 10
set -euo pipefail

file=${1:?usage: tools/compare.sh <file> [loops]}
loops=${2:-10}
cd "$(dirname "$0")/.."
dotnet build -c Release tools/ManagedCodecs.Cli -v q -nologo > /dev/null
mcdec=$(find tools/ManagedCodecs.Cli/bin/Release -name mcdec.dll | head -n 1)

ours=$(dotnet "$mcdec" "$file" --loops "$loops")
theirs=$(ffmpeg -hide_banner -nostdin -benchmark -threads 1 -stream_loop $((loops - 1)) -i "$file" -f null - 2>&1)
frames=$(sed -n 's/^decoded \([0-9]*\) video and \([0-9]*\) audio.*/\1 \2/p' <<< "$ours" | awk '{ print $1 + $2 }')
rtime() { sed -n 's/.*rtime=\([0-9.]*\)s.*/\1/p' <<< "$1"; }

echo "$ours" | head -n 1
printf '%s frames x %s loops, wall time per frame:\n' "$frames" "$loops"
awk -v t="$(rtime "$ours")" -v n=$((frames * loops)) 'BEGIN { printf "  ManagedCodecs %8.0f us\n", t * 1e6 / n }'
awk -v t="$(rtime "$theirs")" -v n=$((frames * loops)) 'BEGIN { printf "  FFmpeg        %8.0f us\n", t * 1e6 / n }'
