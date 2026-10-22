using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace ManagedCodecs.H264;

/// <summary>
/// The row-based interpolation for samples of more than 8 bits (ushort planes): the same steps as
/// the 8-bit version, in 32-bit lanes, as a 6-tap sum of 10-bit samples (up to 42 * 1023) no
/// longer fits in 16 bits. The window is always a clamped copy, widened to int.
/// </summary>
internal static partial class InterPrediction
{
    // Window rows: 21 samples (-2 .. 16 + 2) and room for a 4-lane load starting at the last one.
    private const int Window16Stride = 24;

    // Rows of the intermediate planes (first, second, b1): up to 16 samples, plus a 4-lane load's slack.
    private const int Plane16Stride = 16;

    /// <summary>Same output as <see cref="PredictLumaReference{TPixel}(Picture, int, int, int, int, int, int, Span{TPixel}, int, int)"/> for ushort planes.</summary>
    [SkipLocalsInit]
    public static void PredictLuma16(Picture reference, int x, int y, int width, int height, int mvX, int mvY, Span<ushort> output, int outputStride, int component = 0)
    {
        int stride = reference.LumaStride;
        int maxY = (reference.HeightInMbs * 16) - 1;
        ReadOnlySpan<ushort> plane = reference.Plane<ushort>(component);
        if (reference.Parity >= 0)
        {
            plane = plane[(reference.Parity * stride)..];
            stride *= 2;
            maxY = (reference.HeightInMbs * 8) - 1;
        }

        int xFrac = mvX & 3;
        int yFrac = mvY & 3;
        int maxX = (reference.WidthInMbs * 16) - 1;
        var max = Vector128.Create((1 << reference.BitDepth) - 1);

        // Samples -2 .. size + 2 around the integer position; G of output (0, 0) at window (2, 2).
        Span<int> window = stackalloc int[(Window16Stride * 21) + 4];
        FetchWindow16(plane, stride, maxX, maxY, x + (mvX >> 2) - 2, y + (mvY >> 2) - 2, width + 5, height + 5, window);
        const int ws = Window16Stride;
        const int g = (2 * Window16Stride) + 2;

        if (xFrac == 0 && yFrac == 0)
        {
            Average16(window, ws, g, window, ws, g, width, height, output, outputStride);
            return;
        }

        Span<int> first = stackalloc int[(Plane16Stride * 16) + 4];
        Span<int> second = stackalloc int[(Plane16Stride * 16) + 4];
        const int ps = Plane16Stride;

        switch (xFrac, yFrac)
        {
            case (_, 0):
                HalfHorizontal16(window, g, width, height, first, max);
                if (xFrac == 2)
                {
                    Average16(first, ps, 0, first, ps, 0, width, height, output, outputStride);
                }
                else
                {
                    Average16(first, ps, 0, window, ws, g + (xFrac == 3 ? 1 : 0), width, height, output, outputStride);
                }

                break;

            case (0, _):
                HalfVertical16(window, g, width, height, first, max);
                if (yFrac == 2)
                {
                    Average16(first, ps, 0, first, ps, 0, width, height, output, outputStride);
                }
                else
                {
                    Average16(first, ps, 0, window, ws, g + (yFrac == 3 ? ws : 0), width, height, output, outputStride);
                }

                break;

            case (2, 2):
                Center16(window, g, width, height, first, max);
                Average16(first, ps, 0, first, ps, 0, width, height, output, outputStride);
                break;

            case (2, _):
                Center16(window, g, width, height, first, max);
                HalfHorizontal16(window, g + (yFrac == 3 ? ws : 0), width, height, second, max);
                Average16(first, ps, 0, second, ps, 0, width, height, output, outputStride);
                break;

            case (_, 2):
                Center16(window, g, width, height, first, max);
                HalfVertical16(window, g + (xFrac == 3 ? 1 : 0), width, height, second, max);
                Average16(first, ps, 0, second, ps, 0, width, height, output, outputStride);
                break;

            default:
                HalfHorizontal16(window, g + (yFrac == 3 ? ws : 0), width, height, first, max);
                HalfVertical16(window, g + (xFrac == 3 ? 1 : 0), width, height, second, max);
                Average16(first, ps, 0, second, ps, 0, width, height, output, outputStride);
                break;
        }
    }

    /// <summary>Same output as <see cref="PredictChromaReference{TPixel}(Picture, int, int, int, int, int, int, int, Span{TPixel}, int)"/> for ushort planes.</summary>
    [SkipLocalsInit]
    public static void PredictChroma16(Picture reference, int component, int x, int y, int width, int height, int mvX, int mvY, Span<ushort> output, int outputStride)
    {
        int stride = reference.ChromaStride;
        int maxY = (reference.HeightInMbs * reference.MbHeightC) - 1;
        ReadOnlySpan<ushort> plane = reference.Plane<ushort>(component);
        if (reference.Parity >= 0)
        {
            plane = plane[(reference.Parity * stride)..];
            stride *= 2;
            maxY = (reference.HeightInMbs * reference.MbHeightC / 2) - 1;
        }

        int xFrac = mvX & 7;
        int yFrac = mvY & 7;
        int maxX = (reference.WidthInMbs * 8) - 1;

        // Up to 9 x 17 samples (an 8x16 block of 4:2:2 and its right and lower neighbours).
        Span<int> window = stackalloc int[(Window16Stride * 17) + 4];
        FetchWindow16(plane, stride, maxX, maxY, x + (mvX >> 3), y + (mvY >> 3), width + 1, height + 1, window);

        var wA = Vector128.Create((8 - xFrac) * (8 - yFrac));
        var wB = Vector128.Create(xFrac * (8 - yFrac));
        var wC = Vector128.Create((8 - xFrac) * yFrac);
        var wD = Vector128.Create(xFrac * yFrac);
        var round = Vector128.Create(32);
        for (int j = 0; j < height; j++)
        {
            int upper = j * Window16Stride;
            int lower = upper + Window16Stride;
            for (int i = 0; i < width; i += 4)
            {
                var sum = (Lanes(window, upper + i) * wA) + (Lanes(window, upper + i + 1) * wB)
                    + (Lanes(window, lower + i) * wC) + (Lanes(window, lower + i + 1) * wD);
                StoreLanes16((sum + round) >> 6, output[((j * outputStride) + i)..], Math.Min(4, width - i));
            }
        }
    }

    /// <summary>
    /// Copies a <paramref name="width"/> x <paramref name="height"/> area at (<paramref name="x"/>,
    /// <paramref name="y"/>) into the window as ints, clamping coordinates into the picture.
    /// </summary>
    private static void FetchWindow16(ReadOnlySpan<ushort> plane, int stride, int maxX, int maxY, int x, int y, int width, int height, Span<int> window)
    {
        bool inside = x >= 0 && x + width - 1 <= maxX;
        for (int j = 0; j < height; j++)
        {
            var row = window.Slice(j * Window16Stride, width);
            int sy = Math.Clamp(y + j, 0, maxY) * stride;
            if (inside)
            {
                var source = plane.Slice(sy + x, width);
                for (int i = 0; i < width; i++)
                {
                    row[i] = source[i];
                }

                continue;
            }

            for (int i = 0; i < width; i++)
            {
                row[i] = plane[sy + Math.Clamp(x + i, 0, maxX)];
            }
        }
    }

    /// <summary>b: Clip1((b1 + 16) >> 5) for the half positions right of each G.</summary>
    private static void HalfHorizontal16(ReadOnlySpan<int> window, int origin, int width, int height, Span<int> output, Vector128<int> max)
    {
        for (int j = 0; j < height; j++)
        {
            for (int x = 0; x < width; x += 4)
            {
                int p = origin + (j * Window16Stride) - 2 + x;
                var tap = Tap(Lanes(window, p), Lanes(window, p + 1), Lanes(window, p + 2), Lanes(window, p + 3), Lanes(window, p + 4), Lanes(window, p + 5));
                Vector128.Clamp((tap + Vector128.Create(16)) >> 5, Vector128<int>.Zero, max).CopyTo(output[((j * Plane16Stride) + x)..]);
            }
        }
    }

    /// <summary>h: Clip1((h1 + 16) >> 5) for the half positions below each G.</summary>
    private static void HalfVertical16(ReadOnlySpan<int> window, int origin, int width, int height, Span<int> output, Vector128<int> max)
    {
        const int ws = Window16Stride;
        for (int j = 0; j < height; j++)
        {
            for (int x = 0; x < width; x += 4)
            {
                int p = origin + ((j - 2) * ws) + x;
                var tap = Tap(Lanes(window, p), Lanes(window, p + ws), Lanes(window, p + (2 * ws)), Lanes(window, p + (3 * ws)), Lanes(window, p + (4 * ws)), Lanes(window, p + (5 * ws)));
                Vector128.Clamp((tap + Vector128.Create(16)) >> 5, Vector128<int>.Zero, max).CopyTo(output[((j * Plane16Stride) + x)..]);
            }
        }
    }

    /// <summary>j: the vertical 6-tap over unrounded horizontal intermediates, Clip1((j1 + 512) >> 10).</summary>
    [SkipLocalsInit]
    private static void Center16(ReadOnlySpan<int> window, int origin, int width, int height, Span<int> output, Vector128<int> max)
    {
        // b1 for rows -2 .. height + 2.
        Span<int> b1 = stackalloc int[(Plane16Stride * (16 + 5)) + 4];
        for (int j = 0; j < height + 5; j++)
        {
            for (int x = 0; x < width; x += 4)
            {
                int p = origin + ((j - 2) * Window16Stride) - 2 + x;
                Tap(Lanes(window, p), Lanes(window, p + 1), Lanes(window, p + 2), Lanes(window, p + 3), Lanes(window, p + 4), Lanes(window, p + 5))
                    .CopyTo(b1[((j * Plane16Stride) + x)..]);
            }
        }

        const int ps = Plane16Stride;
        for (int j = 0; j < height; j++)
        {
            for (int x = 0; x < width; x += 4)
            {
                int p = (j * ps) + x;
                var tap = Tap(Lanes(b1, p), Lanes(b1, p + ps), Lanes(b1, p + (2 * ps)), Lanes(b1, p + (3 * ps)), Lanes(b1, p + (4 * ps)), Lanes(b1, p + (5 * ps)));
                Vector128.Clamp((tap + Vector128.Create(512)) >> 10, Vector128<int>.Zero, max).CopyTo(output[p..]);
            }
        }
    }

    /// <summary>(a + b + 1) >> 1 of two planes into the output (a plane with itself: a copy).</summary>
    private static void Average16(ReadOnlySpan<int> a, int aStride, int aOrigin, ReadOnlySpan<int> b, int bStride, int bOrigin, int width, int height, Span<ushort> output, int outputStride)
    {
        var one = Vector128.Create(1);
        for (int j = 0; j < height; j++)
        {
            for (int x = 0; x < width; x += 4)
            {
                var sum = Lanes(a, aOrigin + (j * aStride) + x) + Lanes(b, bOrigin + (j * bStride) + x) + one;
                StoreLanes16(sum >> 1, output[((j * outputStride) + x)..], Math.Min(4, width - x));
            }
        }
    }

    private static Vector128<int> Lanes(ReadOnlySpan<int> data, int offset) => Vector128.Create(data.Slice(offset, 4));

    private static Vector128<int> Tap(Vector128<int> e, Vector128<int> f, Vector128<int> g, Vector128<int> h, Vector128<int> i, Vector128<int> j) =>
        (e + j) - ((f + i) * 5) + ((g + h) * 20);

    /// <summary>Writes the first <paramref name="count"/> (2 or 4) lanes, values 0 .. 65535, as ushorts.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreLanes16(Vector128<int> value, Span<ushort> destination, int count)
    {
        var narrow = Vector128.Narrow(value.AsUInt32(), value.AsUInt32());
        if (count == 4)
        {
            MemoryMarshal.Write(MemoryMarshal.AsBytes(destination[..4]), narrow.AsUInt64().ToScalar());
        }
        else
        {
            MemoryMarshal.Write(MemoryMarshal.AsBytes(destination[..2]), narrow.AsUInt32().ToScalar());
        }
    }
}
