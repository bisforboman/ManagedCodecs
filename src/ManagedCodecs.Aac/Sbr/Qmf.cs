using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace ManagedCodecs.Aac.Sbr;

/// <summary>
/// The 32-band complex QMF analysis bank (ISO/IEC 14496-3 4.6.18.4.1): each call takes 32 samples of the core decoder's
/// output and gives one time slot of 32 complex subband samples. The bank keeps the last 320 samples across calls, so
/// one instance serves one channel for the whole stream.
/// </summary>
/// <remarks>
/// The spec's sum X(k) = the sum over n in 0..63 of u(n) 2 exp(i pi/64 (k + 0.5)(2n - 0.5)) is a 64-point DFT: the
/// exponent splits into 2 pi k n / 64 + pi n / 64 - pi (2k + 1) / 256, so X(k) = 2 exp(-i pi (2k + 1) / 256) times the
/// DFT of u(n) exp(i pi n / 64) at k. u is real, so that DFT takes a complex FFT of 32 (<see cref="Process"/>): with
/// z(m) = (u(2m) + i u(2m + 1)) exp(i pi m / 32) and Z its FFT, the even samples' DFT (turned) is E(k) = (Z(k) +
/// conj Z(31 - k)) / 2, the odd ones' O(k) = -i (Z(k) - conj Z(31 - k)) / 2, and the DFT at k is E(k) + exp(i pi (2k + 1)
/// / 64) O(k). With the output turn, X(k) = exp(-i pi (2k + 1) / 256) P(k) + exp(i pi (6k - 125) / 256) M(k), where P
/// and M are Z(k) plus and minus conj Z(31 - k). Tests compare it with the sums on random input, and with the DFT of 64
/// it replaced.
/// </remarks>
internal sealed class QmfAnalysis
{
    public const int Bands = 32;

    private static readonly QmfFft Fft = new(32);

    // exp(i pi m / 32) for the packed input; exp(-i pi (2k + 1) / 256) and exp(i pi (6k - 125) / 256) for the output.
    private static readonly double[] InCos = [.. Enumerable.Range(0, 32).Select(m => QmfTrig.CosPi256(8 * m))];
    private static readonly double[] InSin = [.. Enumerable.Range(0, 32).Select(m => QmfTrig.SinPi256(8 * m))];
    private static readonly double[] PlusCos = [.. Enumerable.Range(0, Bands).Select(k => QmfTrig.CosPi256(-((2 * k) + 1)))];
    private static readonly double[] PlusSin = [.. Enumerable.Range(0, Bands).Select(k => QmfTrig.SinPi256(-((2 * k) + 1)))];
    private static readonly double[] MinusCos = [.. Enumerable.Range(0, Bands).Select(k => QmfTrig.CosPi256((6 * k) - 125))];
    private static readonly double[] MinusSin = [.. Enumerable.Range(0, Bands).Select(k => QmfTrig.SinPi256((6 * k) - 125))];

    // c(2n), the window's coefficients this bank takes, at even n and at odd n, in order.
    private static readonly double[] EvenWindow = [.. Enumerable.Range(0, 160).Select(m => QmfWindow.Coefficients[4 * m])];
    private static readonly double[] OddWindow = [.. Enumerable.Range(0, 160).Select(m => QmfWindow.Coefficients[(4 * m) + 2])];

    // x, the last 320 samples with the newest first, kept as its values at even n and at odd n (a slot moves n by 32,
    // which keeps its parity), each from _start: each slot moves the start back by 16 rather than the samples forward,
    // and copies them to the end only when the start reaches 0.
    private readonly double[] _even = new double[160 + (16 * 15)];
    private readonly double[] _odd = new double[160 + (16 * 15)];
    private int _start = 16 * 15;

    /// <summary>One time slot: 32 new samples (oldest first) in, 32 complex subband samples out.</summary>
    [SkipLocalsInit]
    public void Process(ReadOnlySpan<double> input, Span<double> real, Span<double> imaginary)
    {
        if (input.Length < 32)
        {
            throw new ArgumentException("A slot takes 32 samples.");
        }

        QmfState.Step(_odd, _start, 160, 16);
        _start = QmfState.Step(_even, _start, 160, 16);
        var even = _even.AsSpan(_start, 160);
        var odd = _odd.AsSpan(_start, 160);

        // x(31 - n) = input(n): x(2m) is input(31 - 2m), x(2m + 1) input(30 - 2m).
        for (int m = 0; m < 16; m++)
        {
            even[m] = input[31 - (2 * m)];
            odd[m] = input[30 - (2 * m)];
        }

        // u(n) = the sum of x(n + 64 j) c(2(n + 64 j)) over j in 0..4, at even and at odd n.
        Span<double> uEven = stackalloc double[32];
        Span<double> uOdd = stackalloc double[32];
        ReadOnlySpan<int> terms = [0, 32, 64, 96, 128];
        Vectors.SumOfProducts(uEven, even, terms, EvenWindow, terms);
        Vectors.SumOfProducts(uOdd, odd, terms, OddWindow, terms);

        Span<double> re = stackalloc double[32];
        Span<double> im = stackalloc double[32];
        QmfState.Rotate(uEven, uOdd, InCos, InSin, re, im);
        Fft.Transform(re, im);
        Output(re, im, real, imaginary);
    }

    /// <summary>X(k) from Z, the FFT (<paramref name="re"/>, <paramref name="im"/>), at k in 0..31.</summary>
    public static void Output(ReadOnlySpan<double> re, ReadOnlySpan<double> im, Span<double> real, Span<double> imaginary, bool scalar = false)
    {
        if (re.Length < Bands || im.Length < Bands || real.Length < Bands || imaginary.Length < Bands)
        {
            throw new ArgumentException("The output takes and gives 32 complex values.");
        }

        ref double r = ref MemoryMarshal.GetReference(re);
        ref double i = ref MemoryMarshal.GetReference(im);
        ref double outRe = ref MemoryMarshal.GetReference(real);
        ref double outIm = ref MemoryMarshal.GetReference(imaginary);
        ref double plusCos = ref MemoryMarshal.GetArrayDataReference(PlusCos);
        ref double plusSin = ref MemoryMarshal.GetArrayDataReference(PlusSin);
        ref double minusCos = ref MemoryMarshal.GetArrayDataReference(MinusCos);
        ref double minusSin = ref MemoryMarshal.GetArrayDataReference(MinusSin);
        int k = 0;
        if (Vector128.IsHardwareAccelerated && !scalar)
        {
            for (; k < Bands; k += 2)
            {
                // Z(31 - k) for k and k + 1: the two values below 32 - k, swapped.
                var ar = Vector128.LoadUnsafe(ref r, (nuint)k);
                var ai = Vector128.LoadUnsafe(ref i, (nuint)k);
                var br = Vector128.Shuffle(Vector128.LoadUnsafe(ref r, (nuint)(30 - k)), Vector128.Create(1L, 0L));
                var bi = Vector128.Shuffle(Vector128.LoadUnsafe(ref i, (nuint)(30 - k)), Vector128.Create(1L, 0L));
                var pr = ar + br;
                var pi = ai - bi;
                var mr = ar - br;
                var mi = ai + bi;
                var c = Vector128.LoadUnsafe(ref plusCos, (nuint)k);
                var s = Vector128.LoadUnsafe(ref plusSin, (nuint)k);
                var d = Vector128.LoadUnsafe(ref minusCos, (nuint)k);
                var t = Vector128.LoadUnsafe(ref minusSin, (nuint)k);
                ((pr * c) - (pi * s) + ((mr * d) - (mi * t))).StoreUnsafe(ref outRe, (nuint)k);
                ((pr * s) + (pi * c) + ((mr * t) + (mi * d))).StoreUnsafe(ref outIm, (nuint)k);
            }
        }

        for (; k < Bands; k++)
        {
            // P(k) = Z(k) + conj Z(31 - k), M(k) = Z(k) - conj Z(31 - k).
            double ar = Unsafe.Add(ref r, k);
            double ai = Unsafe.Add(ref i, k);
            double br = Unsafe.Add(ref r, 31 - k);
            double bi = Unsafe.Add(ref i, 31 - k);
            double pr = ar + br;
            double pi = ai - bi;
            double mr = ar - br;
            double mi = ai + bi;
            double c = Unsafe.Add(ref plusCos, k);
            double s = Unsafe.Add(ref plusSin, k);
            double d = Unsafe.Add(ref minusCos, k);
            double t = Unsafe.Add(ref minusSin, k);
            Unsafe.Add(ref outRe, k) = (pr * c) - (pi * s) + ((mr * d) - (mi * t));
            Unsafe.Add(ref outIm, k) = (pr * s) + (pi * c) + ((mr * t) + (mi * d));
        }
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
/// over 64. The full bank takes a shorter route (<see cref="FullTransform"/>): two DCT-IVs of 64 points, each by an FFT
/// of 32, where the DFT took one of 128; the downsampled bank's kernel, a quarter of a sample off, is no DCT-IV, and it
/// keeps the DFT of 64. Tests compare both with the sums on random input, and the full bank with the DFT route.
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

        if (bands == 64)
        {
            FullTransform(real, imaginary, v[..128]);
        }
        else
        {
            Span<double> re = stackalloc double[2 * bands];
            Span<double> im = stackalloc double[2 * bands];
            QmfState.Rotate(real[..bands], imaginary[..bands], t.InCos, t.InSin, re, im);
            re[bands..].Clear();
            im[bands..].Clear();
            t.Fft.Transform(re, im);
            QmfState.RealOfRotation(re, im, t.OutCos, t.OutSin, v[..(2 * bands)]);
        }

        // g takes, of each 4B values of v, the first and the last B; w(n) = g(n) c(n (64 / B)); each output sample sums
        // ten of w, in the order of n.
        var window = t.Window.AsSpan();
        output = output[..bands];
        Span<int> vTerms = stackalloc int[10];
        Span<int> windowTerms = stackalloc int[10];
        for (int i = 0; i < 5; i++)
        {
            vTerms[2 * i] = 4 * bands * i;
            vTerms[(2 * i) + 1] = (4 * bands * i) + (3 * bands);
            windowTerms[2 * i] = 2 * bands * i;
            windowTerms[(2 * i) + 1] = (2 * bands * i) + bands;
        }

        Vectors.SumOfProducts(output, v, vTerms, window, windowTerms);
    }

    /// <summary>
    /// The full bank's v(n), n in 0..127, as two DCT-IVs of 64 points (<see cref="QmfDctIV"/>): with n' = 127 - n the
    /// kernel's real part is X_r(k) cos(t) + X_i(k) sin(t), t = pi/64 (k + 0.5)(n' + 0.5). For n' from 64 the cos kernel
    /// turns into -(-1)^k sin and the sin kernel into (-1)^k cos of n' - 64, and a sin kernel at n equals (-1)^k times the
    /// cos kernel at 63 - n; so with A the DCT-IV of X_r and B that of (-1)^k X_i, v(n) = (-A(n) + B(63 - n)) / 64 below
    /// 64 and (A(127 - n) + B(n - 64)) / 64 from 64. The outputs of A and B at 2p and 63 - 2p come from their FFTs' values
    /// at p, which give v at 2p, 63 - 2p, 64 + 2p and 127 - 2p together.
    /// </summary>
    [SkipLocalsInit]
    internal static void FullTransform(ReadOnlySpan<double> real, ReadOnlySpan<double> imaginary, Span<double> v, bool scalar = false)
    {
        if (v.Length < 128)
        {
            throw new ArgumentException("The full bank gives 128 values of v.");
        }

        Span<double> aRe = stackalloc double[32];
        Span<double> aIm = stackalloc double[32];
        Span<double> bRe = stackalloc double[32];
        Span<double> bIm = stackalloc double[32];
        QmfDctIV.Pretransform(real, alternate: false, aRe, aIm, scalar);
        QmfDctIV.Pretransform(imaginary, alternate: true, bRe, bIm, scalar);
        ref double w = ref MemoryMarshal.GetReference(v);
        if (Vector128.IsHardwareAccelerated && !scalar)
        {
            // Two p at a time, P = (p, p + 1) and Q = (30 - p, 31 - p): the values of v at 2p and 2p + 2 come from P, those
            // between them (63 - 2q) from Q, and the same for Q's even ones and P's odd ones, so each four of v are two
            // vectors zipped.
            ref double ar = ref MemoryMarshal.GetReference(aRe);
            ref double ai = ref MemoryMarshal.GetReference(aIm);
            ref double br = ref MemoryMarshal.GetReference(bRe);
            ref double bi = ref MemoryMarshal.GetReference(bIm);
            for (int p = 0; p < 16; p += 2)
            {
                int q = 30 - p;
                (var aP, var aOddP) = Dct.Output(Vector128.LoadUnsafe(ref ar, (nuint)p), Vector128.LoadUnsafe(ref ai, (nuint)p), p);
                (var bP, var bOddP) = Dct.Output(Vector128.LoadUnsafe(ref br, (nuint)p), Vector128.LoadUnsafe(ref bi, (nuint)p), p);
                (var aQ, var aOddQ) = Dct.Output(Vector128.LoadUnsafe(ref ar, (nuint)q), Vector128.LoadUnsafe(ref ai, (nuint)q), q);
                (var bQ, var bOddQ) = Dct.Output(Vector128.LoadUnsafe(ref br, (nuint)q), Vector128.LoadUnsafe(ref bi, (nuint)q), q);

                // v(2p) = B(63 - 2p) - A(2p), v(63 - 2p) = B(2p) - A(63 - 2p), v(64 + 2p) = A(63 - 2p) + B(2p),
                // v(127 - 2p) = A(2p) + B(63 - 2p), as in the scalar loop.
                Store(ref w, 2 * p, bOddP - aP, QmfState.Reverse(bQ - aOddQ));
                Store(ref w, 60 - (2 * p), bOddQ - aQ, QmfState.Reverse(bP - aOddP));
                Store(ref w, 64 + (2 * p), aOddP + bP, QmfState.Reverse(aQ + bOddQ));
                Store(ref w, 124 - (2 * p), aOddQ + bQ, QmfState.Reverse(aP + bOddP));
            }

            return;
        }

        for (int p = 0; p < 32; p++)
        {
            (double a, double aOdd) = Dct.Output(aRe[p], aIm[p], p);
            (double b, double bOdd) = Dct.Output(bRe[p], bIm[p], p);
            Unsafe.Add(ref w, 2 * p) = bOdd - a; // v(n) = B(63 - n) - A(n), n = 2p
            Unsafe.Add(ref w, 63 - (2 * p)) = b - aOdd; // n = 63 - 2p
            Unsafe.Add(ref w, 64 + (2 * p)) = aOdd + b; // v(64 + n) = A(63 - n) + B(n), n = 2p
            Unsafe.Add(ref w, 127 - (2 * p)) = a + bOdd; // n = 63 - 2p
        }
    }

    // even(0) odd(0) even(1) odd(1) at w[n..n + 4].
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store(ref double w, int n, Vector128<double> even, Vector128<double> odd)
    {
        (var low, var high) = QmfState.Zip(even, odd);
        low.StoreUnsafe(ref w, (nuint)n);
        high.StoreUnsafe(ref w, (nuint)(n + 2));
    }

    // The DCT-IVs with the 1/64 of v(n).
    private static readonly QmfDctIV Dct = new(1.0 / 64);

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
/// The DCT-IV of 64 points, C(n) = the sum over k in 0..63 of x(k) cos(pi/64 (k + 0.5)(n + 0.5)), by a complex FFT of 32:
/// taking x's even values and its odd ones in reverse as one complex sequence v(m) = x(2m) + i x(63 - 2m), the sum at an
/// even n = 2p is the real part of Z(p) = the sum over m of v(m) exp(-i pi/64 (2m + 0.5)(2p + 0.5)), and C(63 - 2p) its
/// negated imaginary part (cos(pi (2m + 0.5) - t) = sin t and the other way round). The exponent splits into
/// 2 pi m p / 32 + pi m / 64 + pi (4p + 1) / 256, so conj Z(p) = exp(i pi (4p + 1) / 256) times the (positive-exponent)
/// FFT of conj v(m) exp(i pi m / 64) at p. All factors are multiples of pi/256 (<see cref="QmfTrig"/>).
/// </summary>
internal sealed class QmfDctIV(double scale = 1)
{
    private static readonly QmfFft Fft = new(32);

    private static readonly double[] PreCos = [.. Enumerable.Range(0, 32).Select(m => QmfTrig.CosPi256(4 * m))];
    private static readonly double[] PreSin = [.. Enumerable.Range(0, 32).Select(m => QmfTrig.SinPi256(4 * m))];

    // With the scale the outputs take (a power of 2, so the same bits as scaling them after).
    private readonly double[] _postCos = [.. Enumerable.Range(0, 32).Select(p => QmfTrig.CosPi256((4 * p) + 1) * scale)];
    private readonly double[] _postSin = [.. Enumerable.Range(0, 32).Select(p => QmfTrig.SinPi256((4 * p) + 1) * scale)];

    /// <summary>The DCT-IV of <paramref name="x"/> (64 values), or of (-1)^k x(k) if <paramref name="alternate"/>, into <paramref name="output"/>, times the scale.</summary>
    [SkipLocalsInit]
    public void Transform(ReadOnlySpan<double> x, bool alternate, Span<double> output)
    {
        if (output.Length < 64)
        {
            throw new ArgumentException("The DCT-IV gives 64 values.");
        }

        Span<double> re = stackalloc double[32];
        Span<double> im = stackalloc double[32];
        Pretransform(x, alternate, re, im);
        ref double r = ref MemoryMarshal.GetReference(re);
        ref double i = ref MemoryMarshal.GetReference(im);
        ref double o = ref MemoryMarshal.GetReference(output);
        for (int p = 0; p < 32; p++)
        {
            (double even, double odd) = Output(Unsafe.Add(ref r, p), Unsafe.Add(ref i, p), p);
            Unsafe.Add(ref o, 2 * p) = even;
            Unsafe.Add(ref o, 63 - (2 * p)) = odd;
        }
    }

    /// <summary>
    /// The FFT of 32 of <paramref name="x"/>'s values packed and turned (<paramref name="re"/>, <paramref name="im"/>, 32
    /// each), whose value at p <see cref="Output(double, double, int)"/> turns into the outputs at 2p and 63 - 2p.
    /// </summary>
    public static void Pretransform(ReadOnlySpan<double> x, bool alternate, Span<double> re, Span<double> im, bool scalar = false)
    {
        if (x.Length < 64 || re.Length < 32 || im.Length < 32)
        {
            throw new ArgumentException("The DCT-IV takes 64 values, through 32 complex ones.");
        }

        double odd = alternate ? -1 : 1;
        ref double input = ref MemoryMarshal.GetReference(x);
        ref double r = ref MemoryMarshal.GetReference(re);
        ref double i = ref MemoryMarshal.GetReference(im);
        ref double preCos = ref MemoryMarshal.GetArrayDataReference(PreCos);
        ref double preSin = ref MemoryMarshal.GetArrayDataReference(PreSin);
        int m = 0;
        if (Vector128.IsHardwareAccelerated && !scalar)
        {
            // x(2m) and x(2m + 2) are the first of the pairs at 2m and 2m + 2; x(63 - 2m) and x(61 - 2m) the second of
            // those at 62 - 2m and 60 - 2m.
            var sign = Vector128.Create(odd);
            for (; m < 32; m += 2)
            {
                var a = QmfState.Zip(Vector128.LoadUnsafe(ref input, (nuint)(2 * m)), Vector128.LoadUnsafe(ref input, (nuint)((2 * m) + 2))).Low;
                var b = sign * QmfState.Zip(Vector128.LoadUnsafe(ref input, (nuint)(62 - (2 * m))), Vector128.LoadUnsafe(ref input, (nuint)(60 - (2 * m)))).High;
                var c = Vector128.LoadUnsafe(ref preCos, (nuint)m);
                var sn = Vector128.LoadUnsafe(ref preSin, (nuint)m);
                ((a * c) + (b * sn)).StoreUnsafe(ref r, (nuint)m);
                ((a * sn) - (b * c)).StoreUnsafe(ref i, (nuint)m);
            }
        }

        for (; m < 32; m++)
        {
            // (a - i b)(cos + i sin), a = x(2m), b = x(63 - 2m) (an odd k, so negated if alternate).
            double a = Unsafe.Add(ref input, 2 * m);
            double b = odd * Unsafe.Add(ref input, 63 - (2 * m));
            double c = Unsafe.Add(ref preCos, m);
            double s = Unsafe.Add(ref preSin, m);
            Unsafe.Add(ref r, m) = (a * c) + (b * s);
            Unsafe.Add(ref i, m) = (a * s) - (b * c);
        }

        Fft.Transform(re, im);
    }

    /// <summary>The outputs at 2p and 63 - 2p (times the scale) from the FFT's value at p, (yr, yi).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (double Even, double Odd) Output(double yr, double yi, int p)
    {
        double c = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_postCos), p);
        double s = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_postSin), p);
        return ((yr * c) - (yi * s), (yr * s) + (yi * c));
    }

    /// <summary><see cref="Output(double, double, int)"/> at p and p + 1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (Vector128<double> Even, Vector128<double> Odd) Output(Vector128<double> yr, Vector128<double> yi, int p)
    {
        var c = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(_postCos), (nuint)p);
        var s = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(_postSin), (nuint)p);
        return ((yr * c) - (yi * s), (yr * s) + (yi * c));
    }
}

/// <summary>
/// The banks' history buffers, kept in place with a moving start (newest value first), and their element-wise
/// rotations, several elements at a time; each the scalar arithmetic in the same order, to the bit.
/// </summary>
internal static class QmfState
{
    /// <summary>(a(0), b(0)) and (a(1), b(1)).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (Vector128<double> Low, Vector128<double> High) Zip(Vector128<double> a, Vector128<double> b)
    {
        if (Sse2.IsSupported)
        {
            return (Sse2.UnpackLow(a, b), Sse2.UnpackHigh(a, b));
        }

        if (AdvSimd.Arm64.IsSupported)
        {
            return (AdvSimd.Arm64.ZipLow(a, b), AdvSimd.Arm64.ZipHigh(a, b));
        }

        return (Vector128.Create(a.GetElement(0), b.GetElement(0)), Vector128.Create(a.GetElement(1), b.GetElement(1)));
    }

    /// <summary>(v(1), v(0)).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<double> Reverse(Vector128<double> v) => Vector128.Shuffle(v, Vector128.Create(1L, 0L));

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
