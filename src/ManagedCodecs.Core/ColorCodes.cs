namespace ManagedCodecs;

/// <summary>
/// ITU-T H.273's code points as FFmpeg 6.1 reads them, from a codec (H.264's VUI) or a container (MP4's 'colr'):
/// a value FFmpeg does not name is unspecified to it. And aspect ratios in lowest terms, as ffprobe prints them.
/// </summary>
internal static class ColorCodes
{
    /// <summary>colour_primaries as sent, or unspecified for a value FFmpeg does not name (it keeps 0 and 3, reserved but named).</summary>
    public static ColorPrimaries PrimariesOf(int value) => value is (>= 0 and <= 12) or 22 ? (ColorPrimaries)value : ColorPrimaries.Unspecified;

    /// <summary>transfer_characteristics as sent, or unspecified for a value FFmpeg does not name (it names 0 to 18).</summary>
    public static ColorTransfer TransferOf(int value) => value is >= 0 and <= 18 ? (ColorTransfer)value : ColorTransfer.Unspecified;

    /// <summary>matrix_coefficients as sent, or unspecified for a value FFmpeg 6.1 does not name (it names 0 to 14).</summary>
    public static ColorMatrix MatrixOf(int value) => value is >= 0 and <= 14 ? (ColorMatrix)value : ColorMatrix.Unspecified;

    /// <summary>
    /// <paramref name="width"/>:<paramref name="height"/> in lowest terms, or 0:1 (unknown) unless both are positive and
    /// the reduced ratio fits a <see cref="Rational"/>.
    /// </summary>
    public static Rational AspectRatio(long width, long height)
    {
        if (width <= 0 || height <= 0)
        {
            return new(0, 1);
        }

        long gcd = (long)System.Numerics.BigInteger.GreatestCommonDivisor(width, height);
        long numerator = width / gcd;
        long denominator = height / gcd;
        return numerator <= int.MaxValue && denominator <= int.MaxValue ? new((int)numerator, (int)denominator) : new(0, 1);
    }
}
