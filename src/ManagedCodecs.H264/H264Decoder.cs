namespace ManagedCodecs.H264;

/// <summary>
/// H.264 decoder for Constrained Baseline streams (CAVLC, I and P slices, 4:2:0, 8-bit,
/// progressive), taking MP4-style samples: length-prefixed NAL units, one picture per sample.
/// </summary>
/// <remarks>
/// Implemented from ITU-T H.264; output is bit-exact with any conforming decoder. Streams that
/// use features outside Constrained Baseline (CABAC, B slices, interlacing, 8x8 transforms,
/// scaling matrices, weighted prediction, FMO, other chroma formats or bit depths) are rejected
/// with <see cref="NotSupportedException"/>. Frames come out in decoding order, which is
/// display order for streams without B slices.
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

    public VideoFrame Decode(ReadOnlySpan<byte> packet)
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

        return FinishPicture(current);
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
                    CheckSupported(sps, pps);

                    if (current is null)
                    {
                        current = StartPicture(header, sps);
                    }
                    else if (header.FrameNum != current.Header.FrameNum || current.Sps != sps)
                    {
                        throw new NotSupportedException("A packet holding more than one picture is not supported.");
                    }

                    var refList = header.Type == SliceType.P ? _references.BuildList(header, current.Sps) : [];
                    current.Decoder.DecodeSlice(header, pps, reader, refList);
                    break;
                }

            case >= NalUnitType.SliceDataPartitionA and <= (NalUnitType)4:
                throw new NotSupportedException("Data partitioning is not supported.");

            default:
                break; // SEI, access unit delimiters, filler data and the like carry no picture data
        }

        return current;
    }

    private PictureState StartPicture(SliceHeader header, SequenceParameterSet sps)
    {
        if (header.IsIdr)
        {
            _references.Clear();
        }

        if (_decoder is null || _decoder.Sps.WidthInMbs != sps.WidthInMbs || _decoder.Sps.HeightInMbs != sps.HeightInMbs)
        {
            _decoder = new PictureDecoder(sps);
        }

        _decoder.Begin(TakePicture(sps, header.FrameNum));
        return new PictureState(header, sps, _decoder);
    }

    /// <summary>A picture of the stream's size that is not a reference, reused when one is free.</summary>
    private Picture TakePicture(SequenceParameterSet sps, int frameNum)
    {
        _pictures.RemoveAll(p => (p.WidthInMbs != sps.WidthInMbs || p.HeightInMbs != sps.HeightInMbs) && !_references.Contains(p));
        var picture = _pictures.Find(p => p.WidthInMbs == sps.WidthInMbs && p.HeightInMbs == sps.HeightInMbs && !_references.Contains(p));
        if (picture is null)
        {
            picture = new Picture(sps.WidthInMbs, sps.HeightInMbs);
            _pictures.Add(picture);
        }

        // Every macroblock of a complete picture is written, so the old samples need no clearing.
        picture.FrameNum = frameNum;
        picture.FrameNumWrap = 0;
        picture.IsShortTermReference = false;
        picture.IsLongTermReference = false;
        picture.LongTermFrameIdx = 0;
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

    /// <summary>The picture being decoded: its first slice header, its SPS and its decoder.</summary>
    private sealed record PictureState(SliceHeader Header, SequenceParameterSet Sps, PictureDecoder Decoder);

    private static void CheckSupported(SequenceParameterSet sps, PictureParameterSet pps)
    {
        if (sps.ChromaFormatIdc != 1)
        {
            throw new NotSupportedException($"chroma_format_idc {sps.ChromaFormatIdc} is not supported (4:2:0 only).");
        }

        if (sps.BitDepthLuma != 8 || sps.BitDepthChroma != 8)
        {
            throw new NotSupportedException("Only 8-bit video is supported.");
        }

        if (sps.HasScalingMatrix || pps.HasScalingMatrix)
        {
            throw new NotSupportedException("Scaling matrices are not supported.");
        }

        if (pps.Transform8x8Mode)
        {
            throw new NotSupportedException("8x8 transforms are not supported.");
        }
    }
}
