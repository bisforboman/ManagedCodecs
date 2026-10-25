# ManagedCodecs benchmark history

Written by `.github/workflows/benchmark.yml` (weekly and on demand), from `tools/bench.py`'s records in `runs/`:
ManagedCodecs' wall time per decoded frame over FFmpeg's, single-threaded (lower is faster; below 1 is faster
than FFmpeg), the medians of interleaved runs on GitHub-hosted runners, which vary by a few percent from run to
run. *4 threads* is mcdec's time with 4 threads over its time with 1 on the 720p sample. A run's own record has
the times in µs, the CPU and the FFmpeg and .NET versions.

## aarch64

| Date | Commit | testsrc-420 | testsrc-444 | noise-q1 | h264-p-refs | h264-bench-720p | h264-main-p-refs | h264-b-pyramid | h264-wp-default | h264-high-default | h264-high-b | h264-10bit-b | h264-high-motion | h264-10bit-motion | h264-mbaff-default | jm-paff-b | aac-stereo-48k | aac-5.1 | heaac-5.1 | heaacv2-stereo | 4 threads |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2026-10-25 | c407720 | 1.21 | 1.40 | 1.08 | 2.77 | 2.65 | 2.45 | 2.76 | 3.35 | 3.38 | 2.66 | 2.52 | 1.57 | 1.52 | 2.87 | 2.47 | 0.90 | 1.76 | 2.25 | 1.88 | 0.36 |

## x86_64

| Date | Commit | testsrc-420 | testsrc-444 | noise-q1 | h264-p-refs | h264-bench-720p | h264-main-p-refs | h264-b-pyramid | h264-wp-default | h264-high-default | h264-high-b | h264-10bit-b | h264-high-motion | h264-10bit-motion | h264-mbaff-default | jm-paff-b | aac-stereo-48k | aac-5.1 | heaac-5.1 | heaacv2-stereo | 4 threads |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2026-10-25 | c407720 | 0.91 | 1.12 | 1.02 | 2.75 | 3.18 | 2.56 | 2.91 | 3.41 | 3.42 | 2.84 | 3.49 | 1.94 | 2.08 | 3.08 | 2.70 | 0.63 | 1.39 | 2.15 | 1.71 | 0.52 |
