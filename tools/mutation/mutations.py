# The mutations tools/mutation/mutate.py makes: each breaks one guard on purpose ("find" must occur exactly once in
# "file"; "replace" takes its place), and at least one test that "tests" selects (a dotnet test filter; theory rows by
# DisplayName~, CLAUDE.md) must then fail. A mutation that survives is a guard no test covers: strengthen the tests,
# never drop the mutation. "repeat": the tests run up to that many times until one fails, for guards whose breaking
# shows only under the tests' random delays. Add one for every new guard of that kind: a frame threading wait, or
# code that reproduces something FFmpeg does unlike the spec or that only some streams reach.

DELAYED_THREADS = "DisplayName~MatchesFfmpegWithDelayedThreads"

MUTATIONS = [
    # ---- Frame threading: a picture reads only what an earlier one has reported final (CLAUDE.md, Frame threading).
    # Not listed: the co-located motion wait (PictureDecoder.Direct.cs), which the motion compensation waits before it
    # cover in practice, so breaking it alone fails no test.

    # A second field starts after its first field's Begin, which resets both fields' state.
    dict(file="src/ManagedCodecs.H264/H264Decoder.cs",
         find="job.FirstField?.Begun.Task.GetAwaiter().GetResult();", replace="_ = job.FirstField;",
         tests=DELAYED_THREADS, repeat=3),
    # A row is final one row after it is deblocked (the next row's top edge filters its bottom).
    dict(file="src/ManagedCodecs.H264/PictureDecoder.Deblock.cs",
         find="Math.Max(0, _deblockedRows - 1)", replace="_deblockedRows",
         tests=DELAYED_THREADS, repeat=3),
    # Motion compensation waits for the lowest reference row it reads, the 6-tap filter's three rows below included.
    dict(file="src/ManagedCodecs.H264/PictureDecoder.Inter.cs",
         find="reference.WaitForRow(lumaY + (mvY >> 2) + h + 3);", replace="reference.WaitForRow(lumaY + (mvY >> 2) + h - 1);",
         tests=DELAYED_THREADS, repeat=3),
    # A field reading a frame coded as a frame: its row y is the frame's row 2y + parity.
    dict(file="src/ManagedCodecs.H264/Picture.cs",
         find=": frame._finalLumaRows > (2 * row) + Parity ||", replace=": frame._finalLumaRows > row ||",
         tests=DELAYED_THREADS, repeat=3),
    # A frame reading a frame coded as fields: its row y is row y / 2 of both fields.
    dict(file="src/ManagedCodecs.H264/Picture.cs",
         find=" && Volatile.Read(ref frame._fieldFinalRows[1]) > row / 2)", replace=")",
         tests=DELAYED_THREADS, repeat=3),
    # Concealment of a missing picture copies the latest reference once it is done.
    dict(file="src/ManagedCodecs.H264/H264Decoder.cs",
         find="latest.WaitForRow(int.MaxValue);", replace="",
         tests="FullyQualifiedName~H264ConcealmentTests", repeat=3),
    # A picture is damaged if a picture in its lists is, known once each is done.
    dict(file="src/ManagedCodecs.H264/H264Decoder.cs",
         find="picture.WaitForRow(int.MaxValue);", replace="",
         tests="FullyQualifiedName~H264ConcealmentTests", repeat=3),

    # ---- H.264
    # MBAFF: frame-to-field vertical motion halves with truncation towards zero.
    dict(file="src/ManagedCodecs.H264/PictureDecoder.Direct.cs",
         find="VerticalScale.FrameToField => col.MvY / 2,", replace="VerticalScale.FrameToField => col.MvY >> 1,",
         tests="FullyQualifiedName~H264ConformanceTests.MatchesFfmpeg&DisplayName~mbaff"),

    # ---- MP4 (as FFmpeg reads it)
    # Negative composition offsets in 'ctts': FFmpeg leaves the last two entries out of the shift.
    dict(file="src/ManagedCodecs.Containers/Mp4/Mp4Demuxer.cs",
         find="if (e + 2 < entries && offset < 0 && runLength > 0)", replace="if (offset < 0 && runLength > 0)",
         tests="FullyQualifiedName~Mp4"),
    # ... every dts moves back by the largest of them, and pts keep their times.
    dict(file="src/ManagedCodecs.Containers/Mp4/Mp4Demuxer.cs",
         find="ptsOffsets[i] = checked(ptsOffsets[i] + dtsShift);", replace="",
         tests="FullyQualifiedName~Mp4"),
    # QuickTime audio: the esds inside a 'wave' box.
    dict(file="src/ManagedCodecs.Containers/Mp4/Mp4Demuxer.cs",
         find="|| (FindChild(children, Box.Wave, out var wave) && FindChild(wave, Box.Esds, out esds))", replace="",
         tests="FullyQualifiedName~Mp4|FullyQualifiedName~Aac"),

    # ---- AAC
    # A FAAC stream's encoder delay, which FFmpeg skips (its name in a fill element).
    dict(file="src/ManagedCodecs.Aac/AacDecoder.cs",
         find="private const int FaacDelay = 1024;", replace="private const int FaacDelay = 0;",
         tests="FullyQualifiedName~AacConformanceTests"),
    # Channel configuration 7 as FFmpeg's 7.1, not the spec's outside front pair.
    dict(file="src/ManagedCodecs.Aac/AacDecoder.cs",
         find="7 => [1, 2, 0, 7, 5, 6, 3, 4],", replace="7 => [1, 2, 0, 7, 3, 4, 5, 6],",
         tests="FullyQualifiedName~AacConformanceTests"),

    # ---- MJPEG
    # FFmpeg takes an image of less than 3/4 of the stream's height as a field.
    dict(file="src/ManagedCodecs.Mjpeg/MjpegDecoder.cs",
         find="height < _streamHeight * 3 / 4", replace="height <= _streamHeight / 2",
         tests="FullyQualifiedName~Mjpeg"),
]
