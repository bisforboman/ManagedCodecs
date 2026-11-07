using System.Runtime.CompilerServices;
#if NET
using System.Runtime.Intrinsics;
#endif
#if NET
using System.Runtime.Intrinsics.X86;
#endif

namespace ManagedCodecs.Aac.Sbr;

/// <summary>
/// The QMF banks' transforms of several time slots at once (#338), laid out by row then lane: row n holds point n of
/// each slot's transform, one slot a lane, so every stage is whole vectors with its factors broadcast, and needs no
/// shuffles but the transposes in and out. Each lane does the one-slot path's scalar arithmetic in its order (no fused
/// multiply-add), so the result is the same to the bit; tests compare the banks' frames with their slots one at a time.
/// </summary>
internal static class QmfLanes
{
    /// <summary>The slots a bank transforms together.</summary>
    public const int Count = 8;

    /// <summary>Whether the banks take their slots <see cref="Count"/> at a time: where vectors are accelerated.</summary>
#if NET
    public static bool Enabled => Vector128.IsHardwareAccelerated;
#else
    public static bool Enabled => false;
#endif

    /// <summary>(a, b) = (a + b, a - b), lane by lane: the FFT's first stage, whose twiddle factor is 1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SumDifference(ref double ra, ref double ia, ref double rb, ref double ib, int lanes)
    {
        int l = 0;
#if NET
        if (Vector256.IsHardwareAccelerated)
        {
            for (; l <= lanes - Vector256<double>.Count; l += Vector256<double>.Count)
            {
                var xr = Vector256.LoadUnsafe(ref ra, (nuint)l);
                var xi = Vector256.LoadUnsafe(ref ia, (nuint)l);
                var yr = Vector256.LoadUnsafe(ref rb, (nuint)l);
                var yi = Vector256.LoadUnsafe(ref ib, (nuint)l);
                (xr + yr).StoreUnsafe(ref ra, (nuint)l);
                (xi + yi).StoreUnsafe(ref ia, (nuint)l);
                (xr - yr).StoreUnsafe(ref rb, (nuint)l);
                (xi - yi).StoreUnsafe(ref ib, (nuint)l);
            }
        }

        if (Vector128.IsHardwareAccelerated)
        {
            for (; l <= lanes - Vector128<double>.Count; l += Vector128<double>.Count)
            {
                var xr = Vector128.LoadUnsafe(ref ra, (nuint)l);
                var xi = Vector128.LoadUnsafe(ref ia, (nuint)l);
                var yr = Vector128.LoadUnsafe(ref rb, (nuint)l);
                var yi = Vector128.LoadUnsafe(ref ib, (nuint)l);
                (xr + yr).StoreUnsafe(ref ra, (nuint)l);
                (xi + yi).StoreUnsafe(ref ia, (nuint)l);
                (xr - yr).StoreUnsafe(ref rb, (nuint)l);
                (xi - yi).StoreUnsafe(ref ib, (nuint)l);
            }
        }
#endif

        for (; l < lanes; l++)
        {
            double xr = Unsafe.Add(ref ra, l);
            double xi = Unsafe.Add(ref ia, l);
            double yr = Unsafe.Add(ref rb, l);
            double yi = Unsafe.Add(ref ib, l);
            Unsafe.Add(ref ra, l) = xr + yr;
            Unsafe.Add(ref ia, l) = xi + yi;
            Unsafe.Add(ref rb, l) = xr - yr;
            Unsafe.Add(ref ib, l) = xi - yi;
        }
    }

    /// <summary>t = b (c + i s); (a, b) = (a + t, a - t), lane by lane, as <see cref="QmfFft"/>'s scalar stages.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Butterfly(ref double ra, ref double ia, ref double rb, ref double ib, double c, double s, int lanes)
    {
        int l = 0;
#if NET
        if (Vector256.IsHardwareAccelerated)
        {
            var vc = Vector256.Create(c);
            var vs = Vector256.Create(s);
            for (; l <= lanes - Vector256<double>.Count; l += Vector256<double>.Count)
            {
                var br = Vector256.LoadUnsafe(ref rb, (nuint)l);
                var bi = Vector256.LoadUnsafe(ref ib, (nuint)l);
                var ar = Vector256.LoadUnsafe(ref ra, (nuint)l);
                var ai = Vector256.LoadUnsafe(ref ia, (nuint)l);
                var tr = (br * vc) - (bi * vs);
                var ti = (br * vs) + (bi * vc);
                (ar - tr).StoreUnsafe(ref rb, (nuint)l);
                (ai - ti).StoreUnsafe(ref ib, (nuint)l);
                (ar + tr).StoreUnsafe(ref ra, (nuint)l);
                (ai + ti).StoreUnsafe(ref ia, (nuint)l);
            }
        }

        if (Vector128.IsHardwareAccelerated)
        {
            var vc = Vector128.Create(c);
            var vs = Vector128.Create(s);
            for (; l <= lanes - Vector128<double>.Count; l += Vector128<double>.Count)
            {
                var br = Vector128.LoadUnsafe(ref rb, (nuint)l);
                var bi = Vector128.LoadUnsafe(ref ib, (nuint)l);
                var ar = Vector128.LoadUnsafe(ref ra, (nuint)l);
                var ai = Vector128.LoadUnsafe(ref ia, (nuint)l);
                var tr = (br * vc) - (bi * vs);
                var ti = (br * vs) + (bi * vc);
                (ar - tr).StoreUnsafe(ref rb, (nuint)l);
                (ai - ti).StoreUnsafe(ref ib, (nuint)l);
                (ar + tr).StoreUnsafe(ref ra, (nuint)l);
                (ai + ti).StoreUnsafe(ref ia, (nuint)l);
            }
        }
#endif

        for (; l < lanes; l++)
        {
            double br = Unsafe.Add(ref rb, l);
            double bi = Unsafe.Add(ref ib, l);
            double tr = (br * c) - (bi * s);
            double ti = (br * s) + (bi * c);
            Unsafe.Add(ref rb, l) = Unsafe.Add(ref ra, l) - tr;
            Unsafe.Add(ref ib, l) = Unsafe.Add(ref ia, l) - ti;
            Unsafe.Add(ref ra, l) += tr;
            Unsafe.Add(ref ia, l) += ti;
        }
    }

    /// <summary>
    /// The analysis bank's X(k) from Z(k) (a) and Z(31 - k) (b), lane by lane, as <see cref="QmfAnalysis.Output"/>:
    /// P = a + conj b and M = a - conj b, turned by (c, s) and (d, t).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AnalysisOutput(ref double ar, ref double ai, ref double br, ref double bi, double c, double s, double d, double t, ref double outRe, ref double outIm, int lanes)
    {
        int l = 0;
#if NET
        if (Vector256.IsHardwareAccelerated)
        {
            var vc = Vector256.Create(c);
            var vs = Vector256.Create(s);
            var vd = Vector256.Create(d);
            var vt = Vector256.Create(t);
            for (; l <= lanes - Vector256<double>.Count; l += Vector256<double>.Count)
            {
                var xr = Vector256.LoadUnsafe(ref ar, (nuint)l);
                var xi = Vector256.LoadUnsafe(ref ai, (nuint)l);
                var yr = Vector256.LoadUnsafe(ref br, (nuint)l);
                var yi = Vector256.LoadUnsafe(ref bi, (nuint)l);
                var pr = xr + yr;
                var pi = xi - yi;
                var mr = xr - yr;
                var mi = xi + yi;
                ((pr * vc) - (pi * vs) + ((mr * vd) - (mi * vt))).StoreUnsafe(ref outRe, (nuint)l);
                ((pr * vs) + (pi * vc) + ((mr * vt) + (mi * vd))).StoreUnsafe(ref outIm, (nuint)l);
            }
        }

        if (Vector128.IsHardwareAccelerated)
        {
            var vc = Vector128.Create(c);
            var vs = Vector128.Create(s);
            var vd = Vector128.Create(d);
            var vt = Vector128.Create(t);
            for (; l <= lanes - Vector128<double>.Count; l += Vector128<double>.Count)
            {
                var xr = Vector128.LoadUnsafe(ref ar, (nuint)l);
                var xi = Vector128.LoadUnsafe(ref ai, (nuint)l);
                var yr = Vector128.LoadUnsafe(ref br, (nuint)l);
                var yi = Vector128.LoadUnsafe(ref bi, (nuint)l);
                var pr = xr + yr;
                var pi = xi - yi;
                var mr = xr - yr;
                var mi = xi + yi;
                ((pr * vc) - (pi * vs) + ((mr * vd) - (mi * vt))).StoreUnsafe(ref outRe, (nuint)l);
                ((pr * vs) + (pi * vc) + ((mr * vt) + (mi * vd))).StoreUnsafe(ref outIm, (nuint)l);
            }
        }
#endif

        for (; l < lanes; l++)
        {
            double xr = Unsafe.Add(ref ar, l);
            double xi = Unsafe.Add(ref ai, l);
            double yr = Unsafe.Add(ref br, l);
            double yi = Unsafe.Add(ref bi, l);
            double pr = xr + yr;
            double pi = xi - yi;
            double mr = xr - yr;
            double mi = xi + yi;
            Unsafe.Add(ref outRe, l) = (pr * c) - (pi * s) + ((mr * d) - (mi * t));
            Unsafe.Add(ref outIm, l) = (pr * s) + (pi * c) + ((mr * t) + (mi * d));
        }
    }

    /// <summary>
    /// The DCT-IV's packing and turn (<see cref="QmfDctIV.Pretransform"/>) lane by lane: with a = x(2m) and b = sign
    /// x(63 - 2m), (a - i b)(c + i s).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void DctPretransform(ref double even, ref double odd, double sign, double c, double s, ref double outRe, ref double outIm, int lanes)
    {
        int l = 0;
#if NET
        if (Vector256.IsHardwareAccelerated)
        {
            var vsign = Vector256.Create(sign);
            var vc = Vector256.Create(c);
            var vs = Vector256.Create(s);
            for (; l <= lanes - Vector256<double>.Count; l += Vector256<double>.Count)
            {
                var a = Vector256.LoadUnsafe(ref even, (nuint)l);
                var b = vsign * Vector256.LoadUnsafe(ref odd, (nuint)l);
                ((a * vc) + (b * vs)).StoreUnsafe(ref outRe, (nuint)l);
                ((a * vs) - (b * vc)).StoreUnsafe(ref outIm, (nuint)l);
            }
        }

        if (Vector128.IsHardwareAccelerated)
        {
            var vsign = Vector128.Create(sign);
            var vc = Vector128.Create(c);
            var vs = Vector128.Create(s);
            for (; l <= lanes - Vector128<double>.Count; l += Vector128<double>.Count)
            {
                var a = Vector128.LoadUnsafe(ref even, (nuint)l);
                var b = vsign * Vector128.LoadUnsafe(ref odd, (nuint)l);
                ((a * vc) + (b * vs)).StoreUnsafe(ref outRe, (nuint)l);
                ((a * vs) - (b * vc)).StoreUnsafe(ref outIm, (nuint)l);
            }
        }
#endif

        for (; l < lanes; l++)
        {
            double a = Unsafe.Add(ref even, l);
            double b = sign * Unsafe.Add(ref odd, l);
            Unsafe.Add(ref outRe, l) = (a * c) + (b * s);
            Unsafe.Add(ref outIm, l) = (a * s) - (b * c);
        }
    }

    /// <summary>
    /// The full synthesis bank's v at 2p, 63 - 2p, 64 + 2p and 127 - 2p from the two DCT-IVs' FFT values at p (A's
    /// (ar, ai), B's (br, bi)) and their turn (c, s), lane by lane, as <see cref="QmfSynthesis.FullTransform"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SynthesisOutput(ref double ar, ref double ai, ref double br, ref double bi, double c, double s, ref double v0, ref double v1, ref double v2, ref double v3, int lanes)
    {
        int l = 0;
#if NET
        if (Vector256.IsHardwareAccelerated)
        {
            var vc = Vector256.Create(c);
            var vs = Vector256.Create(s);
            for (; l <= lanes - Vector256<double>.Count; l += Vector256<double>.Count)
            {
                var yr = Vector256.LoadUnsafe(ref ar, (nuint)l);
                var yi = Vector256.LoadUnsafe(ref ai, (nuint)l);
                var a = (yr * vc) - (yi * vs);
                var aOdd = (yr * vs) + (yi * vc);
                yr = Vector256.LoadUnsafe(ref br, (nuint)l);
                yi = Vector256.LoadUnsafe(ref bi, (nuint)l);
                var b = (yr * vc) - (yi * vs);
                var bOdd = (yr * vs) + (yi * vc);
                (bOdd - a).StoreUnsafe(ref v0, (nuint)l);
                (b - aOdd).StoreUnsafe(ref v1, (nuint)l);
                (aOdd + b).StoreUnsafe(ref v2, (nuint)l);
                (a + bOdd).StoreUnsafe(ref v3, (nuint)l);
            }
        }

        if (Vector128.IsHardwareAccelerated)
        {
            var vc = Vector128.Create(c);
            var vs = Vector128.Create(s);
            for (; l <= lanes - Vector128<double>.Count; l += Vector128<double>.Count)
            {
                var yr = Vector128.LoadUnsafe(ref ar, (nuint)l);
                var yi = Vector128.LoadUnsafe(ref ai, (nuint)l);
                var a = (yr * vc) - (yi * vs);
                var aOdd = (yr * vs) + (yi * vc);
                yr = Vector128.LoadUnsafe(ref br, (nuint)l);
                yi = Vector128.LoadUnsafe(ref bi, (nuint)l);
                var b = (yr * vc) - (yi * vs);
                var bOdd = (yr * vs) + (yi * vc);
                (bOdd - a).StoreUnsafe(ref v0, (nuint)l);
                (b - aOdd).StoreUnsafe(ref v1, (nuint)l);
                (aOdd + b).StoreUnsafe(ref v2, (nuint)l);
                (a + bOdd).StoreUnsafe(ref v3, (nuint)l);
            }
        }
#endif

        for (; l < lanes; l++)
        {
            double yr = Unsafe.Add(ref ar, l);
            double yi = Unsafe.Add(ref ai, l);
            double a = (yr * c) - (yi * s);
            double aOdd = (yr * s) + (yi * c);
            yr = Unsafe.Add(ref br, l);
            yi = Unsafe.Add(ref bi, l);
            double b = (yr * c) - (yi * s);
            double bOdd = (yr * s) + (yi * c);
            Unsafe.Add(ref v0, l) = bOdd - a;
            Unsafe.Add(ref v1, l) = b - aOdd;
            Unsafe.Add(ref v2, l) = aOdd + b;
            Unsafe.Add(ref v3, l) = a + bOdd;
        }
    }

    /// <summary>A 4 by 4 transpose: dj[i] = si[j], each of the four rows in and out four values in a run.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Transpose4(ref double s0, ref double s1, ref double s2, ref double s3, ref double d0, ref double d1, ref double d2, ref double d3)
    {
#if NET
        if (Avx.IsSupported)
        {
            // The halves from memory (no shuffle), then one unpack per row out.
            var r0 = Vector256.Create(Vector128.LoadUnsafe(ref s0), Vector128.LoadUnsafe(ref s2)); // s0[0] s0[1] s2[0] s2[1]
            var r1 = Vector256.Create(Vector128.LoadUnsafe(ref s1), Vector128.LoadUnsafe(ref s3));
            var r2 = Vector256.Create(Vector128.LoadUnsafe(ref s0, 2), Vector128.LoadUnsafe(ref s2, 2)); // s0[2] s0[3] s2[2] s2[3]
            var r3 = Vector256.Create(Vector128.LoadUnsafe(ref s1, 2), Vector128.LoadUnsafe(ref s3, 2));
            Avx.UnpackLow(r0, r1).StoreUnsafe(ref d0);
            Avx.UnpackHigh(r0, r1).StoreUnsafe(ref d1);
            Avx.UnpackLow(r2, r3).StoreUnsafe(ref d2);
            Avx.UnpackHigh(r2, r3).StoreUnsafe(ref d3);
            return;
        }

        if (Vector128.IsHardwareAccelerated)
        {
            Transpose2(ref s0, ref s1, ref d0, ref d1);
            Transpose2(ref s2, ref s3, ref Unsafe.Add(ref d0, 2), ref Unsafe.Add(ref d1, 2));
            Transpose2(ref Unsafe.Add(ref s0, 2), ref Unsafe.Add(ref s1, 2), ref d2, ref d3);
            Transpose2(ref Unsafe.Add(ref s2, 2), ref Unsafe.Add(ref s3, 2), ref Unsafe.Add(ref d2, 2), ref Unsafe.Add(ref d3, 2));
            return;
        }
#endif

        for (int j = 0; j < 4; j++)
        {
            double x0 = Unsafe.Add(ref s0, j);
            double x1 = Unsafe.Add(ref s1, j);
            double x2 = Unsafe.Add(ref s2, j);
            double x3 = Unsafe.Add(ref s3, j);
            ref double d = ref j == 0 ? ref d0 : ref j == 1 ? ref d1 : ref j == 2 ? ref d2 : ref d3;
            d = x0;
            Unsafe.Add(ref d, 1) = x1;
            Unsafe.Add(ref d, 2) = x2;
            Unsafe.Add(ref d, 3) = x3;
        }
    }

#if NET
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose2(ref double s0, ref double s1, ref double d0, ref double d1)
    {
        (var low, var high) = QmfState.Zip(Vector128.LoadUnsafe(ref s0), Vector128.LoadUnsafe(ref s1));
        low.StoreUnsafe(ref d0);
        high.StoreUnsafe(ref d1);
    }
#endif
}
