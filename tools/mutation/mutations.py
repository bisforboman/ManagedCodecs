# The mutations tools/mutation/mutate.py makes: each breaks one guard on purpose ("find" must occur exactly once in
# "file"; "replace" takes its place), and at least one test that "tests" selects (a dotnet test filter; theory rows by
# DisplayName~, CLAUDE.md) must then fail. A mutation that survives is a guard no test covers: strengthen the tests,
# never drop the mutation. "repeat": the tests run up to that many times until one fails, for guards whose breaking
# shows only under the tests' random delays. Add one for every new guard of that kind: a frame threading wait, or
# code that reproduces something FFmpeg does unlike the spec or that only some streams reach.

DELAYED_THREADS = "DisplayName~MatchesFfmpegWithDelayedThreads"

MUTATIONS = [
    # ---- Frame threading: a picture reads only what an earlier one has reported final (src/ManagedCodecs.H264/CLAUDE.md, Frame threading).
    # Not listed, because breaking them alone fails no test: the co-located motion waits (PictureDecoder.Direct.cs,
    # CoLocated and ColZero), which the motion compensation waits before them cover in practice, and ListsDamaged's
    # wait for each picture in the lists
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
    # Audio with a dwSampleSize below 32 comes in pieces of 1024 blocks, from 32 up one block a packet...
    dict(file="src/ManagedCodecs.Containers/Avi/AviDemuxer.cs",
         find="blockSize < 32 ? 1024 * blockSize : blockSize", replace="size",
         tests="FullyQualifiedName~AviDemuxerTests"),
    # ... each timed by all the bytes before it, not by the pieces' durations summed.
    dict(file="src/ManagedCodecs.Containers/Avi/AviDemuxer.cs",
         find="long pts = state.Bytes / blockSize;", replace="long pts = state.Position; state.Position += length / blockSize;",
         tests="FullyQualifiedName~AviDemuxerTests"),

    # ---- MXF partitions (SMPTE ST 377-1): rules only some files reach
    # A key's byte 8 (the registry version) is ignored: earlier encoders wrote the fill item's with version 1 (6.3.3).
    dict(file="src/ManagedCodecs.Containers/Mxf/Klv.cs",
         find="key[..7].SequenceEqual(label[..7]) && key[8..label.Length].SequenceEqual(label[8..])",
         replace="key[..label.Length].SequenceEqual(label)",
         tests="FullyQualifiedName~MxfPartitionTests"),
    # A run-in is under 64 KiB (6.6): the header partition is searched for no further.
    dict(file="src/ManagedCodecs.Containers/Mxf/MxfPartitions.cs",
         find="new byte[Klv.MaxRunIn - 1 + Klv.KeyLength]", replace="new byte[(2 * Klv.MaxRunIn) + Klv.KeyLength]",
         tests="FullyQualifiedName~MxfPartitionTests"),
    # A random index pack is taken only if each partition it lists has its BodySID ...
    dict(file="src/ManagedCodecs.Containers/Mxf/MxfPartitions.cs",
         find="if (partition.BodySid != bodySid)", replace="if (false)",
         tests="FullyQualifiedName~MxfPartitionTests"),
    # ... and without one, the partitions come back from the footer by PreviousPartition (mxf-h264-pcm-norip).
    dict(file="src/ManagedCodecs.Containers/Mxf/MxfPartitions.cs",
         find="if (TryFromFooter(stream, runIn, header, out partitions))", replace="if (false)",
         tests="FullyQualifiedName~MxfPartitionTests"),
    # ---- MXF demuxer (packets as FFmpeg's)
    # An intra-only codec's packets are all keyframes, whatever the index says (FFmpeg's muxer leaves ProRes without
    # the random access flag: mxf-prores-pcm16).
    dict(file="src/ManagedCodecs.Containers/Mxf/MxfDemuxer.cs",
         find="bool keyframe = intra || (entry", replace="bool keyframe = (entry",
         tests="FullyQualifiedName~MxfConformanceTests"),
    # The temporal offset applies only where the index says the element is reordered (11.2.3, PosTableIndex -1).
    dict(file="src/ManagedCodecs.Containers/Mxf/MxfDemuxer.cs",
         find="entry is { } e && index!.FirstElementReordered ? e.TemporalOffset : 0", replace="entry is { } e ? e.TemporalOffset : 0",
         tests="FullyQualifiedName~MxfConformanceTests"),
    # Each stream starts at its own keyframe at or before the time (the audio's every packet).
    dict(file="src/ManagedCodecs.Containers/Mxf/MxfDemuxer.cs",
         find="_audioStart = Start(audio.StreamIndex, Rescale(pts, timeBase, audio.TimeBase));", replace="",
         tests="FullyQualifiedName~MxfConformanceTests|DisplayName~mxf"),
    # Clip-wrapped sound comes in pieces of a 25th of a second, FFmpeg's reading (mxf-opatom-pcm: 1920 samples).
    dict(file="src/ManagedCodecs.Containers/Mxf/MxfDemuxer.cs",
         find="audio.AudioSamplingRate.Numerator / 25)", replace="audio.AudioSamplingRate.Numerator / 50)",
         tests="FullyQualifiedName~MxfConformanceTests"),
    # Clip-wrapped pictures are cut by the index entries' stream offsets (mxf-opatom-h264) ...
    dict(file="src/ManagedCodecs.Containers/Mxf/MxfIndex.cs",
         find="long end = i + 1 < offsets.Count ? offsets[i + 1] : size;", replace="long end = size;",
         tests="FullyQualifiedName~MxfConformanceTests|FullyQualifiedName~MxfIndexTests"),
    # ... or by the constant edit unit size where the index has no entries (mxf-opatom-dnxhr).
    dict(file="src/ManagedCodecs.Containers/Mxf/MxfIndex.cs",
         find="else if (EditUnitByteCount > 0)", replace="else if (false)",
         tests="FullyQualifiedName~MxfConformanceTests|FullyQualifiedName~MxfIndexTests"),
    # Only a track's single element is clip-wrapped: frame-wrapped elements stay whole (every OP1a sample).
    dict(file="src/ManagedCodecs.Containers/Mxf/MxfDemuxer.cs",
         find="""                if (at >= 0)
                {
                    return;
                }
""", replace="",
         tests="FullyQualifiedName~MxfConformanceTests"),
    # ---- MXF header metadata (SMPTE ST 377-1 9, Annexes B, F, G)
    # One fill item may come between the partition pack and the primer pack (9.1), as FFmpeg's muxer writes.
    dict(file="src/ManagedCodecs.Containers/Mxf/MxfHeaderMetadata.cs",
         find="if (Klv.IsFill(key))", replace="if (false)",
         tests="FullyQualifiedName~MxfHeaderTests"),
    # An open header's metadata gives way to a closed partition's (6.2.3; the unit test, since FFmpeg's files close it).
    dict(file="src/ManagedCodecs.Containers/Mxf/MxfHeader.cs",
         find="if (partition.IsClosed && partition.HeaderByteCount > 0)", replace="if (false)",
         tests="FullyQualifiedName~MxfHeaderTests"),
    # A multiple descriptor's sub-descriptor is the one whose LinkedTrackID names the track (mxf-prores-pcm16).
    dict(file="src/ManagedCodecs.Containers/Mxf/MxfHeader.cs",
         find=".FirstOrDefault(d => d.UInt32(MxfLabels.LinkedTrackId) == trackId)", replace=".FirstOrDefault()",
         tests="FullyQualifiedName~MxfHeaderTests"),
    # Separate fields store a field: the frame is twice as tall (G.2.1; mxf-h264-interlaced).
    dict(file="src/ManagedCodecs.Containers/Mxf/MxfHeader.cs",
         find="int fields = layout == 1 ? 2 : 1;", replace="int fields = 1;",
         tests="FullyQualifiedName~MxfHeaderTests"),
    # A duration of -1 is unknown (a pipe-written file's open header: mxf-h264-pipe).
    dict(file="src/ManagedCodecs.Containers/Mxf/MxfHeader.cs",
         find="is { } d and >= 0 ? d : null", replace="is { } d ? d : null",
         tests="FullyQualifiedName~MxfHeaderTests"),
    # Drop-frame timecodes skip codes each minute but the tenth (mxf-h264-interlaced's 01:00:00;02).
    dict(file="src/ManagedCodecs.Containers/Mxf/MxfHeader.cs",
         find="frames += (9L * drop * tens) + (rest > drop ? drop * ((rest - drop) / perMinute) : 0);", replace="",
         tests="FullyQualifiedName~MxfHeaderTests"),

    # ---- Raw H.264: a seek starts at an IDR picture, not at the recovery points FFmpeg's parser also flags as keyframes
    # (an open GOP's pictures shown before one need the GOP before; h264-raw-open-gop).
    dict(file="src/ManagedCodecs.Containers/H264/H264Demuxer.cs",
         find="if (_units[i].Idr)", replace="if (_units[i].Keyframe)",
         tests="DisplayName~h264-raw-open-gop"),

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
         find="int otherStart = otherBase is { } b ? other.KeyframeAtOrBefore(Timestamps.Rescale(pts, seekedBase, b)) : 0;", replace="int otherStart = 0;",
         tests="FullyQualifiedName~SeekTests"),

    # ---- Matroska (as FFmpeg reads it)
    # The track's CodecDelay moves its timestamps back (AAC's and Opus's first packets at negative times) ...
    dict(file="src/ManagedCodecs.Containers/Matroska/MatroskaDemuxer.cs",
         find="long pts = checked(clusterTime + relative - track.CodecDelayTicks);", replace="long pts = checked(clusterTime + relative);",
         tests="FullyQualifiedName~Matroska"),
    # ... rounded to the nearest tick (Opus's 6.5 ms to 7).
    dict(file="src/ManagedCodecs.Containers/Matroska/MatroskaDemuxer.cs",
         find="track.CodecDelayTicks = (track.CodecDelay + (TimestampScale / 2)) / TimestampScale;",
         replace="track.CodecDelayTicks = track.CodecDelay / TimestampScale;",
         tests="FullyQualifiedName~Matroska"),
    # A lace's frames share its duration out, the remainder to the later ones.
    dict(file="src/ManagedCodecs.Containers/Matroska/MatroskaDemuxer.cs",
         find="long share = (duration * (f + 1) / frames) - (duration * f / frames);", replace="long share = duration / frames;",
         tests="FullyQualifiedName~Matroska"),
    # A lace's duration is the DefaultDuration times its frames, in nanoseconds before the division into ticks.
    dict(file="src/ManagedCodecs.Containers/Matroska/MatroskaDemuxer.cs",
         find=": track.DefaultDuration > 0 ? checked(track.DefaultDuration * frames) / TimestampScale",
         replace=": track.DefaultDuration > 0 ? checked(track.DefaultDuration / TimestampScale * frames)",
         tests="FullyQualifiedName~Matroska"),
    # An unknown-size Cluster ends at the next top-level element.
    dict(file="src/ManagedCodecs.Containers/Matroska/MatroskaDemuxer.cs",
         find="if (unknownSize && Ebml.IsTopLevel(id))", replace="if (false)",
         tests="FullyQualifiedName~Matroska"),
    # A BlockGroup with a ReferenceBlock is no keyframe.
    dict(file="src/ManagedCodecs.Containers/Matroska/MatroskaDemuxer.cs",
         find="AddBlock(blockStart, blockSize, clusterTime, keyframe: !referenced, duration, discardPadding);",
         replace="AddBlock(blockStart, blockSize, clusterTime, keyframe: true, duration, discardPadding);",
         tests="FullyQualifiedName~Matroska"),
    # Without DisplayWidth and DisplayHeight the ratio is 1:1 (they default to the pixel size), as FFmpeg reads it.
    dict(file="src/ManagedCodecs.Containers/Matroska/MatroskaDemuxer.cs",
         find="long displayWidth = track.DisplayWidth > 0 ? track.DisplayWidth : track.Width;", replace="long displayWidth = track.DisplayWidth;",
         tests="FullyQualifiedName~Matroska"),
    # Seeking starts at the last keyframe whose pts is at or before the time.
    dict(file="src/ManagedCodecs.Containers/Matroska/MatroskaDemuxer.cs",
         find="if (entry.Slot == slot && entry.Keyframe && entry.Pts <= pts)", replace="if (entry.Slot == slot && entry.Keyframe && entry.Pts < pts)",
         tests="FullyQualifiedName~SeekTests"),

    # DiscardPadding: positive at the end of the block's last frame, negative at the start of its first ...
    dict(file="src/ManagedCodecs.Containers/Matroska/MatroskaDemuxer.cs",
         find="int skipEnd = f == frames - 1 && padding > 0 ? padding : 0;", replace="int skipEnd = 0;",
         tests="FullyQualifiedName~Matroska|DisplayName~mkv-aac-padding"),
    # ... in samples rounded to nearest, as FFmpeg's av_rescale_q.
    dict(file="src/ManagedCodecs.Containers/Matroska/MatroskaDemuxer.cs",
         find="long samples = ((magnitude * sampleRate) + 500_000_000) / 1_000_000_000;", replace="long samples = magnitude * sampleRate / 1_000_000_000;",
         tests="FullyQualifiedName~Matroska"),
    # The AAC decoder drops the end's samples.
    dict(file="src/ManagedCodecs.Aac/AacDecoder.cs",
         find="var output = frame.Drop(dropped, packet.Discard ? 0 : packet.SkipEndSamples);", replace="var output = frame.Drop(dropped, 0);",
         tests="DisplayName~mkv-aac-padding"),
    # A zlib frame's Adler-32 is checked (a frame cut short inflates without an error otherwise).
    dict(file="src/ManagedCodecs.Containers/Matroska/MatroskaDemuxer.cs",
         find="if (zlib.Length < 6 || BinaryPrimitives.ReadUInt32BigEndian(zlib.AsSpan(zlib.Length - 4)) != ((b << 16) | a))", replace="if (zlib.Length < 6)",
         tests="FullyQualifiedName~MatroskaDemuxerTests"),
    # The Projection's roll is the rotation, -90 as 270.
    dict(file="src/ManagedCodecs.Containers/Matroska/MatroskaDemuxer.cs",
         find="return (((int)roll % 360) + 360) % 360;", replace="return (int)roll;",
         tests="FullyQualifiedName~Matroska|DisplayName~mkv-h264-rotate"),

    # A stream's duration as FFmpeg gives it (VideoProbe's against ffprobe's): an edit lasts no longer than the samples it
    # takes (JM's field-picture files) ...
    dict(file="src/ManagedCodecs.Containers/Mp4/Mp4Demuxer.cs",
         find="return Math.Min(edited, total);", replace="return edited;",
         tests="FullyQualifiedName~VideoProbeTests"),
    # ... and a fragmented file's samples outlast its 'mdhd'.
    dict(file="src/ManagedCodecs.Containers/Mp4/Mp4Demuxer.cs",
         find="return Math.Max(MediaDuration, total);", replace="return MediaDuration;",
         tests="FullyQualifiedName~VideoProbeTests"),
    # ... and without an edit list, the shorter of 'mdhd' and the samples (mp4-mdhd-duration: the video's 'mdhd', the
    # audio's samples).
    dict(file="src/ManagedCodecs.Containers/Mp4/Mp4Demuxer.cs",
         find="? Math.Min(MediaDuration, total) :", replace="? MediaDuration :",
         tests="FullyQualifiedName~VideoProbeTests"),
    dict(file="src/ManagedCodecs.Containers/Mp4/Mp4Demuxer.cs",
         find="? Math.Min(MediaDuration, total) :", replace="? total :",
         tests="FullyQualifiedName~VideoProbeTests"),
    # ... and a Matroska file's is its Segment's Duration from the earliest packet, not each stream's start plus it.
    dict(file="src/ManagedCodecs.Thumbnails/VideoProbe.cs",
         find="bool segmentDuration = demuxer is MatroskaDemuxer;", replace="bool segmentDuration = false;",
         tests="FullyQualifiedName~VideoProbeTests"),
    # ... and a stream starts past the samples its first packet skips (h264-mp3*: MP3's encoder delay, 1105 samples into
    # a packet the edit list starts inside).
    dict(file="src/ManagedCodecs.Thumbnails/VideoProbe.cs",
         find="Start = Math.Min(s.Start, packet.Pts + skip)", replace="Start = Math.Min(s.Start, packet.Pts)",
         tests="FullyQualifiedName~VideoProbeTests"),
    # ... and in AVI a stream without a duration (PCM, by dwSampleSize) ends nothing: avi-mjpeg-pcm-u8's audio runs past
    # its video, and ffprobe's duration is the video's (the camera AVIs in realworld.py too).
    dict(file="src/ManagedCodecs.Thumbnails/VideoProbe.cs",
         find="bool durationsOnly = demuxer is AviDemuxer;", replace="bool durationsOnly = false;",
         tests="DisplayName~avi-mjpeg-pcm-u8"),
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
         find="var dg = new Matrix((hIm[bin] - g0.M11) / length,", replace="var dg = new Matrix(((sign * hIm[bin]) - g0.M11) / length,",
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
    # Damaged images as FFmpeg's decoder decodes them (src/ManagedCodecs.Mjpeg/CLAUDE.md, measured against it): a cut
    # scan stops after the MCU that read past the data, which is still decoded (mjpeg-cut*) ...
    dict(file="src/ManagedCodecs.Mjpeg/MjpegDecoder.cs",
         find="ClearScanFrom(frame, scan, mcusX, mcusY, mcu + 1, 0);", replace="ClearScanFrom(frame, scan, mcusX, mcusY, mcu, 0);",
         tests="DisplayName~mjpeg-cut"),
    # ... the frame marked damaged ...
    dict(file="src/ManagedCodecs.Mjpeg/MjpegDecoder.cs",
         find="frame.Damaged = true;\n        for (int mcu = firstMcu;", replace="for (int mcu = firstMcu;",
         tests="FullyQualifiedName~MjpegDecoderTests"),
    # ... a code that is not valid stops it too, the frame given, not refused ...
    dict(file="src/ManagedCodecs.Mjpeg/MjpegDecoder.cs",
         find="ClearScanFrom(frame, scan, mcusX, mcusY, mcu, blockInMcu);\n                return;", replace="throw;",
         tests="FullyQualifiedName~MjpegDecoderTests"),
    # ... the 0xFF of the marker after the data is read as data (mjpeg-cut-eoi), and with a fill byte before the
    # marker, the marker's code after it (mjpeg-cut-ff-eoi) ...
    dict(file="src/ManagedCodecs.Mjpeg/JpegBitReader.cs",
         find="        if (marker.IsEmpty)\n        {\n            return [];", replace="        if (marker.Length >= 0)\n        {\n            return [];",
         tests="DisplayName~mjpeg-cut-eoi"),
    dict(file="src/ManagedCodecs.Mjpeg/JpegBitReader.cs",
         find="return fill >= 2 && fill < marker.Length ? marker.Slice(fill - 1, 2) : marker[..1];", replace="return marker[..1];",
         tests="DisplayName~mjpeg-cut-ff-eoi"),
    # ... a lone 0xFF ending the packet is not data but fill (mjpeg-cut-stuffed) ...
    dict(file="src/ManagedCodecs.Mjpeg/JpegBitReader.cs",
         find="                        b = 0;\n                        _dataBits -= 8;\n", replace="",
         tests="DisplayName~mjpeg-cut-stuffed"),
    # ... in a DC table, a symbol with a run's high bits whose code and bits fit in a combined entry is still an invalid
    # size (no encoder writes one: HuffmanTableTests' random tables) ...
    dict(file="src/ManagedCodecs.Mjpeg/HuffmanTable.cs",
         find="consumed == 0 ? DecodeLong(buffer) : 0xFF;", replace="consumed == 0 ? DecodeLong(buffer) : 0x0B;",
         tests="FullyQualifiedName~HuffmanTableTests"),
    # ... bytes before the SOI marker are skipped, a missing EOI emulated (mjpeg-junk) ...
    dict(file="src/ManagedCodecs.Mjpeg/JpegMarkerReader.cs",
         find="int soi = data.IndexOf([(byte)0xFF, JpegMarker.Soi]);", replace="int soi = data.StartsWith([(byte)0xFF, JpegMarker.Soi]) ? 0 : -1;",
         tests="DisplayName~mjpeg-junk"),
    dict(file="src/ManagedCodecs.Mjpeg/JpegMarkerReader.cs",
         find="segments.Add(new(JpegMarker.Eoi, data.Length, 0));", replace="if (data.Length >= 0) { throw new InvalidDataException(\"Missing EOI marker.\"); }",
         tests="DisplayName~mjpeg-junk"),
    # ... and a packet of less than a quarter byte an 8x8 block is refused (no stream reaches it whole).
    dict(file="src/ManagedCodecs.Mjpeg/MjpegDecoder.cs",
         find="if ((long)packetLength * 4 < blocks)", replace="if ((long)packetLength * 4 < 0)",
         tests="FullyQualifiedName~MjpegDecoderTests"),
    # A component no scan reached is 0, not the last frame's samples (no sample is non-interleaved).
    dict(file="src/ManagedCodecs.Mjpeg/MjpegDecoder.cs",
         find="                frame.Buffers[i].AsSpan().Clear();\n", replace="",
         tests="FullyQualifiedName~MjpegDecoderTests"),
    # ---- Thumbnails: the RGB conversion's defaults, FFmpeg's scaler's rather than the spec's (which leaves them open):
    # a matrix not said (or one the scaler doesn't know) is BT.601, a range not said limited, except MJPEG's, full as
    # FFmpeg converts its grey frames (gray.avi).
    dict(file="src/ManagedCodecs.Thumbnails/RgbaImage.cs",
         find="        _ => (0.299, 0.114),\n    };", replace="        _ => (0.2126, 0.0722),\n    };",
         tests="FullyQualifiedName~RgbaImageTests"),
    dict(file="src/ManagedCodecs.Thumbnails/RgbaImage.cs",
         find="bool full = saidRange == ColorRange.Full ||", replace="bool full = saidRange != ColorRange.Limited ||",
         tests="FullyQualifiedName~RgbaImageTests"),
    dict(file="src/ManagedCodecs.Thumbnails/RgbaImage.cs",
         find=" || (saidRange == ColorRange.Unspecified && codec == CodecIds.Mjpeg)", replace="",
         tests="DisplayName~gray.avi"),
    # The identity matrix (H.273's 0): G, B and R planes, as FFmpeg's gbrp (h264-444-gbr, libx264rgb's).
    dict(file="src/ManagedCodecs.Thumbnails/RgbaImage.cs",
         find="bool identity = said == ColorMatrix.Identity &&", replace="bool identity = false &&",
         tests="DisplayName~h264-444-gbr"),
    # A JPEG's header is read before it is decoded, so a large image inside a small AVI is refused before its planes are
    # made (VideoThumbnail; the frame's own size is checked only after decoding).
    dict(file="src/ManagedCodecs.Thumbnails/VideoThumbnail.cs",
         find='CheckSize(image.Width, image.Height, _options, "A JPEG image in the video declares");', replace="",
         tests="FullyQualifiedName~VideoThumbnailTests"),
    # VideoThumbnail takes ThumbnailOptions.At in the stream's time base to the nearest unit, as FFmpeg rescales -ss
    # (testsrc-420's 1/25: 610 ms is the frame at 600; realworld.py's Sintel trailer, 1/24).
    dict(file="src/ManagedCodecs.Thumbnails/VideoThumbnail.cs",
         find="_target = (long)Math.Round(seconds * video.TimeBase.Denominator / video.TimeBase.Numerator, MidpointRounding.AwayFromZero);",
         replace="_target = (long)Math.Ceiling(seconds * video.TimeBase.Denominator / video.TimeBase.Numerator);",
         tests="DisplayName~testsrc-420.at610"),
    # MediaPlayer.TakeFrame takes a frame as due from half the interval before its time: a game ticking at the video's
    # rate sums a clock that falls a rounding error short of some frames, which a due-at-its-exact-time rule shows late
    # and then drops (the Godot sample's first run, issue #352).
    dict(file="src/ManagedCodecs.Playback/MediaPlayer.cs",
         find="if (_next.Time - _next.Lead > position)", replace="if (_next.Time > position)",
         tests="DisplayName~ClockAtTheFrameRate"),
    # ---- LivePlayer (docs/decisions/2026-11-10-live-mjpeg-over-http.md): the newest frame wins, both while waiting to be
    # decoded and while waiting to be taken ...
    dict(file="src/ManagedCodecs.Playback/LivePlayer.cs",
         find="if (Interlocked.Exchange(ref _pending, received) is not null)",
         replace="if (Interlocked.CompareExchange(ref _pending, received, null) is not null)",
         tests="FullyQualifiedName~LivePlayerTests.ShowsTheNewestFrame"),
    dict(file="src/ManagedCodecs.Playback/LivePlayer.cs",
         find="if (Interlocked.Exchange(ref _latest, rgba) is { } old)",
         replace="if (Interlocked.CompareExchange(ref _latest, rgba, null) is { } old)",
         tests="FullyQualifiedName~LivePlayerTests.DropsFramesNotTakenBeforeANewerOne"),
    # ... 401, 403 and 404 are final, not retried ...
    dict(file="src/ManagedCodecs.Playback/LivePlayer.cs",
         find="if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)",
         replace="if (response.StatusCode is HttpStatusCode.Unauthorized)",
         tests="FullyQualifiedName~LivePlayerTests.GivesUpOnAnswersARetryWontChange"),
    # ... a stall counts from the last part, not from the connection ...
    dict(file="src/ManagedCodecs.Playback/LivePlayer.cs",
         find="                Touch();\n                Interlocked.Increment(ref _receivedCount);",
         replace="                Interlocked.Increment(ref _receivedCount);",
         tests="FullyQualifiedName~LivePlayerTests.ConnectsAgainWhenTheStreamStalls"),
    # ... the wait between connections doubles up to 10 s, and starts again at 0.5 s once a connection played a frame.
    dict(file="src/ManagedCodecs.Playback/LivePlayer.cs",
         find="backoff = backoff + backoff < MaxBackoff ? backoff + backoff : MaxBackoff;",
         replace="backoff = backoff + backoff;",
         tests="FullyQualifiedName~LivePlayerTests.WaitsLongerAfterEachRefusal"),
    dict(file="src/ManagedCodecs.Playback/LivePlayer.cs",
         find="                backoff = FirstBackoff;\n            }",
         replace="            }",
         tests="FullyQualifiedName~LivePlayerTests.StartsTheWaitAgainOnceAFramePlays"),
    # ... and a body sent as application/octet-stream is read as multipart, as FFmpeg's HTTP server sends -f mpjpeg.
    dict(file="src/ManagedCodecs.Playback/LivePlayer.cs",
         find="                && !media.Equals(\"application/octet-stream\", StringComparison.OrdinalIgnoreCase))",
         replace="                && true)",
         tests="DisplayName~application/octet-stream"),
    # ---- RTP H.264 (RtpH264Depacketizer, docs/decisions/2026-11-10-live-rtsp-h264.md): what FFmpeg's packetizer never
    # sends. A new timestamp ends a unit whose marker packet was lost ...
    dict(file="src/ManagedCodecs.Playback/RtpH264Depacketizer.cs",
         find="if (_inUnit && header.Timestamp != _timestamp)", replace="if (false)",
         tests="FullyQualifiedName~RtpH264DepacketizerTests.EndsAUnitAtANewTimestampWhenItsMarkerWasLost"),
    # ... a gap damages the next unit too (it may have held its start) ...
    dict(file="src/ManagedCodecs.Playback/RtpH264Depacketizer.cs",
         find="            _damaged = _gapBeforeNext;\n", replace="            _damaged = false;\n",
         tests="FullyQualifiedName~RtpH264DepacketizerTests.AGapBetweenUnitsDamagesOnlyTheNextOne"),
    # ... fragments whose start was lost are dropped, not glued to nothing ...
    dict(file="src/ManagedCodecs.Playback/RtpH264Depacketizer.cs",
         find="            _damaged = true; // its start was lost: the rest of it is no use\n            return;\n",
         replace="            _damaged = true; // its start was lost: the rest of it is no use\n",
         tests="FullyQualifiedName~RtpH264DepacketizerTests.IgnoresFragmentsWhoseStartWasLost"),
    # ... and a packet from before the expected one (repeated) is skipped, the sequence number compared modulo 2^16.
    dict(file="src/ManagedCodecs.Playback/RtpH264Depacketizer.cs",
         find="if (gap >= 0x8000)", replace="if (gap < 0)",
         tests="FullyQualifiedName~RtpH264DepacketizerTests.IgnoresARepeatedPacket"),
    # ---- LivePlayer over RTSP (docs/decisions/2026-11-10-live-rtsp-h264.md): past MaxLag, skip to the next IDR picture ...
    dict(file="src/ManagedCodecs.Playback/LivePlayer.cs",
         find="else if (waitForIdr || _clock.Elapsed - _origin - queued.Time > maxLag)", replace="else if (waitForIdr)",
         tests="FullyQualifiedName~LiveRtspTests.SkipsToTheNextIdrPictureWhenDecodingFallsBehind"),
    # ... pass over a unit whose slices were all lost, rather than feed the decoder what it refuses ...
    dict(file="src/ManagedCodecs.Playback/LivePlayer.cs",
         find="                    if (!unit.HasPicture)\n                    {\n                        continue;\n                    }\n",
         replace="",
         tests="FullyQualifiedName~LiveRtspTests.ConcealsLostPacketsAndCountsThem"),
    # ... 403, 404 and 454 are final as 401 is ...
    dict(file="src/ManagedCodecs.Playback/LivePlayer.cs",
         find="if (response.Status is 401 or 403 or 404 or 454)", replace="if (response.Status is 401)",
         tests="FullyQualifiedName~LiveRtspTests.GivesUpOnAnswersARetryWontChange"),
    # ... and the session is kept alive at half its timeout.
    dict(file="src/ManagedCodecs.Playback/LivePlayer.cs",
         find="TimeSpan.FromTicks(timeout.Ticks / 2)", replace="timeout",
         tests="FullyQualifiedName~LiveRtspTests.KeepsTheSessionAliveAtHalfItsTimeout"),
    # ---- JPEG over RTP (RFC 2435; docs/decisions/2026-11-12-live-rtsp-jpeg.md), rules neither FFmpeg's nor GStreamer's
    # packetizer reaches (both send Q 255 with the tables in every frame): Q below 128 scales K.1's tables by
    # Appendix A ...
    dict(file="src/ManagedCodecs.Playback/RtpJpegDepacketizer.cs",
         find="int scale = factor < 50 ? 5000 / factor : 200 - (factor * 2);", replace="int scale = 100;",
         tests="FullyQualifiedName~RtpJpegDepacketizerTests.ScalesK1sTablesAsAppendixADoes"),
    # ... tables sent once for a Q from 128 to 254 serve its later frames, which send a length of 0 ...
    dict(file="src/ManagedCodecs.Playback/RtpJpegDepacketizer.cs",
         find="                    if (q < 255)\n", replace="                    if (false)\n",
         tests="FullyQualifiedName~RtpJpegDepacketizerTests.ReusesTablesSentOnceForTheSameQ"),
    # ... and a fragment that doesn't continue the scan (one before it was lost) drops the frame. FFmpeg's one table
    # for both components (a length of 64) its recording does reach.
    dict(file="src/ManagedCodecs.Playback/RtpJpegDepacketizer.cs",
         find="if (_damaged || offset != _scan.Length || ", replace="if (_damaged || ",
         tests="FullyQualifiedName~RtpJpegDepacketizerTests.DropsAFrameMissingAPacketAndKeepsTheOthers"),
    # MJPEG over HTTP: the first delimiter line names the boundary, whatever the header said (cameras get it wrong).
    dict(file="src/ManagedCodecs.Playback/MultipartReader.cs",
         find="            if (_first)\n", replace="            if (_boundary is null)\n",
         tests="FullyQualifiedName~MultipartReaderTests.TakesTheBoundaryFromTheFirstDelimiter"),
    # ---- QuickTime PCM with one tick a sample: each chunk in pieces of up to 1024 frames, as FFmpeg's mov demuxer cuts
    # them (FFmpeg's muxer writes chunks of 1024, so only a hand-built file shows it).
    dict(file="src/ManagedCodecs.Containers/Mp4/Mp4Demuxer.cs",
         find="int piece = (int)Math.Min(1024, left);", replace="int piece = (int)left;",
         tests="FullyQualifiedName~CutsQuickTimePcmChunksAsFfmpeg"),
    # ---- .wav files: packets of 4096 bytes rounded down to whole blocks, as FFmpeg's wav demuxer reads them ...
    dict(file="src/ManagedCodecs.Containers/Wav/WavDemuxer.cs",
         find="int size = block <= PacketBytes ? PacketBytes / block * block : block;", replace="int size = PacketBytes;",
         tests="FullyQualifiedName~WavDemuxerTests"),
    # ... and a 'data' chunk sized 0 runs to the end of the file (checked with ffprobe on a hand-built file).
    dict(file="src/ManagedCodecs.Containers/Wav/WavDemuxer.cs",
         find="long end = size == 0 || start + size", replace="long end = start + size",
         tests="FullyQualifiedName~SkipsOtherChunksAndBoundsTheData"),
    # ... but ISO's 'ipcm' and 'fpcm' in pieces of 'stsz''s samples, as FFmpeg sizes them (mp4-pcm-s24le, whose 'pcmC'
    # says 32 bits over 3-byte samples).
    dict(file="src/ManagedCodecs.Containers/Mp4/Mp4Demuxer.cs",
         find="int frameBytes = _isoPcm && _stsz.Length >= 12", replace="int frameBytes = false && _stsz.Length >= 12",
         tests="DisplayName~mp4-pcm-s24le"),
    # ---- Raw AAC and MP3 seek (docs/decisions/2026-11-11-audio-player.md): a read starts at the frame a Seek found.
    dict(file="src/ManagedCodecs.Containers/Adts/AdtsDemuxer.cs",
         find="if (i >= _start)", replace="if (true)",
         tests="FullyQualifiedName~SeekTests"),
    dict(file="src/ManagedCodecs.Containers/Mp3/Mp3Demuxer.cs",
         find="if (sequence < _start)", replace="if (false)",
         tests="FullyQualifiedName~SeekTests"),
    # ---- PCM: bytes after a packet's last whole sample frame are ignored, as FFmpeg's decoder ignores them (no spec).
    dict(file="src/ManagedCodecs.Core/PcmDecoder.cs",
         find="if (packet.Length > 0 && packet.Length < frameBytes)", replace="if (packet.Length % frameBytes != 0)",
         tests="FullyQualifiedName~PcmDecoderTests"),
    # ---- IMA ADPCM: the difference rounded once, as FFmpeg's decoder does, not by IMA's recommended practice's shifts
    # (up to 2 lower; docs/decisions/2026-11-10-ima-adpcm.md) ...
    dict(file="src/ManagedCodecs.Core/ImaAdpcmDecoder.cs",
         find="int difference = (((2 * (nibble & 7)) + 1) * step) >> 3;",
         replace="int difference = (step >> 3) + ((nibble & 4) != 0 ? step : 0) + ((nibble & 2) != 0 ? step >> 1 : 0) + ((nibble & 1) != 0 ? step >> 2 : 0);",
         tests="FullyQualifiedName~ImaAdpcmDecoderTests"),
    # ... and a block cut short gives its whole groups of 4 bytes a channel, the rest ignored, as FFmpeg's (no spec).
    dict(file="src/ManagedCodecs.Core/ImaAdpcmDecoder.cs",
         find="if (bytes < headers)", replace="if (bytes < headers || (bytes - headers) % headers != 0)",
         tests="FullyQualifiedName~ImaAdpcmDecoderTests"),
    # ---- Microsoft ADPCM: the step capped at int.MaxValue / 768, as FFmpeg caps it (the format has no cap; without one
    # a run of the largest codes overflows it; docs/decisions/2026-11-10-ms-adpcm.md).
    dict(file="src/ManagedCodecs.Core/MsAdpcmDecoder.cs",
         find="delta[c] = Math.Clamp((Adaptation[nibble] * delta[c]) >> 8, 16, MaxDelta);",
         replace="delta[c] = Math.Max((Adaptation[nibble] * delta[c]) >> 8, 16);",
         tests="FullyQualifiedName~MsAdpcmDecoderTests"),
    # ---- MP3: where the decoder does what FFmpeg's does and the spec leaves open, or what only some streams reach.
    # A frame whose main data starts before the reservoir does (a stream cut from a longer one): its granules are
    # skipped, silent, by their lengths until the data is there (mp3-cut).
    dict(file="src/ManagedCodecs.Mp3/Mp3Decoder.cs",
         find="while (firstGranule < header.Granules && bits < mainDataBegin * 8)", replace="while (false)",
         tests="DisplayName~mp3-cut"),
    # A count1 quadruple that reads past its granule's end is dropped (shine's streams).
    dict(file="src/ManagedCodecs.Mp3/Spectrum.cs",
         find="                    i -= 4;\n                    r.Position = lastPosition;\n", replace="",
         tests="DisplayName~mp3-shine"),
    # Region 1 of a short block's big values starts after three short bands: line 72 at 8 kHz, not 36.
    dict(file="src/ManagedCodecs.Mp3/Granule.cs",
         find="Region1Start = BlockType == 2 ? 3 * Mp3Tables.ShortBands[rateIndex][3] : longBands[8];",
         replace="Region1Start = BlockType == 2 ? 36 : longBands[8];",
         tests="DisplayName~mkv-mp3-8k-transient"),
    # A raw MP3 file's info frame (Xing, Info, VBRI) is no packet, as in FFmpeg's demuxer.
    dict(file="src/ManagedCodecs.Containers/Mp3/Mp3Demuxer.cs",
         find="                pos += first.FrameLength;\n", replace="",
         tests="FullyQualifiedName~Mp3DemuxerTests"),
    # Gapless as FFmpeg: the LAME tag's delay and padding each count the decoder's 529 samples too.
    dict(file="src/ManagedCodecs.Containers/Mp3/Mp3Demuxer.cs",
         find="_startSkip = delay + DecoderDelay;", replace="_startSkip = delay;",
         tests="DisplayName~mp3-raw-cbr"),
    dict(file="src/ManagedCodecs.Containers/Mp3/Mp3Demuxer.cs",
         find="- tag.Padding + DecoderDelay;", replace="- tag.Padding;",
         tests="DisplayName~mp3-raw-cbr"),
    # A first frame unlike the next one is no packet, by the mode, copyright, original and emphasis only.
    dict(file="src/ManagedCodecs.Containers/Mp3/Mp3Demuxer.cs",
         find="&& TryReadHeader(_stream, pos + frame.FrameLength, out var next) && !frame.SameStream(next))",
         replace="&& TryReadHeader(_stream, pos + frame.FrameLength, out var next) && false)",
         tests="FullyQualifiedName~Mp3DemuxerTests"),
    dict(file="src/ManagedCodecs.Containers/Mp3/MpegAudioHeader.cs",
         find="private const uint StreamMask = 0xFFFE0CCF;", replace="private const uint StreamMask = 0xFFFE0C0F;",
         tests="FullyQualifiedName~Mp3DemuxerTests"),
    dict(file="src/ManagedCodecs.Containers/Mp3/MpegAudioHeader.cs",
         find="private const uint StreamMask = 0xFFFE0CCF;", replace="private const uint StreamMask = 0xFFFE0FFF;",
         tests="FullyQualifiedName~Mp3DemuxerTests"),
    # A tag frame is no packet only when it counts the frames or the bytes (Xing and Info; VBRI).
    dict(file="src/ManagedCodecs.Containers/Mp3/Mp3Demuxer.cs",
         find="if ((frames ?? 0) == 0 && bytes == 0)", replace="if (false)",
         tests="FullyQualifiedName~Mp3DemuxerTests"),
    dict(file="src/ManagedCodecs.Containers/Mp3/Mp3Demuxer.cs",
         find="return vbriFrames != 0 || ReadUInt32(span[(36 + 10)..]) != 0 ? (vbriFrames, null, 0) : null;",
         replace="return (vbriFrames, null, 0);",
         tests="FullyQualifiedName~Mp3DemuxerTests"),
    # A packet the padding covers whole skips all its samples, not a discarded packet.
    dict(file="src/ManagedCodecs.Containers/Mp3/Mp3Demuxer.cs",
         find="SkipEndSamples = (int)cut,", replace="SkipEndSamples = cut == _samplesPerFrame ? 0 : (int)cut,",
         tests="FullyQualifiedName~Mp3DemuxerTests"),
    # MP4: the last audio packet runs to the edit's end, but not past the media's end compared with its pts as it is
    # (an edit from a later media time, mp3-raw-22k-remux, runs past the last sample; aac-fdk-pce-remux, from 0, doesn't).
    dict(file="src/ManagedCodecs.Containers/Mp4/Mp4Demuxer.cs",
         find="until = Math.Min(until, media.Dts[^1] + media.Durations[^1] - samples.Pts(i));", replace="until = Math.Min(until, samples.Durations[i]);",
         tests="DisplayName~mp3-raw-22k-remux|DisplayName~aac-fdk-pce-remux"),
    dict(file="src/ManagedCodecs.Containers/Mp4/Mp4Demuxer.cs",
         find="until = Math.Min(until, media.Dts[^1] + media.Durations[^1] - samples.Pts(i));", replace="",
         tests="DisplayName~mp3-raw-22k-remux|DisplayName~aac-fdk-pce-remux"),
    # mixed_block_flag gives a start or stop block's lowest subbands the normal window (l3-si_block; mkv-mp3-mixed).
    dict(file="src/ManagedCodecs.Mp3/Hybrid.cs",
         find="LongWindows[g.ShortBlocks || sb < mixedSubbands ? 0 : g.BlockType]", replace="LongWindows[g.ShortBlocks ? 0 : g.BlockType]",
         tests="DisplayName~mkv-mp3-mixed"),
    # Intensity stereo's last band at the lower rates is the centre after a band the right channel codes a value in
    # (l3-test45, l3-test46), long and short; no sample reaches it (FFmpeg misses the ISO reference there).
    dict(file="src/ManagedCodecs.Mp3/Stereo.cs",
         find="bool centre = lsf && band == 21 &&", replace="bool centre = false &&",
         tests="DisplayName~LastBandIsTheCentreAfterACodedBand"),
    dict(file="src/ManagedCodecs.Mp3/Stereo.cs",
         find="bool centre = lsf && band == 12 &&", replace="bool centre = false &&",
         tests="DisplayName~LastBandIsTheCentreAfterACodedBand"),
    # ... and MPEG-1's copies band 20's position, as FFmpeg's does (mkv-mp3-intensity: the centre there is 1.6e-2 off).
    dict(file="src/ManagedCodecs.Mp3/Stereo.cs",
         find="bool centre = lsf && band == 21 &&", replace="bool centre = band == 21 &&",
         tests="DisplayName~mkv-mp3-intensity"),
    # A frame cut short decodes as far as its granules' data is there, as FFmpeg's (l3-compl, l3-sin1k0db).
    dict(file="src/ManagedCodecs.Mp3/Mp3Decoder.cs",
         find="                length = packet.Length - offset;\n                Statistics.TruncatedFrames++;\n",
         replace="                throw new InvalidDataException(\"An MP3 frame is cut short.\");\n",
         tests="DisplayName~FrameCutShortDecodesAsFarAsItsData"),
    # AVI's CBR MP3 is re-split into frames, skipping bytes that start none, as FFmpeg's parser resynchronizes
    # (CbrMp3_SkipsBytesThatStartNoFrame puts junk before a chunk's data).
    dict(file="src/ManagedCodecs.Containers/Avi/AviDemuxer.cs",
         find="                at++;\n                continue;\n", replace="                break;\n",
         tests="DisplayName~CbrMp3_SkipsBytesThatStartNoFrame"),
    # An Annex B stream's (AVI's) frames take its decoding-order times in increasing order, as FFmpeg's do; without it a
    # stream with B pictures comes out at its pictures' decoding times (avi-h264).
    dict(file="src/ManagedCodecs.H264/H264Decoder.cs",
         find="        if (_lengthSize != 0)\n        {\n            return shown;\n        }\n",
         replace="        if (_lengthSize != 0 || shown.Count >= 0)\n        {\n            return shown;\n        }\n",
         tests="DisplayName~avi-h264"),
    # An Annex B stream's k-th frame takes the time of the packet the reorder depth later, as FFmpeg's (frames from time 2
    # in avi-h264, whose B pictures delay two): without it a seek's frame at a time is another than FFmpeg's.
    dict(file="src/ManagedCodecs.H264/H264Decoder.cs",
         find="            int at = _timesDepth;\n", replace="            int at = 0;\n",
         tests="DisplayName~avi-h264.seek"),
    # Seeking indexed AVI video (H.264) starts the largest reorder delay before the time: its packets' times are decoding
    # times, and FFmpeg shows the frame at a time up to that many frames before the keyframe at or before it.
    dict(file="src/ManagedCodecs.Containers/Avi/AviDemuxer.cs",
         find="            videoPts -= MaxReorderFrames;\n", replace="",
         tests="DisplayName~avi-h264.seek"),
]
