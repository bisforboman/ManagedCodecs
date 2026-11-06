#!/usr/bin/env python3
"""Shares of H.264 decoding by stage, for ManagedCodecs (mcdec) and FFmpeg, from perf call-graph profiles.

Each sample goes to a stage. For mcdec, its call chain is read from the leaf up and the first frame naming a stage
decides (so SpanHelpers.Memmove under Crop is the output copy, memset under ResetInter the per-picture clears). For
FFmpeg, whose asm keeps no frame pointers, the leaf decides; its asm's local labels (`..@3485.branch_instr`) are
resolved to their functions through libavcodec's debug symbols (`--ffmpeg-debug`, from Ubuntu's libavcodec60-dbgsym).
FFmpeg inlines motion vector prediction into ff_h264_decode_mb_cavlc and _cabac, so read the header row and the
motion row together for it. docs/notes/h264-stages-2026-11.md is the method in full and the last results.

Record (one thread each; DOTNET_ReadyToRun=0 so CoreLib's helpers have names):
  DOTNET_PerfMapEnabled=1 DOTNET_EnableWriteXorExecute=0 DOTNET_ReadyToRun=0 \\
    perf record -g -e cpu-clock -F 2000 -o ours.data dotnet <mcdec.dll> <file> --loops N
  perf record -g -e cpu-clock -F 2000 -o ff.data ffmpeg -threads 1 -stream_loop N-1 -i <file> -an -f null -
Then:
  tools/h264-stages.py --ours ours.data --ffmpeg ff.data [--ffmpeg-debug <libavcodec .debug>] [--perf <perf>]
  tools/h264-stages.py --ours ours.data --list-other   (the functions that fell to "other", to extend the rules)

Instructions a frame by stage, from callgrind (on a Native AOT mcdec: the JIT falls back to unoptimized code under
valgrind; docs/benchmarking.md), each decoder run twice so that the difference is the steady state:
  valgrind --tool=callgrind --callgrind-out-file=ours-1.out <aot>/mcdec <file> --loops 1   (and --loops 3)
  valgrind --tool=callgrind --callgrind-out-file=ff-1.out ffmpeg -threads 1 -stream_loop 0 -i <file> -an -f null -
  tools/h264-stages.py --callgrind-ours ours-1.out ours-3.out --callgrind-ffmpeg ff-1.out ff-3.out --frames 100
Callgrind gives each function its own count, without its callers: there mcdec's libc memset is taken as the
per-picture clears and memmove as the output copy (what the call chains under perf show them to be).
"""

import argparse
import bisect
import collections
import glob
import re
import subprocess

STAGES = [
    "Residual parsing",
    "Header syntax (mb_type, mvd, ref, cbp)",
    "MV prediction, neighbours, per-MB bookkeeping",
    "Inter prediction",
    "Transform and residual add",
    "Intra prediction",
    "Deblocking: bS and per-edge QP",
    "Deblocking: edge filters",
    "Per-picture setup and clears",
    "Output copy",
    "NAL unescaping, slice headers",
    "Other",
]

# mcdec: (stage, pattern on the frame's symbol), tried in order on each frame from the leaf up.
OURS = [
    (6, r"MacroblockEdgeStrengths|InternalStrengths|BoundaryStrength|MotionStrength|HasUniformMotion|::Far\b|ChromaQp|ChromaParameters|EdgeQp|Strengths?\b"),
    (7, r"::Filter(Macroblock|Rows|Columns|Chroma|Edge|Luma)|WriteColumns|ReadColumns|Transpose|::Deblock"),
    (0, r"Cavlc::ReadResidualBlock|Cabac::ResidualBlock|CabacResidualBlock|ReadResidual|ReadBlock|CodedBlockFlag|LumaNc|ChromaNc|NeighborTotalCoeff|ReadCoeffToken|ExpGolombBypass|CombineNc|BlockIndex|BlockPosition"),
    (4, r"Transform::|AddInterResidual|ReconstructChroma|ReconstructLuma|AddResidual|DecodeInterResidual"),
    (5, r"IntraPrediction::|PredictIntra|Intra(4x4|8x8|16x16)|IntraMbNeighbors|DecodeIntraMacroblock|NeighborIntraMode|ReconstructIntra"),
    (3, r"InterPrediction|PredictFromOne|PredictFromTwo|PredictPartition|PredictComponent|Weighting|WeightOne|WeightBoth|PartitionPlace|LumaPlace|ChromaPlace|CheckRowsFinal|WaitForRow|PictureOf"),
    (2, r"Motion|PredictMv|Direct|CoLocated|DecodeSkip|StoreAbsMvd|StoreReference|Neighbor|MacroblockDone|StartInterMacroblock|ColocatedMv"),
    (1, r"CabacMvd|AbsMvd|CabacRefIdx|CabacCbp|LumaCond|ChromaCond|MbType|SkipFlagInc|SubMbType|Cabac::Decision|Cabac::Bypass|Cabac::Refill|Cabac::Terminate|ReadUe|ReadSe|ReadTe|ReadBits|ReadFlag|BitReader|MbQpDelta|CabacQpDelta|IntraChromaPred|Transform8x8Flag|ReadCbp|DecodeMacroblock|DecodeInterMacroblock|DecodeSliceData|ReadRefIdx|ReadMvd|PrevIntra|DecodeMbaff|DecodeSubMacroblocks|UpdateQp|CheckAddress|ComponentOf"),
    (8, r"::Begin\b|ResetInter|ResetCabac|InitContexts|Picture::\.ctor|::Rent|Pool|ClearCounts|PictureTarget"),
    (9, r"::Crop|OutputQueue|VideoFrame"),
    (10, r"NalUnit|Unescape|SliceHeader|ParameterSets|ReadSps|ReadPps|Demux|Mp4|Avc"),
]

# FFmpeg: on the leaf function only.
FFMPEG = [
    (6, r"check_mv|h264_loop_filter_strength|get_chroma_qp|fill_filter_caches"),
    # The filtering functions' own time (setup, filter_mb_dir inlined) goes with the asm filters, as in the 2026-10 note.
    (7, r"deblock|filter_mb|h264_[hv]_loop_filter|loop_filter"),
    (0, r"decode_residual|decode_cabac_residual|decode_cabac_luma_residual|get_cabac_cbf|coeff_token|decode_significance|get_level_prefix|get_cabac_bypass|decode_cabac_mb_coeff"),
    (4, r"idct|add_pixels|h264_add|dequant|luma_dc_dequant|chroma_dc_dequant|hl_decode_mb_idct"),
    (5, r"pred4x4|pred8x8|pred16x16|intra_pred|ff_h264_pred_|check_intra|pred8x8l|hl_decode_mb_predict"),
    (3, r"mc_part|hl_motion|qpel|chroma_mc|weight|biweight|put_pixels|avg_pixels|prefetch|mc_dir_part|ff_thread_await|emulated_edge|emu_edge"),
    (2, r"pred_motion|fill_decode_caches|fill_decode_neighbors|write_back|pred_direct|pred_spatial|pred_temp|decode_mb_skip|pred_pskip|pred_16x8|pred_8x16|ff_h264_direct|fetch_diagonal|await_reference|hl_decode_mb\b|hl_decode_mb_simple|hl_decode_mb_complex|hl_decode_mb_444"),
    (1, r"ff_h264_decode_mb_cavlc|ff_h264_decode_mb_cabac|decode_cabac|get_cabac|get_ue_golomb|get_se_golomb|decode_mb_skip|get_bits|ff_init_cabac|skip_bits"),
    (8, r"ff_h264_field_start|alloc_picture|h264_field_start|ff_h264_set_erpic|memset|init_dimensions|ff_thread_get_buffer|av_buffer|ff_get_buffer|init_get_bits"),
    (10, r"extract_rbsp|decode_slice_header|parse_nal|h264_slice_init|ff_h2645|h264_init_ps|ff_h264_queue_decode_slice|ff_h264_execute_decode_slices|decode_nal_units|decode_slice\b"),
]


def compile_rules(rules):
    return [(stage, re.compile(pattern)) for stage, pattern in rules]


def read_samples(perf, data, leaf_only):
    """Yields each sample's frames as (symbol, dso, address), leaf first."""
    out = subprocess.run(
        [perf, "script", "-i", data, "-F", "comm,tid,ip,sym,dso", "--no-demangle"],
        capture_output=True, text=True, check=False,
    ).stdout
    frames = []
    for line in out.splitlines():
        if not line.strip():
            if frames:
                yield frames[:1] if leaf_only else frames
            frames = []
            continue
        if not line.startswith(("\t", " ")):
            continue  # the sample's header line (comm, tid)
        m = re.match(r"\s*([0-9a-f]+)\s+(.*?)\s+\((.*)\)\s*$", line)
        if m:
            frames.append((m.group(2), m.group(3), int(m.group(1), 16)))
    if frames:
        yield frames[:1] if leaf_only else frames


def label_resolver(debug_file):
    """
    Maps asm-local labels (`..@N...`) and bare addresses (callgrind's, `0x9eda90`) to the function they lie in, by the
    debug file's symbol table.
    """
    if not debug_file:
        return lambda name: name
    labels = {}
    addresses, functions = [], []
    out = subprocess.run(["nm", "-n", debug_file], capture_output=True, text=True, check=False).stdout
    current = None
    for line in out.splitlines():
        parts = line.split()
        if len(parts) < 3 or parts[1] not in "tTwW":
            continue
        name = parts[2]
        if name.startswith(".."):
            labels[name] = current or name
        else:
            current = name
            addresses.append(int(parts[0], 16))
            functions.append(name)

    def resolve(name):
        if name.startswith(".."):
            return labels.get(name.split("+")[0], name)
        if re.fullmatch(r"0x[0-9a-f]+", name):
            i = bisect.bisect_right(addresses, int(name, 16)) - 1
            return functions[i] if i >= 0 else name
        return name

    return resolve


def method_name(symbol):
    """
    A JIT frame's `Namespace.Class::Method`, without its return type and parameters, which name other types (a method
    returning `List<VideoFrame>` is not the output copy); other symbols as they are.
    """
    head = symbol.split("(", 1)[0]
    return head.split()[-1] if head.strip() else symbol


def classify(perf, data, rules, leaf_only, resolve):
    counts = collections.Counter()
    other = collections.Counter()
    for frames in read_samples(perf, data, leaf_only):
        stage = None
        for symbol, dso, _ in frames:
            symbol = method_name(resolve(symbol))
            for s, pattern in rules:
                if pattern.search(symbol):
                    stage = s
                    break
            if stage is not None:
                break
        if stage is None:
            stage = len(STAGES) - 1
            other[resolve(frames[0][0]) + "  (" + frames[0][1].split("/")[-1] + ")"] += 1
        counts[stage] += 1
    return counts, other


def callgrind_counts(path):
    """Each function's own instruction count in a callgrind output file."""
    out = subprocess.run(["callgrind_annotate", "--threshold=100", "--inclusive=no", path],
                         capture_output=True, text=True, check=False).stdout
    counts = collections.Counter()
    for line in out.splitlines():
        m = re.match(r"\s*([\d,]+) \([^)]*\)\s+[^:]*:(.+?)(?: \[.*\])?$", line)
        if m and "PROGRAM TOTALS" not in line:
            counts[m.group(2)] += int(m.group(1).replace(",", ""))
    return counts


def classify_callgrind(short, long, rules, ours, resolve):
    """Instructions by stage, the long run's counts minus the short run's."""
    a, b = callgrind_counts(short), callgrind_counts(long)
    counts = collections.Counter()
    other = collections.Counter()
    for name in set(a) | set(b):
        n = b[name] - a[name]
        symbol = resolve(name.replace("__", "::")) if ours else resolve(name)
        stage = None
        if ours and "memset" in symbol:
            stage = 8
        elif ours and "memmove" in symbol:
            stage = 9
        else:
            for s, pattern in rules:
                if pattern.search(symbol):
                    stage = s
                    break
        if stage is None:
            stage = len(STAGES) - 1
            other[symbol] += n
        counts[stage] += n
    return counts, other


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--ours")
    parser.add_argument("--ffmpeg")
    parser.add_argument("--ffmpeg-debug", help="libavcodec's .debug file (default: found under /usr/lib/debug)")
    parser.add_argument("--perf", default=(glob.glob("/usr/lib/linux-tools-*/perf") or ["perf"])[0])
    parser.add_argument("--callgrind-ours", nargs=2, metavar=("SHORT", "LONG"))
    parser.add_argument("--callgrind-ffmpeg", nargs=2, metavar=("SHORT", "LONG"))
    parser.add_argument("--frames", type=int, help="frames the long runs decode beyond the short ones (instructions a frame)")
    parser.add_argument("--list-other", action="store_true")
    args = parser.parse_args()

    debug = args.ffmpeg_debug
    if (args.ffmpeg or args.callgrind_ffmpeg) and not debug:
        for f in glob.glob("/usr/lib/debug/.build-id/*/*.debug"):
            if "ff_h264_decode_mb_cavlc" in subprocess.run(["nm", f], capture_output=True, text=True).stdout:
                debug = f
                break

    results = {}
    if args.ours:
        results["mcdec"] = classify(args.perf, args.ours, compile_rules(OURS), False, lambda n: n)
    if args.ffmpeg:
        results["FFmpeg"] = classify(args.perf, args.ffmpeg, compile_rules(FFMPEG), True, label_resolver(debug))
    if args.callgrind_ours:
        results["mcdec"] = classify_callgrind(*args.callgrind_ours, compile_rules(OURS), True, lambda n: n)
    if args.callgrind_ffmpeg:
        results["FFmpeg"] = classify_callgrind(*args.callgrind_ffmpeg, compile_rules(FFMPEG), False, label_resolver(debug))
    if args.callgrind_ours or args.callgrind_ffmpeg:
        names = list(results)
        print("| Stage | " + " | ".join(f"{n} M instr" for n in names) + (" | Ratio |" if len(names) == 2 else " |"))
        print("| --- |" + " ---: |" * (len(names) + (1 if len(names) == 2 else 0)))
        per = args.frames or 1
        for i, stage in enumerate(STAGES + ["Total"]):
            values = [(sum(results[n][0].values()) if stage == "Total" else results[n][0][i]) / per / 1e6 for n in names]
            ratio = f" | {values[0] / values[1]:.1f}" if len(values) == 2 and values[1] > 0.005 else (" | –" if len(values) == 2 else "")
            print(f"| {stage} | " + " | ".join(f"{v:.2f}" for v in values) + ratio + " |")
        if args.list_other:
            for n in names:
                print(f"\n{n}, other:")
                for symbol, count in results[n][1].most_common(20):
                    print(f"  {count / per / 1e6:8.3f} M  {symbol}")
        return

    names = list(results)
    print("| Stage | " + " | ".join(f"{n} %" for n in names) + " |")
    print("| --- |" + " ---: |" * len(names))
    for i, stage in enumerate(STAGES):
        cells = []
        for n in names:
            counts = results[n][0]
            cells.append(f"{100 * counts[i] / max(1, sum(counts.values())):.1f}")
        print(f"| {stage} | " + " | ".join(cells) + " |")

    if args.list_other:
        for n in names:
            print(f"\n{n}, other (leaf):")
            for symbol, count in results[n][1].most_common(25):
                print(f"  {count:6d}  {symbol}")


if __name__ == "__main__":
    main()
