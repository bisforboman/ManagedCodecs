namespace ManagedCodecs.Mjpeg;

/// <summary>
/// Separable integer 8x8 inverse DCT whose arithmetic reproduces FFmpeg's default "simple"
/// IDCT for 8-bit video (the one its MJPEG decoder uses unless <c>-idct</c> says otherwise),
/// so decoded pixels are bit-exact with FFmpeg.
/// </summary>
/// <remarks>
/// Coefficients are cos(k*pi/16) * sqrt(2) * 2^14, rounded (W4 is 2^14 - 1). Rows are
/// transformed with 11 fractional bits kept and stored back as 16-bit values; columns then
/// drop the remaining 20 bits. Rounding is folded into the DC term of each pass. The input
/// carries the JPEG level shift: the DC predictor starts at 128 * 8 instead of 0.
/// </remarks>
internal static class SimpleIdct
{
    private const int W1 = 22725;
    private const int W2 = 21407;
    private const int W3 = 19266;
    private const int W4 = 16383;
    private const int W5 = 12873;
    private const int W6 = 8867;
    private const int W7 = 4520;

    private const int RowShift = 11;
    private const int ColShift = 20;
    private const int DcShift = 3;

    /// <summary>
    /// Transforms <paramref name="block"/> (64 coefficients, natural order) in place and writes
    /// the clamped 8x8 result to <paramref name="dest"/>.
    /// </summary>
    public static void Put(Span<short> block, Span<byte> dest, int stride)
    {
        if (block.Length < 64)
        {
            throw new ArgumentException("Block must hold 64 coefficients.", nameof(block));
        }

        for (int i = 0; i < 8; i++)
        {
            Row(block.Slice(i * 8, 8));
        }

        for (int i = 0; i < 8; i++)
        {
            Column(block, i, dest[i..], stride);
        }
    }

    private static void Row(Span<short> row)
    {
        if (row[1..].IndexOfAnyExcept((short)0) < 0)
        {
            // DC only: a plain shift, which can differ from the full path by one in rare cases.
            short dc = (short)(row[0] << DcShift);
            row.Fill(dc);
            return;
        }

        int a0 = (W4 * row[0]) + (1 << (RowShift - 1));
        int a1 = a0;
        int a2 = a0;
        int a3 = a0;

        a0 += W2 * row[2];
        a1 += W6 * row[2];
        a2 -= W6 * row[2];
        a3 -= W2 * row[2];

        int b0 = (W1 * row[1]) + (W3 * row[3]);
        int b1 = (W3 * row[1]) - (W7 * row[3]);
        int b2 = (W5 * row[1]) - (W1 * row[3]);
        int b3 = (W7 * row[1]) - (W5 * row[3]);

        if (row[4..].IndexOfAnyExcept((short)0) >= 0)
        {
            a0 += (W4 * row[4]) + (W6 * row[6]);
            a1 += (-W4 * row[4]) - (W2 * row[6]);
            a2 += (-W4 * row[4]) + (W2 * row[6]);
            a3 += (W4 * row[4]) - (W6 * row[6]);

            b0 += (W5 * row[5]) + (W7 * row[7]);
            b1 += (-W1 * row[5]) - (W5 * row[7]);
            b2 += (W7 * row[5]) + (W3 * row[7]);
            b3 += (W3 * row[5]) - (W1 * row[7]);
        }

        row[0] = (short)((a0 + b0) >> RowShift);
        row[7] = (short)((a0 - b0) >> RowShift);
        row[1] = (short)((a1 + b1) >> RowShift);
        row[6] = (short)((a1 - b1) >> RowShift);
        row[2] = (short)((a2 + b2) >> RowShift);
        row[5] = (short)((a2 - b2) >> RowShift);
        row[3] = (short)((a3 + b3) >> RowShift);
        row[4] = (short)((a3 - b3) >> RowShift);
    }

    private static void Column(ReadOnlySpan<short> block, int x, Span<byte> dest, int stride)
    {
        int c0 = block[x];
        int c1 = block[8 + x];
        int c2 = block[16 + x];
        int c3 = block[24 + x];
        int c4 = block[32 + x];
        int c5 = block[40 + x];
        int c6 = block[48 + x];
        int c7 = block[56 + x];

        // Adding 2^19 / W4 to the DC before scaling is how the rounding constant gets in.
        int a0 = W4 * (c0 + ((1 << (ColShift - 1)) / W4));
        int a1 = a0;
        int a2 = a0;
        int a3 = a0;

        a0 += (W2 * c2) + (W4 * c4) + (W6 * c6);
        a1 += (W6 * c2) - (W4 * c4) - (W2 * c6);
        a2 += (-W6 * c2) - (W4 * c4) + (W2 * c6);
        a3 += (-W2 * c2) + (W4 * c4) - (W6 * c6);

        int b0 = (W1 * c1) + (W3 * c3) + (W5 * c5) + (W7 * c7);
        int b1 = (W3 * c1) - (W7 * c3) - (W1 * c5) - (W5 * c7);
        int b2 = (W5 * c1) - (W1 * c3) + (W7 * c5) + (W3 * c7);
        int b3 = (W7 * c1) - (W5 * c3) + (W3 * c5) - (W1 * c7);

        dest[0] = Clamp((a0 + b0) >> ColShift);
        dest[stride] = Clamp((a1 + b1) >> ColShift);
        dest[2 * stride] = Clamp((a2 + b2) >> ColShift);
        dest[3 * stride] = Clamp((a3 + b3) >> ColShift);
        dest[4 * stride] = Clamp((a3 - b3) >> ColShift);
        dest[5 * stride] = Clamp((a2 - b2) >> ColShift);
        dest[6 * stride] = Clamp((a1 - b1) >> ColShift);
        dest[7 * stride] = Clamp((a0 - b0) >> ColShift);
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
}
