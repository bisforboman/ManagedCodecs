using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ManagedCodecs.H264;

/// <summary>
/// The CABAC arithmetic decoding engine (H.264 clause 9.3.1.2 and 9.3.3.2) and the context
/// variables of one slice, reading from a <see cref="BitReader"/>.
/// </summary>
/// <remarks>
/// The specification's decoder reads 9 bits at initialization and one per renormalization step.
/// This one reads ahead 32 bits at a time: <c>_value</c> holds codIOffset followed by the
/// <c>_bits</c> bits read beyond it, so renormalizing is only a matter of counting. After a
/// terminate bin of 1 (end of slice, I_PCM) the reader is moved back by those bits, so it stands
/// right after the last bit the encoder flushed, as with the specification's decoder, and I_PCM
/// samples can be read from it.
/// </remarks>
internal sealed class Cabac
{
    // Per context: pStateIdx in bits 0-5, valMPS in bit 6.
    private readonly byte[] _contexts = new byte[CabacTables.ContextCount];

    private BitReader _reader = null!;
    private int _range;

    // codIOffset << _bits, plus the _bits bits after it (at most 7 + 32, so under 2^48).
    private ulong _value;
    private int _bits;

    /// <summary>
    /// Whether the engine has consumed bits past the end of the slice data (the bits it read ahead do not count): a
    /// conforming slice ends inside its data (9.3.4.5 writes the stop bit last), so its data was cut short or damaged.
    /// </summary>
    public bool PastEnd => _reader.Position - _bits > _reader.BitLength;

    /// <summary>Initializes every context variable (9.3.1.1) for a slice.</summary>
    /// <param name="table">0 for I slices, else 1 + cabac_init_idc.</param>
    /// <param name="sliceQp">SliceQP_Y.</param>
    public void InitContexts(int table, int sliceQp)
    {
        var values = CabacTables.InitValues;
        int qp = Math.Clamp(sliceQp, 0, 51);
        for (int ctxIdx = 0; ctxIdx < CabacTables.ContextCount; ctxIdx++)
        {
            int m = values[(ctxIdx * 8) + (table * 2)];
            int n = values[(ctxIdx * 8) + (table * 2) + 1];
            int preCtxState = Math.Clamp(((m * qp) >> 4) + n, 1, 126);
            _contexts[ctxIdx] = (byte)(preCtxState <= 63 ? 63 - preCtxState : (preCtxState - 64) | 64);
        }
    }

    /// <summary>Starts (or, after I_PCM samples, restarts) the decoding engine at the reader's position (9.3.1.2).</summary>
    public void InitEngine(BitReader reader)
    {
        _reader = reader;
        _range = 510;
        _value = reader.ReadBits(9);
        _bits = 0;
        if (_value >= 510)
        {
            throw new InvalidDataException("CABAC offset out of range.");
        }

        _value = Refill(_value);
        _bits += 32;
    }

    /// <summary>DecodeDecision (9.3.3.2.1) with context variable <paramref name="ctxIdx"/>.</summary>
    public int Decision(int ctxIdx)
    {
        int range = _range;
        ulong value = _value;
        int bits = _bits;
        int bin = Decide(ref _contexts[ctxIdx], ref range, ref value, ref bits);
        _range = range;
        _value = value;
        _bits = bits;
        return bin;
    }

    /// <summary>DecodeBypass (9.3.3.2.3).</summary>
    public int Bypass()
    {
        ulong value = _value;
        int bits = _bits;
        int bin = Bypass(_range, ref value, ref bits);
        _value = value;
        _bits = bits;
        return bin;
    }

    /// <summary>
    /// The significance map and levels of residual_block_cabac (7.3.5.3.3), with the engine's
    /// state in locals throughout. The nonzero levels go to <paramref name="block"/>[<paramref name="scan"/>[position]]
    /// for scan positions 0..maxNumCoeff-1; the caller clears the block first. Returns the number
    /// of nonzero coefficients.
    /// </summary>
    /// <param name="codedBlockFlagCtx">ctxIdx of coded_block_flag, or -1 when it is inferred to be 1.</param>
    /// <param name="significantInc">ctxIdxInc of significant_coeff_flag by scan position.</param>
    /// <param name="lastInc">ctxIdxInc of last_significant_coeff_flag by scan position.</param>
    /// <param name="maxGt1Inc">The largest ctxIdxInc of the coeff_abs_level_minus1 bins after the first.</param>
    [SkipLocalsInit]
    public int ResidualBlock(int codedBlockFlagCtx, int significantBase, int lastBase, ReadOnlySpan<byte> significantInc, ReadOnlySpan<byte> lastInc, int absBase, int maxGt1Inc, int maxNumCoeff, Span<int> block, ReadOnlySpan<byte> scan)
    {
        ref byte contexts = ref MemoryMarshal.GetArrayDataReference(_contexts);
        int range = _range;
        ulong value = _value;
        int bits = _bits;

        if (codedBlockFlagCtx >= 0 && Decide(ref Unsafe.Add(ref contexts, codedBlockFlagCtx), ref range, ref value, ref bits) == 0)
        {
            _range = range;
            _value = value;
            _bits = bits;
            return 0;
        }

        // Significance map: positions of the nonzero coefficients, in scan order. The increments are checked against
        // maxNumCoeff once, here, and read without checks in the loop (as are the contexts, whose bases come from the
        // tables).
        if ((uint)maxNumCoeff > 64 || significantInc.Length < maxNumCoeff - 1 || lastInc.Length < maxNumCoeff - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxNumCoeff));
        }

        ref byte significant = ref Unsafe.Add(ref contexts, significantBase);
        ref byte last = ref Unsafe.Add(ref contexts, lastBase);
        ref byte significantIncs = ref MemoryMarshal.GetReference(significantInc);
        ref byte lastIncs = ref MemoryMarshal.GetReference(lastInc);
        Span<byte> positions = stackalloc byte[64];
        ref byte position = ref MemoryMarshal.GetReference(positions);
        int count = 0;
        int i = 0;
        for (; i < maxNumCoeff - 1; i++)
        {
            if (Decide(ref Unsafe.Add(ref significant, Unsafe.Add(ref significantIncs, i)), ref range, ref value, ref bits) == 1)
            {
                Unsafe.Add(ref position, count++) = (byte)i;
                if (Decide(ref Unsafe.Add(ref last, Unsafe.Add(ref lastIncs, i)), ref range, ref value, ref bits) == 1)
                {
                    break;
                }
            }
        }

        if (i == maxNumCoeff - 1)
        {
            Unsafe.Add(ref position, count++) = (byte)i; // no last flag came: the last coefficient is nonzero
        }

        // Levels, from the last coefficient back: coeff_abs_level_minus1 as UEG0 with uCoff 14,
        // then the sign.
        int equal1 = 0;
        int greater1 = 0;
        for (int k = count - 1; k >= 0; k--)
        {
            int absMinus1 = 0;
            if (Decide(ref Unsafe.Add(ref contexts, absBase + (greater1 != 0 ? 0 : Math.Min(4, 1 + equal1))), ref range, ref value, ref bits) == 1)
            {
                ref byte context = ref Unsafe.Add(ref contexts, absBase + 5 + Math.Min(maxGt1Inc, greater1));
                absMinus1 = 1;
                while (absMinus1 < 14 && Decide(ref context, ref range, ref value, ref bits) == 1)
                {
                    absMinus1++;
                }

                if (absMinus1 == 14)
                {
                    // Rare (levels above 14): through the fields, so that no method out of line takes the locals by
                    // reference, which would keep them in memory throughout.
                    _value = value;
                    _bits = bits;
                    absMinus1 += ExpGolombBypass(range);
                    value = _value;
                    bits = _bits;
                }

                greater1++;
            }
            else
            {
                equal1++;
            }

            int level = absMinus1 + 1;
            block[scan[positions[k]]] = Bypass(range, ref value, ref bits) == 1 ? -level : level;
        }

        _range = range;
        _value = value;
        _bits = bits;
        return count;
    }

    /// <summary>DecodeTerminate (9.3.3.2.2): end_of_slice_flag and the I_PCM bin of mb_type.</summary>
    public int Terminate()
    {
        _range -= 2;
        if (_value >= (ulong)_range << _bits)
        {
            // No renormalization: decoding stops or restarts here, where the specification's
            // decoder stands, so the bits read ahead go back.
            _reader.Skip(-_bits);
            _bits = 0;
            return 1;
        }

        // RenormD: the range is at least 254 here, so at most one doubling.
        if (_range < 256)
        {
            _range <<= 1;
            if (--_bits < 0)
            {
                _value = Refill(_value);
                _bits += 32;
            }
        }

        return 0;
    }

    /// <summary>
    /// DecodeDecision on engine state held by the caller: the decision, the context's transition,
    /// and RenormD (9.3.3.2.2), which doubles the range until it is at least 256, taking one more
    /// bit of the read-ahead into codIOffset per doubling.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Decide(ref byte context, ref int range, ref ulong value, ref int bits)
    {
        int ctx = context;
        int state = ctx & 63;
#if NET
        int lps = Unsafe.Add(ref MemoryMarshal.GetReference(CabacTables.RangeTabLps), (state << 2) + ((range >> 6) & 3)); // state < 64, so under 256
#else
        int lps = CabacTables.RangeTabLps[(state << 2) + ((range >> 6) & 3)]; // the Unity build's arrays
#endif
        range -= lps;
        ulong scaledRange = (ulong)range << bits;
        int bin = ctx >> 6;
        if (value < scaledRange)
        {
#if NET
            context = Unsafe.Add(ref MemoryMarshal.GetReference(MpsNext), ctx); // ctx < 128
#else
            context = MpsNext[ctx];
#endif
            if (range >= 256)
            {
                return bin;
            }
        }
        else
        {
            value -= scaledRange;
            range = lps;
            bin ^= 1;
#if NET
            context = Unsafe.Add(ref MemoryMarshal.GetReference(LpsNext), ctx);
#else
            context = LpsNext[ctx];
#endif
        }

        int shift = BitOperations.LeadingZeroCount((uint)range) - 23;
        range <<= shift;
        bits -= shift;
        if (bits < 0)
        {
            value = Refill(value);
            bits += 32;
        }

        return bin;
    }

    /// <summary>DecodeBypass (9.3.3.2.3) on engine state held by the caller.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Bypass(int range, ref ulong value, ref int bits)
    {
        if (--bits < 0)
        {
            value = Refill(value);
            bits += 32;
        }

        ulong scaledRange = (ulong)range << bits;
        if (value >= scaledRange)
        {
            value -= scaledRange;
            return 1;
        }

        return 0;
    }

    /// <summary>The k-th order Exp-Golomb suffix of a UEGk binarization (9.3.2.3), with k 0: all bypass bins.</summary>
    private int ExpGolombBypass(int range)
    {
        ulong value = _value;
        int bits = _bits;
        int k = 0;
        int result = 0;
        while (Bypass(range, ref value, ref bits) == 1)
        {
            result += 1 << k;
            k++;
            if (k > 24)
            {
                throw new InvalidDataException("Exp-Golomb suffix too long.");
            }
        }

        while (k-- > 0)
        {
            result += Bypass(range, ref value, ref bits) << k;
        }

        _value = value;
        _bits = bits;
        return result;
    }

    /// <summary>
    /// <paramref name="value"/> with 32 more bits taken into the read-ahead (the caller adds 32 to its bit count): at
    /// initialization, and whenever the count went negative because codIOffset needs bits the read-ahead did not hold
    /// yet.
    /// </summary>
    /// <remarks>
    /// Out of line, and so taking and returning the value rather than references to the caller's locals: a reference
    /// passed to a method that is not inlined keeps the local in memory, loaded and stored at every decision.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private ulong Refill(ulong value) => (value << 32) | _reader.ReadBits(32);

    /// <summary>The context after an MPS, by context: transIdxMPS of its state (Table 9-45), valMPS kept (9.3.3.2.1.1).</summary>
    /// <remarks>
    /// Constant data, as <see cref="CabacTables.RangeTabLps"/> is, and not a static array built from the tables: a method
    /// compiled before the class's static constructor ran (mcdec compiles each method once, at its first call) checks
    /// that the constructor ran and loads the array at every use. The tests derive it from Table 9-45.
    /// </remarks>
#if NET
    internal static ReadOnlySpan<byte> MpsNext =>
#else
    // An array in the Unity build: Mono calls a ReadOnlySpan property's getter at every use, where CoreCLR reads the
    // constant data in place (docs/decisions/2026-10-31-unity-netstandard.md, Mono's profile).
    internal static readonly byte[] MpsNext =
#endif
    [
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
        17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
        33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48,
        49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 62, 63,
        65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79, 80,
        81, 82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96,
        97, 98, 99, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112,
        113, 114, 115, 116, 117, 118, 119, 120, 121, 122, 123, 124, 125, 126, 126, 127,
    ];

    /// <summary>
    /// The context after an LPS, by context: transIdxLPS of its state (Table 9-45), valMPS switched at state 0
    /// (9.3.3.2.1.1). Constant data, as <see cref="MpsNext"/>.
    /// </summary>
#if NET
    internal static ReadOnlySpan<byte> LpsNext =>
#else
    // An array in the Unity build: Mono calls a ReadOnlySpan property's getter at every use, where CoreCLR reads the
    // constant data in place (docs/decisions/2026-10-31-unity-netstandard.md, Mono's profile).
    internal static readonly byte[] LpsNext =
#endif
    [
        64, 0, 1, 2, 2, 4, 4, 5, 6, 7, 8, 9, 9, 11, 11, 12,
        13, 13, 15, 15, 16, 16, 18, 18, 19, 19, 21, 21, 22, 22, 23, 24,
        24, 25, 26, 26, 27, 27, 28, 29, 29, 30, 30, 30, 31, 32, 32, 33,
        33, 33, 34, 34, 35, 35, 35, 36, 36, 36, 37, 37, 37, 38, 38, 63,
        0, 64, 65, 66, 66, 68, 68, 69, 70, 71, 72, 73, 73, 75, 75, 76,
        77, 77, 79, 79, 80, 80, 82, 82, 83, 83, 85, 85, 86, 86, 87, 88,
        88, 89, 90, 90, 91, 91, 92, 93, 93, 94, 94, 94, 95, 96, 96, 97,
        97, 97, 98, 98, 99, 99, 99, 100, 100, 100, 101, 101, 101, 102, 102, 127,
    ];
}
