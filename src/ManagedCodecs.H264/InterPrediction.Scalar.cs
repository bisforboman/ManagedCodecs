// The Unity build's (netstandard2.1) stand-ins for the vector entry points of InterPrediction.Fast.cs and
// InterPrediction.Fast16.cs, which only the net10.0 build compiles: each the reference it is tested against.
#if !NET
namespace ManagedCodecs.H264;

internal static partial class InterPrediction
{
    public static void PredictLuma(Picture reference, byte[] dest, int stride, int x, int y, int width, int height, int mvX, int mvY) =>
        PredictLumaReference(reference, dest, stride, x, y, width, height, mvX, mvY);

    public static void PredictLuma(Picture reference, int x, int y, int width, int height, int mvX, int mvY, Span<byte> output, int outputStride, int component = 0) =>
        PredictLumaReference(reference, x, y, width, height, mvX, mvY, output, outputStride, component);

    public static void PredictLuma16(Picture reference, int x, int y, int width, int height, int mvX, int mvY, Span<ushort> output, int outputStride, int component = 0) =>
        PredictLumaReference(reference, x, y, width, height, mvX, mvY, output, outputStride, component);

    public static void PredictChroma(Picture reference, int component, byte[] dest, int stride, int x, int y, int width, int height, int mvX, int mvY) =>
        PredictChromaReference(reference, component, dest, stride, x, y, width, height, mvX, mvY);

    public static void PredictChroma(Picture reference, int component, int x, int y, int width, int height, int mvX, int mvY, Span<byte> output, int outputStride) =>
        PredictChromaReference(reference, component, x, y, width, height, mvX, mvY, output, outputStride);

    public static void PredictChroma16(Picture reference, int component, int x, int y, int width, int height, int mvX, int mvY, Span<ushort> output, int outputStride) =>
        PredictChromaReference(reference, component, x, y, width, height, mvX, mvY, output, outputStride);

    public static void PredictChromaPair(Picture reference, int x, int y, int width, int height, int mvX, int mvY, Span<byte> cb, Span<byte> cr, int outputStride)
    {
        PredictChromaReference(reference, 1, x, y, width, height, mvX, mvY, cb, outputStride);
        PredictChromaReference(reference, 2, x, y, width, height, mvX, mvY, cr, outputStride);
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
}
#endif
