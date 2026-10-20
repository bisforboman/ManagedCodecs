namespace ManagedCodecs;

/// <summary>Planar pixel layouts produced by decoders.</summary>
public enum PixelFormat
{
    Gray8,
    Yuv420P,
    Yuv422P,
    Yuv444P,
}

public static class PixelFormatExtensions
{
    public static int PlaneCount(this PixelFormat format) => format == PixelFormat.Gray8 ? 1 : 3;

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
            PixelFormat.Yuv420P => ((width + 1) >> 1, (height + 1) >> 1),
            PixelFormat.Yuv422P => ((width + 1) >> 1, height),
            PixelFormat.Yuv444P => (width, height),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };
    }
}
