namespace ManagedCodecs.H264;

/// <summary>
/// H.264 decoder for Constrained Baseline, Main and High profile streams (CAVLC or CABAC; I, P and B
/// slices; 4:2:0, 8-bit, progressive), taking MP4-style samples: length-prefixed NAL units, one
/// picture per sample.
/// </summary>
/// <remarks>
/// Implemented from ITU-T H.264; output is bit-exact with any conforming decoder. Streams that
/// use other features (interlacing, FMO, other chroma formats
/// or bit depths) are rejected with
/// <see cref="NotSupportedException"/>. Frames come out in display order (picture order
/// count, 8.2.1), held back as far as the stream's max_num_reorder_frames says reordering can
/// reach; <see cref="Flush"/> returns the rest at the end.
/// </remarks>
public sealed class H264Decoder : IVideoDecoder
{
    private readonly Dictionary<int, SequenceParameterSet> _sps = [];
    private readonly Dictionary<int, PictureParameterSet> _pps = [];
    private readonly ReferenceList _references = new();
    private readonly int _lengthSize;

    // Pictures are reused once they are no longer references (output is a copy, see Crop), and so
    // is the per-macroblock state, as long as the picture size stays the same.
    private readonly List<Picture> _pictures = [];
    private PictureDecoder? _decoder;
    private long _pictureCount;

    // frame_num of the last reference picture (PrevRefFrameNum, 7.4.3); -1 before the first.
    private int _prevRefFrameNum = -1;

    private readonly PictureOrder _order = new();
    private readonly OutputQueue _output = new();

    /// <param name="codecPrivate">The AVCDecoderConfigurationRecord ('avcC') from the container.</param>
    public H264Decoder(ReadOnlySpan<byte> codecPrivate)
    {
        var config = AvcConfiguration.Parse(codecPrivate);
        _lengthSize = config.LengthSize;
        foreach (var sps in config.Sps)
        {
            HandleNal(NalUnit.Parse(sps), current: null);
        }

        foreach (var pps in config.Pps)
        {
            HandleNal(NalUnit.Parse(pps), current: null);
        }
    }

    public H264Decoder(VideoStreamInfo stream)
        : this(stream.CodecPrivate.Span)
    {
    }

    /// <summary>Decodes one packet, which holds one picture (MP4 sample), and returns the frames ready for display.</summary>
    public IReadOnlyList<VideoFrame> Decode(Packet packet)
    {
        var (frame, state) = DecodePicture(packet.Data.Span);
        frame.Pts = packet.Pts;

        bool restart = state.Header.IsIdr || state.Header.MemoryManagement.Any(op => op.Op == 5);
        return _output.Add(frame, state.Poc, restart, ReorderDepth(state.Sps));
    }

    public IReadOnlyList<VideoFrame> Flush() => _output.Flush();

    /// <summary>
    /// How many decoded frames can precede, in decoding order, a frame shown before them: the
    /// VUI's max_num_reorder_frames, else none for order count type 2 (which follows decoding
    /// order), else the whole DPB (A.3.1, the bound with no other information).
    /// </summary>
    private static int ReorderDepth(SequenceParameterSet sps)
    {
        if (sps.MaxNumReorderFrames is int reorder)
        {
            return reorder;
        }

        if (sps.PicOrderCntType == 2)
        {
            return 0;
        }

        if (sps.MaxDecFrameBuffering is int buffering)
        {
            return buffering;
        }

        // MaxDpbMbs of Table A-1 by level_idc; level 1b counts as 1.1, the larger.
        int maxDpbMbs = sps.LevelIdc switch
        {
            <= 10 => 396,
            11 => 900,
            <= 20 => 2376,
            21 => 4752,
            <= 30 => 8100,
            31 => 18000,
            32 => 20480,
            <= 41 => 32768,
            42 => 34816,
            50 => 110400,
            <= 52 => 184320,
            _ => 696320,
        };
        return Math.Clamp(maxDpbMbs / (sps.WidthInMbs * sps.HeightInMbs), 1, 16);
    }

    private (VideoFrame Frame, PictureState State) DecodePicture(ReadOnlySpan<byte> packet)
    {
        PictureState? current = null;
        int pos = 0;
        while (pos < packet.Length)
        {
            if (pos + _lengthSize > packet.Length)
            {
                throw new InvalidDataException("Truncated NAL length prefix.");
            }

            int length = 0;
            for (int i = 0; i < _lengthSize; i++)
            {
                length = (length << 8) | packet[pos + i];
            }

            pos += _lengthSize;
            if (length <= 0 || pos + length > packet.Length)
            {
                throw new InvalidDataException($"NAL unit length {length} overruns the packet.");
            }

            current = HandleNal(NalUnit.Parse(packet.Slice(pos, length)), current);
            pos += length;
        }

        if (current is null)
        {
            throw new InvalidDataException("The packet contains no picture.");
        }

        return (FinishPicture(current), current);
    }

    private PictureState? HandleNal(NalUnit nal, PictureState? current)
    {
        switch (nal.Type)
        {
            case NalUnitType.Sps:
                {
                    var sps = SequenceParameterSet.Parse(nal.CreateReader());
                    _sps[sps.Id] = sps;
                    break;
                }

            case NalUnitType.Pps:
                {
                    var pps = PictureParameterSet.Parse(nal.CreateReader());
                    _pps[pps.Id] = pps;
                    break;
                }

            case NalUnitType.Slice or NalUnitType.IdrSlice:
                {
                    var reader = nal.CreateReader();
                    var header = SliceHeader.Parse(reader, nal, _sps, _pps, out var sps, out var pps);
                    CheckSupported(sps);

                    if (current is null)
                    {
                        current = StartPicture(header, sps);
                    }
                    else if (header.FrameNum != current.Header.FrameNum || current.Sps != sps)
                    {
                        throw new NotSupportedException("A packet holding more than one picture is not supported.");
                    }

                    var (list0, list1) = header.Type switch
                    {
                        SliceType.P => (_references.BuildList(header, current.Sps), []),
                        SliceType.B => _references.BuildLists(header, current.Sps, current.Poc),
                        _ => (new List<Picture>(), new List<Picture>()),
                    };
                    current.Decoder.DecodeSlice(header, pps, reader, list0, list1);
                    break;
                }

            case >= NalUnitType.SliceDataPartitionA and <= (NalUnitType)4:
                throw new NotSupportedException("Data partitioning is not supported.");

            default:
                break; // SEI, access unit delimiters, filler data and the like carry no picture data
        }

        return current;
    }

    /// <summary>How often MBAFF frames used field and frame pairs and mixed them, so tests can check samples exercise them.</summary>
    internal MbaffStatistics MbaffStatistics { get; } = new();

    private PictureState StartPicture(SliceHeader header, SequenceParameterSet sps)
    {
        if (header.IsIdr)
        {
            _references.Clear();
        }
        else
        {
            InferGapFrames(header, sps);
        }

        if (_decoder is null || _decoder.Sps.WidthInMbs != sps.WidthInMbs || _decoder.Sps.HeightInMbs != sps.HeightInMbs)
        {
            _decoder = new PictureDecoder(sps) { Statistics = MbaffStatistics };
        }

        int poc = _order.Next(header, sps);
        _decoder.Begin(TakePicture(sps, header.FrameNum, poc), sps);
        return new PictureState(header, sps, _decoder, poc);
    }

    /// <summary>
    /// Decoding process for gaps in frame_num (8.2.5.2): a frame_num more than one past the
    /// previous reference picture's means reference frames are missing (dropped temporal layers,
    /// or lost). Each missing frame_num gets a "non-existing" frame, marked as a short-term
    /// reference by the sliding window, so that later list positions and the window come out as
    /// the encoder had them.
    /// </summary>
    private void InferGapFrames(SliceHeader header, SequenceParameterSet sps)
    {
        int maxFrameNum = 1 << sps.Log2MaxFrameNum;
        if (_prevRefFrameNum < 0 || header.FrameNum == _prevRefFrameNum || header.FrameNum == (_prevRefFrameNum + 1) % maxFrameNum)
        {
            return;
        }

        for (int frameNum = (_prevRefFrameNum + 1) % maxFrameNum; frameNum != header.FrameNum; frameNum = (frameNum + 1) % maxFrameNum)
        {
            var picture = TakePicture(sps, frameNum, _order.NextInferred(frameNum, sps));
            picture.IsNonExisting = true;
            _references.MarkNonExisting(picture, sps);
        }

        _prevRefFrameNum = (header.FrameNum + maxFrameNum - 1) % maxFrameNum;
    }

    /// <summary>A picture of the stream's size that is not a reference, reused when one is free.</summary>
    private Picture TakePicture(SequenceParameterSet sps, int frameNum, int poc)
    {
        _pictures.RemoveAll(p => (p.WidthInMbs != sps.WidthInMbs || p.HeightInMbs != sps.HeightInMbs) && !_references.Contains(p));
        var picture = _pictures.Find(p => p.WidthInMbs == sps.WidthInMbs && p.HeightInMbs == sps.HeightInMbs && !_references.Contains(p));
        if (picture is null)
        {
            picture = new Picture(sps.WidthInMbs, sps.HeightInMbs);
            _pictures.Add(picture);
        }

        // Every macroblock of a complete picture is written, so the old samples need no clearing.
        picture.Id = ++_pictureCount;
        picture.Poc = poc;
        picture.FrameNum = frameNum;
        picture.FrameNumWrap = 0;
        picture.IsShortTermReference = false;
        picture.IsLongTermReference = false;
        picture.LongTermFrameIdx = 0;
        picture.IsNonExisting = false;
        return picture;
    }

    private VideoFrame FinishPicture(PictureState state)
    {
        if (!state.Decoder.IsComplete)
        {
            throw new InvalidDataException("The picture is missing macroblocks (error concealment is not supported).");
        }

        state.Decoder.Deblock();

        var picture = state.Decoder.Picture;
        if (state.Header.NalRefIdc != 0)
        {
            _references.Mark(state.Header, picture, state.Sps);

            // PrevRefFrameNum (7.4.3); after memory_management_control_operation 5 it is 0.
            _prevRefFrameNum = state.Header.MemoryManagement.Any(op => op.Op == 5) ? 0 : state.Header.FrameNum;
        }

        return Crop(picture, state.Sps);
    }

    /// <summary>Copies the visible area (frame cropping, 7.4.2.1.1) into a new frame.</summary>
    private static VideoFrame Crop(Picture picture, SequenceParameterSet sps)
    {
        var frame = new VideoFrame(sps.Width, sps.Height, PixelFormat.Yuv420P);
        var (left, _, top, _) = sps.Crop;
        for (int plane = 0; plane < 3; plane++)
        {
            int shift = plane == 0 ? 0 : 1;
            int stride = plane == 0 ? picture.LumaStride : picture.ChromaStride;
            var (width, height) = PixelFormat.Yuv420P.PlaneSize(plane, sps.Width, sps.Height);
            byte[] source = picture.Plane(plane);
            for (int y = 0; y < height; y++)
            {
                source.AsSpan((((top >> shift) + y) * stride) + (left >> shift), width).CopyTo(frame.GetRow(plane, y));
            }
        }

        return frame;
    }

    /// <summary>The picture being decoded: its first slice header, its SPS, its decoder and its order count.</summary>
    private sealed record PictureState(SliceHeader Header, SequenceParameterSet Sps, PictureDecoder Decoder, int Poc);

    private static void CheckSupported(SequenceParameterSet sps)
    {
        if (sps.ChromaFormatIdc != 1)
        {
            throw new NotSupportedException($"chroma_format_idc {sps.ChromaFormatIdc} is not supported (4:2:0 only).");
        }

        if (sps.BitDepthLuma != 8 || sps.BitDepthChroma != 8)
        {
            throw new NotSupportedException("Only 8-bit video is supported.");
        }
    }
}
