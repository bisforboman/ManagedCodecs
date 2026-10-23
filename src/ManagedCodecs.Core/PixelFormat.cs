namespace ManagedCodecs;

/// <summary>Planar pixel layouts produced by decoders.</summary>
/// <remarks>
/// The 8-bit formats take a byte a sample; the deeper ones (9 to 14 bits, by the number in the name) a
/// little-endian 16-bit word holding the value, as FFmpeg's gray10le, yuv420p10le and the like.
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
    Gray10,
    Gray9,
    Yuv420P9,
    Yuv422P9,
    Yuv444P9,
    Gray12,
    Yuv420P12,
    Yuv422P12,
    Yuv444P12,
    Gray14,
    Yuv420P14,
    Yuv422P14,
    Yuv444P14,
}

public static class PixelFormatExtensions
{
    public static int PlaneCount(this PixelFormat format) => format.Chroma() == 0 ? 1 : 3;

    /// <summary>Bits a sample holds: 8, 9, 10, 12 or 14.</summary>
    public static int BitDepth(this PixelFormat format) => format switch
    {
        PixelFormat.Gray8 or PixelFormat.Yuv420P or PixelFormat.Yuv422P or PixelFormat.Yuv444P => 8,
        PixelFormat.Gray9 or PixelFormat.Yuv420P9 or PixelFormat.Yuv422P9 or PixelFormat.Yuv444P9 => 9,
        PixelFormat.Gray10 or PixelFormat.Yuv420P10 or PixelFormat.Yuv422P10 or PixelFormat.Yuv444P10 => 10,
        PixelFormat.Gray12 or PixelFormat.Yuv420P12 or PixelFormat.Yuv422P12 or PixelFormat.Yuv444P12 => 12,
        PixelFormat.Gray14 or PixelFormat.Yuv420P14 or PixelFormat.Yuv422P14 or PixelFormat.Yuv444P14 => 14,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    /// <summary>Bytes a sample takes in the planes: 1, or 2 for the formats deeper than 8 bits.</summary>
    public static int BytesPerSample(this PixelFormat format) => format.BitDepth() > 8 ? 2 : 1;

    /// <summary>
    /// The format of <paramref name="chroma"/> sampling (0 gray, 1 4:2:0, 2 4:2:2, 3 4:4:4, as H.264's
    /// chroma_format_idc) and <paramref name="bitDepth"/>.
    /// </summary>
    public static PixelFormat Of(int chroma, int bitDepth)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chroma);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chroma, 3);
        int row = bitDepth switch
        {
            8 => 0,
            9 => 1,
            10 => 2,
            12 => 3,
            14 => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(bitDepth), bitDepth, "8, 9, 10, 12 or 14."),
        };
        return ByDepthAndChroma[(row * 4) + chroma];
    }

    private static readonly PixelFormat[] ByDepthAndChroma =
    [
        PixelFormat.Gray8, PixelFormat.Yuv420P, PixelFormat.Yuv422P, PixelFormat.Yuv444P,
        PixelFormat.Gray9, PixelFormat.Yuv420P9, PixelFormat.Yuv422P9, PixelFormat.Yuv444P9,
        PixelFormat.Gray10, PixelFormat.Yuv420P10, PixelFormat.Yuv422P10, PixelFormat.Yuv444P10,
        PixelFormat.Gray12, PixelFormat.Yuv420P12, PixelFormat.Yuv422P12, PixelFormat.Yuv444P12,
        PixelFormat.Gray14, PixelFormat.Yuv420P14, PixelFormat.Yuv422P14, PixelFormat.Yuv444P14,
    ];

    /// <summary>Width and height of a plane in samples. Chroma sizes round up, as in FFmpeg.</summary>
    public static (int Width, int Height) PlaneSize(this PixelFormat format, int plane, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(plane);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(plane, format.PlaneCount());

        if (plane == 0)
        {
            return (width, height);
        }

        return format.Chroma() switch
        {
            1 => ((width + 1) >> 1, (height + 1) >> 1),
            2 => ((width + 1) >> 1, height),
            _ => (width, height),
        };
    }

    /// <summary>The chroma sampling: 0 gray, 1 4:2:0, 2 4:2:2, 3 4:4:4.</summary>
    private static int Chroma(this PixelFormat format) => format switch
    {
        PixelFormat.Gray8 or PixelFormat.Gray9 or PixelFormat.Gray10 or PixelFormat.Gray12 or PixelFormat.Gray14 => 0,
        PixelFormat.Yuv420P or PixelFormat.Yuv420P9 or PixelFormat.Yuv420P10 or PixelFormat.Yuv420P12 or PixelFormat.Yuv420P14 => 1,
        PixelFormat.Yuv422P or PixelFormat.Yuv422P9 or PixelFormat.Yuv422P10 or PixelFormat.Yuv422P12 or PixelFormat.Yuv422P14 => 2,
        PixelFormat.Yuv444P or PixelFormat.Yuv444P9 or PixelFormat.Yuv444P10 or PixelFormat.Yuv444P12 or PixelFormat.Yuv444P14 => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };
}
