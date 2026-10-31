namespace ManagedCodecs;

/// <summary>A decoded planar video frame.</summary>
/// <remarks>
/// Each plane has its own stride and offset (where its first visible sample is). A frame made
/// with the size alone owns tight planes: stride equals plane width and offsets are 0. A frame
/// that wraps a decoder's buffers (H.264's borrowed frames) has wider strides and nonzero
/// offsets, so read rows through <see cref="GetRow"/> or with <see cref="Offsets"/> and
/// <see cref="Strides"/>. Strides and offsets count bytes, which for the formats deeper than 8 bits are
/// two a sample (<see cref="PixelFormatExtensions.BytesPerSample"/>).
/// <para>
/// Disposing a frame is optional. A frame a decoder made on planes of its own pool (the H.264
/// decoder's copied frames) then gives its planes back for the next frame, which saves
/// allocating and collecting them; a disposed frame's samples must not be used again, and
/// <see cref="Planes"/>, <see cref="GetRow"/> and <see cref="WritePacked"/> throw.
/// </para>
/// </remarks>
public sealed class VideoFrame : IDisposable
{
    private readonly byte[][] _planes;
    private readonly VideoFramePool? _pool;
    private bool _disposed;

    /// <summary>A frame that owns new, zeroed, tight planes: each stride the plane's width in bytes, each offset 0.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The width or height is not positive.</exception>
    public VideoFrame(int width, int height, PixelFormat format)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Width = width;
        Height = height;
        Format = format;

        int count = format.PlaneCount();
        _planes = new byte[count][];
        Strides = new int[count];
        for (int i = 0; i < count; i++)
        {
            var (planeWidth, planeHeight) = format.PlaneSize(i, width, height);
            planeWidth *= format.BytesPerSample();
            Strides[i] = planeWidth;
            _planes[i] = new byte[planeWidth * planeHeight];
        }

        Offsets = new int[count];
    }

    /// <summary>A frame on planes of <paramref name="pool"/> (tight, or rows <paramref name="strides"/> apart), which takes them back on <see cref="Dispose"/>.</summary>
    internal VideoFrame(int width, int height, PixelFormat format, byte[][] planes, int[] strides, VideoFramePool pool)
        : this(width, height, format, planes, strides, new int[planes.Length])
    {
        _pool = pool;
    }

    /// <summary>
    /// Wraps existing planes without copying: plane i's visible rows start at
    /// <paramref name="offsets"/>[i] and lie <paramref name="strides"/>[i] apart.
    /// </summary>
    public VideoFrame(int width, int height, PixelFormat format, byte[][] planes, int[] strides, int[] offsets)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        int count = format.PlaneCount();
        if (planes.Length != count || strides.Length != count || offsets.Length != count)
        {
            throw new ArgumentException($"{format} has {count} planes.");
        }

        for (int i = 0; i < count; i++)
        {
            var (planeWidth, planeHeight) = format.PlaneSize(i, width, height);
            planeWidth *= format.BytesPerSample();
            if (strides[i] < planeWidth || offsets[i] < 0 || offsets[i] + ((planeHeight - 1) * (long)strides[i]) + planeWidth > planes[i].Length)
            {
                throw new ArgumentException($"Plane {i} does not hold {planeWidth}x{planeHeight} samples at offset {offsets[i]}, stride {strides[i]}.");
            }
        }

        Width = width;
        Height = height;
        Format = format;
        _planes = planes;
        Strides = strides;
        Offsets = offsets;
    }

    /// <summary>Visible width in luma samples.</summary>
    public int Width { get; }

    /// <summary>Visible height in luma samples.</summary>
    public int Height { get; }

    /// <summary>The planes' layout and sample depth.</summary>
    public PixelFormat Format { get; }

    /// <summary>The planes (Y, or Y, Cb and Cr), each a byte array laid out by <see cref="Strides"/> and <see cref="Offsets"/>.</summary>
    /// <exception cref="ObjectDisposedException">The frame was disposed.</exception>
    public byte[][] Planes => _disposed ? throw new ObjectDisposedException(nameof(VideoFrame)) : _planes;

    /// <summary>Bytes from one row of each plane to the next.</summary>
    public int[] Strides { get; }

    /// <summary>Index of each plane's first visible sample in <see cref="Planes"/>.</summary>
    public int[] Offsets { get; }

    /// <summary>
    /// Presentation timestamp in the stream's time base, from the packet the frame was coded in;
    /// 0 for frames decoded without one.
    /// </summary>
    public long Pts { get; set; }

    /// <summary>
    /// Whether the decoder concealed errors in the frame, or in a frame it was predicted from (H.264 with
    /// <c>ConcealErrors</c>): parts of it are guesses.
    /// </summary>
    public bool IsDamaged { get; internal set; }

    /// <summary>
    /// How the frame was coded, as FFmpeg's <c>pict_type</c>: in H.264 the type of the picture's first slice (of its first
    /// field, for a frame of two field pictures), I for every MJPEG frame; <see cref="PictureType.None"/> for a frame no
    /// decoder made.
    /// </summary>
    public PictureType PictureType { get; internal set; }

    /// <summary>
    /// Whether the frame is interlaced, as FFmpeg's <c>interlaced_frame</c> (<c>AV_FRAME_FLAG_INTERLACED</c>). H.264 takes
    /// the picture timing SEI's pic_struct where the SPS has one and it came, else whether the frame was coded interlaced
    /// (two field pictures, or an MBAFF frame); false for every MJPEG frame, as in FFmpeg.
    /// </summary>
    public bool IsInterlaced { get; internal set; }

    /// <summary>
    /// Whether the frame's top field comes first, as FFmpeg's <c>top_field_first</c> (<c>AV_FRAME_FLAG_TOP_FIELD_FIRST</c>):
    /// in H.264 the top field's order count below the bottom's where they differ, else pic_struct 3 or 5 where the SPS
    /// has the picture timing SEI and it came, else whether the frame is <see cref="IsInterlaced"/>. False for every
    /// MJPEG frame.
    /// </summary>
    public bool IsTopFieldFirst { get; internal set; }

    /// <summary>
    /// The field order FFmpeg's H.264 parser gives the frame's picture (its second field's, for a frame of two field
    /// pictures): pic_struct 3 or 5 <see cref="FieldOrder.TT"/>, 4 or 6 <see cref="FieldOrder.BB"/> and any other
    /// <see cref="FieldOrder.Progressive"/> where the SPS has the picture timing SEI and it came with the picture; else
    /// the top and bottom field order counts' order, <see cref="FieldOrder.Progressive"/> where they are equal; for a
    /// field, the order of its frame's fields. <see cref="FieldOrder.Unknown"/> for MJPEG, and for a frame whose field
    /// came alone. A stream's order, as <c>ffprobe -show_streams</c> prints it, is that of its first picture:
    /// <c>H264Decoder.FieldOrder</c>.
    /// </summary>
    public FieldOrder FieldOrder { get; internal set; }

    /// <summary>
    /// Whether the frame was coded as two field pictures (field_pic_flag, PAFF), not as one frame picture (progressive,
    /// or MBAFF). Also true for a frame made from one field whose second field was lost (H.264's <c>ConcealErrors</c>).
    /// </summary>
    public bool IsCodedAsFields { get; internal set; }

    /// <summary>
    /// Which values the samples take, as FFmpeg's <c>color_range</c>: in H.264 the SPS's video_full_range_flag where its
    /// VUI has a video signal type, else <see cref="ColorRange.Unspecified"/> (most streams, limited range in practice).
    /// </summary>
    public ColorRange ColorRange { get; internal set; }

    /// <summary>
    /// The colour primaries, as FFmpeg's <c>color_primaries</c>: in H.264 the VUI's colour_primaries where it has a colour
    /// description, else (and for a value ITU-T H.273 does not define) <see cref="ColorPrimaries.Unspecified"/>.
    /// </summary>
    public ColorPrimaries ColorPrimaries { get; internal set; } = ColorPrimaries.Unspecified;

    /// <summary>
    /// The transfer characteristics, as FFmpeg's <c>color_transfer</c>: in H.264 the VUI's transfer_characteristics where
    /// it has a colour description, else (and for a value ITU-T H.273 does not define) <see cref="ColorTransfer.Unspecified"/>.
    /// </summary>
    public ColorTransfer ColorTransfer { get; internal set; } = ColorTransfer.Unspecified;

    /// <summary>
    /// The YCbCr to RGB matrix, as FFmpeg's <c>color_space</c>: in H.264 the VUI's matrix_coefficients where it has a
    /// colour description, else (and for a value ITU-T H.273 does not define) <see cref="ColorMatrix.Unspecified"/>.
    /// Nothing is guessed: an unspecified matrix is usually BT.601 below 720 lines and BT.709 from 720.
    /// </summary>
    public ColorMatrix ColorMatrix { get; internal set; } = ColorMatrix.Unspecified;

    /// <summary>
    /// Where the chroma samples sit, as FFmpeg's <c>chroma_location</c>: in H.264 the VUI's
    /// chroma_sample_loc_type_top_field plus one where it says, <see cref="ChromaLocation.Left"/> for a VUI that
    /// does not (as FFmpeg reads it, whatever the chroma format), <see cref="ChromaLocation.Unspecified"/> without a VUI.
    /// </summary>
    public ChromaLocation ChromaLocation { get; internal set; }

    /// <summary>
    /// The width of a sample over its height, as FFmpeg's <c>sample_aspect_ratio</c>: a frame is shown
    /// <see cref="Width"/> times this wide. 0:1 when unknown (FFmpeg's <c>N/A</c>). In H.264 the VUI's aspect_ratio_idc
    /// (Table E-1) or sar_width:sar_height, in lowest terms.
    /// </summary>
    public Rational SampleAspectRatio { get; internal set; } = new(0, 1);

    /// <summary>The visible samples of one row in one plane, as bytes (two a sample in the formats deeper than 8 bits).</summary>
    public Span<byte> GetRow(int plane, int y)
    {
        var (planeWidth, planeHeight) = Format.PlaneSize(plane, Width, Height);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, planeHeight);
        return Planes[plane].AsSpan(Offsets[plane] + (y * Strides[plane]), planeWidth * Format.BytesPerSample());
    }

    /// <summary>
    /// Writes all planes tightly packed, one after another. This is the same layout FFmpeg's
    /// rawvideo output uses, so the hash of this data matches FFmpeg's <c>framemd5</c> lines.
    /// </summary>
    public void WritePacked(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        for (int plane = 0; plane < Planes.Length; plane++)
        {
            var (_, planeHeight) = Format.PlaneSize(plane, Width, Height);
            for (int y = 0; y < planeHeight; y++)
            {
                destination.Write(GetRow(plane, y));
            }
        }
    }

    /// <summary>
    /// Ends the frame's use of its samples: a pooled frame's planes go back to its pool, to be
    /// written over by a later frame. Calling it more than once does nothing more.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pool?.Return(_planes);
    }
}
