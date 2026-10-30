using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace ManagedCodecs.H264;

/// <summary>Scaling (dequantization) and inverse transforms for 4x4 and 8x8 blocks (H.264 clause 8.5).</summary>
/// <remarks>
/// LevelScale4x4 and LevelScale8x8 are the scaling list's weights times the normAdjust values v of
/// equations 8-315 and 8-318; <see cref="ScalingMatrix"/> holds them per list. Blocks are row-major:
/// index = row * size + column.
/// </remarks>
internal static class Transform
{
    /// <summary>Frame zig-zag scan (Table 8-13): scan position to row-major index.</summary>
    public static ReadOnlySpan<byte> ZigZag4x4 => [0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15];

    // normAdjust4x4 (8-315) per qP % 6: positions with both coordinates even, both odd, mixed.
    private static ReadOnlySpan<byte> NormEven => [10, 11, 13, 14, 16, 18];

    private static ReadOnlySpan<byte> NormOdd => [16, 18, 20, 23, 25, 29];

    private static ReadOnlySpan<byte> NormMixed => [13, 14, 16, 18, 20, 23];

    /// <summary>LevelScale4x4(qP % 6, row, column) for a flat matrix (weights 16): 16 times normAdjust4x4.</summary>
    public static int LevelScale(int qpRem, int index)
    {
        int row = index >> 2;
        int column = index & 3;
        int v = (row & 1, column & 1) switch
        {
            (0, 0) => NormEven[qpRem],
            (1, 1) => NormOdd[qpRem],
            _ => NormMixed[qpRem],
        };

        return 16 * v;
    }

    /// <summary>
    /// LevelScale4x4 for every qP % 6 and position (entry qpRem * 16 + index) of a scaling list given
    /// in zig-zag order (8-315).
    /// </summary>
    public static int[] LevelScale4x4Table(ReadOnlySpan<byte> list)
    {
        var table = new int[6 * 16];
        for (int k = 0; k < 16; k++)
        {
            int index = ZigZag4x4[k];
            for (int m = 0; m < 6; m++)
            {
                table[(m * 16) + index] = list[k] * LevelScale(m, index) / 16;
            }
        }

        return table;
    }

    /// <summary>
    /// Scales the coefficients of a 4x4 block in place (8.5.12.1), with the LevelScale4x4 table of
    /// its scaling list. With <paramref name="skipDc"/> the DC is left alone because it was already
    /// produced by a DC transform.
    /// </summary>
    public static void Dequantize(Span<int> block, int qp, bool skipDc, ReadOnlySpan<int> levelScale)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            DequantizeScalar(block, qp, skipDc, levelScale);
            return;
        }

        // The same arithmetic four coefficients at a time (32-bit lanes, so the same result for every input), the DC
        // put back where a DC transform made it.
        _ = block[15];
        var scale = levelScale.Slice((qp % 6) * 16, 16);
        int qpDiv = qp / 6;
        int dc = block[0];
        ref int values = ref MemoryMarshal.GetReference(block);
        ref int factors = ref MemoryMarshal.GetReference(scale);
        if (qp >= 24)
        {
            int shift = qpDiv - 4;
            for (int i = 0; i < 16; i += 4)
            {
                var product = Vector128.LoadUnsafe(ref values, (nuint)i) * Vector128.LoadUnsafe(ref factors, (nuint)i);
                (product << shift).StoreUnsafe(ref values, (nuint)i);
            }
        }
        else
        {
            int shift = 4 - qpDiv;
            var round = Vector128.Create(1 << (3 - qpDiv));
            for (int i = 0; i < 16; i += 4)
            {
                var product = Vector128.LoadUnsafe(ref values, (nuint)i) * Vector128.LoadUnsafe(ref factors, (nuint)i);
                ((product + round) >> shift).StoreUnsafe(ref values, (nuint)i);
            }
        }

        if (skipDc)
        {
            block[0] = dc;
        }
    }

    /// <summary>The scalar <see cref="Dequantize"/>: without vector hardware, and the vector one's reference.</summary>
    internal static void DequantizeScalar(Span<int> block, int qp, bool skipDc, ReadOnlySpan<int> levelScale)
    {
        int qpDiv = qp / 6;
        var scale = levelScale.Slice((qp % 6) * 16, 16);
        int first = skipDc ? 1 : 0;
        if (qp >= 24)
        {
            int shift = qpDiv - 4;
            for (int i = first; i < 16; i++)
            {
                block[i] = (block[i] * scale[i]) << shift;
            }
        }
        else
        {
            int shift = 4 - qpDiv;
            int round = 1 << (3 - qpDiv);
            for (int i = first; i < 16; i++)
            {
                block[i] = ((block[i] * scale[i]) + round) >> shift;
            }
        }
    }

    /// <summary>8x8 frame zig-zag scan (Table 8-14): scan position to row-major index.</summary>
    public static ReadOnlySpan<byte> ZigZag8x8 =>
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    // normAdjust8x8 (8-318): v per qP % 6 (rows) and position class (columns, see Class8x8).
    private static ReadOnlySpan<byte> Norm8x8 =>
    [
        20, 18, 32, 19, 25, 24,
        22, 19, 35, 21, 28, 26,
        26, 23, 42, 24, 33, 31,
        28, 25, 45, 26, 35, 33,
        32, 28, 51, 30, 40, 38,
        36, 32, 58, 34, 46, 43,
    ];

    /// <summary>Which column of v (8-318) applies at row i, column j of an 8x8 block.</summary>
    private static int Class8x8(int i, int j) =>
        (i % 4 == 0 && j % 4 == 0) ? 0
        : (i % 2 == 1 && j % 2 == 1) ? 1
        : (i % 4 == 2 && j % 4 == 2) ? 2
        : ((i % 4 == 0 && j % 2 == 1) || (i % 2 == 1 && j % 4 == 0)) ? 3
        : ((i % 4 == 0 && j % 4 == 2) || (i % 4 == 2 && j % 4 == 0)) ? 4
        : 5;

    /// <summary>
    /// LevelScale8x8 for every qP % 6 and position (entry qpRem * 64 + index) of a scaling list given
    /// in zig-zag order (8-318).
    /// </summary>
    public static int[] LevelScale8x8Table(ReadOnlySpan<byte> list)
    {
        var table = new int[6 * 64];
        for (int k = 0; k < 64; k++)
        {
            int index = ZigZag8x8[k];
            for (int m = 0; m < 6; m++)
            {
                table[(m * 64) + index] = list[k] * Norm8x8[(m * 6) + Class8x8(index >> 3, index & 7)];
            }
        }

        return table;
    }

    /// <summary>Scales the coefficients of an 8x8 block in place (8.5.13.1), with the LevelScale8x8 table of its scaling list.</summary>
    public static void Dequantize8x8(Span<int> block, int qp, ReadOnlySpan<int> levelScale)
    {
        int qpDiv = qp / 6;
        var scale = levelScale.Slice((qp % 6) * 64, 64);
        if (qp >= 36)
        {
            int shift = qpDiv - 6;
            for (int i = 0; i < 64; i++)
            {
                block[i] = (block[i] * scale[i]) << shift;
            }
        }
        else
        {
            int shift = 6 - qpDiv;
            int round = 1 << (5 - qpDiv);
            for (int i = 0; i < 64; i++)
            {
                block[i] = ((block[i] * scale[i]) + round) >> shift;
            }
        }
    }

    /// <summary>
    /// 8x8 inverse transform (8.5.13.2) of scaled coefficients, added to the prediction already
    /// in <paramref name="dest"/> and clipped to <paramref name="bitDepth"/> bits (8.5.14).
    /// </summary>
    public static void InverseTransform8x8Add<TPixel>(Span<int> d, Span<TPixel> dest, int stride, int bitDepth)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        if (typeof(TPixel) == typeof(byte))
        {
            InverseTransform8x8Add(d, MemoryMarshal.Cast<TPixel, byte>(dest), stride);
            return;
        }

        InverseTransform8x8AddScalar(d, dest, stride, bitDepth);
    }

    /// <summary>
    /// <see cref="InverseTransform8x8Add{TPixel}"/> for 8-bit samples: the rows' transform as in the scalar one, then the
    /// columns' four columns at a time (<see cref="Columns8"/>), each output row added to its eight samples and clipped
    /// in 32-bit lanes. The same integer arithmetic as <see cref="InverseTransform8x8AddScalar"/>, its reference.
    /// </summary>
    [SkipLocalsInit]
    private static void InverseTransform8x8Add(Span<int> d, Span<byte> dest, int stride)
    {
        _ = d[63];
        _ = dest[(7 * stride) + 7];
        if (Simd.IsSupported && InverseTransform8x8Add16(d, dest, stride))
        {
            return;
        }

        Span<int> g = stackalloc int[64];
        for (int i = 0; i < 8; i++)
        {
            Transform8(d, i * 8, 1, g, i * 8);
        }

        Span<Vector128<int>> left = stackalloc Vector128<int>[8];
        Span<Vector128<int>> right = stackalloc Vector128<int>[8];
        ref int rows = ref MemoryMarshal.GetReference(g);
        Columns8(ref rows, left);
        Columns8(ref Unsafe.Add(ref rows, 4), right);
        var round = Vector128.Create(32);
        ref byte target = ref MemoryMarshal.GetReference(dest);
        for (int i = 0; i < 8; i++)
        {
            AddRow(ref Unsafe.Add(ref target, i * stride), (left[i] + round) >> 6);
            AddRow(ref Unsafe.Add(ref target, (i * stride) + 4), (right[i] + round) >> 6);
        }
    }

    /// <summary>
    /// <see cref="InverseTransform8x8Add(Span{int}, Span{byte}, int)"/> in 16-bit lanes, a row (or column) a vector, as
    /// FFmpeg's 8-bit transform, for coefficients under 2^9 in magnitude (false, and nothing written, for others): one
    /// pass of the 8-point transform multiplies the largest magnitude by at most 7.375 (its intermediate values too),
    /// both by 54.4, so from those every value is under 54.4 (2^9 + 32) and a few for the shifts' floors, within 16
    /// bits. Transposed, each lane is a row for the rows' pass; transposed back, a column for the columns'. The rounding
    /// is 32 added to the DC, which reaches every output once, and the clip to 8 bits the saturating pack.
    /// </summary>
    [SkipLocalsInit]
    private static bool InverseTransform8x8Add16(Span<int> d, Span<byte> dest, int stride)
    {
        ref int coefficients = ref MemoryMarshal.GetReference(d);
        var magnitudes = Vector128<int>.Zero;
        for (int i = 0; i < 64; i += 4)
        {
            magnitudes |= Vector128.Abs(Vector128.LoadUnsafe(ref coefficients, (nuint)i));
        }

        if ((magnitudes & Vector128.Create(~511)) != Vector128<int>.Zero)
        {
            return false;
        }

        var v0 = Pack(ref coefficients, 0) + Vector128.CreateScalar((short)32);
        var v1 = Pack(ref coefficients, 8);
        var v2 = Pack(ref coefficients, 16);
        var v3 = Pack(ref coefficients, 24);
        var v4 = Pack(ref coefficients, 32);
        var v5 = Pack(ref coefficients, 40);
        var v6 = Pack(ref coefficients, 48);
        var v7 = Pack(ref coefficients, 56);
        Transpose8x8(ref v0, ref v1, ref v2, ref v3, ref v4, ref v5, ref v6, ref v7);
        Transform8Lanes(ref v0, ref v1, ref v2, ref v3, ref v4, ref v5, ref v6, ref v7);
        Transpose8x8(ref v0, ref v1, ref v2, ref v3, ref v4, ref v5, ref v6, ref v7);
        Transform8Lanes(ref v0, ref v1, ref v2, ref v3, ref v4, ref v5, ref v6, ref v7);

        ref byte target = ref MemoryMarshal.GetReference(dest);
        AddRows(ref target, stride, v0, v1);
        AddRows(ref Unsafe.Add(ref target, 2 * stride), stride, v2, v3);
        AddRows(ref Unsafe.Add(ref target, 4 * stride), stride, v4, v5);
        AddRows(ref Unsafe.Add(ref target, 6 * stride), stride, v6, v7);
        return true;

        static Vector128<short> Pack(ref int row, int offset) =>
            Simd.PackSignedSaturate(Vector128.LoadUnsafe(ref row, (nuint)offset), Vector128.LoadUnsafe(ref row, (nuint)(offset + 4)));
    }

    /// <summary>Two rows of residuals, each shifted down 6, added to their 8 samples and clipped to 8 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddRows(ref byte row0, int stride, Vector128<short> residual0, Vector128<short> residual1)
    {
        ref byte row1 = ref Unsafe.Add(ref row0, stride);
        var prediction = Vector128.Create(Unsafe.ReadUnaligned<ulong>(ref row0), Unsafe.ReadUnaligned<ulong>(ref row1)).AsByte();
        var sum0 = Simd.Add(Simd.UnpackLow(prediction, Vector128<byte>.Zero).AsInt16(), Simd.ShiftRightArithmetic(residual0, 6));
        var sum1 = Simd.Add(Simd.UnpackHigh(prediction, Vector128<byte>.Zero).AsInt16(), Simd.ShiftRightArithmetic(residual1, 6));
        var samples = Simd.PackUnsignedSaturate(sum0, sum1).AsUInt64();
        Unsafe.WriteUnaligned(ref row0, samples.GetElement(0));
        Unsafe.WriteUnaligned(ref row1, samples.GetElement(1));
    }

    /// <summary>Transposes an 8x8 matrix of shorts held a row a vector.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose8x8(ref Vector128<short> v0, ref Vector128<short> v1, ref Vector128<short> v2, ref Vector128<short> v3, ref Vector128<short> v4, ref Vector128<short> v5, ref Vector128<short> v6, ref Vector128<short> v7)
    {
        var t0 = Simd.UnpackLow(v0, v1);
        var t1 = Simd.UnpackHigh(v0, v1);
        var t2 = Simd.UnpackLow(v2, v3);
        var t3 = Simd.UnpackHigh(v2, v3);
        var t4 = Simd.UnpackLow(v4, v5);
        var t5 = Simd.UnpackHigh(v4, v5);
        var t6 = Simd.UnpackLow(v6, v7);
        var t7 = Simd.UnpackHigh(v6, v7);
        var u0 = Simd.UnpackLow(t0.AsInt32(), t2.AsInt32());
        var u1 = Simd.UnpackHigh(t0.AsInt32(), t2.AsInt32());
        var u2 = Simd.UnpackLow(t1.AsInt32(), t3.AsInt32());
        var u3 = Simd.UnpackHigh(t1.AsInt32(), t3.AsInt32());
        var u4 = Simd.UnpackLow(t4.AsInt32(), t6.AsInt32());
        var u5 = Simd.UnpackHigh(t4.AsInt32(), t6.AsInt32());
        var u6 = Simd.UnpackLow(t5.AsInt32(), t7.AsInt32());
        var u7 = Simd.UnpackHigh(t5.AsInt32(), t7.AsInt32());
        v0 = Simd.UnpackLow(u0.AsInt64(), u4.AsInt64()).AsInt16();
        v1 = Simd.UnpackHigh(u0.AsInt64(), u4.AsInt64()).AsInt16();
        v2 = Simd.UnpackLow(u1.AsInt64(), u5.AsInt64()).AsInt16();
        v3 = Simd.UnpackHigh(u1.AsInt64(), u5.AsInt64()).AsInt16();
        v4 = Simd.UnpackLow(u2.AsInt64(), u6.AsInt64()).AsInt16();
        v5 = Simd.UnpackHigh(u2.AsInt64(), u6.AsInt64()).AsInt16();
        v6 = Simd.UnpackLow(u3.AsInt64(), u7.AsInt64()).AsInt16();
        v7 = Simd.UnpackHigh(u3.AsInt64(), u7.AsInt64()).AsInt16();
    }

    /// <summary>The 8-point transform of 8.5.13.2 lane by lane: input k in vk, output k back there.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transform8Lanes(ref Vector128<short> v0, ref Vector128<short> v1, ref Vector128<short> v2, ref Vector128<short> v3, ref Vector128<short> v4, ref Vector128<short> v5, ref Vector128<short> v6, ref Vector128<short> v7)
    {
        var d0 = v0;
        var d1 = v1;
        var d2 = v2;
        var d3 = v3;
        var d4 = v4;
        var d5 = v5;
        var d6 = v6;
        var d7 = v7;

        var e0 = d0 + d4;
        var e1 = d5 - d3 - d7 - (d7 >> 1);
        var e2 = d0 - d4;
        var e3 = d1 + d7 - d3 - (d3 >> 1);
        var e4 = (d2 >> 1) - d6;
        var e5 = d7 - d1 + d5 + (d5 >> 1);
        var e6 = d2 + (d6 >> 1);
        var e7 = d3 + d5 + d1 + (d1 >> 1);

        var f0 = e0 + e6;
        var f1 = e1 + (e7 >> 2);
        var f2 = e2 + e4;
        var f3 = e3 + (e5 >> 2);
        var f4 = e2 - e4;
        var f5 = (e3 >> 2) - e5;
        var f6 = e0 - e6;
        var f7 = e7 - (e1 >> 2);

        v0 = f0 + f7;
        v1 = f2 + f5;
        v2 = f4 + f3;
        v3 = f6 + f1;
        v4 = f6 - f1;
        v5 = f4 - f3;
        v6 = f2 - f5;
        v7 = f0 - f7;
    }

    /// <summary>
    /// <see cref="Transform8"/> down four columns at once: <paramref name="g"/> is the first of them in row 0 of a
    /// row-major 8x8 block; output row k of the four columns goes to <paramref name="o"/>[k].
    /// </summary>
    private static void Columns8(ref int g, Span<Vector128<int>> o)
    {
        var d0 = Vector128.LoadUnsafe(ref g);
        var d1 = Vector128.LoadUnsafe(ref g, 8);
        var d2 = Vector128.LoadUnsafe(ref g, 16);
        var d3 = Vector128.LoadUnsafe(ref g, 24);
        var d4 = Vector128.LoadUnsafe(ref g, 32);
        var d5 = Vector128.LoadUnsafe(ref g, 40);
        var d6 = Vector128.LoadUnsafe(ref g, 48);
        var d7 = Vector128.LoadUnsafe(ref g, 56);

        var e0 = d0 + d4;
        var e1 = d5 - d3 - d7 - (d7 >> 1);
        var e2 = d0 - d4;
        var e3 = d1 + d7 - d3 - (d3 >> 1);
        var e4 = (d2 >> 1) - d6;
        var e5 = d7 - d1 + d5 + (d5 >> 1);
        var e6 = d2 + (d6 >> 1);
        var e7 = d3 + d5 + d1 + (d1 >> 1);

        var f0 = e0 + e6;
        var f1 = e1 + (e7 >> 2);
        var f2 = e2 + e4;
        var f3 = e3 + (e5 >> 2);
        var f4 = e2 - e4;
        var f5 = (e3 >> 2) - e5;
        var f6 = e0 - e6;
        var f7 = e7 - (e1 >> 2);

        o[0] = f0 + f7;
        o[1] = f2 + f5;
        o[2] = f4 + f3;
        o[3] = f6 + f1;
        o[4] = f6 - f1;
        o[5] = f4 - f3;
        o[6] = f2 - f5;
        o[7] = f0 - f7;
    }

    /// <summary>The scalar <see cref="InverseTransform8x8Add{TPixel}"/>: for deeper samples, and the 8-bit one's reference.</summary>
    [SkipLocalsInit]
    internal static void InverseTransform8x8AddScalar<TPixel>(Span<int> d, Span<TPixel> dest, int stride, int bitDepth)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        int max = Samples.Max<TPixel>(bitDepth);
        Span<int> g = stackalloc int[64];

        // Rows (horizontal) first, then columns.
        for (int i = 0; i < 8; i++)
        {
            Transform8(d, i * 8, 1, g, i * 8);
        }

        Span<int> m = stackalloc int[8];
        for (int j = 0; j < 8; j++)
        {
            Transform8(g, j, 8, m, 0);
            for (int i = 0; i < 8; i++)
            {
                AddClipped(dest, (i * stride) + j, (m[i] + 32) >> 6, max);
            }
        }
    }

    /// <summary>
    /// The one-dimensional 8-point transform of 8-338 to 8-361 on s[start + k * step], k = 0..7,
    /// written to output[outputStart + k].
    /// </summary>
    private static void Transform8(ReadOnlySpan<int> s, int start, int step, Span<int> output, int outputStart)
    {
        int d0 = s[start];
        int d1 = s[start + step];
        int d2 = s[start + (2 * step)];
        int d3 = s[start + (3 * step)];
        int d4 = s[start + (4 * step)];
        int d5 = s[start + (5 * step)];
        int d6 = s[start + (6 * step)];
        int d7 = s[start + (7 * step)];

        int e0 = d0 + d4;
        int e1 = -d3 + d5 - d7 - (d7 >> 1);
        int e2 = d0 - d4;
        int e3 = d1 + d7 - d3 - (d3 >> 1);
        int e4 = (d2 >> 1) - d6;
        int e5 = -d1 + d7 + d5 + (d5 >> 1);
        int e6 = d2 + (d6 >> 1);
        int e7 = d3 + d5 + d1 + (d1 >> 1);

        int f0 = e0 + e6;
        int f1 = e1 + (e7 >> 2);
        int f2 = e2 + e4;
        int f3 = e3 + (e5 >> 2);
        int f4 = e2 - e4;
        int f5 = (e3 >> 2) - e5;
        int f6 = e0 - e6;
        int f7 = e7 - (e1 >> 2);

        var o = output.Slice(outputStart, 8);
        o[0] = f0 + f7;
        o[1] = f2 + f5;
        o[2] = f4 + f3;
        o[3] = f6 + f1;
        o[4] = f6 - f1;
        o[5] = f4 - f3;
        o[6] = f2 - f5;
        o[7] = f0 - f7;
    }

    /// <summary>
    /// Intra 16x16 luma DC (8.5.10): inverse Hadamard of the 4x4 DC levels, then scaling.
    /// Input and output are row-major in the block layout of the macroblock; the scale is
    /// LevelScale4x4(qP % 6, 0, 0) of the Intra Y list.
    /// </summary>
    public static void InverseLumaDc(Span<int> c, int qp, ReadOnlySpan<int> levelScale)
    {
        Span<int> f = stackalloc int[16];
        Hadamard4x4(c, f);

        int qpDiv = qp / 6;
        int scale = levelScale[(qp % 6) * 16];
        for (int i = 0; i < 16; i++)
        {
            c[i] = qp >= 36
                ? (f[i] * scale) << (qpDiv - 6)
                : ((f[i] * scale) + (1 << (5 - qpDiv))) >> (6 - qpDiv);
        }
    }

    /// <summary>
    /// 4:2:0 chroma DC (8.5.11): 2x2 transform, then scaling with LevelScale4x4(qP % 6, 0, 0) of
    /// the component's list. c = [c00, c01, c10, c11].
    /// </summary>
    public static void InverseChromaDc(Span<int> c, int qp, ReadOnlySpan<int> levelScale)
    {
        int f0 = c[0] + c[1] + c[2] + c[3];
        int f1 = c[0] - c[1] + c[2] - c[3];
        int f2 = c[0] + c[1] - c[2] - c[3];
        int f3 = c[0] - c[1] - c[2] + c[3];

        int scale = levelScale[(qp % 6) * 16];
        int qpDiv = qp / 6;
        c[0] = ((f0 * scale) << qpDiv) >> 5;
        c[1] = ((f1 * scale) << qpDiv) >> 5;
        c[2] = ((f2 * scale) << qpDiv) >> 5;
        c[3] = ((f3 * scale) << qpDiv) >> 5;
    }

    /// <summary>
    /// 4:2:2 chroma DC (8.5.11): the 2x4 DC levels (row-major, two to a row) through the 4x4
    /// transform down the columns and the 2x2 one across the rows, then scaling at
    /// qP,DC = qP + 3 with LevelScale4x4(qP,DC % 6, 0, 0) of the component's list.
    /// </summary>
    public static void InverseChromaDc422(Span<int> c, int qp, ReadOnlySpan<int> levelScale)
    {
        Span<int> g = stackalloc int[8];
        for (int i = 0; i < 4; i++)
        {
            g[i * 2] = c[i * 2] + c[(i * 2) + 1];
            g[(i * 2) + 1] = c[i * 2] - c[(i * 2) + 1];
        }

        int qpDc = qp + 3;
        int qpDiv = qpDc / 6;
        int scale = levelScale[(qpDc % 6) * 16];
        for (int j = 0; j < 2; j++)
        {
            int g0 = g[j];
            int g1 = g[2 + j];
            int g2 = g[4 + j];
            int g3 = g[6 + j];
            Scale(ref c[j], g0 + g1 + g2 + g3);
            Scale(ref c[2 + j], g0 + g1 - g2 - g3);
            Scale(ref c[4 + j], g0 - g1 - g2 + g3);
            Scale(ref c[6 + j], g0 - g1 + g2 - g3);
        }

        void Scale(ref int dc, int f) => dc = qpDc >= 36
            ? (f * scale) << (qpDiv - 6)
            : ((f * scale) + (1 << (5 - qpDiv))) >> (6 - qpDiv);
    }

    /// <summary>
    /// 4x4 inverse transform (8.5.12.2) of scaled coefficients, added to the prediction already
    /// in <paramref name="dest"/> and clipped to <paramref name="bitDepth"/> bits (8.5.14).
    /// </summary>
    public static void InverseTransformAdd<TPixel>(Span<int> d, Span<TPixel> dest, int stride, int bitDepth)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        if (typeof(TPixel) == typeof(byte))
        {
            InverseTransformAdd(d, MemoryMarshal.Cast<TPixel, byte>(dest), stride);
            return;
        }

        InverseTransformAddScalar(d, dest, stride, bitDepth);
    }

    /// <summary>
    /// <see cref="InverseTransformAdd{TPixel}"/> for 8-bit samples: the rows' transform as in the scalar one, then the
    /// columns' for all four columns at once, each output row a vector, added to its four samples and clipped in 32-bit
    /// lanes (the same integer arithmetic, so the same result for every input; <see cref="InverseTransformAddScalar"/>
    /// is the reference a test compares it with).
    /// </summary>
    [SkipLocalsInit]
    private static void InverseTransformAdd(Span<int> d, Span<byte> dest, int stride)
    {
        _ = d[15];
        _ = dest[(3 * stride) + 3];
        if (Simd.IsSupported && InverseTransformAdd16(d, dest, stride))
        {
            return;
        }

        Span<int> f = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int d0 = d[i * 4];
            int d1 = d[(i * 4) + 1];
            int d2 = d[(i * 4) + 2];
            int d3 = d[(i * 4) + 3];
            int e0 = d0 + d2;
            int e1 = d0 - d2;
            int e2 = (d1 >> 1) - d3;
            int e3 = d1 + (d3 >> 1);
            f[i * 4] = e0 + e3;
            f[(i * 4) + 1] = e1 + e2;
            f[(i * 4) + 2] = e1 - e2;
            f[(i * 4) + 3] = e0 - e3;
        }

        ref int rows = ref MemoryMarshal.GetReference(f);
        var f0 = Vector128.LoadUnsafe(ref rows);
        var f1 = Vector128.LoadUnsafe(ref rows, 4);
        var f2 = Vector128.LoadUnsafe(ref rows, 8);
        var f3 = Vector128.LoadUnsafe(ref rows, 12);
        var g0 = f0 + f2;
        var g1 = f0 - f2;
        var g2 = (f1 >> 1) - f3;
        var g3 = f1 + (f3 >> 1);
        var round = Vector128.Create(32);
        ref byte target = ref MemoryMarshal.GetReference(dest);
        AddRow(ref target, (g0 + g3 + round) >> 6);
        AddRow(ref Unsafe.Add(ref target, stride), (g1 + g2 + round) >> 6);
        AddRow(ref Unsafe.Add(ref target, 2 * stride), (g1 - g2 + round) >> 6);
        AddRow(ref Unsafe.Add(ref target, 3 * stride), (g0 - g3 + round) >> 6);
    }

    /// <summary>
    /// <see cref="InverseTransformAdd(Span{int}, Span{byte}, int)"/> in 16-bit lanes, two rows (or columns) a vector, as
    /// FFmpeg's 8-bit transform, for coefficients under 2^11 in magnitude (false, and nothing written, for others): from
    /// those, every intermediate value is under 12.25 (2^11 + 32), within 16 bits, so the sums are the scalar ones.
    /// (8.5.12 bounds a conforming 8-bit stream's to 2^15, but JM's encoder, with scaling matrices, exceeds it:
    /// `jm-planes-qm`.) Transposed, each lane is a row for the rows' pass; transposed back, a column for the columns'.
    /// The rounding is 32 added to the DC, which reaches every output once, and the clip to 8 bits the saturating pack.
    /// </summary>
    private static bool InverseTransformAdd16(Span<int> d, Span<byte> dest, int stride)
    {
        ref int coefficients = ref MemoryMarshal.GetReference(d);
        var rows0 = Vector128.LoadUnsafe(ref coefficients);
        var rows1 = Vector128.LoadUnsafe(ref coefficients, 4);
        var rows2 = Vector128.LoadUnsafe(ref coefficients, 8);
        var rows3 = Vector128.LoadUnsafe(ref coefficients, 12);
        var magnitudes = Vector128.Abs(rows0) | Vector128.Abs(rows1) | Vector128.Abs(rows2) | Vector128.Abs(rows3);
        if ((magnitudes & Vector128.Create(~2047)) != Vector128<int>.Zero)
        {
            return false;
        }

        var rows01 = Simd.PackSignedSaturate(rows0 + Vector128.CreateScalar(32), rows1);
        var rows23 = Simd.PackSignedSaturate(rows2, rows3);
        var (columns01, columns23) = Transpose4x4(rows01, rows23);
        var (f01, f32) = Transform4Lanes(columns01, columns23); // f0 | f1 and f3 | f2, a lane a row
        var (g01, g23) = Transpose4x4(f01, Simd.SwapHalves(f32.AsInt32()).AsInt16());
        var (h01, h32) = Transform4Lanes(g01, g23); // rows 0 | 1 and 3 | 2 of the result
        h01 = Simd.ShiftRightArithmetic(h01, 6);
        h32 = Simd.ShiftRightArithmetic(h32, 6);

        ref byte target = ref MemoryMarshal.GetReference(dest);
        ref byte row1 = ref Unsafe.Add(ref target, stride);
        ref byte row2 = ref Unsafe.Add(ref target, 2 * stride);
        ref byte row3 = ref Unsafe.Add(ref target, 3 * stride);
        var prediction01 = Vector128.Create(Unsafe.ReadUnaligned<uint>(ref target), Unsafe.ReadUnaligned<uint>(ref row1), 0, 0).AsByte();
        var prediction32 = Vector128.Create(Unsafe.ReadUnaligned<uint>(ref row3), Unsafe.ReadUnaligned<uint>(ref row2), 0, 0).AsByte();
        var sum01 = Simd.Add(Simd.UnpackLow(prediction01, Vector128<byte>.Zero).AsInt16(), h01);
        var sum32 = Simd.Add(Simd.UnpackLow(prediction32, Vector128<byte>.Zero).AsInt16(), h32);
        var samples = Simd.PackUnsignedSaturate(sum01, sum32).AsUInt32();
        Unsafe.WriteUnaligned(ref target, samples.GetElement(0));
        Unsafe.WriteUnaligned(ref row1, samples.GetElement(1));
        Unsafe.WriteUnaligned(ref row3, samples.GetElement(2));
        Unsafe.WriteUnaligned(ref row2, samples.GetElement(3));
        return true;
    }

    /// <summary>Transposes a 4x4 matrix of shorts held as rows 0 | 1 and rows 2 | 3.</summary>
    private static (Vector128<short> Columns01, Vector128<short> Columns23) Transpose4x4(Vector128<short> rows01, Vector128<short> rows23)
    {
        var low = Simd.UnpackLow(rows01, rows23); // r0c0 r2c0 r0c1 r2c1 r0c2 r2c2 r0c3 r2c3
        var high = Simd.UnpackHigh(rows01, rows23); // r1c0 r3c0 r1c1 r3c1 ...
        var mixed0 = Simd.UnpackLow(low, high); // r0c0 r1c0 r2c0 r3c0 r0c1 r1c1 r2c1 r3c1
        var mixed1 = Simd.UnpackHigh(low, high); // columns 2 and 3
        return (mixed0, mixed1);
    }

    /// <summary>
    /// The 4-point transform of 8.5.12.2 lane by lane, its inputs as x0 | x1 and x2 | x3 (four lanes each): returns
    /// y0 | y1 and y3 | y2.
    /// </summary>
    private static (Vector128<short> Y01, Vector128<short> Y32) Transform4Lanes(Vector128<short> x01, Vector128<short> x23)
    {
        // The high halves (x1 and x3) are the ones halved: x0 + x2 | x1 + (x3 >> 1) and x0 - x2 | (x1 >> 1) - x3.
        var high = Vector128.Create(0, 0, 0, 0, -1, -1, -1, -1).AsInt16();
        var x01Half = Vector128.ConditionalSelect(high, Simd.ShiftRightArithmetic(x01, 1), x01);
        var x23Half = Vector128.ConditionalSelect(high, Simd.ShiftRightArithmetic(x23, 1), x23);
        var e03 = Simd.Add(x01, x23Half); // e0 | e3
        var e12 = Simd.Subtract(x01Half, x23); // e1 | e2
        var e01 = Simd.UnpackLow(e03.AsInt64(), e12.AsInt64()).AsInt16();
        var e32 = Simd.UnpackHigh(e03.AsInt64(), e12.AsInt64()).AsInt16();
        return (Simd.Add(e01, e32), Simd.Subtract(e01, e32));
    }

    /// <summary>The scalar <see cref="InverseTransformAdd{TPixel}"/>: for deeper samples, and the 8-bit one's reference.</summary>
    internal static void InverseTransformAddScalar<TPixel>(Span<int> d, Span<TPixel> dest, int stride, int bitDepth)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        int max = Samples.Max<TPixel>(bitDepth);
        Span<int> f = stackalloc int[16];

        // Rows (horizontal) first.
        for (int i = 0; i < 4; i++)
        {
            int d0 = d[i * 4];
            int d1 = d[(i * 4) + 1];
            int d2 = d[(i * 4) + 2];
            int d3 = d[(i * 4) + 3];
            int e0 = d0 + d2;
            int e1 = d0 - d2;
            int e2 = (d1 >> 1) - d3;
            int e3 = d1 + (d3 >> 1);
            f[i * 4] = e0 + e3;
            f[(i * 4) + 1] = e1 + e2;
            f[(i * 4) + 2] = e1 - e2;
            f[(i * 4) + 3] = e0 - e3;
        }

        // Then columns, and the final rounding shift.
        for (int j = 0; j < 4; j++)
        {
            int f0 = f[j];
            int f1 = f[4 + j];
            int f2 = f[8 + j];
            int f3 = f[12 + j];
            int g0 = f0 + f2;
            int g1 = f0 - f2;
            int g2 = (f1 >> 1) - f3;
            int g3 = f1 + (f3 >> 1);
            AddClipped(dest, j, (g0 + g3 + 32) >> 6, max);
            AddClipped(dest, stride + j, (g1 + g2 + 32) >> 6, max);
            AddClipped(dest, (2 * stride) + j, (g1 - g2 + 32) >> 6, max);
            AddClipped(dest, (3 * stride) + j, (g0 - g3 + 32) >> 6, max);
        }
    }

    /// <summary>
    /// <see cref="InverseTransformAdd"/> for a block whose only nonzero coefficient is the DC
    /// <paramref name="dc"/> (already scaled): every residual sample is then (dc + 32) &gt;&gt; 6.
    /// </summary>
    public static void AddDc<TPixel>(int dc, Span<TPixel> dest, int stride, int bitDepth)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        int residual = (dc + 32) >> 6;
        if (residual == 0)
        {
            return;
        }

        if (typeof(TPixel) == typeof(byte))
        {
            var bytes = MemoryMarshal.Cast<TPixel, byte>(dest);
            _ = bytes[(3 * stride) + 3];
            ref byte target = ref MemoryMarshal.GetReference(bytes);
            var r = Vector128.Create(residual);
            AddRow(ref target, r);
            AddRow(ref Unsafe.Add(ref target, stride), r);
            AddRow(ref Unsafe.Add(ref target, 2 * stride), r);
            AddRow(ref Unsafe.Add(ref target, 3 * stride), r);
            return;
        }

        AddDcScalar(dc, dest, stride, bitDepth);
    }

    /// <summary>The scalar <see cref="AddDc{TPixel}"/>: for deeper samples, and the 8-bit one's reference.</summary>
    internal static void AddDcScalar<TPixel>(int dc, Span<TPixel> dest, int stride, int bitDepth)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        int residual = (dc + 32) >> 6;
        int max = Samples.Max<TPixel>(bitDepth);
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                AddClipped(dest, (y * stride) + x, residual, max);
            }
        }
    }

    /// <summary>
    /// The residual of a lossless macroblock (TransformBypassModeFlag, 8.5.12.1 and 8.5.15): the
    /// levels <paramref name="r"/> themselves (<paramref name="width"/> x <paramref name="height"/>,
    /// row-major), added to the prediction. Intra blocks predicted vertically or horizontally first
    /// sum their residual down each column (<paramref name="dpcm"/> <see cref="BypassDpcm.Vertical"/>)
    /// or along each row (<see cref="BypassDpcm.Horizontal"/>), in place.
    /// </summary>
    public static void AddBypass<TPixel>(Span<int> r, int width, int height, BypassDpcm dpcm, Span<TPixel> dest, int stride, int bitDepth)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        if (dpcm == BypassDpcm.Vertical)
        {
            for (int i = width; i < width * height; i++)
            {
                r[i] += r[i - width];
            }
        }
        else if (dpcm == BypassDpcm.Horizontal)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 1; x < width; x++)
                {
                    r[(y * width) + x] += r[(y * width) + x - 1];
                }
            }
        }

        int max = Samples.Max<TPixel>(bitDepth);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                AddClipped(dest, (y * stride) + x, r[(y * width) + x], max);
            }
        }
    }

    /// <summary>Adds four residuals to the four 8-bit samples at <paramref name="dest"/> and clips them to 0 .. 255, unchecked.</summary>
    private static void AddRow(ref byte dest, Vector128<int> residual)
    {
        var samples = Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(ref dest)).AsByte()));
        var sum = Vector128.Min(Vector128.Max(samples.AsInt32() + residual, Vector128<int>.Zero), Vector128.Create(255));
        var words = Vector128.Narrow(sum, sum).AsUInt16();
        Unsafe.WriteUnaligned(ref dest, Vector128.Narrow(words, words).AsUInt32().ToScalar());
    }

    private static void AddClipped<TPixel>(Span<TPixel> dest, int index, int residual, int max)
        where TPixel : unmanaged, IBinaryInteger<TPixel> =>
        dest[index] = TPixel.CreateTruncating(Math.Clamp(int.CreateTruncating(dest[index]) + residual, 0, max));

    private static void Hadamard4x4(ReadOnlySpan<int> c, Span<int> f)
    {
        Span<int> t = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int c0 = c[i * 4];
            int c1 = c[(i * 4) + 1];
            int c2 = c[(i * 4) + 2];
            int c3 = c[(i * 4) + 3];
            t[i * 4] = c0 + c1 + c2 + c3;
            t[(i * 4) + 1] = c0 + c1 - c2 - c3;
            t[(i * 4) + 2] = c0 - c1 - c2 + c3;
            t[(i * 4) + 3] = c0 - c1 + c2 - c3;
        }

        for (int j = 0; j < 4; j++)
        {
            int t0 = t[j];
            int t1 = t[4 + j];
            int t2 = t[8 + j];
            int t3 = t[12 + j];
            f[j] = t0 + t1 + t2 + t3;
            f[4 + j] = t0 + t1 - t2 - t3;
            f[8 + j] = t0 - t1 - t2 + t3;
            f[12 + j] = t0 - t1 + t2 - t3;
        }
    }

    /// <summary>
    /// The SSE2 operations of the 16-bit transforms, on x64 SSE2's own and on ARM64 the same in AdvSimd (saturating
    /// narrows, zips): the 16-bit paths need one or the other, and elsewhere (WebAssembly) the 32-bit ones run.
    /// </summary>
    private static class Simd
    {
        public static bool IsSupported => Sse2.IsSupported || AdvSimd.Arm64.IsSupported;

        /// <summary>a's lanes then b's, each saturated to 16 bits, as packssdw.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<short> PackSignedSaturate(Vector128<int> a, Vector128<int> b) => Sse2.IsSupported
            ? Sse2.PackSignedSaturate(a, b)
            : AdvSimd.ExtractNarrowingSaturateUpper(AdvSimd.ExtractNarrowingSaturateLower(a), b);

        /// <summary>a's lanes then b's, each saturated to 0 .. 255, as packuswb.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<byte> PackUnsignedSaturate(Vector128<short> a, Vector128<short> b) => Sse2.IsSupported
            ? Sse2.PackUnsignedSaturate(a, b)
            : AdvSimd.ExtractNarrowingSaturateUnsignedUpper(AdvSimd.ExtractNarrowingSaturateUnsignedLower(a), b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<byte> UnpackLow(Vector128<byte> a, Vector128<byte> b) => Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<byte> UnpackHigh(Vector128<byte> a, Vector128<byte> b) => Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<short> UnpackLow(Vector128<short> a, Vector128<short> b) => Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<short> UnpackHigh(Vector128<short> a, Vector128<short> b) => Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<int> UnpackLow(Vector128<int> a, Vector128<int> b) => Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<int> UnpackHigh(Vector128<int> a, Vector128<int> b) => Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<long> UnpackLow(Vector128<long> a, Vector128<long> b) => Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<long> UnpackHigh(Vector128<long> a, Vector128<long> b) => Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<short> Add(Vector128<short> a, Vector128<short> b) => a + b;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<short> Subtract(Vector128<short> a, Vector128<short> b) => a - b;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<short> ShiftRightArithmetic(Vector128<short> value, byte count) => value >> count;

        /// <summary>Lanes 2, 3, 0, 1: the halves swapped.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<int> SwapHalves(Vector128<int> value) =>
            Sse2.IsSupported ? Sse2.Shuffle(value, 0b01_00_11_10) : AdvSimd.ExtractVector128(value, value, 2);
    }
}

/// <summary>The direction a lossless intra residual is summed in (8.5.15), if any.</summary>
internal enum BypassDpcm
{
    None,
    Vertical,
    Horizontal,
}
