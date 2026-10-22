namespace ManagedCodecs;

/// <summary>Planar pixel layouts produced by decoders.</summary>
/// <remarks>
/// The 8-bit formats take a byte a sample; <see cref="Yuv420P10"/> and <see cref="Yuv422P10"/> a
/// little-endian 16-bit word holding a 10-bit value (FFmpeg's yuv420p10le, yuv422p10le and yuv444p10le).
/// </remarks>
public enum PixelFormat
{
    Gray8,
    Yuv420P,
    Yuv422P,
    Yuv444P,
    Yuv420P10,
    Yuv422P10,
    Yuv444P10,
}

public static class PixelFormatExtensions
{
    public static int PlaneCount(this PixelFormat format) => format == PixelFormat.Gray8 ? 1 : 3;

    /// <summary>Bytes a sample takes in the planes: 1, or 2 for the 10-bit formats.</summary>
    public static int BytesPerSample(this PixelFormat format) => format is PixelFormat.Yuv420P10 or PixelFormat.Yuv422P10 or PixelFormat.Yuv444P10 ? 2 : 1;

    /// <summary>Width and height of a plane in samples. Chroma sizes round up, as in FFmpeg.</summary>
    public static (int Width, int Height) PlaneSize(this PixelFormat format, int plane, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(plane);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(plane, format.PlaneCount());

        if (plane == 0)
        {
            return (width, height);
        }

        return format switch
        {
            PixelFormat.Yuv420P or PixelFormat.Yuv420P10 => ((width + 1) >> 1, (height + 1) >> 1),
            PixelFormat.Yuv422P or PixelFormat.Yuv422P10 => ((width + 1) >> 1, height),
            PixelFormat.Yuv444P or PixelFormat.Yuv444P10 => (width, height),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };
    }
}
