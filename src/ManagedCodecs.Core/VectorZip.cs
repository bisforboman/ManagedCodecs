using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace ManagedCodecs;

/// <summary>
/// SSE2's unpack (interleave) instructions, for the transposes: SSE2's own on x64, ARM64's zip1 and zip2 (the same
/// interleaves), and elsewhere (WebAssembly) cross-platform <see cref="Vector128"/> operations: each lane comes from a
/// constant shuffle of one input, the inputs alternating lane by lane.
/// </summary>
/// <remarks>
/// On x64 each is SSE2's own unpack and on ARM64 a zip; the <c>Portable</c> forms are the portable code alone, for
/// WebAssembly and the tests that check them on x64 and ARM64. On ARM64 the portable form was two table lookups and a
/// select an interleave. The portable form's <see cref="Vector128.Shuffle(Vector128{byte}, Vector128{byte})"/>
/// is one pshufb only where SSSE3 is part of the compiled baseline: a Native AOT build for the default x64 target
/// (SSE2) calls a software shuffle instead, which took a third of MJPEG's time there.
/// </remarks>
internal static class VectorZip
{
    /// <summary>a0 b0 a1 b1 .. a7 b7, as punpcklbw.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> Low(Vector128<byte> a, Vector128<byte> b) =>
        Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipLow(a, b) : PortableLow(a, b);

    /// <summary><see cref="Low(Vector128{byte}, Vector128{byte})"/> in portable operations alone (what WebAssembly runs).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<byte> PortableLow(Vector128<byte> a, Vector128<byte> b) => Vector128.ConditionalSelect(
        Vector128.Create(0xFF, 0, 0xFF, 0, 0xFF, 0, 0xFF, 0, 0xFF, 0, 0xFF, 0, 0xFF, 0, 0xFF, 0).AsByte(),
        Vector128.Shuffle(a, Vector128.Create((byte)0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7)),
        Vector128.Shuffle(b, Vector128.Create((byte)0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7)));

    /// <summary>a8 b8 a9 b9 .. a15 b15, as punpckhbw.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> High(Vector128<byte> a, Vector128<byte> b) =>
        Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipHigh(a, b) : PortableHigh(a, b);

    /// <summary><see cref="High(Vector128{byte}, Vector128{byte})"/> in portable operations alone (what WebAssembly runs).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<byte> PortableHigh(Vector128<byte> a, Vector128<byte> b) => Vector128.ConditionalSelect(
        Vector128.Create(0xFF, 0, 0xFF, 0, 0xFF, 0, 0xFF, 0, 0xFF, 0, 0xFF, 0, 0xFF, 0, 0xFF, 0).AsByte(),
        Vector128.Shuffle(a, Vector128.Create((byte)8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15)),
        Vector128.Shuffle(b, Vector128.Create((byte)8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15)));

    /// <summary>a0 b0 a1 b1 a2 b2 a3 b3, as punpcklwd.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ushort> Low(Vector128<ushort> a, Vector128<ushort> b) =>
        Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipLow(a, b) : PortableLow(a, b);

    /// <summary><see cref="Low(Vector128{ushort}, Vector128{ushort})"/> in portable operations alone (what WebAssembly runs).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<ushort> PortableLow(Vector128<ushort> a, Vector128<ushort> b) => Vector128.ConditionalSelect(
        Vector128.Create(0xFFFF, 0, 0xFFFF, 0, 0xFFFF, 0, 0xFFFF, 0).AsUInt16(),
        Vector128.Shuffle(a, Vector128.Create((ushort)0, 0, 1, 1, 2, 2, 3, 3)),
        Vector128.Shuffle(b, Vector128.Create((ushort)0, 0, 1, 1, 2, 2, 3, 3)));

    /// <summary>a4 b4 a5 b5 a6 b6 a7 b7, as punpckhwd.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ushort> High(Vector128<ushort> a, Vector128<ushort> b) =>
        Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipHigh(a, b) : PortableHigh(a, b);

    /// <summary><see cref="High(Vector128{ushort}, Vector128{ushort})"/> in portable operations alone (what WebAssembly runs).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<ushort> PortableHigh(Vector128<ushort> a, Vector128<ushort> b) => Vector128.ConditionalSelect(
        Vector128.Create(0xFFFF, 0, 0xFFFF, 0, 0xFFFF, 0, 0xFFFF, 0).AsUInt16(),
        Vector128.Shuffle(a, Vector128.Create((ushort)4, 4, 5, 5, 6, 6, 7, 7)),
        Vector128.Shuffle(b, Vector128.Create((ushort)4, 4, 5, 5, 6, 6, 7, 7)));

    /// <summary>a0 b0 a1 b1, as punpckldq.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Low(Vector128<uint> a, Vector128<uint> b) =>
        Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipLow(a, b) : PortableLow(a, b);

    /// <summary><see cref="Low(Vector128{uint}, Vector128{uint})"/> in portable operations alone (what WebAssembly runs).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<uint> PortableLow(Vector128<uint> a, Vector128<uint> b) => Vector128.ConditionalSelect(
        Vector128.Create(uint.MaxValue, 0, uint.MaxValue, 0),
        Vector128.Shuffle(a, Vector128.Create(0u, 0, 1, 1)),
        Vector128.Shuffle(b, Vector128.Create(0u, 0, 1, 1)));

    /// <summary>a2 b2 a3 b3, as punpckhdq.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> High(Vector128<uint> a, Vector128<uint> b) =>
        Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipHigh(a, b) : PortableHigh(a, b);

    /// <summary><see cref="High(Vector128{uint}, Vector128{uint})"/> in portable operations alone (what WebAssembly runs).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<uint> PortableHigh(Vector128<uint> a, Vector128<uint> b) => Vector128.ConditionalSelect(
        Vector128.Create(uint.MaxValue, 0, uint.MaxValue, 0),
        Vector128.Shuffle(a, Vector128.Create(2u, 2, 3, 3)),
        Vector128.Shuffle(b, Vector128.Create(2u, 2, 3, 3)));

    /// <summary>
    /// <see cref="Vector128.Shuffle(Vector128{byte}, Vector128{byte})"/> for indices 0 to 15: SSSE3's pshufb where the
    /// CPU has it, checked at run time, which a Native AOT build for the default x64 target does not do for the
    /// portable shuffle (it calls a software one: 6% of 720p H.264's time there).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> Shuffle(Vector128<byte> vector, Vector128<byte> indices) =>
        Ssse3.IsSupported ? Ssse3.Shuffle(vector, indices) : Vector128.Shuffle(vector, indices);

    /// <summary>a0 b0, as punpcklqdq.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ulong> Low(Vector128<ulong> a, Vector128<ulong> b) => Vector128.Create(a.GetLower(), b.GetLower());

    /// <summary>a1 b1, as punpckhqdq.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ulong> High(Vector128<ulong> a, Vector128<ulong> b) => Vector128.Create(a.GetUpper(), b.GetUpper());
}
