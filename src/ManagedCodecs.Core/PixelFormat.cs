namespace ManagedCodecs;

/// <summary>Planar pixel layouts produced by decoders.</summary>
/// <remarks>
/// The 8-bit formats take a byte a sample; the deeper ones (9 to 14 bits, by the number in the name) a
/// little-endian 16-bit word holding the value, as FFmpeg's gray10le, yuv420p10le and the like.
/// </remarks>
public enum PixelFormat
{
    /// <summary>8-bit luma only (monochrome); a byte a sample.</summary>
    Gray8,

    /// <summary>8-bit Y, Cb, Cr planes; chroma half wide and half high; a byte a sample.</summary>
    Yuv420P,

    /// <summary>8-bit Y, Cb, Cr planes; chroma half wide, full height; a byte a sample.</summary>
    Yuv422P,

    /// <summary>8-bit Y, Cb, Cr planes, all full size; a byte a sample.</summary>
    Yuv444P,

    /// <summary>10-bit Y, Cb, Cr planes; chroma half wide and half high; a little-endian 16-bit word a sample.</summary>
    Yuv420P10,

    /// <summary>10-bit Y, Cb, Cr planes; chroma half wide, full height; a little-endian 16-bit word a sample.</summary>
    Yuv422P10,

    /// <summary>10-bit Y, Cb, Cr planes, all full size; a little-endian 16-bit word a sample.</summary>
    Yuv444P10,

    /// <summary>10-bit luma only (monochrome); a little-endian 16-bit word a sample.</summary>
    Gray10,

    /// <summary>9-bit luma only (monochrome); a little-endian 16-bit word a sample.</summary>
    Gray9,

    /// <summary>9-bit Y, Cb, Cr planes; chroma half wide and half high; a little-endian 16-bit word a sample.</summary>
    Yuv420P9,

    /// <summary>9-bit Y, Cb, Cr planes; chroma half wide, full height; a little-endian 16-bit word a sample.</summary>
    Yuv422P9,

    /// <summary>9-bit Y, Cb, Cr planes, all full size; a little-endian 16-bit word a sample.</summary>
    Yuv444P9,

    /// <summary>12-bit luma only (monochrome); a little-endian 16-bit word a sample.</summary>
    Gray12,

    /// <summary>12-bit Y, Cb, Cr planes; chroma half wide and half high; a little-endian 16-bit word a sample.</summary>
    Yuv420P12,

    /// <summary>12-bit Y, Cb, Cr planes; chroma half wide, full height; a little-endian 16-bit word a sample.</summary>
    Yuv422P12,

    /// <summary>12-bit Y, Cb, Cr planes, all full size; a little-endian 16-bit word a sample.</summary>
    Yuv444P12,

    /// <summary>14-bit luma only (monochrome); a little-endian 16-bit word a sample.</summary>
    Gray14,

    /// <summary>14-bit Y, Cb, Cr planes; chroma half wide and half high; a little-endian 16-bit word a sample.</summary>
    Yuv420P14,

    /// <summary>14-bit Y, Cb, Cr planes; chroma half wide, full height; a little-endian 16-bit word a sample.</summary>
    Yuv422P14,

    /// <summary>14-bit Y, Cb, Cr planes, all full size; a little-endian 16-bit word a sample.</summary>
    Yuv444P14,

    /// <summary>8-bit Y, Cb, Cr planes; chroma a quarter wide, full height; a byte a sample (MJPEG's 4:1:1).</summary>
    Yuv411P,

    /// <summary>8-bit Y, Cb, Cr planes; chroma full width, half high; a byte a sample (MJPEG's 4:4:0).</summary>
    Yuv440P,
}

/// <summary>Facts about a <see cref="PixelFormat"/>: planes, plane sizes and sample depth.</summary>
public static class PixelFormatExtensions
{
    /// <summary>Planes a frame of the format has: 1 for gray, 3 (Y, Cb, Cr) for the others.</summary>
    public static int PlaneCount(this PixelFormat format) => format.ChromaShift().X < 0 ? 1 : 3;

    /// <summary>Bits a sample holds: 8, 9, 10, 12 or 14.</summary>
    public static int BitDepth(this PixelFormat format) => format switch
    {
        PixelFormat.Gray8 or PixelFormat.Yuv420P or PixelFormat.Yuv422P or PixelFormat.Yuv444P or PixelFormat.Yuv411P or PixelFormat.Yuv440P => 8,
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
    internal static PixelFormat Of(int chroma, int bitDepth)
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

        var (x, y) = format.ChromaShift();
        return (-(-width >> x), -(-height >> y));
    }

    /// <summary>
    /// How far the chroma planes' width and height are shifted down from the luma's (sizes round up, as in FFmpeg):
    /// (1, 1) for 4:2:0, (1, 0) for 4:2:2, (2, 0) for 4:1:1, (0, 1) for 4:4:0, (0, 0) for 4:4:4; (-1, -1) for gray.
    /// </summary>
    internal static (int X, int Y) ChromaShift(this PixelFormat format) => format switch
    {
        PixelFormat.Gray8 or PixelFormat.Gray9 or PixelFormat.Gray10 or PixelFormat.Gray12 or PixelFormat.Gray14 => (-1, -1),
        PixelFormat.Yuv420P or PixelFormat.Yuv420P9 or PixelFormat.Yuv420P10 or PixelFormat.Yuv420P12 or PixelFormat.Yuv420P14 => (1, 1),
        PixelFormat.Yuv422P or PixelFormat.Yuv422P9 or PixelFormat.Yuv422P10 or PixelFormat.Yuv422P12 or PixelFormat.Yuv422P14 => (1, 0),
        PixelFormat.Yuv444P or PixelFormat.Yuv444P9 or PixelFormat.Yuv444P10 or PixelFormat.Yuv444P12 or PixelFormat.Yuv444P14 => (0, 0),
        PixelFormat.Yuv411P => (2, 0),
        PixelFormat.Yuv440P => (0, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };
}
