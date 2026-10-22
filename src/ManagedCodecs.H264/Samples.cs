using System.Numerics;

namespace ManagedCodecs.H264;

/// <summary>Helpers for picture samples of either type: byte (8-bit video) or ushort (deeper).</summary>
internal static class Samples
{
    /// <summary>
    /// (1 &lt;&lt; <paramref name="bitDepth"/>) - 1, the largest sample value (Clip1's bound); for byte
    /// samples the constant 255, which the JIT folds into each byte instantiation.
    /// </summary>
    public static int Max<TPixel>(int bitDepth)
        where TPixel : unmanaged, IBinaryInteger<TPixel> =>
        typeof(TPixel) == typeof(byte) ? 255 : (1 << bitDepth) - 1;
}
