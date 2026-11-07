using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET
using System.Runtime.Intrinsics;
#endif
#if NET
using System.Runtime.Intrinsics.Arm;
#endif
#if NET
using System.Runtime.Intrinsics.X86;
#endif

namespace ManagedCodecs.Aac.Sbr;

/// <summary>
/// The 32-band complex QMF analysis bank (ISO/IEC 14496-3 4.6.18.4.1): each call takes 32 samples of the core decoder's
/// output and gives one time slot of 32 complex subband samples. The bank keeps the last 320 samples across calls, so
/// one instance serves one channel for the whole stream.
/// </summary>
/// <remarks>
/// The spec's sum X(k) = the sum over n in 0..63 of u(n) 2 exp(i pi/64 (k + 0.5)(2n - 0.5)) is a 64-point DFT: the
/// exponent splits into 2 pi k n / 64 + pi n / 64 - pi (2k + 1) / 256, so X(k) = 2 exp(-i pi (2k + 1) / 256) times the
/// DFT of u(n) exp(i pi n / 64) at k. u is real, so that DFT takes a complex FFT of 32 (<see cref="QmfSingle.Analysis"/>): with
/// z(m) = (u(2m) + i u(2m + 1)) exp(i pi m / 32) and Z its FFT, the even samples' DFT (turned) is E(k) = (Z(k) +
/// conj Z(31 - k)) / 2, the odd ones' O(k) = -i (Z(k) - conj Z(31 - k)) / 2, and the DFT at k is E(k) + exp(i pi (2k + 1)
/// / 64) O(k). With the output turn, X(k) = exp(-i pi (2k + 1) / 256) P(k) + exp(i pi (6k - 125) / 256) M(k), where P
/// and M are Z(k) plus and minus conj Z(31 - k), in single precision. Tests compare it with the sums on random input.
/// </remarks>
internal sealed class QmfAnalysis
{
    public const int Bands = 32;

    // c(2n), the window's coefficients this bank takes, at even n and at odd n, in order.
    private static readonly double[] EvenWindow = Window(0, 1);
    private static readonly double[] OddWindow = Window(2, 1);

    // The window this bank takes, times its scale.
    private readonly double[] _evenWindow;
    private readonly double[] _oddWindow;

    // x, the last 320 samples with the newest first, kept as its values at even n and at odd n (a slot moves n by 32,
    // which keeps its parity), each from _start: each slot moves the start back by 16 rather than the samples forward,
    // and copies them to the end only when the start reaches 0.
    private readonly double[] _even = new double[160 + (16 * 15)];
    private readonly double[] _odd = new double[160 + (16 * 15)];
    private int _start = 16 * 15;

    /// <summary>
    /// A bank whose input is taken as times <paramref name="scale"/>, folded into its window: a power of two (SBR's
    /// 32768) gives the same output as scaling the samples, exactly.
    /// </summary>
    public QmfAnalysis(double scale = 1)
    {
        _evenWindow = scale == 1 ? EvenWindow : Window(0, scale);
        _oddWindow = scale == 1 ? OddWindow : Window(2, scale);
    }

    /// <summary>One time slot: 32 new samples (oldest first) in, 32 complex subband samples out.</summary>
    public void Process(ReadOnlySpan<double> input, Span<double> real, Span<double> imaginary)
    {
        if (input.Length < 32)
        {
            throw new ArgumentException("A slot takes 32 samples.");
        }

        Step();
        var even = _even.AsSpan(_start, 160);
        var odd = _odd.AsSpan(_start, 160);

        // x(31 - n) = input(n): x(2m) is input(31 - 2m), x(2m + 1) input(30 - 2m).
        for (int m = 0; m < 16; m++)
        {
            even[m] = input[31 - (2 * m)];
            odd[m] = input[30 - (2 * m)];
        }

        Transform(even, odd, real, imaginary);
    }

    /// <summary>As <see cref="Process(ReadOnlySpan{double}, Span{double}, Span{double})"/>, from single precision.</summary>
    public void Process(ReadOnlySpan<float> input, Span<double> real, Span<double> imaginary)
    {
        if (input.Length < 32)
        {
            throw new ArgumentException("A slot takes 32 samples.");
        }

        Step();
        var even = _even.AsSpan(_start, 160);
        var odd = _odd.AsSpan(_start, 160);
        for (int m = 0; m < 16; m++)
        {
            even[m] = input[31 - (2 * m)];
            odd[m] = input[30 - (2 * m)];
        }

        Transform(even, odd, real, imaginary);
    }

    // c(2(2m + offset / 2)) times scale, for m in 0..159.
    private static double[] Window(int offset, double scale) =>
        [.. Enumerable.Range(0, 160).Select(m => QmfWindow.Coefficients[(4 * m) + offset] * scale)];

    // The slot's x from _start, at even and at odd n, its newest 16 of each still to be written.
    private void Step()
    {
        QmfState.Step(_odd, _start, 160, 16);
        _start = QmfState.Step(_even, _start, 160, 16);
    }

    [SkipLocalsInit]
    private void Transform(ReadOnlySpan<double> even, ReadOnlySpan<double> odd, Span<double> real, Span<double> imaginary)
    {
        // u(n) = the sum of x(n + 64 j) c(2(n + 64 j)) over j in 0..4, at even and at odd n.
        Span<double> uEven = stackalloc double[32];
        Span<double> uOdd = stackalloc double[32];
        ReadOnlySpan<int> terms = [0, 32, 64, 96, 128];
        Vectors.SumOfProducts(uEven, even, terms, _evenWindow, terms);
        Vectors.SumOfProducts(uOdd, odd, terms, _oddWindow, terms);

        QmfSingle.Analysis(uEven, uOdd, real, imaginary);
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
/// over 64. The full bank takes a shorter route (<see cref="QmfSingle.FullSynthesis"/>): two DCT-IVs of 64 points, each
/// by an FFT of 32, where the DFT took one of 128; the downsampled bank's kernel, a quarter of a sample off, is no DCT-IV,
/// and it keeps the DFT of 64 (<see cref="QmfSingle.HalvedSynthesis"/>). Both in single precision; tests compare them
/// with the sums on random input.
/// </remarks>
internal sealed class QmfSynthesis
{
    private static readonly Tables Full = new(64);
    private static readonly Tables Halved = new(32);

    private readonly Tables _tables;

    // The tables' window, times the bank's scale.
    private readonly double[] _window;

    // v, the last 20B values with the newest first, at _v[_start..]: each slot moves the start back by 2B rather than
    // the values forward (as the analysis bank's samples).
    private readonly double[] _v;
    private int _start;

    /// <summary>
    /// A bank of 64 bands, or 32 <paramref name="downsampled"/>, whose output is times <paramref name="scale"/>, folded
    /// into its window: a power of two (SBR's 1 / 32768) gives the same output as scaling the samples, exactly.
    /// </summary>
    public QmfSynthesis(bool downsampled = false, double scale = 1)
    {
        _tables = downsampled ? Halved : Full;
        _window = scale == 1 ? _tables.Window : [.. _tables.Window.Select(c => c * scale)];
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
            QmfSingle.FullSynthesis(real, imaginary, v[..128]);
        }
        else
        {
            QmfSingle.HalvedSynthesis(real, imaginary, v[..64]);
        }

        // g takes, of each 4B values of v, the first and the last B; w(n) = g(n) c(n (64 / B)); each output sample sums
        // ten of w, in the order of n.
        var window = _window.AsSpan();
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

    // The bank's bands and window.
    private sealed class Tables(int bands)
    {
        public int Bands { get; } = bands;

        // c(n (64 / B)), the window's coefficients the bank takes, in order.
        public double[] Window { get; } = [.. Enumerable.Range(0, 10 * bands).Select(n => QmfWindow.Coefficients[64 / bands * n])];
    }
}

/// <summary>
/// The banks' history buffers, kept in place with a moving start (newest value first), and the 64-bit zips and
/// reversals other vector loops share.
/// </summary>
internal static class QmfState
{
#if NET
    /// <summary>(a(0), b(0)) and (a(1), b(1)).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (Vector128<double> Low, Vector128<double> High) Zip(Vector128<double> a, Vector128<double> b)
    {
#if NET
        if (Sse2.IsSupported)
        {
            return (Sse2.UnpackLow(a, b), Sse2.UnpackHigh(a, b));
        }
#endif

#if NET
        if (AdvSimd.Arm64.IsSupported)
        {
            return (AdvSimd.Arm64.ZipLow(a, b), AdvSimd.Arm64.ZipHigh(a, b));
        }
#endif

        return (Vector128.Create(a.GetElement(0), b.GetElement(0)), Vector128.Create(a.GetElement(1), b.GetElement(1)));
    }
#endif

#if NET
    /// <summary>(v(1), v(0)).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<double> Reverse(Vector128<double> v) => Vector128.Shuffle(v, Vector128.Create(1L, 0L));
#endif

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
/// banks' 32 and 64 points, in single precision: its twiddle factors are <see cref="QmfTrig"/>'s, rounded once, so that
/// it gives the same bits everywhere.
/// </summary>
internal sealed class QmfFft
{
    private readonly int _length;

    // The bit-reversal permutation: where each index's input comes from.
    private readonly int[] _reversed;

    // Each stage's twiddle factors in a row of their own (a stage of half-span h uses exp(2 pi i j / (2h)) for j < h,
    // kept from index h on).
    private readonly float[] _cos;
    private readonly float[] _sin;

    public QmfFft(int length)
    {
        if (length < 8 || (length & (length - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The FFT takes a power of two from 8 points.");
        }

        _length = length;
        int bits = System.Numerics.BitOperations.Log2((uint)length);
        _reversed = new int[length];
        for (int i = 0; i < length; i++)
        {
            _reversed[i] = (int)(ReverseBits((uint)i) >> (32 - bits));
        }

        _cos = new float[length];
        _sin = new float[length];
        for (int half = 1; half < length; half *= 2)
        {
            for (int j = 0; j < half; j++)
            {
                _cos[half + j] = (float)QmfTrig.CosPi256(256 / half * j);
                _sin[half + j] = (float)QmfTrig.SinPi256(256 / half * j);
            }
        }
    }

    /// <summary>
    /// In place: the bit reversal and the first stage (whose twiddle factor is 1) in one pass from a copy, then the later
    /// stages, four butterflies a vector where Vector128 is accelerated (the second stage two groups of four at a time,
    /// split and joined by 64-bit zips). Each lane does the scalar arithmetic in the same order (no fused multiply-add),
    /// so the result is the same to the bit (<paramref name="scalar"/> for tests).
    /// </summary>
    [SkipLocalsInit]
    public void Transform(Span<float> re, Span<float> im, bool scalar = false)
    {
        int length = _length;
        if (re.Length < length || im.Length < length)
        {
            throw new ArgumentException("The FFT's buffers are shorter than its length.");
        }

        Span<float> copyRe = stackalloc float[length];
        Span<float> copyIm = stackalloc float[length];
        re[..length].CopyTo(copyRe);
        im[..length].CopyTo(copyIm);
        ref float r = ref MemoryMarshal.GetReference(re);
        ref float i = ref MemoryMarshal.GetReference(im);
        ref float sr = ref MemoryMarshal.GetReference(copyRe);
        ref float si = ref MemoryMarshal.GetReference(copyIm);
        ref int reversed = ref MemoryMarshal.GetArrayDataReference(_reversed);
        for (int a = 0; a < length; a += 2)
        {
            int x = Unsafe.Add(ref reversed, a);
            int y = Unsafe.Add(ref reversed, a + 1);
            float xr = Unsafe.Add(ref sr, x);
            float xi = Unsafe.Add(ref si, x);
            float yr = Unsafe.Add(ref sr, y);
            float yi = Unsafe.Add(ref si, y);
            Unsafe.Add(ref r, a) = xr + yr;
            Unsafe.Add(ref i, a) = xi + yi;
            Unsafe.Add(ref r, a + 1) = xr - yr;
            Unsafe.Add(ref i, a + 1) = xi - yi;
        }

        ref float cos = ref MemoryMarshal.GetArrayDataReference(_cos);
        ref float sin = ref MemoryMarshal.GetArrayDataReference(_sin);
#if NET
        if (Vector128.IsHardwareAccelerated && !scalar)
        {
            VectorStages(ref r, ref i, ref cos, ref sin, length);
            return;
        }
#endif

        for (int half = 2; half < length; half *= 2)
        {
            for (int start = 0; start < length; start += 2 * half)
            {
                for (int j = 0; j < half; j++)
                {
                    float c = Unsafe.Add(ref cos, half + j);
                    float s = Unsafe.Add(ref sin, half + j);
                    int a = start + j;
                    int b = a + half;
                    float rb = Unsafe.Add(ref r, b);
                    float ib = Unsafe.Add(ref i, b);
                    float tr = (rb * c) - (ib * s);
                    float ti = (rb * s) + (ib * c);
                    float ra = Unsafe.Add(ref r, a);
                    float ia = Unsafe.Add(ref i, a);
                    Unsafe.Add(ref r, b) = ra - tr;
                    Unsafe.Add(ref i, b) = ia - ti;
                    Unsafe.Add(ref r, a) = ra + tr;
                    Unsafe.Add(ref i, a) = ia + ti;
                }
            }
        }
    }

#if NET
    // The stages from a half-span of 2, four butterflies a vector.
    private static void VectorStages(ref float r, ref float i, ref float cos, ref float sin, int length)
    {
        // Half-span 2: a group of four holds two butterflies' a halves then their b halves, so two groups' halves are
        // split by 64-bit zips, take the scalar butterfly lane by lane, and are zipped back.
        var c2 = Vector128.Create(Unsafe.Add(ref cos, 2), Unsafe.Add(ref cos, 3), Unsafe.Add(ref cos, 2), Unsafe.Add(ref cos, 3));
        var s2 = Vector128.Create(Unsafe.Add(ref sin, 2), Unsafe.Add(ref sin, 3), Unsafe.Add(ref sin, 2), Unsafe.Add(ref sin, 3));
        for (int start = 0; start < length; start += 8)
        {
            var (ra, rb) = QmfState.Zip(Vector128.LoadUnsafe(ref r, (nuint)start).AsDouble(), Vector128.LoadUnsafe(ref r, (nuint)(start + 4)).AsDouble());
            var (ia, ib) = QmfState.Zip(Vector128.LoadUnsafe(ref i, (nuint)start).AsDouble(), Vector128.LoadUnsafe(ref i, (nuint)(start + 4)).AsDouble());
            var rbf = rb.AsSingle();
            var ibf = ib.AsSingle();
            var tr = (rbf * c2) - (ibf * s2);
            var ti = (rbf * s2) + (ibf * c2);
            var (r0, r1) = QmfState.Zip((ra.AsSingle() + tr).AsDouble(), (ra.AsSingle() - tr).AsDouble());
            var (i0, i1) = QmfState.Zip((ia.AsSingle() + ti).AsDouble(), (ia.AsSingle() - ti).AsDouble());
            r0.AsSingle().StoreUnsafe(ref r, (nuint)start);
            r1.AsSingle().StoreUnsafe(ref r, (nuint)(start + 4));
            i0.AsSingle().StoreUnsafe(ref i, (nuint)start);
            i1.AsSingle().StoreUnsafe(ref i, (nuint)(start + 4));
        }

        for (int half = 4; half < length; half *= 2)
        {
            for (int start = 0; start < length; start += 2 * half)
            {
                for (int j = 0; j < half; j += 4)
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
#endif

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
