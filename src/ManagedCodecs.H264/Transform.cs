using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
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
    /// in <paramref name="dest"/> and clipped to <paramref name="bitDepth"/> bits (8.5.14). With
    /// SSE2 eight rows at a time (<see cref="InverseTransform8x8AddVector"/>), else
    /// <see cref="InverseTransform8x8AddReference"/>.
    /// </summary>
    public static void InverseTransform8x8Add<TPixel>(Span<int> d, Span<TPixel> dest, int stride, int bitDepth)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        if (Sse2.IsSupported)
        {
            InverseTransform8x8AddVector(d, dest, stride, bitDepth);
            return;
        }

        InverseTransform8x8AddReference(d, dest, stride, bitDepth);
    }

    /// <summary>
    /// <see cref="InverseTransform8x8Add"/> on vectors: each 8-point transform of the specification
    /// runs on four rows or columns at once, lane by lane, between transposes, so the rows are
    /// transformed before the columns and every sum and shift is the scalar one.
    /// </summary>
    [SkipLocalsInit]
    internal static void InverseTransform8x8AddVector<TPixel>(Span<int> d, Span<TPixel> dest, int stride, int bitDepth)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        if (d.Length < 64 || dest.Length < (7 * stride) + 8)
        {
            throw new ArgumentException("An 8x8 block needs 64 coefficients and 8 rows of 8 samples.");
        }

        // v[2r] and v[2r + 1]: columns 0-3 and 4-7 of row r.
        Span<Vector128<int>> v = stackalloc Vector128<int>[16];
        for (int k = 0; k < 16; k++)
        {
            v[k] = Vector128.Create(d.Slice(k * 4, 4));
        }

        // Rows first: transposed, v holds the columns, and each lane is a row.
        Transpose8x8(v);
        Transform8Lanes(v);

        // Then columns: transposed back, each lane is a column; v[2r], v[2r + 1] are row r of the result.
        Transpose8x8(v);
        Transform8Lanes(v);

        var max = Vector128.Create(Samples.Max<TPixel>(bitDepth));
        var round = Vector128.Create(32);
        for (int r = 0; r < 8; r++)
        {
            var row = dest.Slice(r * stride, 8);
            var residual0 = (v[2 * r] + round) >> 6;
            var residual1 = (v[(2 * r) + 1] + round) >> 6;
            if (typeof(TPixel) == typeof(byte))
            {
                var bytes = MemoryMarshal.Cast<TPixel, byte>(row);
                var words = Vector128.WidenLower(Vector128.CreateScalar(MemoryMarshal.Read<ulong>(bytes)).AsByte());
                var sum0 = Vector128.Clamp(Vector128.WidenLower(words).AsInt32() + residual0, Vector128<int>.Zero, max);
                var sum1 = Vector128.Clamp(Vector128.WidenUpper(words).AsInt32() + residual1, Vector128<int>.Zero, max);
                var narrow = Vector128.Narrow(Vector128.Narrow(sum0, sum1).AsUInt16(), Vector128<ushort>.Zero);
                MemoryMarshal.Write(bytes, narrow.AsUInt64().ToScalar());
            }
            else
            {
                var samples = MemoryMarshal.Cast<TPixel, ushort>(row);
                var words = Vector128.Create(samples);
                var sum0 = Vector128.Clamp(Vector128.WidenLower(words).AsInt32() + residual0, Vector128<int>.Zero, max);
                var sum1 = Vector128.Clamp(Vector128.WidenUpper(words).AsInt32() + residual1, Vector128<int>.Zero, max);
                Vector128.Narrow(sum0, sum1).AsUInt16().CopyTo(samples);
            }
        }
    }

    /// <summary>Transposes an 8x8 matrix held as v[2r] (columns 0-3) and v[2r + 1] (columns 4-7) of each row r.</summary>
    private static void Transpose8x8(Span<Vector128<int>> v)
    {
        // Each 4x4 quadrant transposes in place; the two off-diagonal ones also swap places.
        Transpose4x4(ref v[0], ref v[2], ref v[4], ref v[6]);
        Transpose4x4(ref v[9], ref v[11], ref v[13], ref v[15]);
        Transpose4x4(ref v[1], ref v[3], ref v[5], ref v[7]);
        Transpose4x4(ref v[8], ref v[10], ref v[12], ref v[14]);
        (v[1], v[8]) = (v[8], v[1]);
        (v[3], v[10]) = (v[10], v[3]);
        (v[5], v[12]) = (v[12], v[5]);
        (v[7], v[14]) = (v[14], v[7]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose4x4(ref Vector128<int> a, ref Vector128<int> b, ref Vector128<int> c, ref Vector128<int> d)
    {
        var ab0 = Sse2.UnpackLow(a, b);  // a0 b0 a1 b1
        var ab1 = Sse2.UnpackHigh(a, b); // a2 b2 a3 b3
        var cd0 = Sse2.UnpackLow(c, d);
        var cd1 = Sse2.UnpackHigh(c, d);
        a = Sse2.UnpackLow(ab0.AsInt64(), cd0.AsInt64()).AsInt32();  // a0 b0 c0 d0
        b = Sse2.UnpackHigh(ab0.AsInt64(), cd0.AsInt64()).AsInt32(); // a1 b1 c1 d1
        c = Sse2.UnpackLow(ab1.AsInt64(), cd1.AsInt64()).AsInt32();
        d = Sse2.UnpackHigh(ab1.AsInt64(), cd1.AsInt64()).AsInt32();
    }

    /// <summary><see cref="Transform8"/> lane by lane on both halves: input k is v[2k] and v[2k + 1], and so is output k.</summary>
    private static void Transform8Lanes(Span<Vector128<int>> v)
    {
        for (int half = 0; half < 2; half++)
        {
            var d0 = v[half];
            var d1 = v[2 + half];
            var d2 = v[4 + half];
            var d3 = v[6 + half];
            var d4 = v[8 + half];
            var d5 = v[10 + half];
            var d6 = v[12 + half];
            var d7 = v[14 + half];

            var e0 = d0 + d4;
            var e1 = -d3 + d5 - d7 - (d7 >> 1);
            var e2 = d0 - d4;
            var e3 = d1 + d7 - d3 - (d3 >> 1);
            var e4 = (d2 >> 1) - d6;
            var e5 = -d1 + d7 + d5 + (d5 >> 1);
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

            v[half] = f0 + f7;
            v[2 + half] = f2 + f5;
            v[4 + half] = f4 + f3;
            v[6 + half] = f6 + f1;
            v[8 + half] = f6 - f1;
            v[10 + half] = f4 - f3;
            v[12 + half] = f2 - f5;
            v[14 + half] = f0 - f7;
        }
    }

    /// <summary><see cref="InverseTransform8x8Add"/> by the specification's equations, one sample at a time.</summary>
    [SkipLocalsInit]
    internal static void InverseTransform8x8AddReference<TPixel>(Span<int> d, Span<TPixel> dest, int stride, int bitDepth)
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
        if (Sse2.IsSupported)
        {
            InverseTransformAddVector(d, dest, stride, bitDepth);
            return;
        }

        InverseTransformAddReference(d, dest, stride, bitDepth);
    }

    /// <summary>
    /// <see cref="InverseTransformAdd"/> on vectors: the four rows, transposed, are transformed
    /// lane by lane, then the four columns, as in <see cref="InverseTransform8x8AddVector"/>.
    /// </summary>
    internal static void InverseTransformAddVector<TPixel>(Span<int> d, Span<TPixel> dest, int stride, int bitDepth)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        if (d.Length < 16 || dest.Length < (3 * stride) + 4)
        {
            throw new ArgumentException("A 4x4 block needs 16 coefficients and 4 rows of 4 samples.");
        }

        var r0 = Vector128.Create(d[..4]);
        var r1 = Vector128.Create(d.Slice(4, 4));
        var r2 = Vector128.Create(d.Slice(8, 4));
        var r3 = Vector128.Create(d.Slice(12, 4));
        Transpose4x4(ref r0, ref r1, ref r2, ref r3);
        Transform4Lanes(ref r0, ref r1, ref r2, ref r3);
        Transpose4x4(ref r0, ref r1, ref r2, ref r3);
        Transform4Lanes(ref r0, ref r1, ref r2, ref r3);

        var max = Vector128.Create(Samples.Max<TPixel>(bitDepth));
        var round = Vector128.Create(32);
        AddRow4(dest, (r0 + round) >> 6, max);
        AddRow4(dest[stride..], (r1 + round) >> 6, max);
        AddRow4(dest[(2 * stride)..], (r2 + round) >> 6, max);
        AddRow4(dest[(3 * stride)..], (r3 + round) >> 6, max);
    }

    /// <summary>The 4-point transform of 8-326 to 8-337, lane by lane: inputs and outputs 0 to 3.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transform4Lanes(ref Vector128<int> d0, ref Vector128<int> d1, ref Vector128<int> d2, ref Vector128<int> d3)
    {
        var e0 = d0 + d2;
        var e1 = d0 - d2;
        var e2 = (d1 >> 1) - d3;
        var e3 = d1 + (d3 >> 1);
        d0 = e0 + e3;
        d1 = e1 + e2;
        d2 = e1 - e2;
        d3 = e0 - e3;
    }

    /// <summary>Adds four residual samples to a row of the prediction, clipped to 0 .. max.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddRow4<TPixel>(Span<TPixel> row, Vector128<int> residual, Vector128<int> max)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        if (typeof(TPixel) == typeof(byte))
        {
            var bytes = MemoryMarshal.Cast<TPixel, byte>(row[..4]);
            var samples = Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalar(MemoryMarshal.Read<uint>(bytes)).AsByte())).AsInt32();
            var sum = Vector128.Clamp(samples + residual, Vector128<int>.Zero, max);
            var narrow = Vector128.Narrow(Vector128.Narrow(sum, sum).AsUInt16(), Vector128<ushort>.Zero);
            MemoryMarshal.Write(bytes, narrow.AsUInt32().ToScalar());
            return;
        }

        var words = MemoryMarshal.Cast<TPixel, ushort>(row[..4]);
        var wide = Vector128.WidenLower(Vector128.CreateScalar(MemoryMarshal.Read<ulong>(MemoryMarshal.AsBytes(words))).AsUInt16()).AsInt32();
        var total = Vector128.Clamp(wide + residual, Vector128<int>.Zero, max);
        MemoryMarshal.Write(MemoryMarshal.AsBytes(words), Vector128.Narrow(total, total).AsUInt64().ToScalar());
    }

    /// <summary><see cref="InverseTransformAdd"/> by the specification's equations, one sample at a time.</summary>
    [SkipLocalsInit]
    internal static void InverseTransformAddReference<TPixel>(Span<int> d, Span<TPixel> dest, int stride, int bitDepth)
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
}

/// <summary>The direction a lossless intra residual is summed in (8.5.15), if any.</summary>
internal enum BypassDpcm
{
    None,
    Vertical,
    Horizontal,
}
