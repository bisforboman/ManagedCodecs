using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif
using ManagedCodecs.Aac.Sbr;

namespace ManagedCodecs.Aac;

/// <summary>
/// The IMDCT of 4.6.18.2. <see cref="Inverse"/> computes it as a DCT-IV of the N/2 coefficients, folded out to N
/// samples, with the DCT-IV done by an N/4-point complex FFT between two twiddle passes, in single precision: four floats
/// a vector where double gave two (13 to 15% off AAC LC on ARM64), within 2e-6 of the block's peak of the definition,
/// and within 4.8e-7 of FFmpeg's output on every sample and real film where double came to 4.2e-7 (docs/conformance.md,
/// AAC). The twiddles are computed in double and rounded once. <see cref="InverseReference"/> evaluates the definition in double, term by term, and is kept for tests.
/// </summary>
internal static class Imdct
{
    private static readonly Plan Long = new(1024);
    private static readonly Plan Short = new(128);
    private static readonly double[] CosLong = CosTable(2048);
    private static readonly double[] CosShort = CosTable(256);

    /// <summary>
    /// x[n] = 2/N * sum over k of spec[k] * cos(2 pi / N * (n + n0) * (k + 1/2)), n0 = (N/2 + 1) / 2,
    /// for n = 0 .. N - 1, where N = 2 * spec.Length (2048 or 256); the spectrum rounded to float, the output widened back.
    /// </summary>
    [SkipLocalsInit]
    public static void Inverse(ReadOnlySpan<double> spec, Span<double> output)
    {
#if NET
        if (Vector128.IsHardwareAccelerated)
        {
            InverseVector(spec, output);
            return;
        }
#endif

        InverseScalar(spec, output);
    }

    /// <summary>The scalar <see cref="Inverse"/>: without vector hardware, and the vector one's reference.</summary>
    [SkipLocalsInit]
    internal static void InverseScalar(ReadOnlySpan<double> spec, Span<double> output)
    {
        var plan = spec.Length == 1024 ? Long : Short;
        int m = plan.Size;
        int quarter = m / 2;
        if (spec.Length < m || output.Length < 2 * m)
        {
            throw new ArgumentException("The spectrum and the output are too short for the transform.");
        }

        Span<float> sourceRe = stackalloc float[quarter];
        Span<float> sourceIm = stackalloc float[quarter];
        Span<float> re = stackalloc float[quarter];
        Span<float> im = stackalloc float[quarter];
        for (int j = 0; j < quarter; j++)
        {
            float a = (float)spec[2 * j];
            float b = (float)spec[m - 1 - (2 * j)];
            float c = plan.PreCos[j];
            float s = plan.PreSin[j];
            sourceRe[j] = (a * c) + (b * s);
            sourceIm[j] = (b * c) - (a * s);
        }

        plan.Transform(sourceRe, sourceIm, re, im, vector: false);

        Span<float> u = stackalloc float[m];
        for (int l = 0; l < quarter; l++)
        {
            float c = plan.PostCos[l];
            float s = plan.PostSin[l];
            u[2 * l] = (re[l] * c) + (im[l] * s);
            u[m - 1 - (2 * l)] = -((im[l] * c) - (re[l] * s));
        }

        float scale = 1f / m;
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

#if NET
    /// <summary><see cref="InverseScalar"/> four floats a vector, each lane its arithmetic in its order.</summary>
    [SkipLocalsInit]
    private static void InverseVector(ReadOnlySpan<double> spec, Span<double> output)
    {
        var plan = spec.Length == 1024 ? Long : Short;
        int m = plan.Size;
        int quarter = m / 2;
        if (spec.Length < m || output.Length < 2 * m)
        {
            throw new ArgumentException("The spectrum and the output are too short for the transform.");
        }

        Span<float> sourceRe = stackalloc float[quarter];
        Span<float> sourceIm = stackalloc float[quarter];
        Span<float> re = stackalloc float[quarter];
        Span<float> im = stackalloc float[quarter];

        // The pre-twiddle four j at a time: X[2j] and X[M-1-2j] gathered by zips of doubles, then narrowed.
        ref double x = ref MemoryMarshal.GetReference(spec);
        ref float preCos = ref MemoryMarshal.GetArrayDataReference(plan.PreCos);
        ref float preSin = ref MemoryMarshal.GetArrayDataReference(plan.PreSin);
        ref float sourceReRef = ref MemoryMarshal.GetReference(sourceRe);
        ref float sourceImRef = ref MemoryMarshal.GetReference(sourceIm);
        for (int j = 0; j < quarter; j += 4)
        {
            var a0 = QmfState.Zip(Vector128.LoadUnsafe(ref x, (nuint)(2 * j)), Vector128.LoadUnsafe(ref x, (nuint)((2 * j) + 2))).Low;
            var a1 = QmfState.Zip(Vector128.LoadUnsafe(ref x, (nuint)((2 * j) + 4)), Vector128.LoadUnsafe(ref x, (nuint)((2 * j) + 6))).Low;
            var b0 = QmfState.Zip(Vector128.LoadUnsafe(ref x, (nuint)(m - 2 - (2 * j))), Vector128.LoadUnsafe(ref x, (nuint)(m - 4 - (2 * j)))).High;
            var b1 = QmfState.Zip(Vector128.LoadUnsafe(ref x, (nuint)(m - 6 - (2 * j))), Vector128.LoadUnsafe(ref x, (nuint)(m - 8 - (2 * j)))).High;
            var a = Vector128.Narrow(a0, a1);
            var b = Vector128.Narrow(b0, b1);
            var c = Vector128.LoadUnsafe(ref preCos, (nuint)j);
            var sn = Vector128.LoadUnsafe(ref preSin, (nuint)j);
            ((a * c) + (b * sn)).StoreUnsafe(ref sourceReRef, (nuint)j);
            ((b * c) - (a * sn)).StoreUnsafe(ref sourceImRef, (nuint)j);
        }

        plan.Transform(sourceRe, sourceIm, re, im, vector: true);
        ref float reRef = ref MemoryMarshal.GetReference(re);
        ref float imRef = ref MemoryMarshal.GetReference(im);

        // The post-twiddle: p[l] = u[2l], q[l] = u[M-1-2l]; u's eight values from 2l on are p[l..l+3] zipped with
        // q[quarter-4-l..quarter-1-l] reversed.
        Span<float> u = stackalloc float[m];
        Span<float> q = stackalloc float[quarter];
        ref float postCos = ref MemoryMarshal.GetArrayDataReference(plan.PostCos);
        ref float postSin = ref MemoryMarshal.GetArrayDataReference(plan.PostSin);
        ref float qRef = ref MemoryMarshal.GetReference(q);
        ref float uRef = ref MemoryMarshal.GetReference(u);
        for (int l = 0; l < quarter; l += 4)
        {
            var c = Vector128.LoadUnsafe(ref postCos, (nuint)l);
            var s = Vector128.LoadUnsafe(ref postSin, (nuint)l);
            var r = Vector128.LoadUnsafe(ref reRef, (nuint)l);
            var i = Vector128.LoadUnsafe(ref imRef, (nuint)l);
            ((r * c) + (i * s)).StoreUnsafe(ref reRef, (nuint)l);
            (-((i * c) - (r * s))).StoreUnsafe(ref qRef, (nuint)l);
        }

        for (int l = 0; l < quarter; l += 4)
        {
            var p = Vector128.LoadUnsafe(ref reRef, (nuint)l);
            var odd = Reverse(Vector128.LoadUnsafe(ref qRef, (nuint)(quarter - 4 - l)));
            var (low, high) = Zip(p, odd);
            low.StoreUnsafe(ref uRef, (nuint)(2 * l));
            high.StoreUnsafe(ref uRef, (nuint)((2 * l) + 4));
        }

        // The fold, four floats a vector, widened to doubles.
        var scale = Vector128.Create(1f / m);
        int half = m / 2;
        ref double y = ref MemoryMarshal.GetReference(output);
        for (int n = 0; n < half; n += 4)
        {
            Store(Vector128.LoadUnsafe(ref uRef, (nuint)(n + half)) * scale, ref y, n);
        }

        for (int n = half; n < 3 * half; n += 4)
        {
            // output[n .. n + 3] are -u[3M/2 - 1 - n] down to -u[3M/2 - 4 - n].
            Store(-Reverse(Vector128.LoadUnsafe(ref uRef, (nuint)((3 * half) - 4 - n))) * scale, ref y, n);
        }

        for (int n = 3 * half; n < 2 * m; n += 4)
        {
            Store(-Vector128.LoadUnsafe(ref uRef, (nuint)(n - (3 * half))) * scale, ref y, n);
        }
    }

    private static void Store(Vector128<float> v, ref double y, int n)
    {
        var (low, high) = Vector128.Widen(v);
        low.StoreUnsafe(ref y, (nuint)n);
        high.StoreUnsafe(ref y, (nuint)(n + 2));
    }

    private static Vector128<float> Reverse(Vector128<float> v) => Vector128.Shuffle(v, Vector128.Create(3, 2, 1, 0));

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

    /// <summary>
    /// The FFT of <see cref="Inverse"/> (length 512 or 64) in place, for tests: the vector one, the scalar one, or with
    /// <paramref name="reference"/> the one by the book.
    /// </summary>
    internal static void Fft(Span<float> re, Span<float> im, bool vector, bool reference = false)
    {
        var plan = re.Length == Long.Size / 2 ? Long : Short;
        if (reference)
        {
            plan.FftReference(re, im);
            return;
        }

        float[] sourceRe = re.ToArray();
        float[] sourceIm = im.ToArray();
        plan.Transform(sourceRe, sourceIm, re, im, vector);
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

    /// <summary>Twiddles and an in-place radix-2 FFT for one DCT-IV size M (FFT length M / 2), in float.</summary>
    private sealed class Plan
    {
        private readonly int[] _bitReverse;
        private readonly float[] _stageCos;
        private readonly float[] _stageSin;

        public Plan(int size)
        {
            Size = size;
            int length = size / 2;
            PreCos = new float[length];
            PreSin = new float[length];
            PostCos = new float[length];
            PostSin = new float[length];
            for (int j = 0; j < length; j++)
            {
                double pre = Math.PI * (j + 0.25) / size;
                PreCos[j] = (float)Math.Cos(pre);
                PreSin[j] = (float)Math.Sin(pre);
                double post = Math.PI * j / size;
                PostCos[j] = (float)Math.Cos(post);
                PostSin[j] = (float)Math.Sin(post);
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

            _stageCos = new float[length];
            _stageSin = new float[length];
            for (int span = 1; span < length; span *= 2)
            {
                for (int k = 0; k < span; k++)
                {
                    int t = k * (length / (2 * span));
                    _stageCos[span - 1 + k] = (float)Math.Cos(2 * Math.PI * t / length);
                    _stageSin[span - 1 + k] = (float)-Math.Sin(2 * Math.PI * t / length);
                }
            }
        }

        public int Size { get; }

        public float[] PreCos { get; }

        public float[] PreSin { get; }

        public float[] PostCos { get; }

        public float[] PostSin { get; }

        /// <summary>
        /// Forward FFT, X[l] = sum of x[j] e^(-2 pi i j l / length), from the source into re and im: the bit reversal and
        /// the first stage (twiddle 1) in one pass, then the later stages, four butterflies a vector where
        /// <paramref name="vector"/> and the hardware allow. Every path does each butterfly's arithmetic in the same
        /// order (no fused multiply-add), so the vector and scalar results are the same to the bit; against
        /// <see cref="FftReference"/>, which multiplies the first stage by 1 - 0i, they differ at most in a zero's sign.
        /// </summary>
        public void Transform(ReadOnlySpan<float> sourceRe, ReadOnlySpan<float> sourceIm, Span<float> re, Span<float> im, bool vector)
        {
            int length = re.Length;
            if (sourceRe.Length < length || sourceIm.Length < length || im.Length < length || length != Size / 2)
            {
                throw new ArgumentException("The FFT's buffers do not match its length.");
            }

            ref float r = ref MemoryMarshal.GetReference(re);
            ref float i = ref MemoryMarshal.GetReference(im);
            ref float cos = ref MemoryMarshal.GetArrayDataReference(_stageCos);
            ref float sin = ref MemoryMarshal.GetArrayDataReference(_stageSin);
#if NET
            if (vector && Vector128.IsHardwareAccelerated)
            {
                ReorderFirstStageVector(sourceRe, sourceIm, re, im);
                SecondStageVector(ref r, ref i, ref cos, ref sin, length);
                for (int span = 4; span < length; span *= 2)
                {
                    int k0 = span - 1;
                    for (int start = 0; start < length; start += 2 * span)
                    {
                        for (int k = 0; k < span; k += Vector128<float>.Count)
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

                return;
            }
#endif

            ReorderFirstStage(sourceRe, sourceIm, re, im);
            for (int span = 2; span < length; span *= 2)
            {
                int k0 = span - 1;
                for (int start = 0; start < length; start += 2 * span)
                {
                    for (int k = 0; k < span; k++)
                    {
                        float c = Unsafe.Add(ref cos, k0 + k);
                        float s = Unsafe.Add(ref sin, k0 + k);
                        int a = start + k;
                        int b = a + span;
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

        /// <summary>
        /// The FFT by the book, in place: the bit reversal as swaps, then every stage's butterflies one at a time with the
        /// roots by stride, the first stage's included. The reference for <see cref="Transform"/> in tests.
        /// </summary>
        public void FftReference(Span<float> re, Span<float> im)
        {
            int length = re.Length;
            for (int x = 0; x < length; x++)
            {
                int y = _bitReverse[x];
                if (y > x)
                {
                    (re[x], re[y]) = (re[y], re[x]);
                    (im[x], im[y]) = (im[y], im[x]);
                }
            }

            for (int span = 1; span < length; span *= 2)
            {
                int stride = length / (2 * span);
                for (int start = 0; start < length; start += 2 * span)
                {
                    for (int k = 0; k < span; k++)
                    {
                        int t = k * stride;
                        float c = (float)Math.Cos(2 * Math.PI * t / length);
                        float s = (float)-Math.Sin(2 * Math.PI * t / length);
                        int a = start + k;
                        int b = a + span;
                        float tr = (re[b] * c) - (im[b] * s);
                        float ti = (re[b] * s) + (im[b] * c);
                        re[b] = re[a] - tr;
                        im[b] = im[a] - ti;
                        re[a] += tr;
                        im[a] += ti;
                    }
                }
            }
        }

        /// <summary>
        /// The bit reversal and the first stage (half-span 1, twiddle 1) in one pass. With h = length / 2 and j below h,
        /// reverse(j) = a is even and reverse(j + h) = a + 1, so the stage's sum x[j] + x[j + h] lands at a and its
        /// difference at a + 1; for odd j, a = reverse(j - 1) + h.
        /// </summary>
        private void ReorderFirstStage(ReadOnlySpan<float> sourceRe, ReadOnlySpan<float> sourceIm, Span<float> re, Span<float> im)
        {
            int h = re.Length / 2;
            for (int j = 0; j < h; j++)
            {
                int a = _bitReverse[j];
                float ar = sourceRe[j];
                float br = sourceRe[j + h];
                float ai = sourceIm[j];
                float bi = sourceIm[j + h];
                re[a] = ar + br;
                im[a] = ai + bi;
                re[a + 1] = ar - br;
                im[a + 1] = ai - bi;
            }
        }

#if NET
        /// <summary>
        /// <see cref="ReorderFirstStage"/> four j at a time: for j a multiple of 4 and a = reverse(j), j + 1, j + 2 and
        /// j + 3 land at a + h, a + h / 2 and a + 3h / 2. The sums and differences zipped give each j its pair, stored as
        /// one 64-bit lane.
        /// </summary>
        private void ReorderFirstStageVector(ReadOnlySpan<float> sourceRe, ReadOnlySpan<float> sourceIm, Span<float> re, Span<float> im)
        {
            int h = re.Length / 2;
            int quarter = h / 2;
            ref float xr = ref MemoryMarshal.GetReference(sourceRe);
            ref float xi = ref MemoryMarshal.GetReference(sourceIm);
            ref float r = ref MemoryMarshal.GetReference(re);
            ref float i = ref MemoryMarshal.GetReference(im);
            ref int reversed = ref MemoryMarshal.GetArrayDataReference(_bitReverse);
            for (int j = 0; j < h; j += 4)
            {
                int a = Unsafe.Add(ref reversed, j);
                var ar = Vector128.LoadUnsafe(ref xr, (nuint)j);
                var br = Vector128.LoadUnsafe(ref xr, (nuint)(j + h));
                var ai = Vector128.LoadUnsafe(ref xi, (nuint)j);
                var bi = Vector128.LoadUnsafe(ref xi, (nuint)(j + h));
                StorePairs(ref r, a, h, quarter, ar + br, ar - br);
                StorePairs(ref i, a, h, quarter, ai + bi, ai - bi);
            }
        }

        private static void StorePairs(ref float destination, int a, int h, int quarter, Vector128<float> sum, Vector128<float> difference)
        {
            var (low, high) = Zip(sum, difference);
            var lowPairs = low.AsDouble();
            var highPairs = high.AsDouble();
            Unsafe.WriteUnaligned(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref destination, a)), lowPairs.ToScalar());
            Unsafe.WriteUnaligned(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref destination, a + h)), lowPairs.GetElement(1));
            Unsafe.WriteUnaligned(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref destination, a + quarter)), highPairs.ToScalar());
            Unsafe.WriteUnaligned(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref destination, a + h + quarter)), highPairs.GetElement(1));
        }

        /// <summary>
        /// The second stage (half-span 2), two groups of four a pass: the groups' a halves and b halves split by 64-bit
        /// zips, each lane the scalar butterfly, zipped back.
        /// </summary>
        private static void SecondStageVector(ref float r, ref float i, ref float cos, ref float sin, int length)
        {
            // Half-span 2's roots are at 1 and 2, the same for both groups.
            var c = Vector128.Create(Unsafe.Add(ref cos, 1), Unsafe.Add(ref cos, 2), Unsafe.Add(ref cos, 1), Unsafe.Add(ref cos, 2));
            var s = Vector128.Create(Unsafe.Add(ref sin, 1), Unsafe.Add(ref sin, 2), Unsafe.Add(ref sin, 1), Unsafe.Add(ref sin, 2));
            for (int start = 0; start < length; start += 8)
            {
                var (ra, rb) = QmfState.Zip(Vector128.LoadUnsafe(ref r, (nuint)start).AsDouble(), Vector128.LoadUnsafe(ref r, (nuint)(start + 4)).AsDouble());
                var (ia, ib) = QmfState.Zip(Vector128.LoadUnsafe(ref i, (nuint)start).AsDouble(), Vector128.LoadUnsafe(ref i, (nuint)(start + 4)).AsDouble());
                var rbf = rb.AsSingle();
                var ibf = ib.AsSingle();
                var raf = ra.AsSingle();
                var iaf = ia.AsSingle();
                var tr = (rbf * c) - (ibf * s);
                var ti = (rbf * s) + (ibf * c);
                var (r0, r1) = QmfState.Zip((raf + tr).AsDouble(), (raf - tr).AsDouble());
                var (i0, i1) = QmfState.Zip((iaf + ti).AsDouble(), (iaf - ti).AsDouble());
                r0.AsSingle().StoreUnsafe(ref r, (nuint)start);
                r1.AsSingle().StoreUnsafe(ref r, (nuint)(start + 4));
                i0.AsSingle().StoreUnsafe(ref i, (nuint)start);
                i1.AsSingle().StoreUnsafe(ref i, (nuint)(start + 4));
            }
        }
#endif
    }
}
