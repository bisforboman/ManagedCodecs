namespace ManagedCodecs.Aac.Sbr;

/// <summary>
/// cos and sin in additions, multiplications and divisions alone (a two-part reduction by pi/2, then a series on
/// [-pi/4, pi/4]), so that parametric stereo's mixing and phase factors are the same bits on every runtime, where
/// Math.Cos and Math.Sin can differ in their last one.
/// </summary>
internal static class PsMath
{
    // pi/2 in two parts: the first exact in 33 bits, so k times it is exact for the small k these angles need.
    private const double HalfPiHigh = 1.57079632673412561417e+00;
    private const double HalfPiLow = 6.07710050650619224932e-11;

    public static double Cos(double x)
    {
        var (quadrant, r) = Reduce(x);
        return quadrant switch
        {
            0 => CosSeries(r),
            1 => -SinSeries(r),
            2 => -CosSeries(r),
            _ => SinSeries(r),
        };
    }

    public static double Sin(double x)
    {
        var (quadrant, r) = Reduce(x);
        return quadrant switch
        {
            0 => SinSeries(r),
            1 => CosSeries(r),
            2 => -SinSeries(r),
            _ => -CosSeries(r),
        };
    }

    // x = k pi/2 + r with |r| <= pi/4 (or a hair more), and k mod 4.
    private static (int Quadrant, double R) Reduce(double x)
    {
        if (Math.Abs(x) > 1e6)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        double k = Math.Round(x / (Math.PI / 2));
        double r = (x - (k * HalfPiHigh)) - (k * HalfPiLow);
        return ((((int)k % 4) + 4) % 4, r);
    }

    // The Taylor series, nested, to terms far below double precision for |x| up to pi/4.
    private static double CosSeries(double x)
    {
        double square = x * x;
        double value = 1;
        for (int k = 11; k >= 1; k--)
        {
            value = 1 - (square / ((2 * k) - 1) / (2 * k) * value);
        }

        return value;
    }

    private static double SinSeries(double x)
    {
        double square = x * x;
        double value = 1;
        for (int k = 11; k >= 1; k--)
        {
            value = 1 - (square / (2 * k) / ((2 * k) + 1) * value);
        }

        return x * value;
    }
}
