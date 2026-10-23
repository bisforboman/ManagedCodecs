using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace ManagedCodecs;

/// <summary>
/// SSE2's unpack (interleave) instructions in cross-platform <see cref="Vector128"/> operations, for the transposes
/// of the paths x64 runs with SSE2 and other machines (ARM64) with these: each lane comes from a constant shuffle of
/// one input, the inputs alternating lane by lane.
/// </summary>
internal static class VectorZip
{
    /// <summary>a0 b0 a1 b1 .. a7 b7, as punpcklbw.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> Low(Vector128<byte> a, Vector128<byte> b) => Vector128.ConditionalSelect(
        Vector128.Create(0xFF, 0, 0xFF, 0, 0xFF, 0, 0xFF, 0, 0xFF, 0, 0xFF, 0, 0xFF, 0, 0xFF, 0).AsByte(),
        Vector128.Shuffle(a, Vector128.Create((byte)0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7)),
        Vector128.Shuffle(b, Vector128.Create((byte)0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7)));

    /// <summary>a8 b8 a9 b9 .. a15 b15, as punpckhbw.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> High(Vector128<byte> a, Vector128<byte> b) => Vector128.ConditionalSelect(
        Vector128.Create(0xFF, 0, 0xFF, 0, 0xFF, 0, 0xFF, 0, 0xFF, 0, 0xFF, 0, 0xFF, 0, 0xFF, 0).AsByte(),
        Vector128.Shuffle(a, Vector128.Create((byte)8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15)),
        Vector128.Shuffle(b, Vector128.Create((byte)8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15)));

    /// <summary>a0 b0 a1 b1 a2 b2 a3 b3, as punpcklwd.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ushort> Low(Vector128<ushort> a, Vector128<ushort> b) => Vector128.ConditionalSelect(
        Vector128.Create(0xFFFF, 0, 0xFFFF, 0, 0xFFFF, 0, 0xFFFF, 0).AsUInt16(),
        Vector128.Shuffle(a, Vector128.Create((ushort)0, 0, 1, 1, 2, 2, 3, 3)),
        Vector128.Shuffle(b, Vector128.Create((ushort)0, 0, 1, 1, 2, 2, 3, 3)));

    /// <summary>a4 b4 a5 b5 a6 b6 a7 b7, as punpckhwd.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ushort> High(Vector128<ushort> a, Vector128<ushort> b) => Vector128.ConditionalSelect(
        Vector128.Create(0xFFFF, 0, 0xFFFF, 0, 0xFFFF, 0, 0xFFFF, 0).AsUInt16(),
        Vector128.Shuffle(a, Vector128.Create((ushort)4, 4, 5, 5, 6, 6, 7, 7)),
        Vector128.Shuffle(b, Vector128.Create((ushort)4, 4, 5, 5, 6, 6, 7, 7)));

    /// <summary>a0 b0 a1 b1, as punpckldq.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Low(Vector128<uint> a, Vector128<uint> b) => Vector128.ConditionalSelect(
        Vector128.Create(uint.MaxValue, 0, uint.MaxValue, 0),
        Vector128.Shuffle(a, Vector128.Create(0u, 0, 1, 1)),
        Vector128.Shuffle(b, Vector128.Create(0u, 0, 1, 1)));

    /// <summary>a2 b2 a3 b3, as punpckhdq.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> High(Vector128<uint> a, Vector128<uint> b) => Vector128.ConditionalSelect(
        Vector128.Create(uint.MaxValue, 0, uint.MaxValue, 0),
        Vector128.Shuffle(a, Vector128.Create(2u, 2, 3, 3)),
        Vector128.Shuffle(b, Vector128.Create(2u, 2, 3, 3)));

    /// <summary>a0 b0, as punpcklqdq.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ulong> Low(Vector128<ulong> a, Vector128<ulong> b) => Vector128.Create(a.GetLower(), b.GetLower());

    /// <summary>a1 b1, as punpckhqdq.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ulong> High(Vector128<ulong> a, Vector128<ulong> b) => Vector128.Create(a.GetUpper(), b.GetUpper());
}
