using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

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
}

/// <summary>The direction a lossless intra residual is summed in (8.5.15), if any.</summary>
internal enum BypassDpcm
{
    None,
    Vertical,
    Horizontal,
}
