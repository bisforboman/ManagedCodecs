namespace ManagedCodecs.Aac;

/// <summary>Window sequences of ics_info (Table 4.153).</summary>
internal enum WindowSequence
{
    OnlyLong = 0,
    LongStart = 1,
    EightShort = 2,
    LongStop = 3,
}

/// <summary>
/// Synthesis filterbank (ISO/IEC 14496-3 4.6.18): IMDCT, windowing with sine or KBD windows,
/// and overlap-add, for one channel. Output is scaled from the spec's 16-bit range to [-1, 1].
/// </summary>
internal sealed class Filterbank
{
    // Rising window halves; the falling half of a window of length 2n is w[2n - 1 - i].
    public static readonly double[] SineLong = Sine(1024);
    public static readonly double[] SineShort = Sine(128);
    public static readonly double[] KbdLong = Kbd(1024, 4);
    public static readonly double[] KbdShort = Kbd(128, 6);

    private readonly double[] _overlap = new double[1024];
    private readonly double[] _time = new double[2048];
    private readonly double[] _shortTime = new double[256];
    private int _previousShape;

    /// <summary>
    /// Turns 1024 spectral coefficients (8 x 128 for short windows) into 1024 output samples.
    /// </summary>
    public void Synthesize(WindowSequence sequence, int shape, ReadOnlySpan<double> spectrum, Span<float> output)
    {
        double[] longRise = _previousShape == 1 ? KbdLong : SineLong;
        double[] longFall = shape == 1 ? KbdLong : SineLong;
        double[] shortRisePrevious = _previousShape == 1 ? KbdShort : SineShort;
        double[] shortCurrent = shape == 1 ? KbdShort : SineShort;
        var z = _time;

        if (sequence == WindowSequence.EightShort)
        {
            Array.Clear(z);
            for (int w = 0; w < 8; w++)
            {
                Imdct.Inverse(spectrum.Slice(w * 128, 128), _shortTime);
                double[] rise = w == 0 ? shortRisePrevious : shortCurrent;
                int start = 448 + (w * 128);
                for (int n = 0; n < 128; n++)
                {
                    z[start + n] += _shortTime[n] * rise[n];
                    z[start + 128 + n] += _shortTime[128 + n] * shortCurrent[127 - n];
                }
            }
        }
        else
        {
            Imdct.Inverse(spectrum[..1024], z);
            switch (sequence)
            {
                case WindowSequence.OnlyLong:
                    for (int n = 0; n < 1024; n++)
                    {
                        z[n] *= longRise[n];
                        z[1024 + n] *= longFall[1023 - n];
                    }

                    break;

                case WindowSequence.LongStart:
                    for (int n = 0; n < 1024; n++)
                    {
                        z[n] *= longRise[n];
                    }

                    // 448 ones, a falling short window, then zeros.
                    for (int n = 0; n < 128; n++)
                    {
                        z[1472 + n] *= shortCurrent[127 - n];
                    }

                    Array.Clear(z, 1600, 448);
                    break;

                case WindowSequence.LongStop:
                    // Zeros, a rising short window, 448 ones, then the falling long window.
                    Array.Clear(z, 0, 448);
                    for (int n = 0; n < 128; n++)
                    {
                        z[448 + n] *= shortRisePrevious[n];
                    }

                    for (int n = 0; n < 1024; n++)
                    {
                        z[1024 + n] *= longFall[1023 - n];
                    }

                    break;
            }
        }

        const double Scale = 1.0 / 32768;
        for (int n = 0; n < 1024; n++)
        {
            output[n] = (float)((z[n] + _overlap[n]) * Scale);
            _overlap[n] = z[1024 + n];
        }

        _previousShape = shape;
    }

    private static double[] Sine(int half)
    {
        var w = new double[half];
        for (int n = 0; n < half; n++)
        {
            w[n] = Math.Sin(Math.PI / (2 * half) * (n + 0.5));
        }

        return w;
    }

    /// <summary>Kaiser-Bessel derived window (4.6.11.3.1), rising half of a window of length 2 * half.</summary>
    private static double[] Kbd(int half, double alpha)
    {
        // Kaiser kernel W'(p) for p = 0 .. N/2, with N = 2 * half.
        var kernel = new double[half + 1];
        for (int p = 0; p <= half; p++)
        {
            double t = (p - (half / 2.0)) / (half / 2.0);
            kernel[p] = BesselI0(Math.PI * alpha * Math.Sqrt(1 - (t * t)));
        }

        double total = kernel.Sum();
        var w = new double[half];
        double sum = 0;
        for (int n = 0; n < half; n++)
        {
            sum += kernel[n];
            w[n] = Math.Sqrt(sum / total);
        }

        return w;
    }

    /// <summary>Modified Bessel function of the first kind, order 0, by its power series.</summary>
    private static double BesselI0(double x)
    {
        double sum = 1;
        double term = 1;
        double quarter = x * x / 4;
        for (int k = 1; k < 64; k++)
        {
            term *= quarter / (k * k);
            sum += term;
            if (term < sum * 1e-17)
            {
                break;
            }
        }

        return sum;
    }
}

/// <summary>The IMDCT of 4.6.18.2, computed straight from its definition.</summary>
internal static class Imdct
{
    private static readonly double[] CosLong = CosTable(2048);
    private static readonly double[] CosShort = CosTable(256);

    /// <summary>
    /// x[n] = 2/N * sum over k of spec[k] * cos(2 pi / N * (n + n0) * (k + 1/2)), n0 = (N/2 + 1) / 2,
    /// for n = 0 .. N - 1, where N = 2 * spec.Length.
    /// </summary>
    public static void Inverse(ReadOnlySpan<double> spec, Span<double> output)
    {
        int half = spec.Length;
        int n = 2 * half;
        double[] cos = n == 2048 ? CosLong : CosShort;
        int mask = (4 * n) - 1;
        double scale = 2.0 / n;

        // The angle is 2 pi * m / (4N) with m = (2n + N/2 + 1)(2k + 1), an integer.
        for (int i = 0; i < n; i++)
        {
            int a = (2 * i) + half + 1;
            double sum = 0;
            for (int k = 0; k < half; k++)
            {
                double s = spec[k];
                if (s != 0)
                {
                    sum += s * cos[(a * ((2 * k) + 1)) & mask];
                }
            }

            output[i] = sum * scale;
        }
    }

    private static double[] CosTable(int n)
    {
        var table = new double[4 * n];
        for (int m = 0; m < table.Length; m++)
        {
            table[m] = Math.Cos(2 * Math.PI * m / (4 * n));
        }

        return table;
    }
}
