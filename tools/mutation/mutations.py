# The mutations tools/mutation/mutate.py makes: each breaks one guard on purpose ("find" must occur exactly once in
# "file"; "replace" takes its place), and at least one test that "tests" selects (a dotnet test filter; theory rows by
# DisplayName~, CLAUDE.md) must then fail. A mutation that survives is a guard no test covers: strengthen the tests,
# never drop the mutation. "repeat": the tests run up to that many times until one fails, for guards whose breaking
# shows only under the tests' random delays. Add one for every new guard of that kind: a frame threading wait, or
# code that reproduces something FFmpeg does unlike the spec or that only some streams reach.

DELAYED_THREADS = "DisplayName~MatchesFfmpegWithDelayedThreads"

MUTATIONS = [
    # ---- Frame threading: a picture reads only what an earlier one has reported final (src/ManagedCodecs.H264/CLAUDE.md, Frame threading).
    # Not listed, because breaking them alone fails no test: the co-located motion wait (PictureDecoder.Direct.cs), which
    # the motion compensation waits before it cover in practice, and ListsDamaged's wait for each picture in the lists
    # (H264Decoder.cs), which the motion compensation waits cover too: a picture reads its references' bottom rows,
    # final only once the reference is done and its Damaged set.

    # A second field starts after its first field's Begin, which resets both fields' state.
    dict(file="src/ManagedCodecs.H264/H264Decoder.cs",
         find="job.FirstField?.Begun.Task.GetAwaiter().GetResult();", replace="_ = job.FirstField;",
         tests=DELAYED_THREADS, repeat=3),
    # A row is final one row after it is deblocked (the next row's top edge filters its bottom).
    dict(file="src/ManagedCodecs.H264/PictureDecoder.Deblock.cs",
         find="Math.Max(0, _deblockedRows - 1)", replace="_deblockedRows",
         tests=DELAYED_THREADS, repeat=3),
    # Motion compensation waits for the lowest reference row it reads; with the delays, a check after each wait
    # (PictureDecoder.CheckRowsFinal) throws if that row is not final, so a short wait fails whenever a picture catches
    # up with its reference, not only when the rows it read early change afterwards. (Its + 3, for the 6-tap filter's rows below, is
    # not mutated: a row is reported final only once the macroblock row below it is deblocked, by which time the three
    # rows below are final too, so dropping it changes no frame.)
    dict(file="src/ManagedCodecs.H264/PictureDecoder.Inter.cs",
         find="reference.WaitForRow(lumaY + (mvY >> 2) + h + 3);", replace="reference.WaitForRow(lumaY + (mvY >> 2));",
         tests=DELAYED_THREADS, repeat=3),
    # The same wait in the path for one-reference partitions (PredictFromOne).
    dict(file="src/ManagedCodecs.H264/PictureDecoder.Inter.cs",
         find="int lowestRow = lumaY + (mvY >> 2) + h + 3;", replace="int lowestRow = lumaY + (mvY >> 2);",
         tests=DELAYED_THREADS, repeat=3),
    # And in the path for bi-predicted partitions (PredictFromTwo), for each of the two references. Fewer pictures
    # catch up with their list-0 reference there (B pictures' lists reach back further): before the check, breaking
    # that wait survived 10 runs once, and now fails several samples on the first.
    dict(file="src/ManagedCodecs.H264/PictureDecoder.Inter.cs",
         find="int lowestRow0 = lumaY + (mvY0 >> 2) + h + 3;", replace="int lowestRow0 = lumaY + (mvY0 >> 2);",
         tests=DELAYED_THREADS, repeat=3),
    dict(file="src/ManagedCodecs.H264/PictureDecoder.Inter.cs",
         find="int lowestRow1 = lumaY + (mvY1 >> 2) + h + 3;", replace="int lowestRow1 = lumaY + (mvY1 >> 2);",
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

    # ---- H.264
    # A frame of two field pictures takes its first field's picture type, as FFmpeg does (h264_frame_start), not its
    # second's: JM's PAFF samples pair an I field with a P one.
    dict(file="src/ManagedCodecs.H264/H264Decoder.cs",
         find="PictureType = _firstField.PictureType, Interlacing", replace="PictureType = ToPictureType(header.Type), Interlacing",
         tests="FullyQualifiedName~H264ConformanceTests.MatchesFfmpegFrameByFrame&DisplayName~jm-paff.mp4"),
    # Interlacing (VideoFrame.IsInterlaced, IsTopFieldFirst, FieldOrder; H264Decoder.FieldOrder), FFmpeg's rules
    # (h264_export_frame_props, h264_parser.c), each reached by some streams only.
    # The picture timing SEI of a first field's packet holds until its second field, where the frame's flags are set.
    dict(file="src/ManagedCodecs.H264/H264Decoder.cs",
         find="        if (_firstField is null)\n        {\n            _timing = null;\n        }",
         replace="        _timing = null;",
         tests="FullyQualifiedName~H264InterlacingTests.FirstFieldsSei"),
    # A first field's order count is compared as decoded, before memory_management_control_operation 5 rebased it.
    dict(file="src/ManagedCodecs.H264/H264Decoder.cs",
         find="int decodedPoc = header.BottomField ? _order.DecodedFields.Bottom : _order.DecodedFields.Top;",
         replace="int decodedPoc = header.BottomField ? _order.Fields.Bottom : _order.Fields.Top;",
         tests="FullyQualifiedName~H264ConformanceTests.MatchesFfmpegFrameByFrame&DisplayName~jm-paff-mmco5"),
    # The stream's field order is its first picture's, not the first known one (a field pair's second field's).
    dict(file="src/ManagedCodecs.H264/H264Decoder.cs",
         find="if (!_started)", replace="if (!_started || order != FieldOrder.Unknown)",
         tests="FullyQualifiedName~H264ConformanceTests.MatchesFfmpegFrameByFrame&DisplayName~jm-paff.mp4"),
    # The previous frame's flag starts over at a new picture size.
    dict(file="src/ManagedCodecs.H264/H264Decoder.cs",
         find="            _decoders.Clear();\n            _previousInterlaced = true;", replace="            _decoders.Clear();",
         tests="FullyQualifiedName~H264InterlacingTests.PreviousFlag"),
    # Without the SEI, an MBAFF frame is interlaced (as a field pair is).
    dict(file="src/ManagedCodecs.H264/H264Decoder.cs",
         find="header.FieldPic || sps.MbAdaptiveFrameField, _previousInterlaced", replace="header.FieldPic, _previousInterlaced",
         tests="FullyQualifiedName~H264ConformanceTests.MatchesFfmpegFrameByFrame&DisplayName~nosei"),
    # pic_struct 3 and 4 on a progressive frame keep the previous frame's flag (soft telecine).
    dict(file="src/ManagedCodecs.H264/Interlacing.cs",
         find="3 or 4 => fieldOrMbaff || previousInterlaced,", replace="3 or 4 => fieldOrMbaff,",
         tests="FullyQualifiedName~H264ConformanceTests.MatchesFfmpegFrameByFrame&DisplayName~h264-pic-struct"),
    # A clock timestamp's ct_type 0 or 1 overrides pic_struct, only up to pic_struct 4.
    dict(file="src/ManagedCodecs.H264/Interlacing.cs",
         find="if ((t.CtTypes & 3) != 0 && t.PicStruct <= 4)", replace="if ((t.CtTypes & 3) != 0 && t.PicStruct < 0)",
         tests="FullyQualifiedName~H264ConformanceTests.MatchesFfmpegFrameByFrame&DisplayName~pic-struct"),
    dict(file="src/ManagedCodecs.H264/Interlacing.cs",
         find="if ((t.CtTypes & 3) != 0 && t.PicStruct <= 4)", replace="if ((t.CtTypes & 3) != 0)",
         tests="FullyQualifiedName~H264ConformanceTests.MatchesFfmpegFrameByFrame&DisplayName~pic-struct"),
    # With equal order counts, pic_struct 3 and 5 say top field first.
    dict(file="src/ManagedCodecs.H264/Interlacing.cs",
         find="u.PicStruct is 3 or 5", replace="u.PicStruct is 3",
         tests="FullyQualifiedName~H264ConformanceTests.MatchesFfmpegFrameByFrame&DisplayName~h264-pic-struct"),
    # The parser's field order follows pic_struct, not the order counts, where the SEI is there.
    dict(file="src/ManagedCodecs.H264/Interlacing.cs",
         find="        if (timing is { } t)\n        {\n            return t.PicStruct switch",
         replace="        if (timing is { } t && t.PicStruct < 0)\n        {\n            return t.PicStruct switch",
         tests="FullyQualifiedName~H264ConformanceTests.MatchesFfmpegFrameByFrame&DisplayName~h264-mbaff-bff.mp4"),
    # The picture timing SEI starts with the HRD delays where the SPS has HRD parameters.
    dict(file="src/ManagedCodecs.H264/Interlacing.cs",
         find="if (layout.HasDelays)", replace="if (false)",
         tests="FullyQualifiedName~H264ConformanceTests.MatchesFfmpegFrameByFrame&DisplayName~hrd"),
    # Concealment guesses a missing macroblock's motion from its neighbours (boundary matching), not zero motion.
    dict(file="src/ManagedCodecs.H264/PictureDecoder.Conceal.cs",
         find="        if (count == 1)\n        {\n            return candidates[0];\n        }\n\n        var (origin",
         replace="        if (count >= 1)\n        {\n            return candidates[0];\n        }\n\n        var (origin",
         tests="FullyQualifiedName~CutPacket_MotionGuessed"),
    # The same in MBAFF frames, whose neighbours' motion is converted between field and frame macroblocks.
    dict(file="src/ManagedCodecs.H264/PictureDecoder.Conceal.cs",
         find="        if (count == 1)\n        {\n            return candidates[0];\n        }\n\n        var plane",
         replace="        if (count >= 1)\n        {\n            return candidates[0];\n        }\n\n        var plane",
         tests="FullyQualifiedName~CutPacket_MotionGuessed&DisplayName~mbaff"),
    # MBAFF: frame-to-field vertical motion halves with truncation towards zero.
    dict(file="src/ManagedCodecs.H264/PictureDecoder.Direct.cs",
         find="VerticalScale.FrameToField => col.MvY / 2,", replace="VerticalScale.FrameToField => col.MvY >> 1,",
         tests="FullyQualifiedName~H264ConformanceTests.MatchesFfmpeg&DisplayName~mbaff"),
    # The container's picture size as more cropping, as FFmpeg's decoder takes it (init_dimensions): applied; not where
    # the SPS crops the top; not where it rounds up to another multiple of 16.
    dict(file="src/ManagedCodecs.H264/H264Decoder.cs",
         find="            return (_containerWidth, _containerHeight);", replace="            return (sps.Width, sps.Height);",
         tests="DisplayName~h264-container-crop"),
    dict(file="src/ManagedCodecs.H264/H264Decoder.cs",
         find="&& sps.Crop.Top == 0 && sps.Crop.Left == 0", replace="&& sps.Crop.Left == 0",
         tests="DisplayName~h264-container-crop"),
    dict(file="src/ManagedCodecs.H264/H264Decoder.cs",
         find="&& RoundUp16(containerHeight) == RoundUp16(sps.Height)", replace="",
         tests="DisplayName~h264-container-crop"),
    # Long-term field pictures (reference: JM's decoder; FFmpeg breaks the second): an IDR field marked long-term by
    # long_term_reference_flag; a field taking a LongTermFrameIdx keeps it on the other field of its own frame.
    dict(file="src/ManagedCodecs.H264/ReferenceList.cs",
         find="                MarkFieldLongTerm(field, 0);", replace="                field.IsShortTermReference = true;",
         tests="DisplayName~jm-paff-ltr"),
    dict(file="src/ManagedCodecs.H264/ReferenceList.cs",
         find="if (holder is not null && !(holder == keep && !holder.IsLongTermReference))", replace="if (holder is not null)",
         tests="DisplayName~jm-paff-ltr"),
    # The second field of a frame whose first field is long-term does not slide the window either (a unit test).
    dict(file="src/ManagedCodecs.H264/ReferenceList.cs",
         find="else if (!(secondField && frame.Field(1 - header.Parity).IsReference))",
         replace="else if (!(secondField && frame.Field(1 - header.Parity).IsShortTermReference))",
         tests="FullyQualifiedName~ReferenceListTests"),
    # Memory management operation 5 in a field picture: the field's order count rebased to 0 (a unit test), and its
    # second field paired with frame_num 0.
    dict(file="src/ManagedCodecs.H264/PictureOrder.cs",
         find="int field = mmco5 ? 0 : decoded;", replace="int field = decoded;",
         tests="FullyQualifiedName~PictureOrderTests"),
    dict(file="src/ManagedCodecs.H264/H264Decoder.cs",
         find="&& FrameNumAfter(first.Header) == header.FrameNum", replace="&& first.Header.FrameNum == header.FrameNum",
         tests="DisplayName~paff-mmco5"),
    # A non-IDR picture activating an SPS of another size or format (against 7.4.1.2.1): the references, of the old
    # format, are dropped rather than read as the new one's (the fuzzing finding casts 16-bit planes to 8-bit).
    dict(file="src/ManagedCodecs.H264/H264Decoder.cs",
         find="else if (_references.Any(p => !FitsFormat(p, sps)))", replace="else if (false)",
         tests="DisplayName~sps-format-change-without-idr"),

    # ---- AVI (as FFmpeg reads it)
    # An empty video chunk (a dropped frame) gives no packet but takes its tick.
    dict(file="src/ManagedCodecs.Containers/Avi/AviDemuxer.cs",
         find="if (isVideoChunk && size == 0)\n                {\n                    frame++;\n                }\n                else ",
         replace="", tests="FullyQualifiedName~Avi|DisplayName~avi-dropped-frame"),

    # ---- MP4 (as FFmpeg reads it)
    # Negative composition offsets in 'ctts': FFmpeg leaves the last two entries out of the shift.
    dict(file="src/ManagedCodecs.Containers/Mp4/Mp4Demuxer.cs",
         find="if (e + 2 < entries && offset < 0 && runLength > 0)", replace="if (offset < 0 && runLength > 0)",
         tests="FullyQualifiedName~Mp4"),
    # ... every dts moves back by the largest of them, and pts keep their times.
    dict(file="src/ManagedCodecs.Containers/Mp4/Mp4Demuxer.cs",
         find="ptsOffsets[i] = checked(ptsOffsets[i] + dtsShift);", replace="",
         tests="FullyQualifiedName~Mp4"),
    # FFmpeg's mov_fix_index: the first pts shown, less leading empty edits, moves to 0 (discarded samples don't count).
    dict(file="src/ManagedCodecs.Containers/Mp4/Mp4Demuxer.cs",
         find="samples.Dts[i] -= late;", replace="",
         tests="FullyQualifiedName~Mp4"),
    dict(file="src/ManagedCodecs.Containers/Mp4/Mp4Demuxer.cs",
         find="if ((samples.Flags[i] & SampleTable.Discard) == 0)", replace="if (true)",
         tests="FullyQualifiedName~Mp4"),
    dict(file="src/ManagedCodecs.Containers/Mp4/Mp4Demuxer.cs",
         find="firstShown - edits[0].Start;", replace="firstShown;",
         tests="FullyQualifiedName~Mp4"),
    # QuickTime audio: the esds inside a 'wave' box.
    dict(file="src/ManagedCodecs.Containers/Mp4/Mp4Demuxer.cs",
         find="|| (FindChild(children, Box.Wave, out var wave) && FindChild(wave, Box.Esds, out esds))", replace="",
         tests="FullyQualifiedName~Mp4|FullyQualifiedName~Aac"),

    # Seeking starts at the last keyframe whose pts is at or before the time: a frame shown at a keyframe's own time
    # decodes from that keyframe, not the one before (FFmpeg's -ss reaches it the same way).
    dict(file="src/ManagedCodecs.Containers/Mp4/Mp4Demuxer.cs",
         find="if (Sizes[i] != 0 && IsKeyframe(i) && Pts(i) <= pts)", replace="if (Sizes[i] != 0 && IsKeyframe(i) && Pts(i) < pts)",
         tests="FullyQualifiedName~SeekTests"),
    # The other stream starts at the same instant, not at its beginning.
    dict(file="src/ManagedCodecs.Containers/Mp4/Mp4Demuxer.cs",
         find="int otherStart = otherBase is { } b ? other.KeyframeAtOrBefore(Rescale(pts, seekedBase, b)) : 0;", replace="int otherStart = 0;",
         tests="FullyQualifiedName~SeekTests"),

    # A stream's duration as FFmpeg gives it (VideoProbe's against ffprobe's): an edit lasts no longer than the samples it
    # takes (JM's field-picture files) ...
    dict(file="src/ManagedCodecs.Containers/Mp4/Mp4Demuxer.cs",
         find="return Math.Min(edited, total);", replace="return edited;",
         tests="FullyQualifiedName~VideoProbeTests"),
    # ... and a fragmented file's samples outlast its 'mdhd'.
    dict(file="src/ManagedCodecs.Containers/Mp4/Mp4Demuxer.cs",
         find="return Math.Max(MediaDuration, total);", replace="return MediaDuration;",
         tests="FullyQualifiedName~VideoProbeTests"),
    # The probe's H.264 frame size is the SPS's visible area or the container's crop of it, as the decoder's frames.
    dict(file="src/ManagedCodecs.H264/H264Decoder.cs",
         find="return TakesContainerSize(sps, stream.Width, stream.Height) ? (stream.Width, stream.Height) : (sps.Width, sps.Height);",
         replace="return (stream.Width, stream.Height);",
         tests="FullyQualifiedName~VideoProbeTests"),

    # ---- AAC
    # A FAAC stream's encoder delay, which FFmpeg skips (its name in a fill element).
    dict(file="src/ManagedCodecs.Aac/AacDecoder.cs",
         find="private const int FaacDelay = 1024;", replace="private const int FaacDelay = 0;",
         tests="FullyQualifiedName~AacConformanceTests"),
    # A frame's pts moves by the samples dropped from its start, counted at the output rate (with SBR twice the core's).
    dict(file="src/ManagedCodecs.Aac/AacDecoder.cs",
         find="long denominator = (long)SampleRate * timeBase.Numerator;", replace="long denominator = (long)_config.SampleRate * timeBase.Numerator;",
         tests="FullyQualifiedName~FramePtsMatchFfmpeg"),
    dict(file="src/ManagedCodecs.Aac/AacDecoder.cs",
         find="output.Pts = packet.Pts + SamplesToTime(dropped);", replace="output.Pts = packet.Pts;",
         tests="FullyQualifiedName~FramePtsMatchFfmpeg"),
    # Parametric stereo's mixing procedure Rb, which only the patched-FDK heaacv2-rb sample sends, and its rho taken as at
    # least 0.05, as FFmpeg takes it.
    dict(file="src/ManagedCodecs.Aac/Sbr/PsProcessor.cs",
         find="bool rb = hasData && data.EnableIcc ? data.IccMode >= 3 : _lastRb;", replace="bool rb = false;",
         tests="DisplayName~heaacv2-rb"),
    dict(file="src/ManagedCodecs.Aac/Sbr/PsProcessor.cs",
         find="rho = Math.Max(rho, 0.05);", replace="rho = Math.Max(rho, -1);",
         tests="DisplayName~heaacv2-rb"),
    # Parametric stereo's phase parameters (IPD, OPD), which only the patched-FDK heaacv2-ipd sample sends, applied as
    # FFmpeg applies them: the frame's enable_ipdopd, smoothed over envelopes, and in the two sub-subbands of negative
    # frequency the previous envelope's imaginary parts negated at the start only.
    dict(file="src/ManagedCodecs.Aac/Sbr/PsProcessor.cs",
         find="bool phases = hasData ? data.EnableIpdOpd : _lastIpdOpd;", replace="bool phases = false;",
         tests="DisplayName~heaacv2-ipd"),
    dict(file="src/ManagedCodecs.Aac/Sbr/PsProcessor.cs",
         find="_ipdHistory[bin] = ipd & 63;", replace="_ipdHistory[bin] = 0;",
         tests="DisplayName~heaacv2-ipd"),
    dict(file="src/ManagedCodecs.Aac/Sbr/PsProcessor.cs",
         find="negated: (0, 1));", replace="negated: (-1, -1));",
         tests="DisplayName~heaacv2-ipd"),
    dict(file="src/ManagedCodecs.Aac/Sbr/PsProcessor.cs",
         find="double e11 = (hIm[bin] - g11) / length;", replace="double e11 = ((sign * hIm[bin]) - g11) / length;",
         tests="DisplayName~heaacv2-ipd"),
    # Parametric stereo in 34 bands, which only the patched-FDK heaacv2-34 and heaacv2-34-ipd samples send, as FFmpeg
    # processes it: 34 bands when either IID or ICC is in 34, a switch clearing the decorrelator and moving the matrices,
    # its 8-band filter's prototype of its own, the decay's cutoff at QMF band 5, the phases' imaginary parts negated in
    # groups 9 to 13 and taken from the stale envelope slots above the phase bands, and the phase history cleared.
    dict(file="src/ManagedCodecs.Aac/Sbr/PsProcessor.cs",
         find="_is34 = (data.EnableIid && data.IidBands == 34) || (data.EnableIcc && data.IccBands == 34);",
         replace="_is34 = (data.EnableIid && data.IidBands == 34) && (data.EnableIcc && data.IccBands == 34);",
         tests="DisplayName~heaacv2-34"),
    dict(file="src/ManagedCodecs.Aac/Sbr/PsProcessor.cs",
         find="Filters(Prototype8Fine, 8)", replace="Filters(Prototype8, 8)",
         tests="DisplayName~heaacv2-34"),
    dict(file="src/ManagedCodecs.Aac/Sbr/PsProcessor.cs",
         find="decayCutoff: 5,", replace="decayCutoff: 3,",
         tests="DisplayName~heaacv2-34"),
    dict(file="src/ManagedCodecs.Aac/Sbr/PsProcessor.cs",
         find="negated: (9, 13));", replace="negated: (9, 11));",
         tests="DisplayName~heaacv2-34-ipd"),
    dict(file="src/ManagedCodecs.Aac/Sbr/PsProcessor.cs",
         find="hIm[(m * MaxBins) + bin] = _imSlots[m][e + 1][bin];", replace="hIm[(m * MaxBins) + bin] = 0;",
         tests="DisplayName~heaacv2-34-ipd"),
    dict(file="src/ManagedCodecs.Aac/Sbr/PsProcessor.cs",
         find="            MapValues(_previousH[m], to34);\n", replace="",
         tests="DisplayName~heaacv2-34"),
    dict(file="src/ManagedCodecs.Aac/Sbr/PsProcessor.cs",
         find="            MapValues(_previousHIm[m], to34);\n", replace="",
         tests="DisplayName~heaacv2-34-ipd"),
    dict(file="src/ManagedCodecs.Aac/Sbr/PsProcessor.cs",
         find="        Array.Clear(_ipdHistory);\n", replace="",
         tests="DisplayName~heaacv2-34-ipd"),
    dict(file="src/ManagedCodecs.Aac/Sbr/PsProcessor.cs",
         find="        Array.Clear(_energy);\n", replace="",
         tests="FullyQualifiedName~SwitchStartsTheDecorrelatorOver"),
    dict(file="src/ManagedCodecs.Aac/Sbr/PsProcessor.cs",
         find="        m[1] = (p[0] + p[1]) / 2;", replace="        m[1] = p[0];",
         tests="FullyQualifiedName~ParameterMapsMatchFfmpeg"),
    dict(file="src/ManagedCodecs.Aac/Sbr/PsProcessor.cs",
         find="            values[1] = (p[0] + p[1]) / 2;", replace="            values[1] = p[0];",
         tests="FullyQualifiedName~MatrixValueMapsMatchFfmpeg"),
    # The phase parameters' band count kept from the last header that enabled IID, as FFmpeg keeps it (heaacv2-ipd's
    # first phases come under a header without IID; its output there stays within the SBR bound, so a parsing test).
    dict(file="src/ManagedCodecs.Aac/Sbr/PsData.cs",
         find="PhaseBands = EnableIid ? PhaseBandCounts[IidMode % 3] : PhaseBands;", replace="PhaseBands = PhaseBandCounts[IidMode % 3];",
         tests="FullyQualifiedName~PhaseBandsFollowTheLastHeaderWithIid"),
    # A frame without envelopes under a coarse header, keeping fine IID indices from before (two fuzzing findings, which
    # indexed past the coarse scale factors): rejected, as FFmpeg rejects it.
    dict(file="src/ManagedCodecs.Aac/Sbr/PsProcessor.cs",
         find="if (hasData && data.EnableIid && _lastIid.AsSpan(0, layout.Bins).ContainsAnyExceptInRange(data.FineIid ? -15 : -7, data.FineIid ? 15 : 7))",
         replace="if (false)",
         tests="DisplayName~ps-fine-iid-kept-by-coarse-header"),
    # A pulse on a zero quantized value goes negative (4.6.3.3: x_quant > 0 adds the amplitude, else subtracts it); only
    # the hand-built aac-pulse sample (tools/make-aac-pulse.py) has pulse data.
    dict(file="src/ManagedCodecs.Aac/AacDecoder.cs",
         find="q[k] += q[k] > 0 ? stream.PulseAmplitude[i] : -stream.PulseAmplitude[i];",
         replace="q[k] += q[k] >= 0 ? stream.PulseAmplitude[i] : -stream.PulseAmplitude[i];",
         tests="DisplayName~aac-pulse"),
    # Channel configuration 7 as FFmpeg's 7.1, not the spec's outside front pair.
    dict(file="src/ManagedCodecs.Aac/AacDecoder.cs",
         find="7 => [1, 2, 0, 7, 5, 6, 3, 4],", replace="7 => [1, 2, 0, 7, 3, 4, 5, 6],",
         tests="FullyQualifiedName~AacConformanceTests"),

    # ---- H.264 transforms
    # The 16-bit 4x4 transform takes only coefficients under 2^11, whose sums fit 16 bits; JM's encoder with scaling
    # matrices writes larger ones (jm-planes-qm), which go to the 32-bit transform.
    dict(file="src/ManagedCodecs.H264/Transform.cs",
         find="(magnitudes & Vector128.Create(~2047))", replace="(magnitudes & Vector128.Create(int.MinValue))",
         tests="DisplayName~jm-planes-qm"),
    # The 16-bit 8x8 transform takes only coefficients under 2^9, whose sums fit 16 bits (the random test's larger
    # ones overflow without it).
    dict(file="src/ManagedCodecs.H264/Transform.cs",
         find="(magnitudes & Vector128.Create(~511))", replace="(magnitudes & Vector128.Create(int.MinValue))",
         tests="FullyQualifiedName~TransformTests"),

    # run_before's codes longer than 3 bits (runs of 7 to 14, only with more than 6 zeros left) come from their
    # leading zeros, not the table.
    dict(file="src/ManagedCodecs.H264/Cavlc.cs",
         find="return zeros + 4;", replace="return zeros + 3;",
         tests="FullyQualifiedName~H264"),

    # The VUI's signal as FFmpeg reads it (H.264 E.2.1; the frames' colour properties). A VUI without chroma_loc_info
    # means left, whatever the chroma format: libx264's and OpenH264's streams (h264-color-*, name.color).
    dict(file="src/ManagedCodecs.H264/ParameterSets.cs",
         find="var location = ChromaLocation.Left;", replace="var location = ChromaLocation.Unspecified;",
         tests="FullyQualifiedName~VideoSignalTests.EmptyVui|DisplayName~h264-color-bt709"),
    # FFmpeg's av_image_check_sar: a ratio that shows the frame as nothing wide (or high) is unknown; no encoder writes one.
    dict(file="src/ManagedCodecs.H264/VideoSignal.cs",
         find="return shown > 0 ? ratio : new(0, 1);", replace="return ratio;",
         tests="FullyQualifiedName~VideoSignalTests"),
    # Matrix coefficients FFmpeg 6.1 does not name (15 and up, IPT-C2 and YCgCo-R among them) are unspecified to it.
    dict(file="src/ManagedCodecs.Core/ColorCodes.cs",
         find="value is >= 0 and <= 14 ? (ColorMatrix)value", replace="value is >= 0 and <= 255 ? (ColorMatrix)value",
         tests="FullyQualifiedName~VideoSignalTests"),
    # The container's colour and ratio as FFmpeg gives frames them: the VUI's range wins over 'colr' where it sends one
    # (h264-colr-conflict), the container's ratio over the VUI's (h264-pasp-conflict, ffprobe's
    # av_guess_sample_aspect_ratio), and an 'nclx' without its range flag means limited (h264-colr-only).
    dict(file="src/ManagedCodecs.H264/VideoSignal.cs",
         find="frame.ColorRange = RangeSent || stream is null ? Range : stream.ColorRange;", replace="frame.ColorRange = stream is null ? Range : stream.ColorRange;",
         tests="DisplayName~h264-colr-conflict"),
    dict(file="src/ManagedCodecs.H264/VideoSignal.cs",
         find="stream is { SampleAspectRatio.Numerator: > 0 } ? stream.SampleAspectRatio : ", replace="",
         tests="DisplayName~h264-pasp-conflict"),
    dict(file="src/ManagedCodecs.Containers/Mp4/Mp4Demuxer.cs",
         find="? ColorRange.Full : ColorRange.Limited) : Color.Range;", replace="? ColorRange.Full : ColorRange.Unspecified) : Color.Range;",
         tests="FullyQualifiedName~Mp4DemuxerTests|DisplayName~h264-colr-only"),
    # FFmpeg's codec context keeps the range and colour description an SPS leaves out (h264_init_ps).
    dict(file="src/ManagedCodecs.H264/VideoSignal.cs",
         find="var signal = RangeSent ? this : this with { Range = previous.Range, RangeSent = previous.RangeSent };", replace="var signal = this;",
         tests="FullyQualifiedName~VideoSignalTests"),
    dict(file="src/ManagedCodecs.H264/VideoSignal.cs",
         find="if (ColourSent)\n        {\n            return signal;", replace="if (true)\n        {\n            return signal;",
         tests="FullyQualifiedName~VideoSignalTests"),
    # The decoder's signal reaches each frame (and the conformance tests compare it with name.color).
    dict(file="src/ManagedCodecs.H264/H264Decoder.cs",
         find="_signal = sps.Signal.After(_signal);", replace="",
         tests="DisplayName~h264-color-bt2020-pq"),
    # ---- MJPEG
    # FFmpeg takes an image of less than 3/4 of the stream's height (integer division) as a field.
    dict(file="src/ManagedCodecs.Mjpeg/MjpegDecoder.cs",
         find="height < _streamHeight * 3 / 4", replace="height <= _streamHeight * 3 / 4",
         tests="FullyQualifiedName~Mjpeg"),
    # FFmpeg's colour properties for JPEG (T.81 leaves them open): a gray image's range is unspecified, not full
    # (gray.avi, ImageMagick's), and its encoder's "CS=ITU601" comment means limited range (mjpeg-limited-range.avi).
    dict(file="src/ManagedCodecs.Mjpeg/MjpegDecoder.cs",
         find="frame.Format == PixelFormat.Gray8 ? _stream?.ColorRange ?? ColorRange.Unspecified : ", replace="",
         tests="FullyQualifiedName~Mjpeg"),
    dict(file="src/ManagedCodecs.Mjpeg/MjpegDecoder.cs",
         find="_limitedRange = true;", replace="_limitedRange = false;",
         tests="FullyQualifiedName~Mjpeg"),
    # MJPEG frames take the container's aspect ratio where it has one, over the JFIF density, as ffprobe shows FFmpeg's
    # frames (mjpeg-vprp-conflict: AVI's 'vprp' 4:3 over JFIF's 2:1).
    dict(file="src/ManagedCodecs.Mjpeg/MjpegDecoder.cs",
         find="frame.SampleAspectRatio = _stream is { SampleAspectRatio.Numerator: > 0 } ? _stream.SampleAspectRatio : _aspectRatio;",
         replace="frame.SampleAspectRatio = _aspectRatio;",
         tests="DisplayName~mjpeg-vprp-conflict"),
    # MJPEG frames take the container's primaries and transfer, which JPEG has none of (mjpeg-colr).
    dict(file="src/ManagedCodecs.Mjpeg/MjpegDecoder.cs",
         find="frame.ColorPrimaries = _stream?.ColorPrimaries ?? ColorPrimaries.Unspecified;", replace="frame.ColorPrimaries = ColorPrimaries.Unspecified;",
         tests="DisplayName~mjpeg-colr"),
]
