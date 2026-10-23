namespace ManagedCodecs.Aac.Sbr;

/// <summary>
/// The 32-band complex QMF analysis bank (ISO/IEC 14496-3 4.6.18.4.1): each call takes 32 samples of the core decoder's
/// output and gives one time slot of 32 complex subband samples. The bank keeps the last 320 samples across calls, so
/// one instance serves one channel for the whole stream.
/// </summary>
/// <remarks>
/// The spec's sum X(k) = the sum over n in 0..63 of u(n) 2 exp(i pi/64 (k + 0.5)(2n - 0.5)) is a 64-point DFT: the
/// exponent splits into 2 pi k n / 64 + pi n / 64 - pi (2k + 1) / 256, so X(k) = 2 exp(-i pi (2k + 1) / 256) times the
/// DFT of u(n) exp(i pi n / 64) at k. Tests compare it with the sums on random input.
/// </remarks>
internal sealed class QmfAnalysis
{
    public const int Bands = 32;

    private static readonly QmfFft Fft = new(64);

    // exp(i pi n / 64) for the input, and 2 exp(-i pi (2k + 1) / 256) for the output.
    private static readonly double[] InCos = [.. Enumerable.Range(0, 64).Select(n => QmfTrig.CosPi256(4 * n))];
    private static readonly double[] InSin = [.. Enumerable.Range(0, 64).Select(n => QmfTrig.SinPi256(4 * n))];
    private static readonly double[] OutCos = [.. Enumerable.Range(0, Bands).Select(k => 2 * QmfTrig.CosPi256(-((2 * k) + 1)))];
    private static readonly double[] OutSin = [.. Enumerable.Range(0, Bands).Select(k => 2 * QmfTrig.SinPi256(-((2 * k) + 1)))];

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

        // z(n) = x(n) c(2n), u(n) = the sum of z(n + 64 j) over j in 0..4, turned by exp(i pi n / 64).
        Span<double> re = stackalloc double[64];
        Span<double> im = stackalloc double[64];
        var c = QmfWindow.Coefficients;
        for (int n = 0; n < 64; n++)
        {
            double sum = 0;
            for (int j = 0; j < 5; j++)
            {
                sum += x[n + (64 * j)] * c[2 * (n + (64 * j))];
            }

            re[n] = sum * InCos[n];
            im[n] = sum * InSin[n];
        }

        Fft.Transform(re, im);
        for (int k = 0; k < Bands; k++)
        {
            real[k] = (re[k] * OutCos[k]) - (im[k] * OutSin[k]);
            imaginary[k] = (re[k] * OutSin[k]) + (im[k] * OutCos[k]);
        }
    }
}

/// <summary>
/// The 64-band complex QMF synthesis bank (ISO/IEC 14496-3 4.6.18.4.2): each call takes one time slot of 64 complex
/// subband samples and gives 64 output samples, at twice the core's rate. The bank keeps 1280 values across calls.
/// </summary>
/// <remarks>
/// The spec's v(n) = the real part of the sum over k of X(k) exp(i pi/128 (k + 0.5)(2n - 255)) / 64 is a 128-point DFT:
/// the exponent splits into 2 pi k n / 128 + pi (2n - 255) / 256 - 510 pi k / 256, so v(n) is the real part of
/// exp(i pi (2n - 255) / 256) times the DFT of X(k) exp(-i 510 pi k / 256) (zero for k from 64) at n, over 64. Tests
/// compare it with the sums on random input.
/// </remarks>
internal sealed class QmfSynthesis
{
    public const int Bands = 64;

    private static readonly QmfFft Fft = new(128);

    // exp(-i 510 pi k / 256) for the input, and exp(i pi (2n - 255) / 256) / 64 for the output.
    private static readonly double[] InCos = [.. Enumerable.Range(0, Bands).Select(k => QmfTrig.CosPi256(-510 * k))];
    private static readonly double[] InSin = [.. Enumerable.Range(0, Bands).Select(k => QmfTrig.SinPi256(-510 * k))];
    private static readonly double[] OutCos = [.. Enumerable.Range(0, 128).Select(n => QmfTrig.CosPi256((2 * n) - 255) / 64)];
    private static readonly double[] OutSin = [.. Enumerable.Range(0, 128).Select(n => QmfTrig.SinPi256((2 * n) - 255) / 64)];

    private readonly double[] _v = new double[1280];

    /// <summary>One time slot: 64 complex subband samples in, 64 output samples (oldest first) out.</summary>
    public void Process(ReadOnlySpan<double> real, ReadOnlySpan<double> imaginary, Span<double> output)
    {
        var v = _v.AsSpan();
        v[..1152].CopyTo(v[128..]);

        Span<double> re = stackalloc double[128];
        Span<double> im = stackalloc double[128];
        for (int k = 0; k < Bands; k++)
        {
            re[k] = (real[k] * InCos[k]) - (imaginary[k] * InSin[k]);
            im[k] = (real[k] * InSin[k]) + (imaginary[k] * InCos[k]);
        }

        re[Bands..].Clear();
        im[Bands..].Clear();
        Fft.Transform(re, im);
        for (int n = 0; n < 128; n++)
        {
            v[n] = (re[n] * OutCos[n]) - (im[n] * OutSin[n]);
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
}

/// <summary>
/// An in-place radix-2 FFT with a positive exponent (y(k) = the sum over n of x(n) exp(2 pi i k n / N)), for the QMF
/// banks' 64 and 128 points, its twiddle factors from <see cref="QmfTrig"/> so that it gives the same bits everywhere.
/// </summary>
internal sealed class QmfFft
{
    private readonly int _length;

    // The bit-reversal permutation as the pairs it swaps, and each stage's twiddle factors in a row of their own
    // (a stage of half-span h uses exp(2 pi i j / (2h)) for j < h, kept from index h on).
    private readonly int[] _swaps;
    private readonly double[] _cos;
    private readonly double[] _sin;

    public QmfFft(int length)
    {
        _length = length;
        int bits = System.Numerics.BitOperations.Log2((uint)length);
        var swaps = new List<int>();
        for (int i = 0; i < length; i++)
        {
            int j = (int)(ReverseBits((uint)i) >> (32 - bits));
            if (j > i)
            {
                swaps.Add(i);
                swaps.Add(j);
            }
        }

        _swaps = [.. swaps];
        _cos = new double[length];
        _sin = new double[length];
        for (int half = 1; half < length; half *= 2)
        {
            for (int j = 0; j < half; j++)
            {
                _cos[half + j] = QmfTrig.CosPi256(256 / half * j);
                _sin[half + j] = QmfTrig.SinPi256(256 / half * j);
            }
        }
    }

    public void Transform(Span<double> re, Span<double> im)
    {
        re = re[.._length];
        im = im[.._length];
        for (int s = 0; s < _swaps.Length; s += 2)
        {
            int i = _swaps[s];
            int j = _swaps[s + 1];
            (re[i], re[j]) = (re[j], re[i]);
            (im[i], im[j]) = (im[j], im[i]);
        }

        // The first stage's twiddle factor is 1.
        for (int a = 0; a < _length; a += 2)
        {
            double tr = re[a + 1];
            double ti = im[a + 1];
            re[a + 1] = re[a] - tr;
            im[a + 1] = im[a] - ti;
            re[a] += tr;
            im[a] += ti;
        }

        for (int half = 2; half < _length; half *= 2)
        {
            var cos = _cos.AsSpan(half, half);
            var sin = _sin.AsSpan(half, half);
            for (int start = 0; start < _length; start += 2 * half)
            {
                var re0 = re.Slice(start, half);
                var im0 = im.Slice(start, half);
                var re1 = re.Slice(start + half, half);
                var im1 = im.Slice(start + half, half);
                for (int j = 0; j < half; j++)
                {
                    double tr = (re1[j] * cos[j]) - (im1[j] * sin[j]);
                    double ti = (re1[j] * sin[j]) + (im1[j] * cos[j]);
                    re1[j] = re0[j] - tr;
                    im1[j] = im0[j] - ti;
                    re0[j] += tr;
                    im0[j] += ti;
                }
            }
        }
    }

    private static uint ReverseBits(uint value)
    {
        uint result = 0;
        for (int i = 0; i < 32; i++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }

        return result;
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
