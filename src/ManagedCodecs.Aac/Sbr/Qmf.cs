namespace ManagedCodecs.Aac.Sbr;

/// <summary>
/// The 32-band complex QMF analysis bank (ISO/IEC 14496-3 4.6.18.4.1): each call takes 32 samples of the core decoder's
/// output and gives one time slot of 32 complex subband samples. The bank keeps the last 320 samples across calls, so
/// one instance serves one channel for the whole stream. Written as the spec's sums.
/// </summary>
internal sealed class QmfAnalysis
{
    public const int Bands = 32;

    // cos and sin of pi/64 (k + 0.5)(2n - 0.5), times the spec's factor 2, per band k and n in 0..63: of pi/256 times
    // (2k + 1)(4n - 1).
    private static readonly double[] Cos = Kernel(QmfTrig.CosPi256);
    private static readonly double[] Sin = Kernel(QmfTrig.SinPi256);

    // x: the newest sample at x[0].
    private readonly double[] _x = new double[320];

    /// <summary>One time slot: 32 new samples (oldest first) in, 32 complex subband samples out.</summary>
    public void Process(ReadOnlySpan<double> input, Span<double> real, Span<double> imaginary)
    {
        var x = _x.AsSpan();
        x[..288].CopyTo(x[32..]);
        for (int n = 0; n < 32; n++)
        {
            x[31 - n] = input[n];
        }

        // z(n) = x(n) c(2n), u(n) = the sum of z(n + 64 j) over j in 0..4.
        Span<double> u = stackalloc double[64];
        var c = QmfWindow.Coefficients;
        for (int n = 0; n < 64; n++)
        {
            double sum = 0;
            for (int j = 0; j < 5; j++)
            {
                sum += x[n + (64 * j)] * c[2 * (n + (64 * j))];
            }

            u[n] = sum;
        }

        for (int k = 0; k < Bands; k++)
        {
            double re = 0;
            double im = 0;
            for (int n = 0; n < 64; n++)
            {
                re += u[n] * Cos[(64 * k) + n];
                im += u[n] * Sin[(64 * k) + n];
            }

            real[k] = re;
            imaginary[k] = im;
        }
    }

    private static double[] Kernel(Func<int, double> f)
    {
        var table = new double[Bands * 64];
        for (int k = 0; k < Bands; k++)
        {
            for (int n = 0; n < 64; n++)
            {
                table[(64 * k) + n] = 2 * f(((2 * k) + 1) * ((4 * n) - 1));
            }
        }

        return table;
    }
}

/// <summary>
/// The 64-band complex QMF synthesis bank (ISO/IEC 14496-3 4.6.18.4.2): each call takes one time slot of 64 complex
/// subband samples and gives 64 output samples, at twice the core's rate. The bank keeps 1280 values across calls.
/// Written as the spec's sums.
/// </summary>
internal sealed class QmfSynthesis
{
    public const int Bands = 64;

    // cos and sin of pi/128 (k + 0.5)(2n - 255), times the spec's factor 1/64, per band k and n in 0..127: of pi/256
    // times (2k + 1)(2n - 255).
    private static readonly double[] Cos = Kernel(QmfTrig.CosPi256);
    private static readonly double[] Sin = Kernel(QmfTrig.SinPi256);

    private readonly double[] _v = new double[1280];

    /// <summary>One time slot: 64 complex subband samples in, 64 output samples (oldest first) out.</summary>
    public void Process(ReadOnlySpan<double> real, ReadOnlySpan<double> imaginary, Span<double> output)
    {
        var v = _v.AsSpan();
        v[..1152].CopyTo(v[128..]);

        // v(n) = the real part of the sum over k of X(k) exp(i pi/128 (k + 0.5)(2n - 255)) / 64.
        for (int n = 0; n < 128; n++)
        {
            double sum = 0;
            for (int k = 0; k < Bands; k++)
            {
                sum += (real[k] * Cos[(128 * k) + n]) - (imaginary[k] * Sin[(128 * k) + n]);
            }

            v[n] = sum;
        }

        // g takes, of each 256 values of v, the first and the last 64; w(n) = g(n) c(n); each output sample sums ten of w.
        var c = QmfWindow.Coefficients;
        for (int n = 0; n < 64; n++)
        {
            double sum = 0;
            for (int i = 0; i < 5; i++)
            {
                sum += v[(256 * i) + n] * c[(128 * i) + n];
                sum += v[(256 * i) + 192 + n] * c[(128 * i) + 64 + n];
            }

            output[n] = sum;
        }
    }

    private static double[] Kernel(Func<int, double> f)
    {
        var table = new double[Bands * 128];
        for (int k = 0; k < Bands; k++)
        {
            for (int n = 0; n < 128; n++)
            {
                table[(128 * k) + n] = f(((2 * k) + 1) * ((2 * n) - 255)) / 64;
            }
        }

        return table;
    }
}

/// <summary>
/// cos and sin of pi/256 times an integer, from the symmetries of the circle and a series on the first octant, in
/// additions, multiplications and divisions alone: the same bits on every runtime, where Math.Cos and Math.Sin can
/// differ in their last one (and the HF generator's prediction, nearly singular for a pure tone, can make that show in
/// the output).
/// </summary>
internal static class QmfTrig
{
    public static double CosPi256(int m)
    {
        m = ((m % 512) + 512) % 512;
        bool negate = false;
        if (m >= 256)
        {
            m -= 256; // cos(x + pi) = -cos(x)
            negate = true;
        }

        if (m > 128)
        {
            m = 256 - m; // cos(pi - x) = -cos(x)
            negate = !negate;
        }

        double value = m <= 64 ? Cos(Math.PI * m / 256) : Sin(Math.PI * (128 - m) / 256); // cos(pi/2 - x) = sin(x)
        return negate ? -value : value;
    }

    public static double SinPi256(int m) => CosPi256(m - 128); // sin(x) = cos(x - pi/2)

    // The Taylor series of cos and sin, nested (1 - x^2/(1*2) (1 - x^2/(3*4) (...)) and x (1 - x^2/(2*3) (...))), to
    // terms far below double precision for x up to pi/4.
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
