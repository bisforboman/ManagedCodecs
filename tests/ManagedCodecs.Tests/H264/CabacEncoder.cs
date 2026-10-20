using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>MSB-first bit writer with the Exp-Golomb codes of H.264 clause 9.1, for building test streams.</summary>
internal sealed class BitWriter
{
    private readonly List<int> _bits = [];

    public int Count => _bits.Count;

    public bool IsByteAligned => _bits.Count % 8 == 0;

    public void Bit(int bit) => _bits.Add(bit & 1);

    public void Bits(long value, int count)
    {
        for (int i = count - 1; i >= 0; i--)
        {
            Bit((int)(value >> i));
        }
    }

    public void Ue(uint value)
    {
        int length = 32 - int.LeadingZeroCount((int)(value + 1));
        Bits(0, length - 1);
        Bits(value + 1, length);
    }

    public void Se(int value) => Ue(value > 0 ? (uint)((2 * value) - 1) : (uint)(-2 * value));

    public void AlignWith(int bit)
    {
        while (!IsByteAligned)
        {
            Bit(bit);
        }
    }

    /// <summary>rbsp_trailing_bits: the stop bit, then zeros to the byte boundary.</summary>
    public void Trailing()
    {
        Bit(1);
        AlignWith(0);
    }

    public byte[] ToBytes()
    {
        var data = new byte[(_bits.Count + 7) / 8];
        for (int i = 0; i < _bits.Count; i++)
        {
            data[i / 8] |= (byte)(_bits[i] << (7 - (i % 8)));
        }

        return data;
    }
}

/// <summary>The CABAC arithmetic encoder of H.264 clause 9.3.4, with the decoder's context initialization.</summary>
internal sealed class CabacEncoder
{
    private readonly byte[] _states = new byte[CabacTables.ContextCount];
    private readonly byte[] _mps = new byte[CabacTables.ContextCount];
    private readonly BitWriter _writer;
    private int _low;
    private int _range;
    private bool _firstBit;
    private int _outstanding;

    /// <param name="table">0 for I slices, else 1 + cabac_init_idc.</param>
    public CabacEncoder(int table, int qp, BitWriter? writer = null)
    {
        _writer = writer ?? new BitWriter();
        var values = CabacTables.InitValues;
        for (int ctx = 0; ctx < CabacTables.ContextCount; ctx++)
        {
            int pre = Math.Clamp(((values[(ctx * 8) + (table * 2)] * qp) >> 4) + values[(ctx * 8) + (table * 2) + 1], 1, 126);
            _states[ctx] = (byte)(pre <= 63 ? 63 - pre : pre - 64);
            _mps[ctx] = (byte)(pre <= 63 ? 0 : 1);
        }

        InitEngine();
    }

    public BitWriter Writer => _writer;

    /// <summary>9.3.4.1: (re)starts the engine; contexts keep their states.</summary>
    public void InitEngine()
    {
        _low = 0;
        _range = 510;
        _firstBit = true;
        _outstanding = 0;
    }

    // 9.3.4.2
    public void Decision(int ctx, int bin)
    {
        int state = _states[ctx];
        int lps = CabacTables.RangeTabLps[(state * 4) + ((_range >> 6) & 3)];
        _range -= lps;
        if (bin != _mps[ctx])
        {
            _low += _range;
            _range = lps;
            if (state == 0)
            {
                _mps[ctx] = (byte)(1 - _mps[ctx]);
            }

            _states[ctx] = CabacTables.TransIdxLps[state];
        }
        else
        {
            _states[ctx] = CabacTables.TransIdxMps[state];
        }

        Renormalize();
    }

    // 9.3.4.4
    public void Bypass(int bin)
    {
        _low <<= 1;
        if (bin != 0)
        {
            _low += _range;
        }

        if (_low >= 1024)
        {
            PutBit(1);
            _low -= 1024;
        }
        else if (_low < 512)
        {
            PutBit(0);
        }
        else
        {
            _low -= 512;
            _outstanding++;
        }
    }

    /// <summary>9.3.4.5; a bin of 1 flushes, and its last bit is the rbsp_stop_one_bit at the end of a slice.</summary>
    public void Terminate(int bin)
    {
        _range -= 2;
        if (bin != 0)
        {
            _low += _range;
            _range = 2;
            Renormalize();
            PutBit((_low >> 9) & 1);
            _writer.Bits(((_low >> 7) & 3) | 1, 2);
        }
        else
        {
            Renormalize();
        }
    }

    // 9.3.4.3
    private void Renormalize()
    {
        while (_range < 256)
        {
            if (_low < 256)
            {
                PutBit(0);
            }
            else if (_low >= 512)
            {
                _low -= 512;
                PutBit(1);
            }
            else
            {
                _low -= 256;
                _outstanding++;
            }

            _range <<= 1;
            _low <<= 1;
        }
    }

    private void PutBit(int bit)
    {
        if (_firstBit)
        {
            _firstBit = false;
        }
        else
        {
            _writer.Bit(bit);
        }

        for (; _outstanding > 0; _outstanding--)
        {
            _writer.Bit(1 - bit);
        }
    }
}
