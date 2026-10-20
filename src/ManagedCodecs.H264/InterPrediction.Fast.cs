using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace ManagedCodecs.H264;

internal static partial class InterPrediction
{
    // The largest luma block is 16x16; its 6-tap filters need 2 samples before and 3 after.
    private const int WindowStride = 16 + 5;

    /// <summary>Same output as <see cref="PredictLumaReference"/>.</summary>
    public static void PredictLuma(Picture reference, byte[] dest, int stride, int x, int y, int width, int height, int mvX, int mvY)
    {
        int xFrac = mvX & 3;
        int yFrac = mvY & 3;

        // Samples -2 .. size + 2 around the integer position. Read them straight from the
        // reference when they are inside it (with room for 16-byte loads), else from a copy
        // with the coordinates clamped.
        int left = x + (mvX >> 2) - 2;
        int top = y + (mvY >> 2) - 2;
        int maxX = (reference.WidthInMbs * 16) - 1;
        int maxY = (reference.HeightInMbs * 16) - 1;
        Span<byte> copy = stackalloc byte[(WindowStride * WindowStride) + 32]; // slack for 16-byte loads
        ReadOnlySpan<byte> window = copy;
        int ws;
        int G;
        if (left >= 0 && top >= 0 && left + width + 4 <= maxX && top + height + 4 <= maxY
            && ((top + height + 5) * stride) + left + 16 <= reference.Y.Length)
        {
            window = reference.Y;
            ws = stride;
            G = ((top + 2) * stride) + left + 2;
        }
        else
        {
            FetchWindow(reference.Y, stride, maxX, maxY, left, top, width + 5, height + 5, copy);
            ws = WindowStride;
            G = (2 * WindowStride) + 2; // G of output (0, 0) sits at window (2, 2)
        }

        var output = dest.AsSpan((y * stride) + x);

        if (xFrac == 0 && yFrac == 0)
        {
            for (int j = 0; j < height; j++)
            {
                window.Slice(G + (j * ws), width).CopyTo(output.Slice(j * stride, width));
            }

            return;
        }

        Span<byte> first = stackalloc byte[(16 * 16) + 16];
        Span<byte> second = stackalloc byte[(16 * 16) + 16];

        switch (xFrac, yFrac)
        {
            case (_, 0):
                // a, b, c: horizontal half sample b, averaged with G or H for the quarter positions.
                HalfHorizontal(window, ws, G, width, height, first);
                if (xFrac == 2)
                {
                    Store(first, width, height, output, stride);
                }
                else
                {
                    AverageWithWindow(first, window, ws, G + (xFrac == 3 ? 1 : 0), width, height, output, stride);
                }

                break;

            case (0, _):
                // d, h, n: vertical half sample h, averaged with G or M.
                HalfVertical(window, ws, G, width, height, first);
                if (yFrac == 2)
                {
                    Store(first, width, height, output, stride);
                }
                else
                {
                    AverageWithWindow(first, window, ws, G + (yFrac == 3 ? ws : 0), width, height, output, stride);
                }

                break;

            case (2, 2):
                Center(window, ws, G, width, height, first);
                Store(first, width, height, output, stride);
                break;

            case (2, _):
                // f, q: j averaged with b on this row or the next.
                Center(window, ws, G, width, height, first);
                HalfHorizontal(window, ws, G + (yFrac == 3 ? ws : 0), width, height, second);
                Average(first, second, width, height, output, stride);
                break;

            case (_, 2):
                // i, k: j averaged with h in this column or the next.
                Center(window, ws, G, width, height, first);
                HalfVertical(window, ws, G + (xFrac == 3 ? 1 : 0), width, height, second);
                Average(first, second, width, height, output, stride);
                break;

            default:
                // e, g, p, r: b (this row or next) averaged with h (this column or next).
                HalfHorizontal(window, ws, G + (yFrac == 3 ? ws : 0), width, height, first);
                HalfVertical(window, ws, G + (xFrac == 3 ? 1 : 0), width, height, second);
                Average(first, second, width, height, output, stride);
                break;
        }
    }

    /// <summary>Same output as <see cref="PredictChromaReference"/>.</summary>
    public static void PredictChroma(Picture reference, int component, byte[] dest, int stride, int x, int y, int width, int height, int mvX, int mvY)
    {
        int xFrac = mvX & 7;
        int yFrac = mvY & 7;

        // As for luma: read the reference directly when the (width + 1) x (height + 1) area is
        // inside it with room for 16-byte loads, else a clamped copy.
        byte[] plane = reference.Plane(component);
        int left = x + (mvX >> 3);
        int top = y + (mvY >> 3);
        int maxX = (reference.WidthInMbs * 8) - 1;
        int maxY = (reference.HeightInMbs * 8) - 1;
        Span<byte> copy = stackalloc byte[(9 * 9) + 32];
        ReadOnlySpan<byte> window = copy;
        int windowStride = 9;
        int origin = 0;
        // The last row is read as 16 bytes from left + 1 (the right-hand neighbours).
        if (left >= 0 && top >= 0 && left + width <= maxX && top + height <= maxY && ((top + height) * stride) + left + 17 <= plane.Length)
        {
            window = plane;
            windowStride = stride;
            origin = (top * stride) + left;
        }
        else
        {
            FetchWindow(plane, stride, maxX, maxY, left, top, width + 1, height + 1, copy, windowStride);
        }

        // The four weights add up to 64, so every sum fits in 16 bits.
        var wA = Vector128.Create((short)((8 - xFrac) * (8 - yFrac)));
        var wB = Vector128.Create((short)(xFrac * (8 - yFrac)));
        var wC = Vector128.Create((short)((8 - xFrac) * yFrac));
        var wD = Vector128.Create((short)(xFrac * yFrac));
        var output = dest.AsSpan((y * stride) + x);

        for (int j = 0; j < height; j++)
        {
            int upper = origin + (j * windowStride);
            int lower = upper + windowStride;
            var sum = (Widen(window, upper) * wA) + (Widen(window, upper + 1) * wB) + (Widen(window, lower) * wC) + (Widen(window, lower + 1) * wD);
            var rounded = ((sum + Vector128.Create((short)32)) >> 6).AsUInt16();
            StorePartial(Vector128.Narrow(rounded, rounded), output[(j * stride)..], width);
        }
    }

    /// <summary>
    /// Copies a <paramref name="width"/> x <paramref name="height"/> area at (<paramref name="x"/>,
    /// <paramref name="y"/>) into the window, clamping coordinates into the picture.
    /// </summary>
    private static void FetchWindow(byte[] plane, int stride, int maxX, int maxY, int x, int y, int width, int height, Span<byte> window, int windowStride = WindowStride)
    {
        bool inside = x >= 0 && y >= 0 && x + width - 1 <= maxX && y + height - 1 <= maxY;
        for (int j = 0; j < height; j++)
        {
            var row = window.Slice(j * windowStride, width);
            int sy = Math.Clamp(y + j, 0, maxY) * stride;
            if (inside)
            {
                plane.AsSpan(sy + x, width).CopyTo(row);
                continue;
            }

            for (int i = 0; i < width; i++)
            {
                row[i] = plane[sy + Math.Clamp(x + i, 0, maxX)];
            }
        }
    }

    /// <summary>b: Clip1((b1 + 16) >> 5) for the half positions right of each G.</summary>
    private static void HalfHorizontal(ReadOnlySpan<byte> window, int ws, int origin, int width, int height, Span<byte> output)
    {
        for (int j = 0; j < height; j++)
        {
            for (int x = 0; x < width; x += 8)
            {
                int p = origin + (j * ws) - 2 + x;
                var tap = Tap(Widen(window, p), Widen(window, p + 1), Widen(window, p + 2), Widen(window, p + 3), Widen(window, p + 4), Widen(window, p + 5));
                StorePartial(RoundHalf(tap), output[((j * 16) + x)..], Math.Min(8, width));
            }
        }
    }

    /// <summary>h: Clip1((h1 + 16) >> 5) for the half positions below each G.</summary>
    private static void HalfVertical(ReadOnlySpan<byte> window, int ws, int origin, int width, int height, Span<byte> output)
    {
        for (int j = 0; j < height; j++)
        {
            for (int x = 0; x < width; x += 8)
            {
                int p = origin + ((j - 2) * ws) + x;
                var tap = Tap(Widen(window, p), Widen(window, p + ws), Widen(window, p + (2 * ws)), Widen(window, p + (3 * ws)), Widen(window, p + (4 * ws)), Widen(window, p + (5 * ws)));
                StorePartial(RoundHalf(tap), output[((j * 16) + x)..], Math.Min(8, width));
            }
        }
    }

    /// <summary>j: vertical 6-tap over unrounded horizontal intermediates, Clip1((j1 + 512) >> 10).</summary>
    private static void Center(ReadOnlySpan<byte> window, int ws, int origin, int width, int height, Span<byte> output)
    {
        // b1 for rows -2 .. height + 2; it fits in 16 bits (-2550 .. 10710).
        Span<short> b1 = stackalloc short[16 * (16 + 5)];
        for (int j = 0; j < height + 5; j++)
        {
            for (int x = 0; x < width; x += 8)
            {
                int p = origin + ((j - 2) * ws) - 2 + x;
                var tap = Tap(Widen(window, p), Widen(window, p + 1), Widen(window, p + 2), Widen(window, p + 3), Widen(window, p + 4), Widen(window, p + 5));
                tap.CopyTo(b1[((j * 16) + x)..]);
            }
        }

        // The vertical pass needs 32 bits.
        for (int j = 0; j < height; j++)
        {
            for (int x = 0; x < width; x += 8)
            {
                int p = (j * 16) + x;
                var low = Vector128.Create(512);
                var high = low;
                for (int k = 0; k < 6; k++)
                {
                    var row = Vector128.Create(b1.Slice(p + (k * 16), 8));
                    int weight = k switch { 0 or 5 => 1, 1 or 4 => -5, _ => 20 };
                    low += Vector128.WidenLower(row) * weight;
                    high += Vector128.WidenUpper(row) * weight;
                }

                var clamped = Vector128.Narrow(Clamp(low >> 10), Clamp(high >> 10));
                StorePartial(Vector128.Narrow(clamped.AsUInt16(), clamped.AsUInt16()), output[p..], Math.Min(8, width));
            }
        }
    }

    private static void Store(ReadOnlySpan<byte> values, int width, int height, Span<byte> output, int stride)
    {
        for (int j = 0; j < height; j++)
        {
            values.Slice(j * 16, width).CopyTo(output.Slice(j * stride, width));
        }
    }

    private static void Average(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int width, int height, Span<byte> output, int stride)
    {
        for (int j = 0; j < height; j++)
        {
            var avg = RoundingAverage(Vector128.Create(a.Slice(j * 16, 16)), Vector128.Create(b.Slice(j * 16, 16)));
            StorePartial(avg, output[(j * stride)..], width);
        }
    }

    private static void AverageWithWindow(ReadOnlySpan<byte> a, ReadOnlySpan<byte> window, int ws, int origin, int width, int height, Span<byte> output, int stride)
    {
        for (int j = 0; j < height; j++)
        {
            var avg = RoundingAverage(Vector128.Create(a.Slice(j * 16, 16)), Vector128.Create(window.Slice(origin + (j * ws), 16)));
            StorePartial(avg, output[(j * stride)..], width);
        }
    }

    /// <summary>(a + b + 1) >> 1 per byte, without widening.</summary>
    private static Vector128<byte> RoundingAverage(Vector128<byte> a, Vector128<byte> b) => (a | b) - ((a ^ b) >> 1);

    /// <summary>Eight samples from <paramref name="offset"/>, widened to 16 bits.</summary>
    private static Vector128<short> Widen(ReadOnlySpan<byte> data, int offset) =>
        Vector128.WidenLower(Vector128.Create(data.Slice(offset, 16))).AsInt16();

    private static Vector128<short> Tap(Vector128<short> e, Vector128<short> f, Vector128<short> g, Vector128<short> h, Vector128<short> i, Vector128<short> j) =>
        (e + j) - ((f + i) * 5) + ((g + h) * 20);

    /// <summary>Clip1((v + 16) >> 5) for eight 16-bit values, as bytes in the low half.</summary>
    private static Vector128<byte> RoundHalf(Vector128<short> tap)
    {
        var rounded = Vector128.Min(Vector128.Max((tap + Vector128.Create((short)16)) >> 5, Vector128<short>.Zero), Vector128.Create((short)255)).AsUInt16();
        return Vector128.Narrow(rounded, rounded);
    }

    private static Vector128<int> Clamp(Vector128<int> value) =>
        Vector128.Min(Vector128.Max(value, Vector128<int>.Zero), Vector128.Create(255));

    /// <summary>Writes the first <paramref name="count"/> (2, 4, 8 or 16) bytes of a vector.</summary>
    private static void StorePartial(Vector128<byte> value, Span<byte> destination, int count)
    {
        switch (count)
        {
            case 16:
                value.CopyTo(destination);
                break;
            case 8:
                MemoryMarshal.Write(destination, value.AsUInt64().ToScalar());
                break;
            case 4:
                MemoryMarshal.Write(destination, value.AsUInt32().ToScalar());
                break;
            default:
                MemoryMarshal.Write(destination, value.AsUInt16().ToScalar());
                break;
        }
    }

    private static byte Clip8(int value) => (byte)Math.Clamp(value, 0, 255);
}
