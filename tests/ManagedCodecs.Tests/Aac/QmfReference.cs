using ManagedCodecs.Aac.Sbr;

namespace ManagedCodecs.Tests.Aac;

/// <summary>
/// The 32-band complex QMF analysis bank (ISO/IEC 14496-3 4.6.18.4.1): each call takes 32 samples of the core decoder's
/// output and gives one time slot of 32 complex subband samples. The bank keeps the last 320 samples across calls, so
/// one instance serves one channel for the whole stream. Written as the spec's sums: the reference the FFT-based
/// <see cref="QmfAnalysis"/> is compared with.
/// </summary>
internal sealed class QmfAnalysisReference
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
/// The complex QMF synthesis bank (ISO/IEC 14496-3 4.6.18.4.2): each call takes one time slot of 64 complex subband
/// samples and gives 64 output samples, at twice the core's rate, keeping 1280 values across calls; downsampled (for
/// SBR at the core's own rate), 32 subband samples in and 32 out, keeping 640, with every other window coefficient.
/// Written as the spec's sums: the reference the FFT-based <see cref="QmfSynthesis"/> is compared with.
/// </summary>
internal sealed class QmfSynthesisReference
{
    private readonly int _bands;

    // cos and sin of pi/128 (k + 0.5)((128/B) n - 255), times the spec's factor 1/64, per band k and n in 0..2B-1: of
    // pi/256 times (2k + 1)((128/B) n - 255) (downsampled, the full bank's kernel at every other n).
    private readonly double[] _cos;
    private readonly double[] _sin;
    private readonly double[] _v;

    public QmfSynthesisReference(bool downsampled = false)
    {
        _bands = downsampled ? 32 : 64;
        _cos = Kernel(QmfTrig.CosPi256);
        _sin = Kernel(QmfTrig.SinPi256);
        _v = new double[20 * _bands];
    }

    /// <summary>One time slot: B complex subband samples in, B output samples (oldest first) out.</summary>
    public void Process(ReadOnlySpan<double> real, ReadOnlySpan<double> imaginary, Span<double> output)
    {
        int bands = _bands;
        var v = _v.AsSpan();
        v[..(18 * bands)].CopyTo(v[(2 * bands)..]);

        // v(n) = the real part of the sum over k of X(k) exp(i pi/128 (k + 0.5)((128/B) n - 255)) / 64.
        for (int n = 0; n < 2 * bands; n++)
        {
            double sum = 0;
            for (int k = 0; k < bands; k++)
            {
                sum += (real[k] * _cos[(2 * bands * k) + n]) - (imaginary[k] * _sin[(2 * bands * k) + n]);
            }

            v[n] = sum;
        }

        // g takes, of each 4B values of v, the first and the last B; w(n) = g(n) c(n (64 / B)); each output sample sums
        // ten of w.
        var c = QmfWindow.Coefficients;
        int step = 64 / bands;
        for (int n = 0; n < bands; n++)
        {
            double sum = 0;
            for (int i = 0; i < 5; i++)
            {
                sum += v[(4 * bands * i) + n] * c[step * ((2 * bands * i) + n)];
                sum += v[(4 * bands * i) + (3 * bands) + n] * c[step * ((2 * bands * i) + bands + n)];
            }

            output[n] = sum;
        }
    }

    private double[] Kernel(Func<int, double> f)
    {
        int bands = _bands;
        var table = new double[bands * 2 * bands];
        for (int k = 0; k < bands; k++)
        {
            for (int n = 0; n < 2 * bands; n++)
            {
                table[(2 * bands * k) + n] = f(((2 * k) + 1) * (((128 / bands) * n) - 255)) / 64;
            }
        }

        return table;
    }
}
