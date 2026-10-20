using System.Runtime.CompilerServices;

namespace ManagedCodecs.Mjpeg;

/// <summary>
/// A canonical Huffman table (T.81 Annex C) with a lookup table for short codes and the
/// MAXCODE/VALPTR procedure of Figure F.16 for longer ones.
/// </summary>
/// <remarks>
/// A second table resolves a whole coefficient in one lookup: the Huffman code and the
/// magnitude bits after it (T.81 F.2.2.1), when both fit in <see cref="CoefficientBits"/>.
/// </remarks>
internal sealed class HuffmanTable
{
    public const int LookupBits = 9;
    public const int CoefficientBits = 10;

    // Entry: (code length << 8) | value, or 0 when the code is longer than LookupBits.
    private readonly ushort[] _lookup = new ushort[1 << LookupBits];
    private readonly int[] _maxCode = new int[18];
    private readonly int[] _valOffset = new int[17];
    private readonly byte[] _values;

    // Entry: (coefficient value << 16) | (run << 8) | bits consumed, or 0 for the slow path.
    // Only symbols with a size of 1..11, EOB (0x00) and ZRL (0xF0) get entries.
    private readonly int[] _coefficients = new int[1 << CoefficientBits];

    public HuffmanTable(ReadOnlySpan<byte> bits, ReadOnlySpan<byte> values)
    {
        if (bits.Length != 16)
        {
            throw new ArgumentException("BITS must have 16 entries.", nameof(bits));
        }

        int total = 0;
        foreach (byte count in bits)
        {
            total += count;
        }

        if (total > 256 || total > values.Length)
        {
            throw new InvalidDataException("Huffman table has more codes than values.");
        }

        _values = values[..total].ToArray();

        int code = 0;
        int k = 0;
        for (int length = 1; length <= 16; length++)
        {
            int count = bits[length - 1];
            _valOffset[length] = k - code;
            code += count;
            if (code > (1 << length))
            {
                throw new InvalidDataException("Huffman table is over-subscribed.");
            }

            _maxCode[length] = count == 0 ? -1 : code - 1;

            for (int i = 0; i < count; i++, k++)
            {
                int c = code - count + i;
                if (length <= LookupBits)
                {
                    int shift = LookupBits - length;
                    ushort entry = (ushort)((length << 8) | _values[k]);
                    _lookup.AsSpan(c << shift, 1 << shift).Fill(entry);
                }

                AddCoefficientEntries(c, length, _values[k]);
            }

            code <<= 1;
        }

        _maxCode[17] = int.MaxValue; // sentinel: no code is longer than 16 bits
    }

    private void AddCoefficientEntries(int code, int length, byte symbol)
    {
        int run = symbol >> 4;
        int size = symbol & 0x0F;
        if (length + size > CoefficientBits || size > 11 || (size == 0 && symbol is not 0x00 and not 0xF0))
        {
            return;
        }

        int free = CoefficientBits - length - size;
        int first = code << (CoefficientBits - length);
        for (int extra = 0; extra < 1 << size; extra++)
        {
            int value = size == 0 ? 0 : Extend(extra, size);
            int entry = (value << 16) | (run << 8) | (length + size);
            _coefficients.AsSpan(first | (extra << free), 1 << free).Fill(entry);
        }
    }

    /// <summary>T.81 F.2.2.1 EXTEND: maps <paramref name="size"/> magnitude bits to a signed value.</summary>
    public static int Extend(int bits, int size) => bits < (1 << (size - 1)) ? bits - (1 << size) + 1 : bits;

    public static HuffmanTable Parse(ReadOnlySpan<byte> payload, out int consumed)
    {
        if (payload.Length < 17)
        {
            throw new InvalidDataException("DHT segment too short.");
        }

        var bits = payload.Slice(1, 16);
        int total = 0;
        foreach (byte count in bits)
        {
            total += count;
        }

        if (payload.Length < 17 + total)
        {
            throw new InvalidDataException("DHT segment shorter than its code count.");
        }

        consumed = 17 + total;
        return new HuffmanTable(bits, payload.Slice(17, total));
    }

    /// <summary>
    /// Decodes one AC symbol and its magnitude. Returns <c>(value &lt;&lt; 8) | run</c>; a value
    /// of 0 is EOB, or ZRL when the run is 15.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DecodeAc(ref JpegBitReader reader)
    {
        int entry = _coefficients[reader.Peek(CoefficientBits)];
        if (entry != 0)
        {
            reader.Skip(entry & 0xFF);
            return entry >> 8;
        }

        int symbol = Decode(ref reader);
        int size = symbol & 0x0F;
        return (reader.ReceiveExtend(size) << 8) | (symbol >> 4);
    }

    /// <summary>Decodes one DC difference (T.81 F.2.2.1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DecodeDc(ref JpegBitReader reader)
    {
        int entry = _coefficients[reader.Peek(CoefficientBits)];
        if (entry != 0 && (entry & 0xFF00) == 0)
        {
            reader.Skip(entry & 0xFF);
            return entry >> 16;
        }

        int size = Decode(ref reader);
        if (size > 11)
        {
            throw new InvalidDataException("Invalid DC coefficient size.");
        }

        return reader.ReceiveExtend(size);
    }

    /// <summary>Decodes one symbol.</summary>
    public int Decode(ref JpegBitReader reader)
    {
        int peek = reader.Peek(16);
        ushort entry = _lookup[peek >> (16 - LookupBits)];
        if (entry != 0)
        {
            reader.Skip(entry >> 8);
            return entry & 0xFF;
        }

        for (int length = LookupBits + 1; length <= 16; length++)
        {
            int code = peek >> (16 - length);
            if (code <= _maxCode[length])
            {
                reader.Skip(length);
                return _values[code + _valOffset[length]];
            }
        }

        throw new InvalidDataException("Invalid Huffman code.");
    }
}
