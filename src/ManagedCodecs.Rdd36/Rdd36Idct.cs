namespace ManagedCodecs.Rdd36;

/// <summary>
/// The 8x8 inverse DCT and the conversion to 10-bit samples (RDD 36, 7.4 and 7.5.1) in integers, with the arithmetic of
/// FFmpeg's decoder, so that decoded frames are bit-exact with it.
/// </summary>
/// <remarks>
/// RDD 36 fixes no arithmetic: it qualifies an inverse transform's accuracy (Annex A), which this one meets
/// (<c>Rdd36IdctTests</c> runs the qualification on <see cref="Unrounded"/>). The arithmetic here was found by measuring
/// FFmpeg's output on crafted streams, not from its source (docs/decisions/2026-11-15-rdd36-decoder.md): two passes of
/// the kind <c>ManagedCodecs.Mjpeg.SimpleIdct</c> has, rows then columns, with these constants and shifts.
/// <para>
/// The input is a block of coefficients times 8 (level * weight * qScale, where 7.3 divides by 8). Rows keep
/// (16384 * x0 + 2^14 + ...) >> 15, which for a row with only its first coefficient is (x0 + 1) >> 1; columns give the
/// sample, (16384 * y0 + 2^17 + ...) >> 18, plus 512, clamped to 4..1019 (7.5.1 allows a clamp that keeps the
/// timing reference levels out). The constants are cos(k * pi / 16) * sqrt(2) * 2^14 rounded, but for W3, which is
/// 19265 where the rounded value is 19266, and W4, which is 2^14.
/// </para>
/// <para>
/// Sums are 32-bit and wrap. They cannot overflow for the coefficients of a picture (RDD 36 expects 12-bit signed
/// coefficients, 7.4); for blocks beyond that range FFmpeg's own C and SIMD transforms give different samples, so
/// there is nothing to be exact with, and this arithmetic is the decoder's definition.
/// </para>
/// </remarks>
internal static class Rdd36Idct
{
    private const int W1 = 22725;
    private const int W2 = 21407;
    private const int W3 = 19265;
    private const int W4 = 16384;
    private const int W5 = 12873;
    private const int W6 = 8867;
    private const int W7 = 4520;

    private const int RowShift = 15;
    private const int ColumnShift = 18;

    /// <summary>The mid-range level of 7.5.1 for 10-bit samples: 2^10 * 256 / 512.</summary>
    private const int Bias = 512;

    private const int MinSample = 4;
    private const int MaxSample = 1019;

    /// <summary>
    /// Transforms <paramref name="block"/> (64 coefficients times 8, at 8 * v + u) and writes the 8x8 samples to
    /// <paramref name="dest"/>, a row every <paramref name="stride"/> samples. Rows of zeros and rows with only their
    /// first coefficient take shortcuts that give what <see cref="PutReference"/> gives.
    /// </summary>
    public static void Put(ReadOnlySpan<short> block, Span<ushort> dest, int stride)
    {
        Check(block, dest, stride);
        Span<int> rows = stackalloc int[64];
        uint nonZeroRows = 0;
        for (int v = 0; v < 8; v++)
        {
            int at = v * 8;
            int x0 = block[at];
            if (((ushort)block[at + 1] | (ushort)block[at + 2] | (ushort)block[at + 3] | (ushort)block[at + 4] | (ushort)block[at + 5] | (ushort)block[at + 6] | (ushort)block[at + 7]) == 0)
            {
                if (x0 == 0)
                {
                    // (2^14) >> 15: a row of zeros stays one (stackalloc gives no zeros by itself).
                    rows.Slice(at, 8).Clear();
                    continue;
                }

                // (16384 * x0 + 2^14) >> 15 for all eight.
                rows.Slice(at, 8).Fill((x0 + 1) >> 1);
            }
            else
            {
                Row(block, at, rows);
            }

            nonZeroRows |= 1u << v;
        }

        if (nonZeroRows <= 1)
        {
            // Only the first row: each column is its first value alone, (16384 * y0 + 2^17) >> 18.
            for (int u = 0; u < 8; u++)
            {
                ushort sample = Clamp(((rows[u] + 8) >> 4) + Bias);
                for (int y = 0; y < 8; y++)
                {
                    dest[(y * stride) + u] = sample;
                }
            }

            return;
        }

        for (int u = 0; u < 8; u++)
        {
            Column(rows, u, dest, stride);
        }
    }

    /// <summary>The same transform with no shortcut: every row, then every column. The reference <see cref="Put"/> is tested against.</summary>
    public static void PutReference(ReadOnlySpan<short> block, Span<ushort> dest, int stride)
    {
        Check(block, dest, stride);
        Span<int> rows = stackalloc int[64];
        for (int v = 0; v < 8; v++)
        {
            Row(block, v * 8, rows);
        }

        for (int u = 0; u < 8; u++)
        {
            Column(rows, u, dest, stride);
        }
    }

    /// <summary>
    /// The transform's values before the last rounding, in 2^-19 of the reconstructed values of 7.4 (which range like
    /// 9-bit signed integers): what Annex A's accuracy qualification measures, "retaining the full precision of the
    /// results". A sample is (value + 2^17) >> 18, plus 512, clamped.
    /// </summary>
    public static void Unrounded(ReadOnlySpan<short> block, Span<int> values)
    {
        Span<int> rows = stackalloc int[64];
        for (int v = 0; v < 8; v++)
        {
            Row(block, v * 8, rows);
        }

        Span<int> column = stackalloc int[8];
        for (int u = 0; u < 8; u++)
        {
            ColumnSums(rows, u, column, 0);
            for (int y = 0; y < 8; y++)
            {
                values[(y * 8) + u] = column[y];
            }
        }
    }

    private static void Check(ReadOnlySpan<short> block, Span<ushort> dest, int stride)
    {
        if (block.Length < 64)
        {
            throw new ArgumentException("The block must hold 64 coefficients.", nameof(block));
        }

        if (stride < 8 || dest.Length < (7 * stride) + 8)
        {
            throw new ArgumentException("The destination must hold 8 rows of 8 samples.", nameof(dest));
        }
    }

    /// <summary>One row of <paramref name="block"/> from <paramref name="at"/> into <paramref name="rows"/> at the same place.</summary>
    private static void Row(ReadOnlySpan<short> block, int at, Span<int> rows)
    {
        unchecked
        {
            int x0 = block[at];
            int x1 = block[at + 1];
            int x2 = block[at + 2];
            int x3 = block[at + 3];
            int x4 = block[at + 4];
            int x5 = block[at + 5];
            int x6 = block[at + 6];
            int x7 = block[at + 7];

            int a0 = (W4 * x0) + (1 << (RowShift - 1));
            int a1 = a0;
            int a2 = a0;
            int a3 = a0;
            a0 += (W2 * x2) + (W4 * x4) + (W6 * x6);
            a1 += (W6 * x2) - (W4 * x4) - (W2 * x6);
            a2 += -(W6 * x2) - (W4 * x4) + (W2 * x6);
            a3 += -(W2 * x2) + (W4 * x4) - (W6 * x6);

            int b0 = (W1 * x1) + (W3 * x3) + (W5 * x5) + (W7 * x7);
            int b1 = (W3 * x1) - (W7 * x3) - (W1 * x5) - (W5 * x7);
            int b2 = (W5 * x1) - (W1 * x3) + (W7 * x5) + (W3 * x7);
            int b3 = (W7 * x1) - (W5 * x3) + (W3 * x5) - (W1 * x7);

            rows[at] = (a0 + b0) >> RowShift;
            rows[at + 1] = (a1 + b1) >> RowShift;
            rows[at + 2] = (a2 + b2) >> RowShift;
            rows[at + 3] = (a3 + b3) >> RowShift;
            rows[at + 4] = (a3 - b3) >> RowShift;
            rows[at + 5] = (a2 - b2) >> RowShift;
            rows[at + 6] = (a1 - b1) >> RowShift;
            rows[at + 7] = (a0 - b0) >> RowShift;
        }
    }

    /// <summary>Column <paramref name="u"/> of <paramref name="rows"/> as samples into the same column of <paramref name="dest"/>.</summary>
    private static void Column(ReadOnlySpan<int> rows, int u, Span<ushort> dest, int stride)
    {
        Span<int> sums = stackalloc int[8];
        ColumnSums(rows, u, sums, 1 << (ColumnShift - 1));
        for (int y = 0; y < 8; y++)
        {
            dest[(y * stride) + u] = Clamp((sums[y] >> ColumnShift) + Bias);
        }
    }

    /// <summary>The eight sums of column <paramref name="u"/>, <paramref name="rounding"/> added to each, not yet shifted.</summary>
    private static void ColumnSums(ReadOnlySpan<int> rows, int u, Span<int> sums, int rounding)
    {
        unchecked
        {
            int y0 = rows[u];
            int y1 = rows[8 + u];
            int y2 = rows[16 + u];
            int y3 = rows[24 + u];
            int y4 = rows[32 + u];
            int y5 = rows[40 + u];
            int y6 = rows[48 + u];
            int y7 = rows[56 + u];

            int a0 = (W4 * y0) + rounding;
            int a1 = a0;
            int a2 = a0;
            int a3 = a0;
            a0 += (W2 * y2) + (W4 * y4) + (W6 * y6);
            a1 += (W6 * y2) - (W4 * y4) - (W2 * y6);
            a2 += -(W6 * y2) - (W4 * y4) + (W2 * y6);
            a3 += -(W2 * y2) + (W4 * y4) - (W6 * y6);

            int b0 = (W1 * y1) + (W3 * y3) + (W5 * y5) + (W7 * y7);
            int b1 = (W3 * y1) - (W7 * y3) - (W1 * y5) - (W5 * y7);
            int b2 = (W5 * y1) - (W1 * y3) + (W7 * y5) + (W3 * y7);
            int b3 = (W7 * y1) - (W5 * y3) + (W3 * y5) - (W1 * y7);

            sums[0] = a0 + b0;
            sums[1] = a1 + b1;
            sums[2] = a2 + b2;
            sums[3] = a3 + b3;
            sums[4] = a3 - b3;
            sums[5] = a2 - b2;
            sums[6] = a1 - b1;
            sums[7] = a0 - b0;
        }
    }

    private static ushort Clamp(int value) => (ushort)(value < MinSample ? MinSample : value > MaxSample ? MaxSample : value);
}
