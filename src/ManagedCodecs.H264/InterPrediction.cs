using System.Numerics;
using System.Runtime.InteropServices;

namespace ManagedCodecs.H264;

/// <summary>
/// Fractional sample interpolation (H.264 clause 8.4.2.2). Reference samples outside the
/// picture repeat the nearest edge sample (coordinates are clamped, equations 8-228/8-229).
/// </summary>
/// <remarks>
/// <c>PredictLuma</c> and <c>PredictChroma</c> copy the reference window once
/// and filter whole rows; the <c>*Reference</c> versions compute every sample directly from the
/// equations and are kept as the specification for tests.
/// </remarks>
internal static partial class InterPrediction
{
    /// <summary>
    /// Predicts a <paramref name="width"/> x <paramref name="height"/> luma block whose top-left
    /// is at (<paramref name="x"/>, <paramref name="y"/>) in the picture, with a motion vector in
    /// quarter samples, into <paramref name="dest"/> at the same position.
    /// </summary>
    public static void PredictLumaReference<TPixel>(Picture reference, TPixel[] dest, int stride, int x, int y, int width, int height, int mvX, int mvY)
        where TPixel : unmanaged, IBinaryInteger<TPixel> =>
        PredictLumaReference(reference, x, y, width, height, mvX, mvY, dest.AsSpan((y * stride) + x), stride);

    /// <summary>
    /// The same, written to <paramref name="output"/> (the block's top-left), from plane
    /// <paramref name="component"/> (a chroma plane in 4:4:4, whose chroma uses the luma filter). A field view
    /// (<see cref="Picture.Field"/>) is a picture of its own: every other row, half the height.
    /// </summary>
    public static void PredictLumaReference<TPixel>(Picture reference, int x, int y, int width, int height, int mvX, int mvY, Span<TPixel> output, int outputStride, int component = 0)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        var (origin, stride, maxY) = Plane(reference, reference.LumaStride, reference.HeightInMbs * 16);
        int maxX = (reference.WidthInMbs * 16) - 1;
        TPixel[] src = reference.Plane<TPixel>(component);
        int max = Samples.Max<TPixel>(reference.BitDepth);
        int xFrac = mvX & 3;
        int yFrac = mvY & 3;

        int S(int sx, int sy) => int.CreateTruncating(src[origin + (Math.Clamp(sy, 0, maxY) * stride) + Math.Clamp(sx, 0, maxX)]);

        // Half-sample values before rounding (b1, h1 and j1 of 8.4.2.2.1).
        int B1(int sx, int sy) => Tap(S(sx - 2, sy), S(sx - 1, sy), S(sx, sy), S(sx + 1, sy), S(sx + 2, sy), S(sx + 3, sy));
        int H1(int sx, int sy) => Tap(S(sx, sy - 2), S(sx, sy - 1), S(sx, sy), S(sx, sy + 1), S(sx, sy + 2), S(sx, sy + 3));
        int J1(int sx, int sy) => Tap(B1(sx, sy - 2), B1(sx, sy - 1), B1(sx, sy), B1(sx, sy + 1), B1(sx, sy + 2), B1(sx, sy + 3));

        int Half(int value) => Math.Clamp((value + 16) >> 5, 0, max);
        int Center(int j1) => Math.Clamp((j1 + 512) >> 10, 0, max);

        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++)
            {
                // G is the integer sample; H to its right, M below.
                int gx = x + i + (mvX >> 2);
                int gy = y + j + (mvY >> 2);

                int value = (xFrac, yFrac) switch
                {
                    (0, 0) => S(gx, gy),
                    (0, 1) => Avg(S(gx, gy), Half(H1(gx, gy))),                 // d
                    (0, 2) => Half(H1(gx, gy)),                                 // h
                    (0, 3) => Avg(S(gx, gy + 1), Half(H1(gx, gy))),             // n
                    (1, 0) => Avg(S(gx, gy), Half(B1(gx, gy))),                 // a
                    (2, 0) => Half(B1(gx, gy)),                                 // b
                    (3, 0) => Avg(S(gx + 1, gy), Half(B1(gx, gy))),             // c
                    (1, 1) => Avg(Half(B1(gx, gy)), Half(H1(gx, gy))),          // e
                    (3, 1) => Avg(Half(B1(gx, gy)), Half(H1(gx + 1, gy))),      // g
                    (1, 3) => Avg(Half(H1(gx, gy)), Half(B1(gx, gy + 1))),      // p
                    (3, 3) => Avg(Half(H1(gx + 1, gy)), Half(B1(gx, gy + 1))),  // r
                    (2, 2) => Center(J1(gx, gy)),                               // j
                    (2, 1) => Avg(Half(B1(gx, gy)), Center(J1(gx, gy))),        // f
                    (2, 3) => Avg(Center(J1(gx, gy)), Half(B1(gx, gy + 1))),    // q
                    (1, 2) => Avg(Half(H1(gx, gy)), Center(J1(gx, gy))),        // i
                    _ => Avg(Center(J1(gx, gy)), Half(H1(gx + 1, gy))),         // k (3, 2)
                };

                output[(j * outputStride) + i] = TPixel.CreateTruncating(value);
            }
        }
    }

    /// <summary>
    /// Predicts a chroma block at chroma position (<paramref name="x"/>, <paramref name="y"/>) with
    /// a motion vector in eighth chroma samples (8.4.2.2.2): the luma vector in 4:2:0; in 4:2:2,
    /// whose chroma has the luma's rows, its vertical component doubled.
    /// </summary>
    public static void PredictChromaReference<TPixel>(Picture reference, int component, TPixel[] dest, int stride, int x, int y, int width, int height, int mvX, int mvY)
        where TPixel : unmanaged, IBinaryInteger<TPixel> =>
        PredictChromaReference(reference, component, x, y, width, height, mvX, mvY, dest.AsSpan((y * stride) + x), stride);

    /// <summary>The same, written to <paramref name="output"/> (the block's top-left); field views as for luma.</summary>
    public static void PredictChromaReference<TPixel>(Picture reference, int component, int x, int y, int width, int height, int mvX, int mvY, Span<TPixel> output, int outputStride)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        var (origin, stride, maxY) = Plane(reference, reference.ChromaStride, reference.HeightInMbs * reference.MbHeightC);
        int maxX = (reference.WidthInMbs * 8) - 1;
        TPixel[] src = reference.Plane<TPixel>(component);
        int xFrac = mvX & 7;
        int yFrac = mvY & 7;

        int S(int sx, int sy) => int.CreateTruncating(src[origin + (Math.Clamp(sy, 0, maxY) * stride) + Math.Clamp(sx, 0, maxX)]);

        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++)
            {
                int ax = x + i + (mvX >> 3);
                int ay = y + j + (mvY >> 3);
                int value = ((8 - xFrac) * (8 - yFrac) * S(ax, ay))
                    + (xFrac * (8 - yFrac) * S(ax + 1, ay))
                    + ((8 - xFrac) * yFrac * S(ax, ay + 1))
                    + (xFrac * yFrac * S(ax + 1, ay + 1));
                output[(j * outputStride) + i] = TPixel.CreateTruncating((value + 32) >> 6);
            }
        }
    }

    /// <summary>
    /// A luma block of any sample type: through the row-based <see cref="PredictLuma(Picture, int, int, int, int, int, int, Span{byte}, int, int)"/>
    /// for 8-bit samples, <see cref="PredictLuma16"/> for deeper ones.
    /// </summary>
    public static void PredictLumaBlock<TPixel>(Picture reference, int x, int y, int width, int height, int mvX, int mvY, Span<TPixel> output, int outputStride, int component = 0)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        if (typeof(TPixel) == typeof(byte))
        {
            PredictLuma(reference, x, y, width, height, mvX, mvY, MemoryMarshal.Cast<TPixel, byte>(output), outputStride, component);
            return;
        }

        PredictLuma16(reference, x, y, width, height, mvX, mvY, MemoryMarshal.Cast<TPixel, ushort>(output), outputStride, component);
    }

    /// <summary>A chroma block of any sample type, as <see cref="PredictLumaBlock"/>.</summary>
    public static void PredictChromaBlock<TPixel>(Picture reference, int component, int x, int y, int width, int height, int mvX, int mvY, Span<TPixel> output, int outputStride)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        if (typeof(TPixel) == typeof(byte))
        {
            PredictChroma(reference, component, x, y, width, height, mvX, mvY, MemoryMarshal.Cast<TPixel, byte>(output), outputStride);
            return;
        }

        PredictChroma16(reference, component, x, y, width, height, mvX, mvY, MemoryMarshal.Cast<TPixel, ushort>(output), outputStride);
    }

    /// <summary>The rounded average of two predictions (8.4.2.3.1), of any sample type, as <see cref="Average(ReadOnlySpan{byte}, ReadOnlySpan{byte}, int, int, int, Span{byte}, int)"/>.</summary>
    public static void AverageBlock<TPixel>(ReadOnlySpan<TPixel> a, ReadOnlySpan<TPixel> b, int stride, int width, int height, Span<TPixel> output, int outputStride)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        if (typeof(TPixel) == typeof(byte))
        {
            Average(MemoryMarshal.Cast<TPixel, byte>(a), MemoryMarshal.Cast<TPixel, byte>(b), stride, width, height, MemoryMarshal.Cast<TPixel, byte>(output), outputStride);
            return;
        }

        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++)
            {
                output[(j * outputStride) + i] = TPixel.CreateTruncating((int.CreateTruncating(a[(j * stride) + i]) + int.CreateTruncating(b[(j * stride) + i]) + 1) >> 1);
            }
        }
    }

    /// <summary>A plane's first sample, row stride and last row: the frame's, or a field view's every other row.</summary>
    private static (int Origin, int Stride, int MaxY) Plane(Picture reference, int stride, int height) =>
        reference.Parity < 0 ? (0, stride, height - 1) : (reference.Parity * stride, stride * 2, (height / 2) - 1);

    private static int Tap(int e, int f, int g, int h, int i, int j) => e - (5 * f) + (20 * g) + (20 * h) - (5 * i) + j;

    private static int Avg(int a, int b) => (a + b + 1) >> 1;
}
