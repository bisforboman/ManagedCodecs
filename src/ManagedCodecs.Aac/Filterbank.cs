using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET
using System.Runtime.Intrinsics;
#endif
using ManagedCodecs.Aac.Sbr;

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

/// <summary>
/// The IMDCT of 4.6.18.2. <see cref="Inverse"/> computes it as a DCT-IV of the N/2 coefficients,
/// folded out to N samples, with the DCT-IV done by an N/4-point complex FFT between two twiddle
/// passes. <see cref="InverseReference"/> evaluates the definition directly and is kept for tests.
/// </summary>
internal static class Imdct
{
    private static readonly Plan Long = new(1024);
    private static readonly Plan Short = new(128);
    private static readonly double[] CosLong = CosTable(2048);
    private static readonly double[] CosShort = CosTable(256);

    /// <summary>
    /// x[n] = 2/N * sum over k of spec[k] * cos(2 pi / N * (n + n0) * (k + 1/2)), n0 = (N/2 + 1) / 2,
    /// for n = 0 .. N - 1, where N = 2 * spec.Length (2048 or 256).
    /// </summary>
    [SkipLocalsInit]
    public static void Inverse(ReadOnlySpan<double> spec, Span<double> output)
    {
#if NET
        if (!Vector128.IsHardwareAccelerated)
#endif
        {
            InverseScalar(spec, output);
            return;
        }
#if NET

        var plan = spec.Length == 1024 ? Long : Short;
        int m = plan.Size;
        int quarter = m / 2;
        if (spec.Length < m || output.Length < 2 * m)
        {
            throw new ArgumentException("The spectrum and the output are too short for the transform.");
        }

        // Every element of these is written before it is read: the pre-twiddle fills the FFT's input, the FFT's first
        // pass all of re and im, and the post-twiddle all of u.
        Span<double> sourceRe = stackalloc double[quarter];
        Span<double> sourceIm = stackalloc double[quarter];
        Span<double> re = stackalloc double[quarter];
        Span<double> im = stackalloc double[quarter];

        // The pre-twiddle two j at a time, X[2j] and X[M-1-2j] gathered by zips: the scalar arithmetic in its order.
        ref double x = ref MemoryMarshal.GetReference(spec);
        ref double preCos = ref MemoryMarshal.GetArrayDataReference(plan.PreCos);
        ref double preSin = ref MemoryMarshal.GetArrayDataReference(plan.PreSin);
        ref double sourceReRef = ref MemoryMarshal.GetReference(sourceRe);
        ref double sourceImRef = ref MemoryMarshal.GetReference(sourceIm);
        for (int j = 0; j < quarter; j += 2)
        {
            var a = QmfState.Zip(Vector128.LoadUnsafe(ref x, (nuint)(2 * j)), Vector128.LoadUnsafe(ref x, (nuint)((2 * j) + 2))).Low;
            var b = QmfState.Zip(Vector128.LoadUnsafe(ref x, (nuint)(m - 2 - (2 * j))), Vector128.LoadUnsafe(ref x, (nuint)(m - 4 - (2 * j)))).High;
            var c = Vector128.LoadUnsafe(ref preCos, (nuint)j);
            var sn = Vector128.LoadUnsafe(ref preSin, (nuint)j);
            ((a * c) + (b * sn)).StoreUnsafe(ref sourceReRef, (nuint)j);
            ((b * c) - (a * sn)).StoreUnsafe(ref sourceImRef, (nuint)j);
        }

        plan.Transform(sourceRe, sourceIm, re, im);

        // The post-twiddle two l at a time, from both ends at once: l, l + 1 and their mirrors p = quarter - 2 - l,
        // p + 1. u[2l + 1] is the mirror's -Im, u[M-1-2(quarter-1-l)], so a zip of l's Re with the mirror's -Im
        // reversed gives u[2l .. 2l + 3] whole. The scalar loop's products and sums, in its order.
        Span<double> u = stackalloc double[m];
        ref double postCos = ref MemoryMarshal.GetArrayDataReference(plan.PostCos);
        ref double postSin = ref MemoryMarshal.GetArrayDataReference(plan.PostSin);
        ref double reRef = ref MemoryMarshal.GetReference(re);
        ref double imRef = ref MemoryMarshal.GetReference(im);
        ref double uRef = ref MemoryMarshal.GetReference(u);
        for (int l = 0; l < quarter / 2; l += 2)
        {
            int p = quarter - 2 - l;
            var reL = Vector128.LoadUnsafe(ref reRef, (nuint)l);
            var imL = Vector128.LoadUnsafe(ref imRef, (nuint)l);
            var cL = Vector128.LoadUnsafe(ref postCos, (nuint)l);
            var sL = Vector128.LoadUnsafe(ref postSin, (nuint)l);
            var reP = Vector128.LoadUnsafe(ref reRef, (nuint)p);
            var imP = Vector128.LoadUnsafe(ref imRef, (nuint)p);
            var cP = Vector128.LoadUnsafe(ref postCos, (nuint)p);
            var sP = Vector128.LoadUnsafe(ref postSin, (nuint)p);
            var evenL = (reL * cL) + (imL * sL);
            var oddL = -((imL * cL) - (reL * sL));
            var evenP = (reP * cP) + (imP * sP);
            var oddP = -((imP * cP) - (reP * sP));
            var (low, high) = QmfState.Zip(evenL, QmfState.Reverse(oddP));
            low.StoreUnsafe(ref uRef, (nuint)(2 * l));
            high.StoreUnsafe(ref uRef, (nuint)((2 * l) + 2));
            (low, high) = QmfState.Zip(evenP, QmfState.Reverse(oddL));
            low.StoreUnsafe(ref uRef, (nuint)(2 * p));
            high.StoreUnsafe(ref uRef, (nuint)((2 * p) + 2));
        }

        // The fold, two samples a vector: the same products as the scalar loops, the reversed range read backwards.
        var scale = Vector128.Create(1.0 / m);
        int half = m / 2;
        ref double y = ref MemoryMarshal.GetReference(output);
        for (int n = 0; n < half; n += 2)
        {
            (Vector128.LoadUnsafe(ref uRef, (nuint)(n + half)) * scale).StoreUnsafe(ref y, (nuint)n);
        }

        for (int n = half; n < 3 * half; n += 2)
        {
            // output[n] and output[n + 1] are -u[3M/2 - 1 - n] and -u[3M/2 - 2 - n].
            var v = Vector128.LoadUnsafe(ref uRef, (nuint)((3 * half) - 2 - n));
            ((-QmfState.Reverse(v)) * scale).StoreUnsafe(ref y, (nuint)n);
        }

        for (int n = 3 * half; n < 2 * m; n += 2)
        {
            ((-Vector128.LoadUnsafe(ref uRef, (nuint)(n - (3 * half)))) * scale).StoreUnsafe(ref y, (nuint)n);
        }
#endif
    }

    /// <summary>The scalar <see cref="Inverse"/>: without vector hardware, and the vector one's reference.</summary>
    internal static void InverseScalar(ReadOnlySpan<double> spec, Span<double> output)
    {
        var plan = spec.Length == 1024 ? Long : Short;
        int m = plan.Size;
        int quarter = m / 2;
        Span<double> sourceRe = stackalloc double[quarter];
        Span<double> sourceIm = stackalloc double[quarter];
        Span<double> re = stackalloc double[quarter];
        Span<double> im = stackalloc double[quarter];

        // Pre-twiddle: v[j] = (X[2j] + i X[M-1-2j]) * e^(-i pi (j + 1/4) / M).
        for (int j = 0; j < quarter; j++)
        {
            double a = spec[2 * j];
            double b = spec[m - 1 - (2 * j)];
            double c = plan.PreCos[j];
            double s = plan.PreSin[j];
            sourceRe[j] = (a * c) + (b * s);
            sourceIm[j] = (b * c) - (a * s);
        }

        plan.Transform(sourceRe, sourceIm, re, im);

        // Post-twiddle by e^(-i pi l / M), giving the DCT-IV u[2l] = Re, u[M-1-2l] = -Im; then
        // fold out: x[n] = u[n + M/2], -u[3M/2 - 1 - n], -u[n - 3M/2] over the three ranges.
        Span<double> u = stackalloc double[m];
        for (int l = 0; l < quarter; l++)
        {
            double c = plan.PostCos[l];
            double s = plan.PostSin[l];
            u[2 * l] = (re[l] * c) + (im[l] * s);
            u[m - 1 - (2 * l)] = -((im[l] * c) - (re[l] * s));
        }

        double scale = 1.0 / m;
        int half = m / 2;
        for (int n = 0; n < half; n++)
        {
            output[n] = u[n + half] * scale;
        }

        for (int n = half; n < 3 * half; n++)
        {
            output[n] = -u[(3 * half) - 1 - n] * scale;
        }

        for (int n = 3 * half; n < 2 * m; n++)
        {
            output[n] = -u[n - (3 * half)] * scale;
        }
    }

    /// <summary>The FFT of <see cref="Inverse"/> (length 512 or 64), for tests: the vector one, or <paramref name="scalar"/>.</summary>
    internal static void Fft(Span<double> re, Span<double> im, bool scalar)
    {
        var plan = re.Length == Long.Size / 2 ? Long : Short;
        if (scalar)
        {
            plan.FftScalar(re, im);
        }
        else
        {
            plan.Fft(re, im);
        }
    }

    /// <summary>The definition, term by term (O(N^2)); the reference for <see cref="Inverse"/>.</summary>
    public static void InverseReference(ReadOnlySpan<double> spec, Span<double> output)
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
                sum += spec[k] * cos[(a * ((2 * k) + 1)) & mask];
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

    /// <summary>Twiddles and an in-place radix-2 FFT for one DCT-IV size M (FFT length M / 2).</summary>
    private sealed class Plan
    {
        private readonly int[] _bitReverse;

        // The bit reversal of each even index below length / 2 (index j / 2): see ReorderFirstStage.
        private readonly int[] _evenReversed;
        private readonly double[] _rootCos;
        private readonly double[] _rootSin;

        public Plan(int size)
        {
            Size = size;
            int length = size / 2;
            PreCos = new double[length];
            PreSin = new double[length];
            PostCos = new double[length];
            PostSin = new double[length];
            for (int j = 0; j < length; j++)
            {
                double pre = Math.PI * (j + 0.25) / size;
                PreCos[j] = Math.Cos(pre);
                PreSin[j] = Math.Sin(pre);
                double post = Math.PI * j / size;
                PostCos[j] = Math.Cos(post);
                PostSin[j] = Math.Sin(post);
            }

            int bits = System.Numerics.BitOperations.Log2((uint)length);
            _bitReverse = new int[length];
            for (int i = 0; i < length; i++)
            {
                int r = 0;
                for (int b = 0; b < bits; b++)
                {
                    r |= ((i >> b) & 1) << (bits - 1 - b);
                }

                _bitReverse[i] = r;
            }

            _evenReversed = new int[length / 4];
            for (int j = 0; j < length / 2; j += 2)
            {
                _evenReversed[j / 2] = _bitReverse[j];
            }

            // e^(-2 pi i t / length) for t < length / 2.
            _rootCos = new double[length / 2];
            _rootSin = new double[length / 2];
            for (int t = 0; t < length / 2; t++)
            {
                _rootCos[t] = Math.Cos(2 * Math.PI * t / length);
                _rootSin[t] = -Math.Sin(2 * Math.PI * t / length);
            }

            // The same roots stage by stage, contiguous: a stage of half-span `span` uses roots k * length / (2 span)
            // for k < span, kept from index span - 1 on.
            _stageCos = new double[length];
            _stageSin = new double[length];
            for (int span = 1; span < length; span *= 2)
            {
                for (int k = 0; k < span; k++)
                {
                    _stageCos[span - 1 + k] = _rootCos[k * (length / (2 * span))];
                    _stageSin[span - 1 + k] = _rootSin[k * (length / (2 * span))];
                }
            }
        }

        private readonly double[] _stageCos;
        private readonly double[] _stageSin;

        public int Size { get; }

        public double[] PreCos { get; }

        public double[] PreSin { get; }

        public double[] PostCos { get; }

        public double[] PostSin { get; }

        /// <summary>The forward FFT of <see cref="Transform"/>, in place (for tests).</summary>
        [SkipLocalsInit]
        public void Fft(Span<double> re, Span<double> im)
        {
            Span<double> sourceRe = stackalloc double[re.Length];
            Span<double> sourceIm = stackalloc double[re.Length];
            re.CopyTo(sourceRe);
            im.CopyTo(sourceIm);
            Transform(sourceRe, sourceIm, re, im);
        }

        /// <summary>
        /// Forward FFT, X[l] = sum of x[j] e^(-2 pi i j l / length), from the source into re and im: <see cref="FftScalar"/>
        /// with the bit reversal and the first stage in one pass (<see cref="ReorderFirstStage"/>) and the butterflies
        /// of a later stage several at a time where the stage is wide enough. Each lane does the scalar arithmetic in the
        /// same order (no fused multiply-add), so the result is the same to the bit, apart from the sign of a zero: the
        /// first stage's twiddle is 1 - 0i, which the reference multiplies by and this pass leaves out.
        /// </summary>
        public void Transform(ReadOnlySpan<double> sourceRe, ReadOnlySpan<double> sourceIm, Span<double> re, Span<double> im)
        {
            int length = re.Length;
            if (sourceRe.Length < length || sourceIm.Length < length || im.Length < length || length != Size / 2)
            {
                throw new ArgumentException("The FFT's buffers do not match its length.");
            }

            ReorderFirstStage(sourceRe, sourceIm, re, im);
            ref double r = ref MemoryMarshal.GetReference(re);
            ref double i = ref MemoryMarshal.GetReference(im);
            ref double cos = ref MemoryMarshal.GetArrayDataReference(_stageCos);
            ref double sin = ref MemoryMarshal.GetArrayDataReference(_stageSin);
            for (int span = 2; span < length; span *= 2)
            {
                int k0 = span - 1;
#if NET
                if (Vector256.IsHardwareAccelerated && span >= Vector256<double>.Count)
                {
                    for (int start = 0; start < length; start += 2 * span)
                    {
                        for (int k = 0; k < span; k += Vector256<double>.Count)
                        {
                            var c = Vector256.LoadUnsafe(ref cos, (nuint)(k0 + k));
                            var s = Vector256.LoadUnsafe(ref sin, (nuint)(k0 + k));
                            nuint a = (nuint)(start + k);
                            nuint b = a + (nuint)span;
                            var rb = Vector256.LoadUnsafe(ref r, b);
                            var ib = Vector256.LoadUnsafe(ref i, b);
                            var ra = Vector256.LoadUnsafe(ref r, a);
                            var ia = Vector256.LoadUnsafe(ref i, a);
                            var tr = (rb * c) - (ib * s);
                            var ti = (rb * s) + (ib * c);
                            (ra - tr).StoreUnsafe(ref r, b);
                            (ia - ti).StoreUnsafe(ref i, b);
                            (ra + tr).StoreUnsafe(ref r, a);
                            (ia + ti).StoreUnsafe(ref i, a);
                        }
                    }
                }
                else if (Vector128.IsHardwareAccelerated && span >= Vector128<double>.Count)
                {
                    for (int start = 0; start < length; start += 2 * span)
                    {
                        for (int k = 0; k < span; k += Vector128<double>.Count)
                        {
                            var c = Vector128.LoadUnsafe(ref cos, (nuint)(k0 + k));
                            var s = Vector128.LoadUnsafe(ref sin, (nuint)(k0 + k));
                            nuint a = (nuint)(start + k);
                            nuint b = a + (nuint)span;
                            var rb = Vector128.LoadUnsafe(ref r, b);
                            var ib = Vector128.LoadUnsafe(ref i, b);
                            var ra = Vector128.LoadUnsafe(ref r, a);
                            var ia = Vector128.LoadUnsafe(ref i, a);
                            var tr = (rb * c) - (ib * s);
                            var ti = (rb * s) + (ib * c);
                            (ra - tr).StoreUnsafe(ref r, b);
                            (ia - ti).StoreUnsafe(ref i, b);
                            (ra + tr).StoreUnsafe(ref r, a);
                            (ia + ti).StoreUnsafe(ref i, a);
                        }
                    }
                }
                else
#endif
                {
                    for (int start = 0; start < length; start += 2 * span)
                    {
                        for (int k = 0; k < span; k++)
                        {
                            double c = Unsafe.Add(ref cos, k0 + k);
                            double s = Unsafe.Add(ref sin, k0 + k);
                            int a = start + k;
                            int b = a + span;
                            double rb = Unsafe.Add(ref r, b);
                            double ib = Unsafe.Add(ref i, b);
                            double tr = (rb * c) - (ib * s);
                            double ti = (rb * s) + (ib * c);
                            double ra = Unsafe.Add(ref r, a);
                            double ia = Unsafe.Add(ref i, a);
                            Unsafe.Add(ref r, b) = ra - tr;
                            Unsafe.Add(ref i, b) = ia - ti;
                            Unsafe.Add(ref r, a) = ra + tr;
                            Unsafe.Add(ref i, a) = ia + ti;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// The bit reversal and the first stage (half-span 1, twiddle 1) in one pass. For an even j below length / 2, with
        /// h = length / 2 and a = reverse(j) (even), the reversal puts x[j] at a and x[j + h] at a + 1, and puts x[j + 1]
        /// and x[j + 1 + h] at a + h and a + h + 1: the stage's sums x[j] + x[j + h] and differences x[j] - x[j + h], two j
        /// at a time from contiguous source, zipped into place.
        /// </summary>
        private void ReorderFirstStage(ReadOnlySpan<double> sourceRe, ReadOnlySpan<double> sourceIm, Span<double> re, Span<double> im)
        {
            int h = re.Length / 2;
            ref double xr = ref MemoryMarshal.GetReference(sourceRe);
            ref double xi = ref MemoryMarshal.GetReference(sourceIm);
            ref double r = ref MemoryMarshal.GetReference(re);
            ref double i = ref MemoryMarshal.GetReference(im);
            ref int reversed = ref MemoryMarshal.GetArrayDataReference(_evenReversed);
#if NET
            if (Vector128.IsHardwareAccelerated)
            {
                for (int j = 0; j < h; j += 2)
                {
                    nuint a = (nuint)Unsafe.Add(ref reversed, j / 2);
                    var ar = Vector128.LoadUnsafe(ref xr, (nuint)j);
                    var br = Vector128.LoadUnsafe(ref xr, (nuint)(j + h));
                    var ai = Vector128.LoadUnsafe(ref xi, (nuint)j);
                    var bi = Vector128.LoadUnsafe(ref xi, (nuint)(j + h));
                    var (low, high) = QmfState.Zip(ar + br, ar - br);
                    low.StoreUnsafe(ref r, a);
                    high.StoreUnsafe(ref r, a + (nuint)h);
                    (low, high) = QmfState.Zip(ai + bi, ai - bi);
                    low.StoreUnsafe(ref i, a);
                    high.StoreUnsafe(ref i, a + (nuint)h);
                }

                return;
            }
#endif

            for (int j = 0; j < h; j++)
            {
                int a = (j & 1) == 0 ? Unsafe.Add(ref reversed, j / 2) : Unsafe.Add(ref reversed, j / 2) + h;
                double ar = Unsafe.Add(ref xr, j);
                double br = Unsafe.Add(ref xr, j + h);
                double ai = Unsafe.Add(ref xi, j);
                double bi = Unsafe.Add(ref xi, j + h);
                Unsafe.Add(ref r, a) = ar + br;
                Unsafe.Add(ref i, a) = ai + bi;
                Unsafe.Add(ref r, a + 1) = ar - br;
                Unsafe.Add(ref i, a + 1) = ai - bi;
            }
        }

        /// <summary>The FFT one butterfly at a time, with the roots by stride: the reference for <see cref="Fft"/>.</summary>
        public void FftScalar(Span<double> re, Span<double> im)
        {
            int length = re.Length;
            for (int i = 0; i < length; i++)
            {
                int j = _bitReverse[i];
                if (j > i)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }

            for (int span = 1; span < length; span *= 2)
            {
                int stride = length / (2 * span);
                for (int start = 0; start < length; start += 2 * span)
                {
                    for (int k = 0; k < span; k++)
                    {
                        double c = _rootCos[k * stride];
                        double s = _rootSin[k * stride];
                        int a = start + k;
                        int b = a + span;
                        double tr = (re[b] * c) - (im[b] * s);
                        double ti = (re[b] * s) + (im[b] * c);
                        re[b] = re[a] - tr;
                        im[b] = im[a] - ti;
                        re[a] += tr;
                        im[a] += ti;
                    }
                }
            }
        }
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
