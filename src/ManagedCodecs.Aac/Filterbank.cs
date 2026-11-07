using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET
using System.Runtime.Intrinsics;
#endif

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

    // The falling halves, w[2n - 1 - i] for i = 0 .. n - 1, so that windowing multiplies element by element.
    private static readonly double[] SineLongFall = [.. SineLong.Reverse()];
    private static readonly double[] SineShortFall = [.. SineShort.Reverse()];
    private static readonly double[] KbdLongFall = [.. KbdLong.Reverse()];
    private static readonly double[] KbdShortFall = [.. KbdShort.Reverse()];

    private readonly double[] _overlap = new double[1024];
    private readonly double[] _time = new double[2048];
    private readonly double[] _shortTime = new double[256];
    private int _previousShape;

    /// <summary>Turns 1024 spectral coefficients (8 x 128 for short windows) into 1024 output samples.</summary>
    public void Synthesize(WindowSequence sequence, int shape, ReadOnlySpan<double> spectrum, Span<float> output)
    {
        double[] longRise = _previousShape == 1 ? KbdLong : SineLong;
        double[] longFall = shape == 1 ? KbdLongFall : SineLongFall;
        double[] shortRisePrevious = _previousShape == 1 ? KbdShort : SineShort;
        double[] shortRise = shape == 1 ? KbdShort : SineShort;
        double[] shortFall = shape == 1 ? KbdShortFall : SineShortFall;
        var z = _time.AsSpan();

        if (sequence == WindowSequence.EightShort)
        {
            z.Clear();
            for (int w = 0; w < 8; w++)
            {
                Imdct.Inverse(spectrum.Slice(w * 128, 128), _shortTime);
                int start = 448 + (w * 128);
                Vectors.MultiplyAdd(z.Slice(start, 128), _shortTime.AsSpan(0, 128), w == 0 ? shortRisePrevious : shortRise);
                Vectors.MultiplyAdd(z.Slice(start + 128, 128), _shortTime.AsSpan(128, 128), shortFall);
            }
        }
        else
        {
            Imdct.Inverse(spectrum[..1024], z);
            switch (sequence)
            {
                case WindowSequence.OnlyLong:
                    Vectors.Multiply(z[..1024], longRise);
                    Vectors.Multiply(z[1024..], longFall);
                    break;

                case WindowSequence.LongStart:
                    // The rising long window; then 448 ones, a falling short window, and zeros.
                    Vectors.Multiply(z[..1024], longRise);
                    Vectors.Multiply(z.Slice(1472, 128), shortFall);
                    z.Slice(1600, 448).Clear();
                    break;

                case WindowSequence.LongStop:
                    // Zeros, a rising short window, 448 ones, then the falling long window.
                    z[..448].Clear();
                    Vectors.Multiply(z.Slice(448, 128), shortRisePrevious);
                    Vectors.Multiply(z[1024..], longFall);
                    break;
            }
        }

        const double Scale = 1.0 / 32768;
        Vectors.AddScaleToSingle(z[..1024], _overlap, Scale, output);
        z[1024..].CopyTo(_overlap);
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

/// <summary>Element-wise double loops of the filterbank, several elements at a time; each the scalar arithmetic, to the bit.</summary>
internal static class Vectors
{
    /// <summary>a[n] *= w[n].</summary>
    public static void Multiply(Span<double> a, ReadOnlySpan<double> w)
    {
        ref double x = ref MemoryMarshal.GetReference(a);
        ref double y = ref MemoryMarshal.GetReference(w);
        int n = 0;
#if NET
        if (Vector256.IsHardwareAccelerated)
        {
            for (; n <= a.Length - Vector256<double>.Count; n += Vector256<double>.Count)
            {
                (Vector256.LoadUnsafe(ref x, (nuint)n) * Vector256.LoadUnsafe(ref y, (nuint)n)).StoreUnsafe(ref x, (nuint)n);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            for (; n <= a.Length - Vector128<double>.Count; n += Vector128<double>.Count)
            {
                (Vector128.LoadUnsafe(ref x, (nuint)n) * Vector128.LoadUnsafe(ref y, (nuint)n)).StoreUnsafe(ref x, (nuint)n);
            }
        }
#endif

        for (; n < a.Length; n++)
        {
            a[n] *= w[n];
        }
    }

    /// <summary>a[n] += b[n] * w[n].</summary>
    public static void MultiplyAdd(Span<double> a, ReadOnlySpan<double> b, ReadOnlySpan<double> w)
    {
        ref double x = ref MemoryMarshal.GetReference(a);
        ref double u = ref MemoryMarshal.GetReference(b);
        ref double y = ref MemoryMarshal.GetReference(w);
        int n = 0;
#if NET
        if (Vector256.IsHardwareAccelerated)
        {
            for (; n <= a.Length - Vector256<double>.Count; n += Vector256<double>.Count)
            {
                (Vector256.LoadUnsafe(ref x, (nuint)n) + (Vector256.LoadUnsafe(ref u, (nuint)n) * Vector256.LoadUnsafe(ref y, (nuint)n))).StoreUnsafe(ref x, (nuint)n);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            for (; n <= a.Length - Vector128<double>.Count; n += Vector128<double>.Count)
            {
                (Vector128.LoadUnsafe(ref x, (nuint)n) + (Vector128.LoadUnsafe(ref u, (nuint)n) * Vector128.LoadUnsafe(ref y, (nuint)n))).StoreUnsafe(ref x, (nuint)n);
            }
        }
#endif

        for (; n < a.Length; n++)
        {
            a[n] += b[n] * w[n];
        }
    }

    /// <summary>
    /// a[n] = the sum over the terms t, in their order and from zero, of b[bOffsets[t] + n] * w[wOffsets[t] + n]: the
    /// same values as <see cref="MultiplyAdd"/> once a term on a cleared <paramref name="a"/>, with each sum kept in a
    /// register rather than stored and loaded again a term (the QMF banks' windows, ten terms of 32 or 64 values).
    /// </summary>
    public static void SumOfProducts(Span<double> a, ReadOnlySpan<double> b, ReadOnlySpan<int> bOffsets, ReadOnlySpan<double> w, ReadOnlySpan<int> wOffsets)
    {
        if (bOffsets.Length != wOffsets.Length)
        {
            throw new ArgumentException("Every term takes an offset into both inputs.");
        }

        for (int t = 0; t < bOffsets.Length; t++)
        {
            if ((uint)bOffsets[t] > (uint)(b.Length - a.Length) || (uint)wOffsets[t] > (uint)(w.Length - a.Length))
            {
                throw new ArgumentOutOfRangeException(nameof(bOffsets), "A term reaches past its input.");
            }
        }

        ref double x = ref MemoryMarshal.GetReference(a);
        ref double u = ref MemoryMarshal.GetReference(b);
        ref double y = ref MemoryMarshal.GetReference(w);
        int terms = bOffsets.Length;
        int n = 0;

        // Four vectors' sums at once: each is a chain of one add a term, so one alone would wait on the adds' latency.
#if NET
        if (Vector256.IsHardwareAccelerated)
        {
            int count = Vector256<double>.Count;
            for (; n <= a.Length - (4 * count); n += 4 * count)
            {
                var sum0 = Vector256<double>.Zero;
                var sum1 = Vector256<double>.Zero;
                var sum2 = Vector256<double>.Zero;
                var sum3 = Vector256<double>.Zero;
                for (int t = 0; t < terms; t++)
                {
                    ref double bt = ref Unsafe.Add(ref u, bOffsets[t] + n);
                    ref double wt = ref Unsafe.Add(ref y, wOffsets[t] + n);
                    sum0 += Vector256.LoadUnsafe(ref bt) * Vector256.LoadUnsafe(ref wt);
                    sum1 += Vector256.LoadUnsafe(ref bt, (nuint)count) * Vector256.LoadUnsafe(ref wt, (nuint)count);
                    sum2 += Vector256.LoadUnsafe(ref bt, (nuint)(2 * count)) * Vector256.LoadUnsafe(ref wt, (nuint)(2 * count));
                    sum3 += Vector256.LoadUnsafe(ref bt, (nuint)(3 * count)) * Vector256.LoadUnsafe(ref wt, (nuint)(3 * count));
                }

                sum0.StoreUnsafe(ref x, (nuint)n);
                sum1.StoreUnsafe(ref x, (nuint)(n + count));
                sum2.StoreUnsafe(ref x, (nuint)(n + (2 * count)));
                sum3.StoreUnsafe(ref x, (nuint)(n + (3 * count)));
            }

            for (; n <= a.Length - count; n += count)
            {
                var sum = Vector256<double>.Zero;
                for (int t = 0; t < terms; t++)
                {
                    sum += Vector256.LoadUnsafe(ref u, (nuint)(bOffsets[t] + n)) * Vector256.LoadUnsafe(ref y, (nuint)(wOffsets[t] + n));
                }

                sum.StoreUnsafe(ref x, (nuint)n);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            int count = Vector128<double>.Count;
            for (; n <= a.Length - (4 * count); n += 4 * count)
            {
                var sum0 = Vector128<double>.Zero;
                var sum1 = Vector128<double>.Zero;
                var sum2 = Vector128<double>.Zero;
                var sum3 = Vector128<double>.Zero;
                for (int t = 0; t < terms; t++)
                {
                    ref double bt = ref Unsafe.Add(ref u, bOffsets[t] + n);
                    ref double wt = ref Unsafe.Add(ref y, wOffsets[t] + n);
                    sum0 += Vector128.LoadUnsafe(ref bt) * Vector128.LoadUnsafe(ref wt);
                    sum1 += Vector128.LoadUnsafe(ref bt, (nuint)count) * Vector128.LoadUnsafe(ref wt, (nuint)count);
                    sum2 += Vector128.LoadUnsafe(ref bt, (nuint)(2 * count)) * Vector128.LoadUnsafe(ref wt, (nuint)(2 * count));
                    sum3 += Vector128.LoadUnsafe(ref bt, (nuint)(3 * count)) * Vector128.LoadUnsafe(ref wt, (nuint)(3 * count));
                }

                sum0.StoreUnsafe(ref x, (nuint)n);
                sum1.StoreUnsafe(ref x, (nuint)(n + count));
                sum2.StoreUnsafe(ref x, (nuint)(n + (2 * count)));
                sum3.StoreUnsafe(ref x, (nuint)(n + (3 * count)));
            }

            for (; n <= a.Length - count; n += count)
            {
                var sum = Vector128<double>.Zero;
                for (int t = 0; t < terms; t++)
                {
                    sum += Vector128.LoadUnsafe(ref u, (nuint)(bOffsets[t] + n)) * Vector128.LoadUnsafe(ref y, (nuint)(wOffsets[t] + n));
                }

                sum.StoreUnsafe(ref x, (nuint)n);
            }
        }
#endif

        // Four sums at once, by reference (the offsets are checked above): each term's offsets are read once for four
        // values, and each sum is the same chain as below. What the Unity build runs (Mono's profile:
        // docs/decisions/2026-10-31-unity-netstandard.md).
        ref int bOffset = ref MemoryMarshal.GetReference(bOffsets);
        ref int wOffset = ref MemoryMarshal.GetReference(wOffsets);
        for (; n <= a.Length - 4; n += 4)
        {
            double sum0 = 0;
            double sum1 = 0;
            double sum2 = 0;
            double sum3 = 0;
            for (int t = 0; t < terms; t++)
            {
                ref double bt = ref Unsafe.Add(ref u, Unsafe.Add(ref bOffset, t) + n);
                ref double wt = ref Unsafe.Add(ref y, Unsafe.Add(ref wOffset, t) + n);
                sum0 += bt * wt;
                sum1 += Unsafe.Add(ref bt, 1) * Unsafe.Add(ref wt, 1);
                sum2 += Unsafe.Add(ref bt, 2) * Unsafe.Add(ref wt, 2);
                sum3 += Unsafe.Add(ref bt, 3) * Unsafe.Add(ref wt, 3);
            }

            Unsafe.Add(ref x, n) = sum0;
            Unsafe.Add(ref x, n + 1) = sum1;
            Unsafe.Add(ref x, n + 2) = sum2;
            Unsafe.Add(ref x, n + 3) = sum3;
        }

        for (; n < a.Length; n++)
        {
            double sum = 0;
            for (int t = 0; t < terms; t++)
            {
                sum += b[bOffsets[t] + n] * w[wOffsets[t] + n];
            }

            a[n] = sum;
        }
    }

    /// <summary>sum[n] += (re[n] * re[n]) + (im[n] * im[n]).</summary>
    public static void AddMagnitudes(Span<double> sum, ReadOnlySpan<double> re, ReadOnlySpan<double> im)
    {
        ref double s = ref MemoryMarshal.GetReference(sum);
        ref double x = ref MemoryMarshal.GetReference(re);
        ref double y = ref MemoryMarshal.GetReference(im);
        int n = 0;
#if NET
        if (Vector256.IsHardwareAccelerated)
        {
            for (; n <= sum.Length - Vector256<double>.Count; n += Vector256<double>.Count)
            {
                var a = Vector256.LoadUnsafe(ref x, (nuint)n);
                var b = Vector256.LoadUnsafe(ref y, (nuint)n);
                (Vector256.LoadUnsafe(ref s, (nuint)n) + ((a * a) + (b * b))).StoreUnsafe(ref s, (nuint)n);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            for (; n <= sum.Length - Vector128<double>.Count; n += Vector128<double>.Count)
            {
                var a = Vector128.LoadUnsafe(ref x, (nuint)n);
                var b = Vector128.LoadUnsafe(ref y, (nuint)n);
                (Vector128.LoadUnsafe(ref s, (nuint)n) + ((a * a) + (b * b))).StoreUnsafe(ref s, (nuint)n);
            }
        }
#endif

        for (; n < sum.Length; n++)
        {
            sum[n] += (re[n] * re[n]) + (im[n] * im[n]);
        }
    }

    /// <summary>output[n] = (float)((a[n] + b[n]) * scale).</summary>
    public static void AddScaleToSingle(ReadOnlySpan<double> a, ReadOnlySpan<double> b, double scale, Span<float> output)
    {
        ref double x = ref MemoryMarshal.GetReference(a);
        ref double y = ref MemoryMarshal.GetReference(b);
        ref float o = ref MemoryMarshal.GetReference(output);
        int n = 0;
#if NET
        if (Vector256.IsHardwareAccelerated)
        {
            var sv = Vector256.Create(scale);
            for (; n <= a.Length - (2 * Vector256<double>.Count); n += 2 * Vector256<double>.Count)
            {
                var low = (Vector256.LoadUnsafe(ref x, (nuint)n) + Vector256.LoadUnsafe(ref y, (nuint)n)) * sv;
                var high = (Vector256.LoadUnsafe(ref x, (nuint)(n + 4)) + Vector256.LoadUnsafe(ref y, (nuint)(n + 4))) * sv;
                Vector256.Narrow(low, high).StoreUnsafe(ref o, (nuint)n);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            var sv = Vector128.Create(scale);
            for (; n <= a.Length - (2 * Vector128<double>.Count); n += 2 * Vector128<double>.Count)
            {
                var low = (Vector128.LoadUnsafe(ref x, (nuint)n) + Vector128.LoadUnsafe(ref y, (nuint)n)) * sv;
                var high = (Vector128.LoadUnsafe(ref x, (nuint)(n + 2)) + Vector128.LoadUnsafe(ref y, (nuint)(n + 2))) * sv;
                Vector128.Narrow(low, high).StoreUnsafe(ref o, (nuint)n);
            }
        }
#endif

        for (; n < a.Length; n++)
        {
            output[n] = (float)((a[n] + b[n]) * scale);
        }
    }
}
