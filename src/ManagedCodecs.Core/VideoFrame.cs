namespace ManagedCodecs;

/// <summary>A decoded planar video frame.</summary>
/// <remarks>
/// Each plane has its own stride. Today stride equals plane width; it is kept separate so
/// planes can later be padded for SIMD-friendly row alignment without changing callers.
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
            Strides[i] = planeWidth;
            Planes[i] = new byte[planeWidth * planeHeight];
        }
    }

    public int Width { get; }

    public int Height { get; }

    public PixelFormat Format { get; }

    public byte[][] Planes { get; }

    public int[] Strides { get; }

    /// <summary>
    /// Presentation timestamp in the stream's time base, from the packet the frame was coded in;
    /// 0 for frames decoded without one.
    /// </summary>
    public long Pts { get; set; }

    /// <summary>The visible samples of one row in one plane.</summary>
    public Span<byte> GetRow(int plane, int y)
    {
        var (planeWidth, planeHeight) = Format.PlaneSize(plane, Width, Height);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, planeHeight);
        return Planes[plane].AsSpan(y * Strides[plane], planeWidth);
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
