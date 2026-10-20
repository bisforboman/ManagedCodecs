using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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
/// <para>
/// The AVX2 path transforms all eight rows at once (one row per 32-bit lane), transposes, then
/// all eight columns at once. It wraps and truncates exactly where the scalar path does, so the
/// two give identical output for any input; <see cref="PutScalar"/> is the reference.
/// </para>
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

    // Adding 2^19 / W4 to the DC before scaling is how the column rounding constant gets in.
    private const int ColumnRounding = (1 << (ColShift - 1)) / W4;

    /// <summary>
    /// Transforms <paramref name="block"/> (64 coefficients, natural order) and writes the
    /// clamped 8x8 result to <paramref name="dest"/>. The block's contents are clobbered.
    /// </summary>
    public static void Put(Span<short> block, Span<byte> dest, int stride)
    {
        if (Avx2.IsSupported)
        {
            PutAvx2(block, dest, stride);
        }
        else
        {
            PutScalar(block, dest, stride);
        }
    }

    /// <summary>
    /// Same as <see cref="Put"/> for a block whose only non-zero coefficient is the DC:
    /// every output sample is the same.
    /// </summary>
    public static void PutDcOnly(short dc, Span<byte> dest, int stride)
    {
        CheckDestination(dest, stride);

        // The row pass turns the DC row into (short)(dc << 3) everywhere and the other rows
        // into zeros; each column then only has its first term.
        int row = (short)(dc << DcShift);
        byte value = Clamp((W4 * (row + ColumnRounding)) >> ColShift);
        for (int y = 0; y < 8; y++)
        {
            dest.Slice(y * stride, 8).Fill(value);
        }
    }

    public static void PutScalar(Span<short> block, Span<byte> dest, int stride)
    {
        if (block.Length < 64)
        {
            throw new ArgumentException("Block must hold 64 coefficients.", nameof(block));
        }

        CheckDestination(dest, stride);

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

        int a0 = W4 * (c0 + ColumnRounding);
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

    internal static void PutAvx2(Span<short> block, Span<byte> dest, int stride)
    {
        if (block.Length < 64)
        {
            throw new ArgumentException("Block must hold 64 coefficients.", nameof(block));
        }

        CheckDestination(dest, stride);

        ref short coefficients = ref MemoryMarshal.GetReference(block);
        var r0 = Vector128.LoadUnsafe(ref coefficients, 0);
        var r1 = Vector128.LoadUnsafe(ref coefficients, 8);
        var r2 = Vector128.LoadUnsafe(ref coefficients, 16);
        var r3 = Vector128.LoadUnsafe(ref coefficients, 24);
        var r4 = Vector128.LoadUnsafe(ref coefficients, 32);
        var r5 = Vector128.LoadUnsafe(ref coefficients, 40);
        var r6 = Vector128.LoadUnsafe(ref coefficients, 48);
        var r7 = Vector128.LoadUnsafe(ref coefficients, 56);

        // Row pass. After the transpose, vector k holds coefficient k of every row.
        Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);

        var dcOnly = Vector128.Equals(r1 | r2 | r3 | r4 | r5 | r6 | r7, Vector128<short>.Zero);
        var dcShortcut = r0 << DcShift;

        Butterfly(
            (Avx2.ConvertToVector256Int32(r0) * W4) + Vector256.Create(1 << (RowShift - 1)),
            r1,
            r2,
            r3,
            r4,
            r5,
            r6,
            r7,
            out var a0b0,
            out var a1b1,
            out var a2b2,
            out var a3b3,
            out var a3b3n,
            out var a2b2n,
            out var a1b1n,
            out var a0b0n);

        r0 = Vector128.ConditionalSelect(dcOnly, dcShortcut, NarrowRow(a0b0));
        r1 = Vector128.ConditionalSelect(dcOnly, dcShortcut, NarrowRow(a1b1));
        r2 = Vector128.ConditionalSelect(dcOnly, dcShortcut, NarrowRow(a2b2));
        r3 = Vector128.ConditionalSelect(dcOnly, dcShortcut, NarrowRow(a3b3));
        r4 = Vector128.ConditionalSelect(dcOnly, dcShortcut, NarrowRow(a3b3n));
        r5 = Vector128.ConditionalSelect(dcOnly, dcShortcut, NarrowRow(a2b2n));
        r6 = Vector128.ConditionalSelect(dcOnly, dcShortcut, NarrowRow(a1b1n));
        r7 = Vector128.ConditionalSelect(dcOnly, dcShortcut, NarrowRow(a0b0n));

        // Column pass. Transposed back, vector k is row k again and each lane is a column.
        Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);

        Butterfly(
            (Avx2.ConvertToVector256Int32(r0) + Vector256.Create(ColumnRounding)) * W4,
            r1,
            r2,
            r3,
            r4,
            r5,
            r6,
            r7,
            out var y0,
            out var y1,
            out var y2,
            out var y3,
            out var y4,
            out var y5,
            out var y6,
            out var y7);

        ref byte output = ref MemoryMarshal.GetReference(dest);
        StoreRows(ref output, stride, 0, y0, y1);
        StoreRows(ref output, stride, 2, y2, y3);
        StoreRows(ref output, stride, 4, y4, y5);
        StoreRows(ref output, stride, 6, y6, y7);
    }

    /// <summary>
    /// The shared even/odd butterfly of both passes. <paramref name="a"/> is the scaled and
    /// rounded DC term; the outputs are a0 + b0, a1 + b1, ..., a0 - b0 in output order.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Butterfly(
        Vector256<int> a,
        Vector128<short> v1,
        Vector128<short> v2,
        Vector128<short> v3,
        Vector128<short> v4,
        Vector128<short> v5,
        Vector128<short> v6,
        Vector128<short> v7,
        out Vector256<int> o0,
        out Vector256<int> o1,
        out Vector256<int> o2,
        out Vector256<int> o3,
        out Vector256<int> o4,
        out Vector256<int> o5,
        out Vector256<int> o6,
        out Vector256<int> o7)
    {
        var c1 = Avx2.ConvertToVector256Int32(v1);
        var c2 = Avx2.ConvertToVector256Int32(v2);
        var c3 = Avx2.ConvertToVector256Int32(v3);
        var c4 = Avx2.ConvertToVector256Int32(v4);
        var c5 = Avx2.ConvertToVector256Int32(v5);
        var c6 = Avx2.ConvertToVector256Int32(v6);
        var c7 = Avx2.ConvertToVector256Int32(v7);

        var w4c4 = c4 * W4;
        var even0 = a + w4c4;
        var even1 = a - w4c4;
        var a0 = even0 + (c2 * W2) + (c6 * W6);
        var a3 = even0 - (c2 * W2) - (c6 * W6);
        var a1 = even1 + (c2 * W6) - (c6 * W2);
        var a2 = even1 - (c2 * W6) + (c6 * W2);

        var b0 = (c1 * W1) + (c3 * W3) + (c5 * W5) + (c7 * W7);
        var b1 = (c1 * W3) - (c3 * W7) - (c5 * W1) - (c7 * W5);
        var b2 = (c1 * W5) - (c3 * W1) + (c5 * W7) + (c7 * W3);
        var b3 = (c1 * W7) - (c3 * W5) + (c5 * W3) - (c7 * W1);

        o0 = a0 + b0;
        o1 = a1 + b1;
        o2 = a2 + b2;
        o3 = a3 + b3;
        o4 = a3 - b3;
        o5 = a2 - b2;
        o6 = a1 - b1;
        o7 = a0 - b0;
    }

    /// <summary>Shifts a row-pass result down and keeps its low 16 bits, like a C store to int16_t.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> NarrowRow(Vector256<int> value)
    {
        // Sign-extend the low 16 bits so the saturating pack below cannot saturate.
        var truncated = Avx2.ShiftRightArithmetic(Avx2.ShiftLeftLogical(Avx2.ShiftRightArithmetic(value, RowShift), 16), 16);
        return Sse2.PackSignedSaturate(truncated.GetLower(), truncated.GetUpper());
    }

    /// <summary>Shifts two column-pass rows down, clamps them to 0..255 and stores them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreRows(ref byte dest, int stride, int y, Vector256<int> upper, Vector256<int> lower)
    {
        upper = Avx2.ShiftRightArithmetic(upper, ColShift);
        lower = Avx2.ShiftRightArithmetic(lower, ColShift);

        // Saturating to int16 and then to uint8 is the same as clamping to 0..255.
        var bytes = Sse2.PackUnsignedSaturate(
            Sse2.PackSignedSaturate(upper.GetLower(), upper.GetUpper()),
            Sse2.PackSignedSaturate(lower.GetLower(), lower.GetUpper())).AsUInt64();

        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dest, y * stride), bytes.GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dest, (y + 1) * stride), bytes.GetElement(1));
    }

    /// <summary>8x8 transpose of 16-bit values held as eight rows.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose(
        ref Vector128<short> r0,
        ref Vector128<short> r1,
        ref Vector128<short> r2,
        ref Vector128<short> r3,
        ref Vector128<short> r4,
        ref Vector128<short> r5,
        ref Vector128<short> r6,
        ref Vector128<short> r7)
    {
        var a0 = Sse2.UnpackLow(r0, r1).AsInt32();
        var a1 = Sse2.UnpackHigh(r0, r1).AsInt32();
        var a2 = Sse2.UnpackLow(r2, r3).AsInt32();
        var a3 = Sse2.UnpackHigh(r2, r3).AsInt32();
        var a4 = Sse2.UnpackLow(r4, r5).AsInt32();
        var a5 = Sse2.UnpackHigh(r4, r5).AsInt32();
        var a6 = Sse2.UnpackLow(r6, r7).AsInt32();
        var a7 = Sse2.UnpackHigh(r6, r7).AsInt32();

        var b0 = Sse2.UnpackLow(a0, a2).AsInt64();
        var b1 = Sse2.UnpackHigh(a0, a2).AsInt64();
        var b2 = Sse2.UnpackLow(a1, a3).AsInt64();
        var b3 = Sse2.UnpackHigh(a1, a3).AsInt64();
        var b4 = Sse2.UnpackLow(a4, a6).AsInt64();
        var b5 = Sse2.UnpackHigh(a4, a6).AsInt64();
        var b6 = Sse2.UnpackLow(a5, a7).AsInt64();
        var b7 = Sse2.UnpackHigh(a5, a7).AsInt64();

        r0 = Sse2.UnpackLow(b0, b4).AsInt16();
        r1 = Sse2.UnpackHigh(b0, b4).AsInt16();
        r2 = Sse2.UnpackLow(b1, b5).AsInt16();
        r3 = Sse2.UnpackHigh(b1, b5).AsInt16();
        r4 = Sse2.UnpackLow(b2, b6).AsInt16();
        r5 = Sse2.UnpackHigh(b2, b6).AsInt16();
        r6 = Sse2.UnpackLow(b3, b7).AsInt16();
        r7 = Sse2.UnpackHigh(b3, b7).AsInt16();
    }

    private static void CheckDestination(Span<byte> dest, int stride)
    {
        if (stride < 8 || dest.Length < (7 * stride) + 8)
        {
            throw new ArgumentException("Destination is too small for an 8x8 block.", nameof(dest));
        }
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
}
