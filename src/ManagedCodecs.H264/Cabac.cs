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

    // The context after an MPS and after an LPS, by context (transIdxMps, transIdxLps and the
    // valMPS switch at state 0, 9.3.3.2.1.1).
    private static readonly byte[] MpsNext = [.. Enumerable.Range(0, 128).Select(c => (byte)((c & 64) | CabacTables.TransIdxMps[c & 63]))];
    private static readonly byte[] LpsNext = [.. Enumerable.Range(0, 128).Select(c => (byte)(((c & 63) == 0 ? (c & 64) ^ 64 : c & 64) | CabacTables.TransIdxLps[c & 63]))];
    private BitReader _reader = null!;
    private int _range;

    // codIOffset << _bits, plus the _bits bits after it (at most 7 + 32, so under 2^48).
    private ulong _value;
    private int _bits;

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

        Refill();
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
    /// The significance map and levels of residual_block_cabac (7.3.5.3.3) into
    /// <paramref name="levels"/> by scan position (all 16, or 64 for 8x8 blocks, cleared first),
    /// with the engine's state in locals throughout. Returns the number of nonzero coefficients.
    /// </summary>
    /// <param name="codedBlockFlagCtx">ctxIdx of coded_block_flag, or -1 when it is inferred to be 1.</param>
    /// <param name="significantInc">ctxIdxInc of significant_coeff_flag by scan position.</param>
    /// <param name="lastInc">ctxIdxInc of last_significant_coeff_flag by scan position.</param>
    /// <param name="maxGt1Inc">The largest ctxIdxInc of the coeff_abs_level_minus1 bins after the first.</param>
    [SkipLocalsInit]
    public int ResidualBlock(int codedBlockFlagCtx, int significantBase, int lastBase, ReadOnlySpan<byte> significantInc, ReadOnlySpan<byte> lastInc, int absBase, int maxGt1Inc, int maxNumCoeff, Span<int> levels)
    {
        levels[..(maxNumCoeff > 16 ? 64 : 16)].Clear();
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

        // Significance map: positions of the nonzero coefficients, in scan order.
        Span<byte> positions = stackalloc byte[64];
        int count = 0;
        int i = 0;
        for (; i < maxNumCoeff - 1; i++)
        {
            if (Decide(ref Unsafe.Add(ref contexts, significantBase + significantInc[i]), ref range, ref value, ref bits) == 1)
            {
                positions[count++] = (byte)i;
                if (Decide(ref Unsafe.Add(ref contexts, lastBase + lastInc[i]), ref range, ref value, ref bits) == 1)
                {
                    break;
                }
            }
        }

        if (i == maxNumCoeff - 1)
        {
            positions[count++] = (byte)i; // no last flag came: the last coefficient is nonzero
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
                    absMinus1 += ExpGolombBypass(range, ref value, ref bits);
                }

                greater1++;
            }
            else
            {
                equal1++;
            }

            int level = absMinus1 + 1;
            levels[positions[k]] = Bypass(range, ref value, ref bits) == 1 ? -level : level;
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

        Renormalize();
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
        int lps = CabacTables.RangeTabLps[(state << 2) + ((range >> 6) & 3)];
        range -= lps;
        ulong scaledRange = (ulong)range << bits;
        int bin = ctx >> 6;
        if (value < scaledRange)
        {
            context = MpsNext[ctx];
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
            context = LpsNext[ctx];
        }

        int shift = BitOperations.LeadingZeroCount((uint)range) - 23;
        range <<= shift;
        bits -= shift;
        if (bits < 0)
        {
            Refill(ref value, ref bits);
        }

        return bin;
    }

    /// <summary>DecodeBypass (9.3.3.2.3) on engine state held by the caller.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Bypass(int range, ref ulong value, ref int bits)
    {
        if (--bits < 0)
        {
            Refill(ref value, ref bits);
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
    private int ExpGolombBypass(int range, ref ulong value, ref int bits)
    {
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

        return result;
    }

    /// <summary>
    /// Takes 32 more bits into the read-ahead, after <paramref name="bits"/> went negative: codIOffset
    /// then needs bits the read-ahead did not hold yet.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Refill(ref ulong value, ref int bits)
    {
        value = (value << 32) | _reader.ReadBits(32);
        bits += 32;
    }

    /// <summary>
    /// RenormD (9.3.3.2.2): doubles the range until it is at least 256, taking one more bit of the
    /// read-ahead into codIOffset per doubling.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Renormalize()
    {
        if (_range < 256)
        {
            int shift = BitOperations.LeadingZeroCount((uint)_range) - 23;
            _range <<= shift;
            _bits -= shift;
            if (_bits < 0)
            {
                _bits += shift;
                Refill();
                _bits -= shift;
            }
        }
    }

    /// <summary>Reads 32 more bits ahead.</summary>
    private void Refill()
    {
        _value = (_value << 32) | _reader.ReadBits(32);
        _bits += 32;
    }
}
