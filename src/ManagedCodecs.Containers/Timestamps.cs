namespace ManagedCodecs.Containers;

/// <summary>Timestamp arithmetic the demuxers share.</summary>
internal static class Timestamps
{
    /// <summary>A time in one time base in another, rounded down (the sample at or before it is wanted).</summary>
    public static long Rescale(long value, Rational from, Rational to)
    {
        long mul = (long)from.Numerator * to.Denominator;
        long div = (long)from.Denominator * to.Numerator;
#if NET
        Int128 product = (Int128)value * mul;
        var quotient = product / div;
        return (long)(product % div != 0 && (product < 0) != (div < 0) ? quotient - 1 : quotient);
#else
        var product = (System.Numerics.BigInteger)value * mul;
        var quotient = System.Numerics.BigInteger.Divide(product, div);
        return (long)(!(product % div).IsZero && (product.Sign < 0) != (div < 0) ? quotient - 1 : quotient);
#endif
    }
}
