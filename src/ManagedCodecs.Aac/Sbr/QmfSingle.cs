using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace ManagedCodecs.Aac.Sbr;

/// <summary>
/// The QMF banks' transforms (<see cref="QmfAnalysis"/>'s and <see cref="QmfSynthesis"/>'s, between their windows), in
/// single precision: four floats a vector, narrowed from the windowed sums as they are read and widened to double as v
/// and the subband samples are stored; every factor is <see cref="QmfTrig"/>'s, rounded once. As FFmpeg's SBR is
/// single precision, this moves the decoder's output against FFmpeg's by less than its third digit (the LFE, through the
/// banks alone, from 2.1e-7 to 2.4e-7; docs/conformance.md, HE-AAC), and the vector loops give the scalar ones' bits
/// (each method's <c>scalar</c> for tests).
/// </summary>
internal static class QmfSingle
{
    private static readonly QmfFft Fft32 = new(32);
    private static readonly QmfFft Fft64 = new(64);

    // The analysis bank's: exp(i pi m / 32) in, exp(-i pi (2k + 1) / 256) and exp(i pi (6k - 125) / 256) out.
    private static readonly float[] InCos = Table(m => QmfTrig.CosPi256(8 * m), 32);
    private static readonly float[] InSin = Table(m => QmfTrig.SinPi256(8 * m), 32);
    private static readonly float[] PlusCos = Table(k => QmfTrig.CosPi256(-((2 * k) + 1)), 32);
    private static readonly float[] PlusSin = Table(k => QmfTrig.SinPi256(-((2 * k) + 1)), 32);
    private static readonly float[] MinusCos = Table(k => QmfTrig.CosPi256((6 * k) - 125), 32);
    private static readonly float[] MinusSin = Table(k => QmfTrig.SinPi256((6 * k) - 125), 32);

    // The DCT-IV's: exp(i pi m / 64) in, exp(i pi (4p + 1) / 256) / 64 out.
    private static readonly float[] PreCos = Table(m => QmfTrig.CosPi256(4 * m), 32);
    private static readonly float[] PreSin = Table(m => QmfTrig.SinPi256(4 * m), 32);
    private static readonly float[] PostCos = Table(p => QmfTrig.CosPi256((4 * p) + 1) / 64, 32);
    private static readonly float[] PostSin = Table(p => QmfTrig.SinPi256((4 * p) + 1) / 64, 32);

    // The downsampled synthesis bank's: exp(-i 510 pi k / 256) in, exp(i pi (4n - 255) / 256) / 64 out.
    private static readonly float[] HalvedInCos = Table(k => QmfTrig.CosPi256(-510 * k), 32);
    private static readonly float[] HalvedInSin = Table(k => QmfTrig.SinPi256(-510 * k), 32);
    private static readonly float[] HalvedOutCos = Table(n => QmfTrig.CosPi256((4 * n) - 255) / 64, 64);
    private static readonly float[] HalvedOutSin = Table(n => QmfTrig.SinPi256((4 * n) - 255) / 64, 64);

    /// <summary>
    /// The analysis bank's slot from its windowed sums u at even and odd n (32 each): X(k) for k in 0..31, as
    /// <see cref="QmfAnalysis"/>'s remarks derive it, z(m) = (u(2m) + i u(2m + 1)) exp(i pi m / 32) through the FFT of 32
    /// and X(k) = exp(-i pi (2k + 1) / 256) P(k) + exp(i pi (6k - 125) / 256) M(k).
    /// </summary>
    [SkipLocalsInit]
    public static void Analysis(ReadOnlySpan<double> uEven, ReadOnlySpan<double> uOdd, Span<double> real, Span<double> imaginary, bool scalar = false)
    {
        if (uEven.Length < 32 || uOdd.Length < 32 || real.Length < 32 || imaginary.Length < 32)
        {
            throw new ArgumentException("The analysis bank takes and gives 32 values.");
        }

        Span<float> re = stackalloc float[32];
        Span<float> im = stackalloc float[32];
        Rotate(uEven, uOdd, InCos, InSin, re, im, 32, scalar);
        Fft32.Transform(re, im, scalar);

        ref float r = ref MemoryMarshal.GetReference(re);
        ref float i = ref MemoryMarshal.GetReference(im);
        ref double outRe = ref MemoryMarshal.GetReference(real);
        ref double outIm = ref MemoryMarshal.GetReference(imaginary);
        int k = 0;
#if NET
        if (Vector128.IsHardwareAccelerated && !scalar)
        {
            ref float plusCos = ref MemoryMarshal.GetArrayDataReference(PlusCos);
            ref float plusSin = ref MemoryMarshal.GetArrayDataReference(PlusSin);
            ref float minusCos = ref MemoryMarshal.GetArrayDataReference(MinusCos);
            ref float minusSin = ref MemoryMarshal.GetArrayDataReference(MinusSin);
            for (; k < 32; k += 4)
            {
                // Z(31 - k) for k .. k + 3: the four values from 28 - k, reversed.
                var ar = Vector128.LoadUnsafe(ref r, (nuint)k);
                var ai = Vector128.LoadUnsafe(ref i, (nuint)k);
                var br = Reverse(Vector128.LoadUnsafe(ref r, (nuint)(28 - k)));
                var bi = Reverse(Vector128.LoadUnsafe(ref i, (nuint)(28 - k)));
                var pr = ar + br;
                var pi = ai - bi;
                var mr = ar - br;
                var mi = ai + bi;
                var c = Vector128.LoadUnsafe(ref plusCos, (nuint)k);
                var s = Vector128.LoadUnsafe(ref plusSin, (nuint)k);
                var d = Vector128.LoadUnsafe(ref minusCos, (nuint)k);
                var t = Vector128.LoadUnsafe(ref minusSin, (nuint)k);
                Store((pr * c) - (pi * s) + ((mr * d) - (mi * t)), ref outRe, k);
                Store((pr * s) + (pi * c) + ((mr * t) + (mi * d)), ref outIm, k);
            }
        }
#endif

        for (; k < 32; k++)
        {
            float ar = Unsafe.Add(ref r, k);
            float ai = Unsafe.Add(ref i, k);
            float br = Unsafe.Add(ref r, 31 - k);
            float bi = Unsafe.Add(ref i, 31 - k);
            float pr = ar + br;
            float pi = ai - bi;
            float mr = ar - br;
            float mi = ai + bi;
            float c = PlusCos[k];
            float s = PlusSin[k];
            float d = MinusCos[k];
            float t = MinusSin[k];
            Unsafe.Add(ref outRe, k) = (pr * c) - (pi * s) + ((mr * d) - (mi * t));
            Unsafe.Add(ref outIm, k) = (pr * s) + (pi * c) + ((mr * t) + (mi * d));
        }
    }

    /// <summary>
    /// The full synthesis bank's v(n), n in 0..127, as two DCT-IVs of 64 points: with n' = 127 - n the kernel's real part
    /// is X_r(k) cos(t) + X_i(k) sin(t), t = pi/64 (k + 0.5)(n' + 0.5). For n' from 64 the cos kernel turns into
    /// -(-1)^k sin and the sin kernel into (-1)^k cos of n' - 64, and a sin kernel at n equals (-1)^k times the cos kernel
    /// at 63 - n; so with A the DCT-IV of X_r and B that of (-1)^k X_i, v(n) = (-A(n) + B(63 - n)) / 64 below 64 and
    /// (A(127 - n) + B(n - 64)) / 64 from 64. Each DCT-IV C(n) = the sum over k of x(k) cos(pi/64 (k + 0.5)(n + 0.5)) is a
    /// complex FFT of 32 (<see cref="Pretransform"/>): with v(m) = x(2m) + i x(63 - 2m), C(2p) is the real part of
    /// Z(p) = the sum over m of v(m) exp(-i pi/64 (2m + 0.5)(2p + 0.5)) and C(63 - 2p) its negated imaginary part; the
    /// exponent splits into 2 pi m p / 32 + pi m / 64 + pi (4p + 1) / 256, so conj Z(p) = exp(i pi (4p + 1) / 256) times
    /// the (positive-exponent) FFT of conj v(m) exp(i pi m / 64) at p. A and B at 2p and 63 - 2p come from their FFTs'
    /// values at p, which give v at 2p, 63 - 2p, 64 + 2p and 127 - 2p together.
    /// </summary>
    [SkipLocalsInit]
    public static void FullSynthesis(ReadOnlySpan<double> real, ReadOnlySpan<double> imaginary, Span<double> v, bool scalar = false)
    {
        if (real.Length < 64 || imaginary.Length < 64 || v.Length < 128)
        {
            throw new ArgumentException("The full bank takes 64 complex values and gives 128 of v.");
        }

        Span<float> aRe = stackalloc float[32];
        Span<float> aIm = stackalloc float[32];
        Span<float> bRe = stackalloc float[32];
        Span<float> bIm = stackalloc float[32];
        Pretransform(real, alternate: false, aRe, aIm, scalar);
        Pretransform(imaginary, alternate: true, bRe, bIm, scalar);
        Fft32.Transform(aRe, aIm, scalar);
        Fft32.Transform(bRe, bIm, scalar);
        ref double w = ref MemoryMarshal.GetReference(v);
#if NET
        if (Vector128.IsHardwareAccelerated && !scalar)
        {
            // Four p at a time, P = p .. p + 3 and Q = 28 - p .. 31 - p, as the double path's two: each eight of v are two
            // vectors zipped, one of them reversed.
            ref float ar = ref MemoryMarshal.GetReference(aRe);
            ref float ai = ref MemoryMarshal.GetReference(aIm);
            ref float br = ref MemoryMarshal.GetReference(bRe);
            ref float bi = ref MemoryMarshal.GetReference(bIm);
            for (int p = 0; p < 16; p += 4)
            {
                int q = 28 - p;
                (var aP, var aOddP) = Post(Vector128.LoadUnsafe(ref ar, (nuint)p), Vector128.LoadUnsafe(ref ai, (nuint)p), p);
                (var bP, var bOddP) = Post(Vector128.LoadUnsafe(ref br, (nuint)p), Vector128.LoadUnsafe(ref bi, (nuint)p), p);
                (var aQ, var aOddQ) = Post(Vector128.LoadUnsafe(ref ar, (nuint)q), Vector128.LoadUnsafe(ref ai, (nuint)q), q);
                (var bQ, var bOddQ) = Post(Vector128.LoadUnsafe(ref br, (nuint)q), Vector128.LoadUnsafe(ref bi, (nuint)q), q);
                StoreZipped(ref w, 2 * p, bOddP - aP, Reverse(bQ - aOddQ));
                StoreZipped(ref w, 56 - (2 * p), bOddQ - aQ, Reverse(bP - aOddP));
                StoreZipped(ref w, 64 + (2 * p), aOddP + bP, Reverse(aQ + bOddQ));
                StoreZipped(ref w, 120 - (2 * p), aOddQ + bQ, Reverse(aP + bOddP));
            }

            return;
        }
#endif

        for (int p = 0; p < 32; p++)
        {
            (float a, float aOdd) = Post(aRe[p], aIm[p], p);
            (float b, float bOdd) = Post(bRe[p], bIm[p], p);
            Unsafe.Add(ref w, 2 * p) = bOdd - a;
            Unsafe.Add(ref w, 63 - (2 * p)) = b - aOdd;
            Unsafe.Add(ref w, 64 + (2 * p)) = aOdd + b;
            Unsafe.Add(ref w, 127 - (2 * p)) = a + bOdd;
        }
    }

    /// <summary>
    /// The downsampled synthesis bank's v(n), n in 0..63, from the slot's lower 32 subband samples: the real part of
    /// exp(i pi (4n - 255) / 256) / 64 times the FFT of 64 of X(k) exp(-i 510 pi k / 256) (zero from k = 32), as
    /// <see cref="QmfSynthesis"/>'s remarks derive it; its kernel, a quarter of a sample off the full bank's, is no DCT-IV.
    /// </summary>
    [SkipLocalsInit]
    public static void HalvedSynthesis(ReadOnlySpan<double> real, ReadOnlySpan<double> imaginary, Span<double> v, bool scalar = false)
    {
        if (real.Length < 32 || imaginary.Length < 32 || v.Length < 64)
        {
            throw new ArgumentException("The downsampled bank takes 32 complex values and gives 64 of v.");
        }

        Span<float> re = stackalloc float[64];
        Span<float> im = stackalloc float[64];
        Rotate(real, imaginary, HalvedInCos, HalvedInSin, re, im, 32, scalar);
        re[32..].Clear();
        im[32..].Clear();
        Fft64.Transform(re, im, scalar);
        ref float r = ref MemoryMarshal.GetReference(re);
        ref float i = ref MemoryMarshal.GetReference(im);
        ref float c = ref MemoryMarshal.GetArrayDataReference(HalvedOutCos);
        ref float s = ref MemoryMarshal.GetArrayDataReference(HalvedOutSin);
        ref double w = ref MemoryMarshal.GetReference(v);
        int n = 0;
#if NET
        if (Vector128.IsHardwareAccelerated && !scalar)
        {
            for (; n < 64; n += 4)
            {
                var x = (Vector128.LoadUnsafe(ref r, (nuint)n) * Vector128.LoadUnsafe(ref c, (nuint)n)) - (Vector128.LoadUnsafe(ref i, (nuint)n) * Vector128.LoadUnsafe(ref s, (nuint)n));
                Store(x, ref w, n);
            }
        }
#endif

        for (; n < 64; n++)
        {
            Unsafe.Add(ref w, n) = (Unsafe.Add(ref r, n) * Unsafe.Add(ref c, n)) - (Unsafe.Add(ref i, n) * Unsafe.Add(ref s, n));
        }
    }

    // (outRe + i outIm)[k] = (re + i im)[k] (cos + i sin)[k] for k below count, the inputs narrowed first.
    private static void Rotate(ReadOnlySpan<double> re, ReadOnlySpan<double> im, float[] cos, float[] sin, Span<float> outRe, Span<float> outIm, int count, bool scalar)
    {
        ref double a = ref MemoryMarshal.GetReference(re);
        ref double b = ref MemoryMarshal.GetReference(im);
        ref float c = ref MemoryMarshal.GetArrayDataReference(cos);
        ref float d = ref MemoryMarshal.GetArrayDataReference(sin);
        ref float x = ref MemoryMarshal.GetReference(outRe);
        ref float y = ref MemoryMarshal.GetReference(outIm);
        int k = 0;
#if NET
        if (Vector128.IsHardwareAccelerated && !scalar)
        {
            for (; k < count; k += 4)
            {
                var r = Vector128.Narrow(Vector128.LoadUnsafe(ref a, (nuint)k), Vector128.LoadUnsafe(ref a, (nuint)(k + 2)));
                var i = Vector128.Narrow(Vector128.LoadUnsafe(ref b, (nuint)k), Vector128.LoadUnsafe(ref b, (nuint)(k + 2)));
                var cs = Vector128.LoadUnsafe(ref c, (nuint)k);
                var sn = Vector128.LoadUnsafe(ref d, (nuint)k);
                ((r * cs) - (i * sn)).StoreUnsafe(ref x, (nuint)k);
                ((r * sn) + (i * cs)).StoreUnsafe(ref y, (nuint)k);
            }
        }
#endif

        for (; k < count; k++)
        {
            float r = (float)Unsafe.Add(ref a, k);
            float i = (float)Unsafe.Add(ref b, k);
            Unsafe.Add(ref x, k) = (r * Unsafe.Add(ref c, k)) - (i * Unsafe.Add(ref d, k));
            Unsafe.Add(ref y, k) = (r * Unsafe.Add(ref d, k)) + (i * Unsafe.Add(ref c, k));
        }
    }

    /// <summary>
    /// A DCT-IV's input packed and turned, (x(2m) - i x(63 - 2m)) exp(i pi m / 64) for m in 0..31, x(63 - 2m) negated if
    /// <paramref name="alternate"/>: conjugated, the FFT's input whose output at p gives C(2p) and C(63 - 2p).
    /// </summary>
    private static void Pretransform(ReadOnlySpan<double> x, bool alternate, Span<float> re, Span<float> im, bool scalar)
    {
        ref double input = ref MemoryMarshal.GetReference(x);
        ref float r = ref MemoryMarshal.GetReference(re);
        ref float i = ref MemoryMarshal.GetReference(im);
        ref float preCos = ref MemoryMarshal.GetArrayDataReference(PreCos);
        ref float preSin = ref MemoryMarshal.GetArrayDataReference(PreSin);
        float odd = alternate ? -1 : 1;
        int m = 0;
#if NET
        if (Vector128.IsHardwareAccelerated && !scalar)
        {
            var sign = Vector128.Create(odd);
            for (; m < 32; m += 4)
            {
                var a0 = QmfState.Zip(Vector128.LoadUnsafe(ref input, (nuint)(2 * m)), Vector128.LoadUnsafe(ref input, (nuint)((2 * m) + 2))).Low;
                var a1 = QmfState.Zip(Vector128.LoadUnsafe(ref input, (nuint)((2 * m) + 4)), Vector128.LoadUnsafe(ref input, (nuint)((2 * m) + 6))).Low;
                var b0 = QmfState.Zip(Vector128.LoadUnsafe(ref input, (nuint)(62 - (2 * m))), Vector128.LoadUnsafe(ref input, (nuint)(60 - (2 * m)))).High;
                var b1 = QmfState.Zip(Vector128.LoadUnsafe(ref input, (nuint)(58 - (2 * m))), Vector128.LoadUnsafe(ref input, (nuint)(56 - (2 * m)))).High;
                var a = Vector128.Narrow(a0, a1);
                var b = sign * Vector128.Narrow(b0, b1);
                var c = Vector128.LoadUnsafe(ref preCos, (nuint)m);
                var sn = Vector128.LoadUnsafe(ref preSin, (nuint)m);
                ((a * c) + (b * sn)).StoreUnsafe(ref r, (nuint)m);
                ((a * sn) - (b * c)).StoreUnsafe(ref i, (nuint)m);
            }

            return;
        }
#endif

        for (; m < 32; m++)
        {
            float a = (float)Unsafe.Add(ref input, 2 * m);
            float b = odd * (float)Unsafe.Add(ref input, 63 - (2 * m));
            float c = Unsafe.Add(ref preCos, m);
            float s = Unsafe.Add(ref preSin, m);
            Unsafe.Add(ref r, m) = (a * c) + (b * s);
            Unsafe.Add(ref i, m) = (a * s) - (b * c);
        }
    }

    private static (float Even, float Odd) Post(float yr, float yi, int p) =>
        ((yr * PostCos[p]) - (yi * PostSin[p]), (yr * PostSin[p]) + (yi * PostCos[p]));

#if NET
    private static (Vector128<float> Even, Vector128<float> Odd) Post(Vector128<float> yr, Vector128<float> yi, int p)
    {
        var c = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(PostCos), (nuint)p);
        var s = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(PostSin), (nuint)p);
        return ((yr * c) - (yi * s), (yr * s) + (yi * c));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store(Vector128<float> v, ref double w, int n)
    {
        var (low, high) = Vector128.Widen(v);
        low.StoreUnsafe(ref w, (nuint)n);
        high.StoreUnsafe(ref w, (nuint)(n + 2));
    }

    // even(0) odd(0) even(1) odd(1) ... even(3) odd(3) at w[n..n + 8], widened.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreZipped(ref double w, int n, Vector128<float> even, Vector128<float> odd)
    {
        var (low, high) = Zip(even, odd);
        Store(low, ref w, n);
        Store(high, ref w, n + 4);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> Reverse(Vector128<float> v) => Vector128.Shuffle(v, Vector128.Create(3, 2, 1, 0));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (Vector128<float> Low, Vector128<float> High) Zip(Vector128<float> a, Vector128<float> b)
    {
        if (Sse.IsSupported)
        {
            return (Sse.UnpackLow(a, b), Sse.UnpackHigh(a, b));
        }

        if (AdvSimd.Arm64.IsSupported)
        {
            return (AdvSimd.Arm64.ZipLow(a, b), AdvSimd.Arm64.ZipHigh(a, b));
        }

        return (
            Vector128.Create(a.GetElement(0), b.GetElement(0), a.GetElement(1), b.GetElement(1)),
            Vector128.Create(a.GetElement(2), b.GetElement(2), a.GetElement(3), b.GetElement(3)));
    }
#endif

    private static float[] Table(Func<int, double> value, int count) => [.. Enumerable.Range(0, count).Select(n => (float)value(n))];
}
