using System.Numerics;

namespace ManagedCodecs.H264;

/// <summary>
/// The CABAC arithmetic decoding engine (H.264 clause 9.3.1.2 and 9.3.3.2) and the context
/// variables of one slice, reading from a <see cref="BitReader"/>.
/// </summary>
/// <remarks>
/// The engine reads exactly the bits the specification's decoder reads: 9 at initialization and
/// one per renormalization step. So after a terminate bin of 1 (end of slice, I_PCM) the reader
/// stands right after the last bit the encoder flushed, and I_PCM samples can be read from it.
/// </remarks>
internal sealed class Cabac
{
    // Per context: pStateIdx in bits 0-5, valMPS in bit 6.
    private readonly byte[] _contexts = new byte[CabacTables.ContextCount];
    private BitReader _reader = null!;
    private int _range;
    private int _offset;

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
        _offset = (int)reader.ReadBits(9);
        if (_offset >= 510)
        {
            throw new InvalidDataException("CABAC offset out of range.");
        }
    }

    /// <summary>DecodeDecision (9.3.3.2.1) with context variable <paramref name="ctxIdx"/>.</summary>
    public int Decision(int ctxIdx)
    {
        int context = _contexts[ctxIdx];
        int state = context & 63;
        int mps = context >> 6;
        int lps = CabacTables.RangeTabLps[(state << 2) + ((_range >> 6) & 3)];
        _range -= lps;

        int bin;
        if (_offset < _range)
        {
            bin = mps;
            _contexts[ctxIdx] = (byte)((mps << 6) | CabacTables.TransIdxMps[state]);
        }
        else
        {
            _offset -= _range;
            _range = lps;
            bin = 1 - mps;
            if (state == 0)
            {
                mps = 1 - mps;
            }

            _contexts[ctxIdx] = (byte)((mps << 6) | CabacTables.TransIdxLps[state]);
        }

        Renormalize();
        return bin;
    }

    /// <summary>DecodeBypass (9.3.3.2.3).</summary>
    public int Bypass()
    {
        _offset = (_offset << 1) | (int)_reader.ReadBits(1);
        if (_offset >= _range)
        {
            _offset -= _range;
            return 1;
        }

        return 0;
    }

    /// <summary>DecodeTerminate (9.3.3.2.2): end_of_slice_flag and the I_PCM bin of mb_type.</summary>
    public int Terminate()
    {
        _range -= 2;
        if (_offset >= _range)
        {
            return 1; // no renormalization: decoding stops or restarts here
        }

        Renormalize();
        return 0;
    }

    /// <summary>RenormD (9.3.3.2.2): doubles the range until it is at least 256, one bit per doubling.</summary>
    private void Renormalize()
    {
        if (_range < 256)
        {
            int shift = BitOperations.LeadingZeroCount((uint)_range) - 23;
            _range <<= shift;
            _offset = (_offset << shift) | (int)_reader.ReadBits(shift);
        }
    }
}
