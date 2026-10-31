using System.Runtime.CompilerServices;

namespace ManagedCodecs.H264;

/// <summary>
/// Helpers for picture samples of either type: byte (8-bit video) or ushort (deeper), the only two the decoder
/// instantiates. Conversions dispatch on <c>typeof(TPixel)</c>, which the JIT folds into each instantiation, rather than
/// on generic math (<c>IBinaryInteger</c>), which needs static abstract interface members that netstandard2.1's runtimes
/// (Mono, IL2CPP) lack (docs/decisions/2026-10-31-unity-netstandard.md).
/// </summary>
internal static class Samples
{
    /// <summary>
    /// (1 &lt;&lt; <paramref name="bitDepth"/>) - 1, the largest sample value (Clip1's bound); for byte
    /// samples the constant 255, which the JIT folds into each byte instantiation.
    /// </summary>
    public static int Max<TPixel>(int bitDepth)
        where TPixel : unmanaged =>
        typeof(TPixel) == typeof(byte) ? 255 : (1 << bitDepth) - 1;

    /// <summary>A sample's value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ToInt<TPixel>(TPixel sample)
        where TPixel : unmanaged =>
        typeof(TPixel) == typeof(byte) ? Unsafe.As<TPixel, byte>(ref sample) : Unsafe.As<TPixel, ushort>(ref sample);

    /// <summary>A sample of the value's low bits (8 or 16, as a cast truncates), as <c>CreateTruncating</c> made it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TPixel From<TPixel>(int value)
        where TPixel : unmanaged
    {
        if (typeof(TPixel) == typeof(byte))
        {
            byte b = (byte)value;
            return Unsafe.As<byte, TPixel>(ref b);
        }

        ushort u = (ushort)value;
        return Unsafe.As<ushort, TPixel>(ref u);
    }
}
