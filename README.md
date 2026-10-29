# ManagedCodecs benchmark history

Written by `.github/workflows/benchmark.yml` (weekly and on demand), from `tools/bench.py`'s records in `runs/`:
ManagedCodecs' wall time per decoded frame over FFmpeg's, single-threaded (lower is faster; below 1 is faster
than FFmpeg), the medians of interleaved runs on GitHub-hosted runners, which vary by a few percent from run to
run. *4 threads* is mcdec's time with 4 threads over its time with 1 on the 720p sample. A run's own record has
the times in µs, the CPU and the FFmpeg and .NET versions. The first table a machine is mcdec's whole process,
.NET's startup and JIT included (a large share of the short samples' time); the second, from 2026-10-29 on,
its last loop alone (steady state) over FFmpeg's time.

## aarch64

| Date | Commit | testsrc-420 | testsrc-444 | noise-q1 | h264-p-refs | h264-bench-720p | h264-main-p-refs | h264-b-pyramid | h264-wp-default | h264-high-default | h264-high-b | h264-10bit-b | h264-high-motion | h264-10bit-motion | h264-mbaff-default | jm-paff-b | aac-stereo-48k | aac-5.1 | heaac-5.1 | heaacv2-stereo | 4 threads |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2026-10-29 | e420633 | 1.20 | 1.37 | 1.05 | 2.44 | 2.34 | 2.17 | 2.57 | 3.14 | 3.19 | 2.52 | 2.48 | 1.50 | 1.49 | 2.82 | 2.42 | 0.95 | 1.87 | 1.85 | 1.71 | 0.38 |
| 2026-10-29 | 0a2d882 | 1.25 | 1.43 | 1.08 | 2.47 | 2.41 | 2.17 | 2.55 | 3.12 | 3.23 | 2.54 | 2.54 | 1.51 | 1.49 | 2.83 | 2.43 | 0.97 | 1.88 | 1.93 | 1.79 | 0.38 |
| 2026-10-29 | 7bb58ec | 1.23 | 1.43 | 1.09 | 2.45 | 2.38 | 2.20 | 2.58 | 3.20 | 3.30 | 2.58 | 2.53 | 1.52 | 1.50 | 2.87 | 2.45 | 0.98 | 1.86 | 1.92 | 1.78 | 0.38 |
| 2026-10-28 | cf0c5be | 1.22 | 1.39 | 1.07 | 2.61 | 2.55 | 2.35 | 2.63 | 3.21 | 3.29 | 2.60 | 2.50 | 1.57 | 1.51 | 2.82 | 2.47 | 0.94 | 1.82 | 2.25 | 1.96 | 0.35 |
| 2026-10-25 | c407720 | 1.21 | 1.40 | 1.08 | 2.77 | 2.65 | 2.45 | 2.76 | 3.35 | 3.38 | 2.66 | 2.52 | 1.57 | 1.52 | 2.87 | 2.47 | 0.90 | 1.76 | 2.25 | 1.88 | 0.36 |

### aarch64, steady state

| Date | Commit | testsrc-420 | testsrc-444 | noise-q1 | h264-p-refs | h264-bench-720p | h264-main-p-refs | h264-b-pyramid | h264-wp-default | h264-high-default | h264-high-b | h264-10bit-b | h264-high-motion | h264-10bit-motion | h264-mbaff-default | jm-paff-b | aac-stereo-48k | aac-5.1 | heaac-5.1 | heaacv2-stereo |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2026-10-29 | e420633 | 1.14 | 1.25 | 1.01 | 1.63 | 2.08 | 1.60 | 1.83 | 2.09 | 2.15 | 1.74 | 2.05 | 1.37 | 1.39 | 2.38 | 2.06 | 0.59 | 1.38 | 1.65 | 1.35 |
| 2026-10-29 | 0a2d882 | 1.19 | 1.30 | 1.03 | 1.63 | 2.11 | 1.57 | 1.81 | 2.04 | 2.15 | 1.74 | 2.09 | 1.37 | 1.39 | 2.38 | 2.04 | 0.59 | 1.37 | 1.73 | 1.40 |

## x86_64

| Date | Commit | testsrc-420 | testsrc-444 | noise-q1 | h264-p-refs | h264-bench-720p | h264-main-p-refs | h264-b-pyramid | h264-wp-default | h264-high-default | h264-high-b | h264-10bit-b | h264-high-motion | h264-10bit-motion | h264-mbaff-default | jm-paff-b | aac-stereo-48k | aac-5.1 | heaac-5.1 | heaacv2-stereo | 4 threads |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2026-10-29 | e420633 | 0.89 | 0.97 | 0.78 | 2.31 | 2.43 | 2.23 | 2.70 | 3.22 | 3.24 | 2.63 | 3.32 | 1.72 | 1.94 | 2.76 | 2.46 | 0.72 | 1.38 | 2.08 | 1.56 | 0.50 |
| 2026-10-29 | 0a2d882 | 0.83 | 0.93 | 0.76 | 2.50 | 2.32 | 2.30 | 2.56 | 3.18 | 3.30 | 2.58 | 3.28 | 1.63 | 1.79 | 2.79 | 2.47 | 0.80 | 1.60 | 2.04 | 1.83 | 0.56 |
| 2026-10-29 | 7bb58ec | 0.95 | 1.14 | 1.02 | 2.50 | 2.61 | 2.39 | 2.75 | 3.29 | 3.36 | 2.71 | 3.51 | 1.82 | 2.05 | 2.99 | 2.65 | 0.80 | 1.68 | 2.02 | 1.84 | 0.53 |
| 2026-10-28 | cf0c5be | 0.91 | 1.05 | 0.98 | 2.82 | 2.75 | 2.57 | 2.90 | 3.48 | 3.51 | 2.82 | 3.52 | 1.85 | 2.00 | 3.08 | 2.78 | 0.88 | 1.73 | 2.37 | 2.18 | 0.51 |
| 2026-10-25 | c407720 | 0.91 | 1.12 | 1.02 | 2.75 | 3.18 | 2.56 | 2.91 | 3.41 | 3.42 | 2.84 | 3.49 | 1.94 | 2.08 | 3.08 | 2.70 | 0.63 | 1.39 | 2.15 | 1.71 | 0.52 |

### x86_64, steady state

| Date | Commit | testsrc-420 | testsrc-444 | noise-q1 | h264-p-refs | h264-bench-720p | h264-main-p-refs | h264-b-pyramid | h264-wp-default | h264-high-default | h264-high-b | h264-10bit-b | h264-high-motion | h264-10bit-motion | h264-mbaff-default | jm-paff-b | aac-stereo-48k | aac-5.1 | heaac-5.1 | heaacv2-stereo |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2026-10-29 | e420633 | 0.81 | 0.87 | 0.74 | 1.58 | 2.16 | 1.83 | 1.86 | 2.06 | 2.08 | 1.76 | 2.75 | 1.53 | 1.79 | 2.32 | 1.97 | 0.39 | 0.94 | 1.80 | 1.13 |
| 2026-10-29 | 0a2d882 | 0.76 | 0.85 | 0.70 | 1.56 | 1.99 | 1.69 | 1.70 | 1.93 | 1.96 | 1.76 | 2.63 | 1.50 | 1.68 | 2.32 | 2.04 | 0.41 | 1.03 | 1.73 | 1.33 |
