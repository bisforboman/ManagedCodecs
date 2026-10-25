using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

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

    // c(2n), the window's coefficients this bank takes, in order.
    private static readonly double[] Window = [.. Enumerable.Range(0, 320).Select(n => QmfWindow.Coefficients[2 * n])];

    // x, the last 320 samples with the newest first, at _x[_start..]: each slot moves the start back by 32 rather than
    // the samples forward, and copies them to the end only when the start reaches 0.
    private readonly double[] _x = new double[320 + (32 * 15)];
    private int _start = 32 * 15;

    /// <summary>One time slot: 32 new samples (oldest first) in, 32 complex subband samples out.</summary>
    [SkipLocalsInit]
    public void Process(ReadOnlySpan<double> input, Span<double> real, Span<double> imaginary)
    {
        _start = QmfState.Step(_x, _start, 320, 32);
        var x = _x.AsSpan(_start, 320);
        for (int n = 0; n < 32; n++)
        {
            x[31 - n] = input[n];
        }

        // z(n) = x(n) c(2n), u(n) = the sum of z(n + 64 j) over j in 0..4, turned by exp(i pi n / 64).
        Span<double> u = stackalloc double[64];
        Span<double> re = stackalloc double[64];
        Span<double> im = stackalloc double[64];
        u.Clear();
        for (int j = 0; j < 5; j++)
        {
            Vectors.MultiplyAdd(u, x.Slice(64 * j, 64), Window.AsSpan(64 * j, 64));
        }

        // In vectors: u(n) cos and u(n) sin (products commute to the bit), then the output turn as QmfState.Rotate does it.
        u.CopyTo(re);
        Vectors.Multiply(re, InCos);
        u.CopyTo(im);
        Vectors.Multiply(im, InSin);
        Fft.Transform(re, im);
        QmfState.Rotate(re[..Bands], im[..Bands], OutCos, OutSin, real, imaginary);
    }
}

/// <summary>
/// The complex QMF synthesis bank (ISO/IEC 14496-3 4.6.18.4.2): each call takes one time slot of 64 complex subband
/// samples and gives 64 output samples, at twice the core's rate; the bank keeps 1280 values across calls. Downsampled
/// (for SBR at the core's own rate), it takes the slot's lower 32 subband samples and gives 32 output samples, keeping
/// 640 values, with every other window coefficient.
/// </summary>
/// <remarks>
/// The spec's v(n) = the real part of the sum over k of X(k) exp(i pi/128 (k + 0.5)(2n - 255)) / 64, n in 0..127; the
/// downsampled bank's is the same kernel at every other n, exp(i pi/128 (k + 0.5)(4n - 255)), n in 0..63, so that its
/// output lies on the full bank's even samples (a whole-sample delay; with 2n - 127 instead, it would lag a quarter of
/// a sample and miss FFmpeg's output by up to 14% at 3 to 6 kHz). With B bands that is a DFT of 2B points: in units of
/// pi/256 the exponent (2k + 1)((128/B) n - 255) splits into 2 pi k n / 2B, (128/B) n - 255 and -510 k, so v(n) is the
/// real part of exp(i pi ((128/B) n - 255) / 256) times the DFT of X(k) exp(-i 510 pi k / 256) (zero for k from B) at n,
/// over 64. Tests compare it with the sums on random input.
/// </remarks>
internal sealed class QmfSynthesis
{
    private static readonly Tables Full = new(64);
    private static readonly Tables Halved = new(32);

    private readonly Tables _tables;

    // v, the last 20B values with the newest first, at _v[_start..]: each slot moves the start back by 2B rather than
    // the values forward (as the analysis bank's samples).
    private readonly double[] _v;
    private int _start;

    public QmfSynthesis(bool downsampled = false)
    {
        _tables = downsampled ? Halved : Full;
        _v = new double[(20 + 18) * Bands];
        _start = 18 * Bands;
    }

    /// <summary>64, or 32 downsampled: the subband samples a slot takes and the output samples it gives.</summary>
    public int Bands => _tables.Bands;

    /// <summary>One time slot: <see cref="Bands"/> complex subband samples in, as many output samples (oldest first) out.</summary>
    [SkipLocalsInit]
    public void Process(ReadOnlySpan<double> real, ReadOnlySpan<double> imaginary, Span<double> output)
    {
        var t = _tables;
        int bands = t.Bands;
        _start = QmfState.Step(_v, _start, 20 * bands, 2 * bands);
        var v = _v.AsSpan(_start, 20 * bands);

        Span<double> re = stackalloc double[2 * bands];
        Span<double> im = stackalloc double[2 * bands];
        QmfState.Rotate(real[..bands], imaginary[..bands], t.InCos, t.InSin, re, im);

        re[bands..].Clear();
        im[bands..].Clear();
        t.Fft.Transform(re, im);
        QmfState.RealOfRotation(re, im, t.OutCos, t.OutSin, v[..(2 * bands)]);

        // g takes, of each 4B values of v, the first and the last B; w(n) = g(n) c(n (64 / B)); each output sample sums
        // ten of w, in the order of n.
        var window = t.Window.AsSpan();
        output = output[..bands];
        output.Clear();
        for (int i = 0; i < 5; i++)
        {
            Vectors.MultiplyAdd(output, v.Slice(4 * bands * i, bands), window.Slice(2 * bands * i, bands));
            Vectors.MultiplyAdd(output, v.Slice((4 * bands * i) + (3 * bands), bands), window.Slice((2 * bands * i) + bands, bands));
        }
    }

    // exp(-i 510 pi k / 256) for the input, and exp(i pi ((128/B) n - 255) / 256) / 64 for the output.
    private sealed class Tables(int bands)
    {
        public int Bands { get; } = bands;

        public QmfFft Fft { get; } = new(2 * bands);

        // c(n (64 / B)), the window's coefficients the bank takes, in order.
        public double[] Window { get; } = [.. Enumerable.Range(0, 10 * bands).Select(n => QmfWindow.Coefficients[64 / bands * n])];

        public double[] InCos { get; } = [.. Enumerable.Range(0, bands).Select(k => QmfTrig.CosPi256(-510 * k))];

        public double[] InSin { get; } = [.. Enumerable.Range(0, bands).Select(k => QmfTrig.SinPi256(-510 * k))];

        public double[] OutCos { get; } = [.. Enumerable.Range(0, 2 * bands).Select(n => QmfTrig.CosPi256(((128 / bands) * n) - 255) / 64)];

        public double[] OutSin { get; } = [.. Enumerable.Range(0, 2 * bands).Select(n => QmfTrig.SinPi256(((128 / bands) * n) - 255) / 64)];
    }
}

/// <summary>
/// The banks' history buffers, kept in place with a moving start (newest value first), and their element-wise
/// rotations, several elements at a time; each the scalar arithmetic in the same order, to the bit.
/// </summary>
internal static class QmfState
{
    /// <summary>(outRe + i outIm)[k] = (re + i im)[k] (cos + i sin)[k], for k below re's length.</summary>
    public static void Rotate(ReadOnlySpan<double> re, ReadOnlySpan<double> im, ReadOnlySpan<double> cos, ReadOnlySpan<double> sin, Span<double> outRe, Span<double> outIm)
    {
        int length = re.Length;
        if (im.Length < length || cos.Length < length || sin.Length < length || outRe.Length < length || outIm.Length < length)
        {
            throw new ArgumentException("The spans are shorter than the first.");
        }

        ref double a = ref MemoryMarshal.GetReference(re);
        ref double b = ref MemoryMarshal.GetReference(im);
        ref double c = ref MemoryMarshal.GetReference(cos);
        ref double d = ref MemoryMarshal.GetReference(sin);
        ref double x = ref MemoryMarshal.GetReference(outRe);
        ref double y = ref MemoryMarshal.GetReference(outIm);
        int k = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            for (; k <= length - Vector128<double>.Count; k += Vector128<double>.Count)
            {
                var r = Vector128.LoadUnsafe(ref a, (nuint)k);
                var i = Vector128.LoadUnsafe(ref b, (nuint)k);
                var cs = Vector128.LoadUnsafe(ref c, (nuint)k);
                var sn = Vector128.LoadUnsafe(ref d, (nuint)k);
                ((r * cs) - (i * sn)).StoreUnsafe(ref x, (nuint)k);
                ((r * sn) + (i * cs)).StoreUnsafe(ref y, (nuint)k);
            }
        }

        for (; k < length; k++)
        {
            outRe[k] = (re[k] * cos[k]) - (im[k] * sin[k]);
            outIm[k] = (re[k] * sin[k]) + (im[k] * cos[k]);
        }
    }

    /// <summary>output[n] = the real part of (re + i im)[n] (cos + i sin)[n], for n below output's length.</summary>
    public static void RealOfRotation(ReadOnlySpan<double> re, ReadOnlySpan<double> im, ReadOnlySpan<double> cos, ReadOnlySpan<double> sin, Span<double> output)
    {
        int length = output.Length;
        if (re.Length < length || im.Length < length || cos.Length < length || sin.Length < length)
        {
            throw new ArgumentException("The spans are shorter than the output.");
        }

        ref double a = ref MemoryMarshal.GetReference(re);
        ref double b = ref MemoryMarshal.GetReference(im);
        ref double c = ref MemoryMarshal.GetReference(cos);
        ref double d = ref MemoryMarshal.GetReference(sin);
        ref double x = ref MemoryMarshal.GetReference(output);
        int n = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            for (; n <= length - Vector128<double>.Count; n += Vector128<double>.Count)
            {
                var r = Vector128.LoadUnsafe(ref a, (nuint)n);
                var i = Vector128.LoadUnsafe(ref b, (nuint)n);
                ((r * Vector128.LoadUnsafe(ref c, (nuint)n)) - (i * Vector128.LoadUnsafe(ref d, (nuint)n))).StoreUnsafe(ref x, (nuint)n);
            }
        }

        for (; n < length; n++)
        {
            output[n] = (re[n] * cos[n]) - (im[n] * sin[n]);
        }
    }

    /// <summary>
    /// The start of the next slot's window of <paramref name="window"/> values in <paramref name="buffer"/>, the newest
    /// <paramref name="step"/> of which the caller then writes: the start moves back by the step, and when it reaches 0
    /// the window's values that stay are copied to the buffer's end first.
    /// </summary>
    public static int Step(double[] buffer, int start, int window, int step)
    {
        if (start >= step)
        {
            return start - step;
        }

        int kept = window - step;
        buffer.AsSpan(start, kept).CopyTo(buffer.AsSpan(buffer.Length - kept));
        return buffer.Length - window;
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

    // The bit-reversal permutation itself: where each index's input comes from.
    private readonly int[] _reversed;
    private readonly double[] _cos;
    private readonly double[] _sin;

    public QmfFft(int length)
    {
        _length = length;
        int bits = System.Numerics.BitOperations.Log2((uint)length);
        var swaps = new List<int>();
        _reversed = new int[length];
        for (int i = 0; i < length; i++)
        {
            int j = (int)(ReverseBits((uint)i) >> (32 - bits));
            _reversed[i] = j;
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

    /// <summary>
    /// In place. The stages from a half-span of 2 take two butterflies at a time where Vector128 is accelerated (four from a
    /// half-span of 4 where Vector256 is); each lane
    /// does the scalar arithmetic in the same order (no fused multiply-add), so the result is the same to the bit
    /// (<paramref name="scalar"/> for tests).
    /// </summary>
    public void Transform(Span<double> re, Span<double> im, bool scalar = false)
    {
        re = re[.._length];
        im = im[.._length];
        if (Vector128.IsHardwareAccelerated && !scalar)
        {
            ReorderFirstStage(re, im);
            VectorStages(re, im);
            return;
        }

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

    // The stages from a half-span of 2, two or four butterflies at a time.
    /// <summary>
    /// The bit-reversal permutation and the first stage (whose twiddle factor is 1) in one pass, from a copy of the
    /// input: each pair is read from its bit-reversed places, with the scalar path's sums (a + b and a - b; addition
    /// commutes to the bit), so the result is the same.
    /// </summary>
    [SkipLocalsInit]
    private void ReorderFirstStage(Span<double> re, Span<double> im)
    {
        Span<double> copyRe = stackalloc double[_length];
        Span<double> copyIm = stackalloc double[_length];
        re.CopyTo(copyRe);
        im.CopyTo(copyIm);
        ref double sourceRe = ref MemoryMarshal.GetReference(copyRe);
        ref double sourceIm = ref MemoryMarshal.GetReference(copyIm);
        ref double r = ref MemoryMarshal.GetReference(re);
        ref double i = ref MemoryMarshal.GetReference(im);
        ref int reversed = ref MemoryMarshal.GetArrayDataReference(_reversed);
        for (int a = 0; a < _length; a += 2)
        {
            int x = Unsafe.Add(ref reversed, a);
            int y = Unsafe.Add(ref reversed, a + 1);
            double xr = Unsafe.Add(ref sourceRe, x);
            double xi = Unsafe.Add(ref sourceIm, x);
            double yr = Unsafe.Add(ref sourceRe, y);
            double yi = Unsafe.Add(ref sourceIm, y);
            Unsafe.Add(ref r, a) = xr + yr;
            Unsafe.Add(ref i, a) = xi + yi;
            Unsafe.Add(ref r, a + 1) = xr - yr;
            Unsafe.Add(ref i, a + 1) = xi - yi;
        }
    }

    private void VectorStages(Span<double> re, Span<double> im)
    {
        ref double r = ref MemoryMarshal.GetReference(re);
        ref double i = ref MemoryMarshal.GetReference(im);
        ref double cos = ref MemoryMarshal.GetArrayDataReference(_cos);
        ref double sin = ref MemoryMarshal.GetArrayDataReference(_sin);
        for (int half = 2; half < _length; half *= 2)
        {
            if (Vector256.IsHardwareAccelerated && half >= Vector256<double>.Count)
            {
                for (int start = 0; start < _length; start += 2 * half)
                {
                    for (int j = 0; j < half; j += Vector256<double>.Count)
                    {
                        var c = Vector256.LoadUnsafe(ref cos, (nuint)(half + j));
                        var s = Vector256.LoadUnsafe(ref sin, (nuint)(half + j));
                        nuint a = (nuint)(start + j);
                        nuint b = a + (nuint)half;
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

                continue;
            }

            for (int start = 0; start < _length; start += 2 * half)
            {
                for (int j = 0; j < half; j += Vector128<double>.Count)
                {
                    var c = Vector128.LoadUnsafe(ref cos, (nuint)(half + j));
                    var s = Vector128.LoadUnsafe(ref sin, (nuint)(half + j));
                    nuint a = (nuint)(start + j);
                    nuint b = a + (nuint)half;
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
