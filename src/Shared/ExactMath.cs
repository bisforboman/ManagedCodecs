using System.Numerics;

// Linked into several projects, each compiling it into its own namespace (as Vlc.cs).
#if MANAGEDCODECS_H264
namespace ManagedCodecs.H264;
#elif MANAGEDCODECS_AAC
namespace ManagedCodecs.Aac;
#elif MANAGEDCODECS_MP3
namespace ManagedCodecs.Mp3;
#endif

/// <summary>
/// Powers and cosines in additions, multiplications and divisions alone, so that tables built from them have the same
/// bits on every runtime: Math.Pow, Math.Cos and Math.Sin differ in their last bit between .NET and the WebAssembly
/// runtime (the AAC decoder's CLAUDE.md), which bit-exact checks of one build against another then see.
/// </summary>
internal static class ExactMath
{
    // 2^0, 2^0.25, 2^0.5 and 2^0.75, correctly rounded.
    private static readonly double[] QuarterPowers = [1.0, 1.1892071150027210667, 1.4142135623730950488, 1.6817928305074290861];

    /// <summary>
    /// x^(4/3) as x cbrt(x), the cube root by Newton's method from a power of two above it: within 1.5 units in the last
    /// place of the exact value, and the same bits on every runtime.
    /// </summary>
    public static double FourThirdsPower(int x)
    {
        if (x == 0)
        {
            return 0;
        }

        double y = Math.ScaleB(1.0, (32 - BitOperations.LeadingZeroCount((uint)x) + 2) / 3);
        for (int i = 0; i < 8; i++)
        {
            y -= ((y * y * y) - x) / (3 * y * y);
        }

        return x * y;
    }

    /// <summary>2^(<paramref name="quarters"/> / 4), correctly rounded.</summary>
    public static double QuarterPowerOfTwo(int quarters) => Math.ScaleB(QuarterPowers[quarters & 3], quarters >> 2);

    /// <summary>cos(pi <paramref name="m"/> / <paramref name="d"/>), reduced to an angle of at most pi / 4 in integers.</summary>
    public static double CosPi(long m, long d)
    {
        // Angles in units of pi / (4 d): one full turn is 8 d of them.
        long turn = 8 * d;
        long a = (((4 * m) % turn) + turn) % turn;
        bool negate = false;
        if (a > 4 * d)
        {
            a = turn - a; // cos(-x) = cos(x)
        }

        if (a > 2 * d)
        {
            a = (4 * d) - a; // cos(pi - x) = -cos(x)
            negate = true;
        }

        // Now 0 <= a <= 2d, an angle of 0 to pi / 2.
        double value = a <= d ? Cos(Math.PI * a / (4 * d)) : Sin(Math.PI * ((2 * d) - a) / (4 * d));
        return negate ? -value : value;
    }

    /// <summary>sin(pi <paramref name="m"/> / <paramref name="d"/>).</summary>
    public static double SinPi(long m, long d) => CosPi((2 * m) - d, 2 * d); // sin(x) = cos(x - pi / 2)

    // The Taylor series, nested, to terms far below double precision for x up to pi / 4.
    private static double Cos(double x)
    {
        double square = x * x;
        double value = 1;
        for (int k = 11; k >= 1; k--)
        {
            value = 1 - (square / ((2 * k) - 1) / (2 * k) * value);
        }

        return value;
    }

    private static double Sin(double x)
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
