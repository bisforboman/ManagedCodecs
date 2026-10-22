using System.Runtime.InteropServices;

namespace ManagedCodecs.H264;

/// <summary>
/// H.264 decoder for Constrained Baseline, Main, High, High 10 and High 4:2:2 profile streams (CAVLC
/// or CABAC; I, P and B slices; 8 or 10 bits; 4:2:0 or 4:2:2, progressive or interlaced (MBAFF
/// frames, field pictures)), taking MP4-style samples: length-prefixed NAL units, one picture per sample.
/// </summary>
/// <remarks>
/// Implemented from ITU-T H.264; output is bit-exact with any conforming decoder. Streams that
/// use other features (FMO, data partitioning, 4:4:4 or monochrome, bit depths other than 8 and
/// 10 or differing between luma and chroma) are rejected with <see cref="NotSupportedException"/>. Frames come out in display order (picture order
/// count, 8.2.1), held back as far as the stream's max_num_reorder_frames says reordering can
/// reach; <see cref="Flush"/> returns the rest at the end.
/// </remarks>
public sealed class H264Decoder : IVideoDecoder
{
    private readonly Dictionary<int, SequenceParameterSet> _sps = [];
    private readonly Dictionary<int, PictureParameterSet> _pps = [];
    private readonly ReferenceList _references = new();
    private readonly int _lengthSize;

    // Pictures are reused once they are no longer references and no frame waiting for output
    // borrows them (output is a copy, see Crop, unless BorrowFrames), and so is the per-macroblock
    // state, as long as the picture size stays the same.
    private readonly List<Picture> _pictures = [];

    // The planes of copied frames, back from frames the caller disposed.
    private readonly VideoFramePool _framePool = new();
    private IPictureDecoder? _decoder;
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

    /// <summary>
    /// Return frames that point into the decoder's own pictures instead of copies, which saves a
    /// copy of every frame (about 5% of the decoding time at 720p). A borrowed frame is valid only
    /// until the next call to <see cref="Decode"/> or <see cref="Flush"/>, which may decode a new
    /// picture into the same memory; copy what has to be kept longer. Its planes are the
    /// decoder's macroblock-aligned buffers (read rows through <see cref="VideoFrame.GetRow"/>, or
    /// with its offsets and strides), and they must not be written: later pictures predict from them.
    /// Frames of more than 8 bits are copies all the same (the decoder keeps their samples as 16-bit
    /// words, which a frame's byte planes cannot hold without one).
    /// </summary>
    public bool BorrowFrames { get; init; }

    /// <summary>
    /// Decodes one packet, which holds one picture (MP4 sample), and returns the frames ready for
    /// display. A field picture is half a frame: the first of a pair gives no frame, the second
    /// the whole frame, with the first one's timestamp.
    /// </summary>
    /// <remarks>
    /// Frames that are copies own their samples; disposing one when done with it gives its
    /// planes back to the decoder for a later frame, which saves allocating (and collecting)
    /// them: about 4 to 7% of the decoding time at 720p.
    /// </remarks>
    public IReadOnlyList<VideoFrame> Decode(Packet packet)
    {
        var state = DecodePicture(packet.Data.Span);
        if (state.Header.FieldPic && !state.SecondField)
        {
            _firstField = (state, packet.Pts);
            return [];
        }

        var first = state.SecondField ? _firstField!.Value : (State: state, packet.Pts);
        _firstField = null;
        var picture = state.Decoder.Picture;
        if (state.SecondField)
        {
            picture.Poc = Math.Min(picture.TopPoc, picture.BottomPoc);
        }

        bool borrow = BorrowFrames && picture.BitDepth == 8;
        var frame = borrow ? Borrow(picture, state.Sps) : Crop(picture, state.Sps);
        frame.Pts = first.Pts;

        bool restart = first.State.Header.IsIdr || first.State.Header.MemoryManagement.Any(op => op.Op == 5);
        return _output.Add(frame, picture.Poc, restart, ReorderDepth(state.Sps), borrow ? picture : null);
    }

    public IReadOnlyList<VideoFrame> Flush()
    {
        if (_firstField is not null)
        {
            throw new NotSupportedException("A field without its second field (an unpaired field) is not supported.");
        }

        return _output.Flush();
    }

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

    private PictureState DecodePicture(ReadOnlySpan<byte> packet)
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

        FinishPicture(current);
        return current;
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
                    var pps = PictureParameterSet.Parse(nal.CreateReader(), _sps);
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
        // The second field of a frame (7.4.1.2.4): the other parity right after the first, with its
        // frame_num, not IDR, and a reference if and only if the first one is.
        bool secondField = header.FieldPic && _firstField?.State is { } first
            && first.Header.Parity == 1 - header.Parity && first.Header.FrameNum == header.FrameNum && first.Sps == sps
            && !header.IsIdr && (first.Header.NalRefIdc != 0) == (header.NalRefIdc != 0);
        if (_firstField is not null && !secondField)
        {
            throw new NotSupportedException("A field without its second field (an unpaired field) is not supported.");
        }

        if (header.IsIdr)
        {
            _references.Clear();
        }
        else if (!secondField)
        {
            InferGapFrames(header, sps);
        }

        if (_decoder is null || _decoder.Sps.WidthInMbs != sps.WidthInMbs || _decoder.Sps.HeightInMbs != sps.HeightInMbs
            || _decoder.Sps.ChromaFormatIdc != sps.ChromaFormatIdc || _decoder.Sps.BitDepthLuma != sps.BitDepthLuma)
        {
            _decoder = sps.BitDepthLuma > 8
                ? new PictureDecoder<ushort>(sps) { Statistics = MbaffStatistics, FieldStatistics = FieldStatistics }
                : new PictureDecoder<byte>(sps) { Statistics = MbaffStatistics, FieldStatistics = FieldStatistics };
        }

        int poc = _order.Next(header, sps);
        Picture picture;
        if (secondField)
        {
            picture = _firstField!.Value.State.Decoder.Picture;
            if (header.BottomField)
            {
                picture.BottomPoc = poc;
            }
            else
            {
                picture.TopPoc = poc;
            }

            // The frame keeps the first field's order count while its second field is decoded:
            // 8.2.4.2.4 orders the first field by PicOrderCnt(fldPrev).
            FieldStatistics.SecondFields++;
        }
        else
        {
            FieldStatistics.FramePictures += header.FieldPic || sps.FrameMbsOnly ? 0 : 1;
            picture = TakePicture(sps, header.FrameNum, poc);
            (picture.TopPoc, picture.BottomPoc) = _order.Fields;
        }

        _decoder.Begin(picture, sps, header.Parity, secondField);
        return new PictureState(header, sps, _decoder, poc, secondField);
    }

    // The first field of a frame being decoded as two field pictures, and its packet's timestamp.
    private (PictureState State, long Pts)? _firstField;

    /// <summary>How often field pictures were decoded, so tests can check samples exercise them.</summary>
    internal FieldStatistics FieldStatistics { get; } = new();

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
        // A picture whose frame was returned by the previous call may be reused: borrowed frames
        // are valid until the next call. One waiting in the output queue may not.
        bool Fits(Picture p) => p.WidthInMbs == sps.WidthInMbs && p.HeightInMbs == sps.HeightInMbs && p.MbHeightC == sps.MbHeightC
            && p.MbWidthC == sps.MbWidthC && p.BitDepth == sps.BitDepthLuma;
        _pictures.RemoveAll(p => !Fits(p) && !_references.Contains(p) && !_output.Holds(p));
        var picture = _pictures.Find(p => Fits(p) && !_references.Contains(p) && !_output.Holds(p));
        if (picture is null)
        {
            picture = new Picture(sps.WidthInMbs, sps.HeightInMbs, sps.MbHeightC, sps.BitDepthLuma, sps.MbWidthC);
            _pictures.Add(picture);
        }

        // Every macroblock of a complete picture is written, so the old samples need no clearing.
        picture.Id = ++_pictureCount;
        picture.Poc = poc;
        picture.TopPoc = poc;
        picture.BottomPoc = poc;
        picture.Mbaff = false;
        picture.FrameNum = frameNum;
        picture.FrameNumWrap = 0;
        picture.IsShortTermReference = false;
        picture.IsLongTermReference = false;
        picture.LongTermFrameIdx = 0;
        picture.IsNonExisting = false;
        return picture;
    }

    private void FinishPicture(PictureState state)
    {
        if (!state.Decoder.IsComplete)
        {
            throw new InvalidDataException("The picture is missing macroblocks (error concealment is not supported).");
        }

        state.Decoder.Deblock();

        var picture = state.Decoder.Picture;
        if (state.Header.NalRefIdc != 0)
        {
            if (state.Header.FieldPic)
            {
                _references.MarkField(state.Header, picture, state.Sps, state.SecondField);
            }
            else
            {
                _references.Mark(state.Header, picture, state.Sps);
            }

            // PrevRefFrameNum (7.4.3); after memory_management_control_operation 5 it is 0.
            _prevRefFrameNum = state.Header.MemoryManagement.Any(op => op.Op == 5) ? 0 : state.Header.FrameNum;
        }
    }

    /// <summary>Copies the visible area (frame cropping, 7.4.2.1.1) into a new frame.</summary>
    private VideoFrame Crop(Picture picture, SequenceParameterSet sps)
    {
        // Every sample is written below, so pooled planes need no clearing.
        var format = FormatOf(picture);
        var frame = _framePool.Rent(sps.Width, sps.Height, format);
        var (left, _, top, _) = sps.Crop;
        for (int plane = 0; plane < 3; plane++)
        {
            int shiftX = plane == 0 || picture.MbWidthC == 16 ? 0 : 1;
            int shiftY = plane == 0 || picture.MbHeightC == 16 ? 0 : 1;
            int stride = plane == 0 ? picture.LumaStride : picture.ChromaStride;
            var (width, height) = format.PlaneSize(plane, sps.Width, sps.Height);
            for (int y = 0; y < height; y++)
            {
                int start = (((top >> shiftY) + y) * stride) + (left >> shiftX);
                if (picture.BitDepth == 8)
                {
                    picture.Plane<byte>(plane).AsSpan(start, width).CopyTo(frame.GetRow(plane, y));
                }
                else
                {
                    // 16-bit words in memory order, which is little-endian on every platform .NET runs on.
                    MemoryMarshal.AsBytes(picture.Plane<ushort>(plane).AsSpan(start, width)).CopyTo(frame.GetRow(plane, y));
                }
            }
        }

        return frame;
    }

    /// <summary>A frame of the cropped area of <paramref name="picture"/>, in its planes: no copy (<see cref="BorrowFrames"/>).</summary>
    private static VideoFrame Borrow(Picture picture, SequenceParameterSet sps)
    {
        var (left, _, top, _) = sps.Crop;
        int chromaOffset = ((picture.MbHeightC == 16 ? top : top >> 1) * picture.ChromaStride) + (picture.MbWidthC == 16 ? left : left >> 1);
        return new VideoFrame(
            sps.Width,
            sps.Height,
            FormatOf(picture),
            [picture.Y, picture.Cb, picture.Cr],
            [picture.LumaStride, picture.ChromaStride, picture.ChromaStride],
            [(top * picture.LumaStride) + left, chromaOffset, chromaOffset]);
    }

    private static PixelFormat FormatOf(Picture picture) => (picture.MbWidthC == 16 ? 3 : picture.MbHeightC, picture.BitDepth) switch
    {
        (3, 8) => PixelFormat.Yuv444P,
        (3, _) => PixelFormat.Yuv444P10,
        (16, 8) => PixelFormat.Yuv422P,
        (16, _) => PixelFormat.Yuv422P10,
        (_, 8) => PixelFormat.Yuv420P,
        _ => PixelFormat.Yuv420P10,
    };

    /// <summary>The picture being decoded: its first slice header, its SPS, its decoder and its order count.</summary>
    private sealed record PictureState(SliceHeader Header, SequenceParameterSet Sps, IPictureDecoder Decoder, int Poc, bool SecondField = false);

    private static void CheckSupported(SequenceParameterSet sps)
    {
        if (sps.ChromaFormatIdc == 0)
        {
            throw new NotSupportedException("Monochrome video (chroma_format_idc 0) is not supported.");
        }

        if (sps.SeparateColourPlanes)
        {
            throw new NotSupportedException("Separate colour planes (separate_colour_plane_flag) are not supported.");
        }

        if (sps.BitDepthLuma is not (8 or 10) || sps.BitDepthChroma != sps.BitDepthLuma)
        {
            throw new NotSupportedException($"Bit depths {sps.BitDepthLuma} (luma) and {sps.BitDepthChroma} (chroma) are not supported (8 or 10, the same for both).");
        }
    }
}

/// <summary>Counts of field pictures, so tests can check samples exercise them.</summary>
internal sealed class FieldStatistics
{
    /// <summary>Second fields decoded (and so frames made of two field pictures).</summary>
    public int SecondFields { get; set; }

    /// <summary>Partitions of a second field predicted from the first field of its own frame.</summary>
    public int FromOwnFirstField { get; set; }

    /// <summary>Frame pictures in streams that may code frames as fields (frame_mbs_only_flag 0).</summary>
    public int FramePictures { get; set; }

    /// <summary>Direct blocks of field pictures whose co-located block is in a frame macroblock (Frm_To_Fld).</summary>
    public int FieldOverFrame { get; set; }

    /// <summary>Direct blocks of frame pictures whose co-located block is in a frame coded as fields (Fld_To_Frm).</summary>
    public int FrameOverFields { get; set; }

    /// <summary>Direct blocks of field pictures, by temporal direct prediction.</summary>
    public int TemporalDirect { get; set; }
}
