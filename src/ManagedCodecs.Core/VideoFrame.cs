namespace ManagedCodecs;

/// <summary>A decoded planar video frame.</summary>
/// <remarks>
/// Each plane has its own stride and offset (where its first visible sample is). A frame made
/// with the size alone owns tight planes: stride equals plane width and offsets are 0. A frame
/// that wraps a decoder's buffers (H.264's borrowed frames) has wider strides and nonzero
/// offsets, so read rows through <see cref="GetRow"/> or with <see cref="Offsets"/> and
/// <see cref="Strides"/>. Strides and offsets count bytes, which for the 10-bit formats are two a
/// sample (<see cref="PixelFormatExtensions.BytesPerSample"/>).
/// </remarks>
public sealed class VideoFrame
{
    public VideoFrame(int width, int height, PixelFormat format)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Width = width;
        Height = height;
        Format = format;

        int count = format.PlaneCount();
        Planes = new byte[count][];
        Strides = new int[count];
        for (int i = 0; i < count; i++)
        {
            var (planeWidth, planeHeight) = format.PlaneSize(i, width, height);
            planeWidth *= format.BytesPerSample();
            Strides[i] = planeWidth;
            Planes[i] = new byte[planeWidth * planeHeight];
        }

        Offsets = new int[count];
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
        Planes = planes;
        Strides = strides;
        Offsets = offsets;
    }

    public int Width { get; }

    public int Height { get; }

    public PixelFormat Format { get; }

    public byte[][] Planes { get; }

    public int[] Strides { get; }

    /// <summary>Index of each plane's first visible sample in <see cref="Planes"/>.</summary>
    public int[] Offsets { get; }

    /// <summary>
    /// Presentation timestamp in the stream's time base, from the packet the frame was coded in;
    /// 0 for frames decoded without one.
    /// </summary>
    public long Pts { get; set; }

    /// <summary>The visible samples of one row in one plane, as bytes (two a sample in the 10-bit formats).</summary>
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
}
