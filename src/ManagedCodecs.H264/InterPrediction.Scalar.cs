// The Unity build's (netstandard2.1) stand-ins for the vector entry points of InterPrediction.Fast.cs and
// InterPrediction.Fast16.cs, which only the net10.0 build compiles. Each block's reference samples are read once,
// clamped, into a window, and the filters run over its rows and columns (the vector paths' order, in scalar code):
// the *Reference versions recompute and clamp every tap of every sample through local functions, which on Mono cost
// most of a P frame (docs/decisions/2026-10-31-unity-netstandard.md, Mono's profile). InterPredictionTests compares
// them with the references on random blocks in the Unity build's test run.
#if !NET
namespace ManagedCodecs.H264;

internal static partial class InterPrediction
{
    // The largest luma block is 16x16; its 6-tap filters need 2 samples before and 3 after.
    private const int LumaWindow = 16 + 5;

    // The largest chroma block is 8x16 (4:2:2), its bilinear filter one sample after.
    private const int ChromaWindow = 16 + 1;

    public static void PredictLuma(Picture reference, byte[] dest, int stride, int x, int y, int width, int height, int mvX, int mvY) =>
        PredictLumaScalar<byte>(reference, x, y, width, height, mvX, mvY, dest.AsSpan((y * stride) + x), stride, 0);

    public static void PredictLuma(Picture reference, int x, int y, int width, int height, int mvX, int mvY, Span<byte> output, int outputStride, int component = 0) =>
        PredictLumaScalar(reference, x, y, width, height, mvX, mvY, output, outputStride, component);

    public static void PredictLuma16(Picture reference, int x, int y, int width, int height, int mvX, int mvY, Span<ushort> output, int outputStride, int component = 0) =>
        PredictLumaScalar(reference, x, y, width, height, mvX, mvY, output, outputStride, component);

    public static void PredictChroma(Picture reference, int component, byte[] dest, int stride, int x, int y, int width, int height, int mvX, int mvY) =>
        PredictChromaScalar<byte>(reference, component, x, y, width, height, mvX, mvY, dest.AsSpan((y * stride) + x), stride);

    public static void PredictChroma(Picture reference, int component, int x, int y, int width, int height, int mvX, int mvY, Span<byte> output, int outputStride) =>
        PredictChromaScalar(reference, component, x, y, width, height, mvX, mvY, output, outputStride);

    public static void PredictChroma16(Picture reference, int component, int x, int y, int width, int height, int mvX, int mvY, Span<ushort> output, int outputStride) =>
        PredictChromaScalar(reference, component, x, y, width, height, mvX, mvY, output, outputStride);

    public static void PredictChromaPair(Picture reference, int x, int y, int width, int height, int mvX, int mvY, Span<byte> cb, Span<byte> cr, int outputStride)
    {
        PredictChromaScalar(reference, 1, x, y, width, height, mvX, mvY, cb, outputStride);
        PredictChromaScalar(reference, 2, x, y, width, height, mvX, mvY, cr, outputStride);
    }

    /// <summary>A cache hint only; nothing to do without the intrinsics.</summary>
    public static void PrefetchAhead(Picture reference, int x, int y, int phase)
    {
    }

    public static void Average(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int stride, int width, int height, Span<byte> output, int outputStride)
    {
        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++)
            {
                output[(j * outputStride) + i] = (byte)((a[(j * stride) + i] + b[(j * stride) + i] + 1) >> 1);
            }
        }
    }

    /// <summary>
    /// The same output as <see cref="PredictLumaReference{TPixel}(Picture, int, int, int, int, int, int, Span{TPixel}, int, int)"/>:
    /// the window of samples -2 .. size + 2 around the block's integer position, clamped (8-228, 8-229), then the
    /// half samples b, h and j (8.4.2.2.1) of whole rows and columns, and the quarter samples as their averages.
    /// </summary>
    private static void PredictLumaScalar<TPixel>(Picture reference, int x, int y, int width, int height, int mvX, int mvY, Span<TPixel> output, int outputStride, int component)
        where TPixel : unmanaged
    {
        var (origin, stride, maxY) = Plane(reference, reference.LumaStride, reference.HeightInMbs * 16);
        int maxX = (reference.WidthInMbs * 16) - 1;
        int max = Samples.Max<TPixel>(reference.BitDepth);
        int xFrac = mvX & 3;
        int yFrac = mvY & 3;

        Span<int> window = stackalloc int[LumaWindow * LumaWindow];
        FetchClamped(reference.Plane<TPixel>(component), origin, stride, maxX, maxY, x + (mvX >> 2) - 2, y + (mvY >> 2) - 2, width + 5, height + 5, window, LumaWindow);

        // G of output (0, 0) sits at window (2, 2); the second set of values is averaged with the first.
        const int G = (2 * LumaWindow) + 2;
        Span<int> first = stackalloc int[16 * 16];
        Span<int> second = stackalloc int[16 * 16];
        bool average = true;
        switch (xFrac, yFrac)
        {
            case (0, 0):
                Full(window, G, width, height, first);
                average = false;
                break;
            case (0, 2):
                HalfVertical(window, G, width, height, max, first);
                average = false;
                break;
            case (2, 0):
                HalfHorizontal(window, G, width, height, max, first);
                average = false;
                break;
            case (2, 2):
                Center(window, width, height, max, first);
                average = false;
                break;
            case (0, _):
                // d, n: h averaged with G or M.
                HalfVertical(window, G, width, height, max, first);
                Full(window, G + (yFrac == 3 ? LumaWindow : 0), width, height, second);
                break;
            case (_, 0):
                // a, c: b averaged with G or H.
                HalfHorizontal(window, G, width, height, max, first);
                Full(window, G + (xFrac == 3 ? 1 : 0), width, height, second);
                break;
            case (2, _):
                // f, q: j averaged with b on this row or the next.
                Center(window, width, height, max, first);
                HalfHorizontal(window, G + (yFrac == 3 ? LumaWindow : 0), width, height, max, second);
                break;
            case (_, 2):
                // i, k: j averaged with h in this column or the next.
                Center(window, width, height, max, first);
                HalfVertical(window, G + (xFrac == 3 ? 1 : 0), width, height, max, second);
                break;
            default:
                // e, g, p, r: b (this row or the next) averaged with h (this column or the next).
                HalfHorizontal(window, G + (yFrac == 3 ? LumaWindow : 0), width, height, max, first);
                HalfVertical(window, G + (xFrac == 3 ? 1 : 0), width, height, max, second);
                break;
        }

        for (int j = 0; j < height; j++)
        {
            Span<TPixel> row = output.Slice(j * outputStride, width);
            int at = j * 16;
            for (int i = 0; i < row.Length; i++)
            {
                int value = average ? (first[at + i] + second[at + i] + 1) >> 1 : first[at + i];
                row[i] = Samples.From<TPixel>(value);
            }
        }
    }

    /// <summary>
    /// The same output as <see cref="PredictChromaReference{TPixel}(Picture, int, int, int, int, int, int, int, Span{TPixel}, int)"/>:
    /// the window of the block's samples and one more column and row, clamped, then the bilinear filter (8-270).
    /// </summary>
    private static void PredictChromaScalar<TPixel>(Picture reference, int component, int x, int y, int width, int height, int mvX, int mvY, Span<TPixel> output, int outputStride)
        where TPixel : unmanaged
    {
        var (origin, stride, maxY) = Plane(reference, reference.ChromaStride, reference.HeightInMbs * reference.MbHeightC);
        int maxX = (reference.WidthInMbs * 8) - 1;
        int xFrac = mvX & 7;
        int yFrac = mvY & 7;

        Span<int> window = stackalloc int[ChromaWindow * ChromaWindow];
        FetchClamped(reference.Plane<TPixel>(component), origin, stride, maxX, maxY, x + (mvX >> 3), y + (mvY >> 3), width + 1, height + 1, window, ChromaWindow);

        int a = (8 - xFrac) * (8 - yFrac);
        int b = xFrac * (8 - yFrac);
        int c = (8 - xFrac) * yFrac;
        int d = xFrac * yFrac;
        for (int j = 0; j < height; j++)
        {
            Span<TPixel> row = output.Slice(j * outputStride, width);
            int at = j * ChromaWindow;
            for (int i = 0; i < row.Length; i++)
            {
                int value = (a * window[at + i]) + (b * window[at + i + 1])
                    + (c * window[at + ChromaWindow + i]) + (d * window[at + ChromaWindow + i + 1]);
                row[i] = Samples.From<TPixel>((value + 32) >> 6);
            }
        }
    }

    /// <summary>
    /// <paramref name="width"/> x <paramref name="height"/> samples from (<paramref name="left"/>, <paramref name="top"/>)
    /// of the plane at <paramref name="origin"/>, each coordinate clamped to the picture, into
    /// <paramref name="window"/> as ints.
    /// </summary>
    private static void FetchClamped<TPixel>(TPixel[] plane, int origin, int stride, int maxX, int maxY, int left, int top, int width, int height, Span<int> window, int windowStride)
        where TPixel : unmanaged
    {
        bool inside = left >= 0 && left + width - 1 <= maxX;
        for (int j = 0; j < height; j++)
        {
            int sy = top + j;
            sy = sy < 0 ? 0 : sy > maxY ? maxY : sy;
            Span<int> row = window.Slice(j * windowStride, width);
            if (inside)
            {
                ReadOnlySpan<TPixel> source = plane.AsSpan(origin + (sy * stride) + left, width);
                for (int i = 0; i < row.Length; i++)
                {
                    row[i] = Samples.ToInt(source[i]);
                }
            }
            else
            {
                int rowStart = origin + (sy * stride);
                for (int i = 0; i < row.Length; i++)
                {
                    int sx = left + i;
                    sx = sx < 0 ? 0 : sx > maxX ? maxX : sx;
                    row[i] = Samples.ToInt(plane[rowStart + sx]);
                }
            }
        }
    }

    /// <summary>The integer samples from window position <paramref name="at"/> on.</summary>
    private static void Full(ReadOnlySpan<int> window, int at, int width, int height, Span<int> output)
    {
        for (int j = 0; j < height; j++)
        {
            window.Slice(at + (j * LumaWindow), width).CopyTo(output.Slice(j * 16, width));
        }
    }

    /// <summary>The horizontal half samples b (8-241, 8-243), their G at window position <paramref name="at"/> on.</summary>
    private static void HalfHorizontal(ReadOnlySpan<int> window, int at, int width, int height, int max, Span<int> output)
    {
        for (int j = 0; j < height; j++)
        {
            ReadOnlySpan<int> s = window.Slice(at + (j * LumaWindow) - 2, width + 5);
            Span<int> o = output.Slice(j * 16, width);
            for (int i = 0; i < o.Length; i++)
            {
                int b1 = s[i] - (5 * s[i + 1]) + (20 * s[i + 2]) + (20 * s[i + 3]) - (5 * s[i + 4]) + s[i + 5];
                o[i] = ClipHalf(b1, max);
            }
        }
    }

    /// <summary>The vertical half samples h (8-242, 8-244), their G at window position <paramref name="at"/> on.</summary>
    private static void HalfVertical(ReadOnlySpan<int> window, int at, int width, int height, int max, Span<int> output)
    {
        for (int j = 0; j < height; j++)
        {
            int top = at + ((j - 2) * LumaWindow);
            Span<int> o = output.Slice(j * 16, width);
            for (int i = 0; i < o.Length; i++)
            {
                int p = top + i;
                int h1 = window[p] - (5 * window[p + LumaWindow]) + (20 * window[p + (2 * LumaWindow)])
                    + (20 * window[p + (3 * LumaWindow)]) - (5 * window[p + (4 * LumaWindow)]) + window[p + (5 * LumaWindow)];
                o[i] = ClipHalf(h1, max);
            }
        }
    }

    /// <summary>The center half samples j (8-245, 8-247): the vertical filter over unrounded b1 of every window row.</summary>
    private static void Center(ReadOnlySpan<int> window, int width, int height, int max, Span<int> output)
    {
        Span<int> b1 = stackalloc int[LumaWindow * 16];
        for (int r = 0; r < height + 5; r++)
        {
            ReadOnlySpan<int> s = window.Slice(r * LumaWindow, width + 5);
            Span<int> o = b1.Slice(r * 16, width);
            for (int i = 0; i < o.Length; i++)
            {
                o[i] = s[i] - (5 * s[i + 1]) + (20 * s[i + 2]) + (20 * s[i + 3]) - (5 * s[i + 4]) + s[i + 5];
            }
        }

        for (int j = 0; j < height; j++)
        {
            Span<int> o = output.Slice(j * 16, width);
            for (int i = 0; i < o.Length; i++)
            {
                int p = (j * 16) + i;
                int j1 = b1[p] - (5 * b1[p + 16]) + (20 * b1[p + 32]) + (20 * b1[p + 48]) - (5 * b1[p + 64]) + b1[p + 80];
                int value = (j1 + 512) >> 10;
                o[i] = value < 0 ? 0 : value > max ? max : value;
            }
        }
    }

    private static int ClipHalf(int value, int max)
    {
        value = (value + 16) >> 5;
        return value < 0 ? 0 : value > max ? max : value;
    }
}
#endif
