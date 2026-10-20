namespace ManagedCodecs.H264;

/// <summary>Scaling (dequantization) and inverse transforms for 4x4 blocks (H.264 clause 8.5).</summary>
/// <remarks>
/// Only flat scaling matrices exist in Baseline, so LevelScale4x4 is 16 times the normAdjust4x4
/// values v of equation 8-315. Matrices are row-major: index = row * 4 + column.
/// </remarks>
internal static class Transform
{
    /// <summary>Frame zig-zag scan (Table 8-13): scan position to row-major index.</summary>
    public static ReadOnlySpan<byte> ZigZag4x4 => [0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15];

    // normAdjust4x4 (8-315) per qP % 6: positions with both coordinates even, both odd, mixed.
    private static ReadOnlySpan<byte> NormEven => [10, 11, 13, 14, 16, 18];

    private static ReadOnlySpan<byte> NormOdd => [16, 18, 20, 23, 25, 29];

    private static ReadOnlySpan<byte> NormMixed => [13, 14, 16, 18, 20, 23];

    /// <summary>LevelScale4x4(qP % 6, row, column) for flat matrices.</summary>
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
    /// Scales the coefficients of a 4x4 block in place (8.5.12.1). With <paramref name="skipDc"/>
    /// the DC is left alone because it was already produced by a DC transform.
    /// </summary>
    public static void Dequantize(Span<int> block, int qp, bool skipDc)
    {
        int qpDiv = qp / 6;
        int qpRem = qp % 6;
        for (int i = skipDc ? 1 : 0; i < 16; i++)
        {
            if (block[i] == 0)
            {
                continue;
            }

            int scaled = block[i] * LevelScale(qpRem, i);
            block[i] = qp >= 24
                ? scaled << (qpDiv - 4)
                : (scaled + (1 << (3 - qpDiv))) >> (4 - qpDiv);
        }
    }

    /// <summary>
    /// Intra 16x16 luma DC (8.5.10): inverse Hadamard of the 4x4 DC levels, then scaling.
    /// Input and output are row-major in the block layout of the macroblock.
    /// </summary>
    public static void InverseLumaDc(Span<int> c, int qp)
    {
        Span<int> f = stackalloc int[16];
        Hadamard4x4(c, f);

        int qpDiv = qp / 6;
        int scale = LevelScale(qp % 6, 0);
        for (int i = 0; i < 16; i++)
        {
            c[i] = qp >= 36
                ? (f[i] * scale) << (qpDiv - 6)
                : ((f[i] * scale) + (1 << (5 - qpDiv))) >> (6 - qpDiv);
        }
    }

    /// <summary>4:2:0 chroma DC (8.5.11): 2x2 transform, then scaling. c = [c00, c01, c10, c11].</summary>
    public static void InverseChromaDc(Span<int> c, int qp)
    {
        int f0 = c[0] + c[1] + c[2] + c[3];
        int f1 = c[0] - c[1] + c[2] - c[3];
        int f2 = c[0] + c[1] - c[2] - c[3];
        int f3 = c[0] - c[1] - c[2] + c[3];

        int scale = LevelScale(qp % 6, 0);
        int qpDiv = qp / 6;
        c[0] = ((f0 * scale) << qpDiv) >> 5;
        c[1] = ((f1 * scale) << qpDiv) >> 5;
        c[2] = ((f2 * scale) << qpDiv) >> 5;
        c[3] = ((f3 * scale) << qpDiv) >> 5;
    }

    /// <summary>
    /// 4x4 inverse transform (8.5.12.2) of scaled coefficients, added to the prediction already
    /// in <paramref name="dest"/> and clipped (8.5.14).
    /// </summary>
    public static void InverseTransformAdd(Span<int> d, Span<byte> dest, int stride)
    {
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
            AddClipped(dest, j, (g0 + g3 + 32) >> 6);
            AddClipped(dest, stride + j, (g1 + g2 + 32) >> 6);
            AddClipped(dest, (2 * stride) + j, (g1 - g2 + 32) >> 6);
            AddClipped(dest, (3 * stride) + j, (g0 - g3 + 32) >> 6);
        }
    }

    private static void AddClipped(Span<byte> dest, int index, int residual) =>
        dest[index] = (byte)Math.Clamp(dest[index] + residual, 0, 255);

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
