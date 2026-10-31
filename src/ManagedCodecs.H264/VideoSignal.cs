namespace ManagedCodecs.H264;

/// <summary>
/// What the SPS's VUI says of the samples' meaning (E.2.1): video_full_range_flag, the colour description, the chroma
/// sample location and the sample aspect ratio, as FFmpeg reads them into a decoded frame's <c>color_range</c>,
/// <c>color_primaries</c>, <c>color_transfer</c>, <c>color_space</c>, <c>chroma_location</c> and
/// <c>sample_aspect_ratio</c>.
/// </summary>
internal readonly record struct VideoSignal(
    ColorRange Range,
    ColorPrimaries Primaries,
    ColorTransfer Transfer,
    ColorMatrix Matrix,
    ChromaLocation Location,
    Rational AspectRatio)
{
    /// <summary>An SPS without a VUI: nothing said, not even the chroma location.</summary>
    public static readonly VideoSignal None = new(
        ColorRange.Unspecified, ColorPrimaries.Unspecified, ColorTransfer.Unspecified, ColorMatrix.Unspecified, ChromaLocation.Unspecified, new(0, 1));

    /// <summary>Table E-1's sample aspect ratios by aspect_ratio_idc, 0 (unspecified) to 16; 255 sends its own.</summary>
    private static readonly Rational[] AspectRatios =
    [
        new(0, 1), new(1, 1), new(12, 11), new(10, 11), new(16, 11), new(40, 33), new(24, 11), new(20, 11), new(32, 11),
        new(80, 33), new(18, 11), new(15, 11), new(64, 33), new(160, 99), new(4, 3), new(3, 2), new(2, 1),
    ];

    /// <summary>
    /// aspect_ratio_idc's ratio, or sar_width:sar_height for Extended_SAR (255) in lowest terms (as ffprobe prints it):
    /// unspecified (0:1) for idc 0, for the reserved 17 to 254 and for a width or height of 0 (E.2.1).
    /// </summary>
    public static Rational AspectRatioOf(int idc, int sarWidth, int sarHeight)
    {
        if (idc == 255 && sarWidth > 0 && sarHeight > 0)
        {
            int gcd = (int)System.Numerics.BigInteger.GreatestCommonDivisor(sarWidth, sarHeight);
            return new(sarWidth / gcd, sarHeight / gcd);
        }

        return idc < 17 ? AspectRatios[idc] : new(0, 1);
    }

    /// <summary>
    /// colour_primaries as sent, or unspecified for a value H.273 (and so FFmpeg, which names its values) does not
    /// define: FFmpeg keeps 0 and 3, reserved but named.
    /// </summary>
    public static ColorPrimaries PrimariesOf(int value) => value is (>= 0 and <= 12) or 22 ? (ColorPrimaries)value : ColorPrimaries.Unspecified;

    /// <summary>transfer_characteristics as sent, or unspecified for a value H.273 does not define (FFmpeg's names, 0 to 18).</summary>
    public static ColorTransfer TransferOf(int value) => value is >= 0 and <= 18 ? (ColorTransfer)value : ColorTransfer.Unspecified;

    /// <summary>matrix_coefficients as sent, or unspecified for a value FFmpeg 6.1 does not name (it names 0 to 14).</summary>
    public static ColorMatrix MatrixOf(int value) => value is >= 0 and <= 14 ? (ColorMatrix)value : ColorMatrix.Unspecified;

    /// <summary>chroma_sample_loc_type_top_field plus one (0 to 5, Figure E-1), or unspecified beyond, as FFmpeg reads it.</summary>
    public static ChromaLocation LocationOf(uint topFieldType) => topFieldType <= 5 ? (ChromaLocation)(topFieldType + 1) : ChromaLocation.Unspecified;

    /// <summary>
    /// The aspect ratio a frame of this size takes, as FFmpeg's <c>ff_set_sar</c> checks it: 0:1 where the ratio would
    /// make the shown width (or, above 1, height) round to nothing (<c>av_image_check_sar</c>), as an extreme
    /// Extended_SAR can.
    /// </summary>
    public static Rational AspectRatioFor(Rational ratio, int width, int height)
    {
        if (ratio.Numerator == 0 || ratio.Numerator == ratio.Denominator)
        {
            return ratio.Numerator == 0 ? new(0, 1) : ratio;
        }

        long shown = ratio.Numerator < ratio.Denominator
            ? (long)width * ratio.Numerator / ratio.Denominator
            : (long)height * ratio.Denominator / ratio.Numerator;
        return shown > 0 ? ratio : new(0, 1);
    }

    /// <summary>The frame's colour properties and sample aspect ratio from this signal.</summary>
    public void Apply(VideoFrame frame)
    {
        frame.ColorRange = Range;
        frame.ColorPrimaries = Primaries;
        frame.ColorTransfer = Transfer;
        frame.ColorMatrix = Matrix;
        frame.ChromaLocation = Location;
        frame.SampleAspectRatio = AspectRatioFor(AspectRatio, frame.Width, frame.Height);
    }
}
