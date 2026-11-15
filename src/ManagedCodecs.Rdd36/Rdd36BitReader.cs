using System.Buffers.Binary;
using System.Numerics;

namespace ManagedCodecs.Rdd36;

/// <summary>
/// Reads the variable-length codes of one colour component's scanned coefficients (RDD 36, 5.3.2 and 7.1.1): bits
/// most significant first, Golomb-Rice/exponential-Golomb combination codes, and the end of the data as the syntax
/// function endOfData() defines it.
/// </summary>
internal ref struct Rdd36BitReader
{
    // The longest suffix a codeword may have. A coefficient is 16 bits after inverse quantization, so a symbol of
    // more bits than this comes from damaged data, not an encoder.
    private const int MaxSuffixBits = 30;

    private readonly ReadOnlySpan<byte> _data;
    private readonly long _lengthInBits;
    private long _position;

    public Rdd36BitReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _lengthInBits = (long)data.Length * 8;
        _position = 0;
    }

    /// <summary>
    /// endOfData(): true when 31 bits or fewer remain and all of them are 0 (5: "Bitstream Syntax"), which is how the
    /// coefficients of a component end.
    /// </summary>
    public readonly bool EndOfData => _lengthInBits - _position <= 31 && (uint)(Peek64() >> 32) == 0;

    /// <summary>
    /// Decodes one codeword of <paramref name="codebook"/> (7.1.1.1): the code level q is the count of zero bits before
    /// the first one bit; up to lastRiceQ the symbol is q * 2^kRice plus a kRice-bit remainder, beyond it an
    /// exponential-Golomb codeword of order kExp follows the first lastRiceQ + 1 zero bits, and
    /// (lastRiceQ + 1) * 2^kRice is added to its symbol.
    /// </summary>
    /// <exception cref="InvalidDataException">The codeword runs past the data's end, or is longer than any coefficient's.</exception>
    public int ReadCode(Rdd36Codebook codebook)
    {
        ulong window = Peek64();
        int level = BitOperations.LeadingZeroCount(window);
        if (level <= codebook.LastRiceQ)
        {
            int rice = codebook.RiceOrder;
            int remainder = rice == 0 ? 0 : (int)((window << (level + 1)) >> (64 - rice));
            Skip(level + 1 + rice);
            return (level << rice) | remainder;
        }

        // The exponential-Golomb codeword: its own level, the one bit, and level + kExp suffix bits; as a number, the
        // one bit and the suffix are the symbol plus 2^kExp.
        int expLevel = level - (codebook.LastRiceQ + 1);
        int suffixBits = expLevel + codebook.ExpOrder;
        if (suffixBits > MaxSuffixBits)
        {
            throw new InvalidDataException("A coefficient's codeword is longer than any coefficient's.");
        }

        Skip(level);
        int value = (int)(Peek64() >> (64 - (suffixBits + 1)));
        Skip(suffixBits + 1);
        return value - (1 << codebook.ExpOrder) + ((codebook.LastRiceQ + 1) << codebook.RiceOrder);
    }

    /// <summary>Reads one bit.</summary>
    /// <exception cref="InvalidDataException">No bit is left.</exception>
    public int ReadBit()
    {
        int bit = (int)(Peek64() >> 63);
        Skip(1);
        return bit;
    }

    /// <summary>
    /// The inverse of the signed integer-to-symbol mapping S(n) (7.1.1.2): even symbols are the non-negative integers,
    /// odd ones the negative.
    /// </summary>
    public static int ToSigned(int symbol) => (symbol & 1) == 0 ? symbol >> 1 : -((symbol + 1) >> 1);

    // The next 64 bits, most significant first, zeros after the data's end.
    private readonly ulong Peek64()
    {
        int at = (int)(_position >> 3);
        int shift = (int)(_position & 7);
        ulong high;
        uint next;
        if (at + 9 <= _data.Length)
        {
            high = BinaryPrimitives.ReadUInt64BigEndian(_data.Slice(at));
            next = _data[at + 8];
        }
        else
        {
            high = 0;
            for (int i = 0; i < 8; i++)
            {
                high = (high << 8) | (at + i < _data.Length ? _data[at + i] : 0u);
            }

            next = 0;
        }

        return shift == 0 ? high : (high << shift) | (next >> (8 - shift));
    }

    private void Skip(int bits)
    {
        _position += bits;
        if (_position > _lengthInBits)
        {
            throw new InvalidDataException("A coefficient's codeword runs past the end of its component's data.");
        }
    }
}
