using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace ManagedCodecs.H264;

/// <summary>Weighted sample prediction (8.4.2.3.2): by the definition for any sample type, and in vectors for 8-bit samples.</summary>
internal static partial class InterPrediction
{
    /// <summary>
    /// Weighted prediction from one list (8-449, 8-450): Clip1(((p * w + 2^(logWd - 1)) >> logWd) + o), or p * w + o
    /// when logWd is 0; <paramref name="o"/> already scaled to the bit depth.
    /// </summary>
    public static void WeightOneReference<TPixel>(ReadOnlySpan<TPixel> prediction, int stride, int width, int height, Span<TPixel> output, int outputStride, int w, int o, int logWd, int max)
        where TPixel : unmanaged
    {
        int round = logWd >= 1 ? 1 << (logWd - 1) : 0;
        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++)
            {
                int p = Samples.ToInt(prediction[(j * stride) + i]);
                int value = logWd >= 1 ? (((p * w) + round) >> logWd) + o : (p * w) + o;
                output[(j * outputStride) + i] = Samples.From<TPixel>(Math.Clamp(value, 0, max));
            }
        }
    }

    /// <summary>Weighted bi-prediction (8-451): Clip1(((p0 * w0 + p1 * w1 + 2^logWd) >> (logWd + 1)) + o).</summary>
    public static void WeightBothReference<TPixel>(ReadOnlySpan<TPixel> prediction0, ReadOnlySpan<TPixel> prediction1, int stride, int width, int height, Span<TPixel> output, int outputStride, int w0, int w1, int o, int logWd, int max)
        where TPixel : unmanaged
    {
        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++)
            {
                int p0 = Samples.ToInt(prediction0[(j * stride) + i]);
                int p1 = Samples.ToInt(prediction1[(j * stride) + i]);
                int value = (((p0 * w0) + (p1 * w1) + (1 << logWd)) >> (logWd + 1)) + o;
                output[(j * outputStride) + i] = Samples.From<TPixel>(Math.Clamp(value, 0, max));
            }
        }
    }

    /// <summary>
    /// <see cref="WeightOneReference"/> for 8-bit samples, a row of up to 16 at a time in 16-bit lanes: with weights
    /// and offsets of -128 to 127 (7.4.3.2) every step stays within 16 bits. Rows are read as 16 bytes.
    /// </summary>
    public static void WeightOne(ReadOnlySpan<byte> prediction, int stride, int width, int height, Span<byte> output, int outputStride, int w, int o, int logWd)
    {
        CheckWeightBounds(prediction.Length, stride, height, output.Length, outputStride, width);
        ref byte source = ref MemoryMarshal.GetReference(prediction);
        ref byte dest = ref MemoryMarshal.GetReference(output);
        var weight = Vector128.Create((short)w);
        var offset = Vector128.Create((short)o);
        var round = Vector128.Create((short)(logWd >= 1 ? 1 << (logWd - 1) : 0));
        for (int j = 0; j < height; j++)
        {
            var p = Load(ref source, j * stride);
            var low = (((Lower(p) * weight) + round) >> logWd) + offset;
            var high = (((Upper(p) * weight) + round) >> logWd) + offset;
            StorePartial(ClampNarrow(low, high), ref Unsafe.Add(ref dest, j * outputStride), width);
        }
    }

    /// <summary><see cref="WeightBothReference"/> for 8-bit samples, in 32-bit lanes (the sum of two products needs 17 bits).</summary>
    public static void WeightBoth(ReadOnlySpan<byte> prediction0, ReadOnlySpan<byte> prediction1, int stride, int width, int height, Span<byte> output, int outputStride, int w0, int w1, int o, int logWd)
    {
        CheckWeightBounds(Math.Min(prediction0.Length, prediction1.Length), stride, height, output.Length, outputStride, width);
        ref byte source0 = ref MemoryMarshal.GetReference(prediction0);
        ref byte source1 = ref MemoryMarshal.GetReference(prediction1);
        ref byte dest = ref MemoryMarshal.GetReference(output);
        var weight0 = Vector128.Create(w0);
        var weight1 = Vector128.Create(w1);
        var offset = Vector128.Create(o);
        var round = Vector128.Create(1 << logWd);
        int shift = logWd + 1;
        for (int j = 0; j < height; j++)
        {
            var p0 = Load(ref source0, j * stride);
            var p1 = Load(ref source1, j * stride);
            var low = Vector128.Narrow(
                Sum(Vector128.WidenLower(Lower(p0)), Vector128.WidenLower(Lower(p1)), weight0, weight1, round, shift, offset),
                Sum(Vector128.WidenUpper(Lower(p0)), Vector128.WidenUpper(Lower(p1)), weight0, weight1, round, shift, offset));
            var high = Vector128.Narrow(
                Sum(Vector128.WidenLower(Upper(p0)), Vector128.WidenLower(Upper(p1)), weight0, weight1, round, shift, offset),
                Sum(Vector128.WidenUpper(Upper(p0)), Vector128.WidenUpper(Upper(p1)), weight0, weight1, round, shift, offset));
            StorePartial(ClampNarrow(low, high), ref Unsafe.Add(ref dest, j * outputStride), width);
        }
    }

    // One bi-predicted lane group, clamped to 0..255 already, so that it narrows to 16 bits as it is.
    private static Vector128<int> Sum(Vector128<int> p0, Vector128<int> p1, Vector128<int> w0, Vector128<int> w1, Vector128<int> round, int shift, Vector128<int> offset) =>
        Vector128.Min(Vector128.Max((((p0 * w0) + (p1 * w1) + round) >> shift) + offset, Vector128<int>.Zero), Vector128.Create(255));

    // Clip1 of sixteen 16-bit values, as bytes.
    private static Vector128<byte> ClampNarrow(Vector128<short> low, Vector128<short> high)
    {
        var max = Vector128.Create((short)255);
        return Vector128.Narrow(
            Vector128.Min(Vector128.Max(low, Vector128<short>.Zero), max).AsUInt16(),
            Vector128.Min(Vector128.Max(high, Vector128<short>.Zero), max).AsUInt16());
    }

    private static void CheckWeightBounds(int predictionLength, int stride, int height, int outputLength, int outputStride, int width)
    {
        if (height < 1 || ((height - 1) * stride) + 16 > predictionLength || ((height - 1) * outputStride) + width > outputLength || width is not (2 or 4 or 8 or 16))
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }
    }
}
