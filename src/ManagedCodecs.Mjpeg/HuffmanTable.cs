namespace ManagedCodecs.Mjpeg;

/// <summary>
/// A canonical Huffman table (T.81 Annex C) with a lookup table for short codes and the
/// MAXCODE/VALPTR procedure of Figure F.16 for longer ones.
/// </summary>
internal sealed class HuffmanTable
{
    public const int LookupBits = 9;

    // Entry: (code length << 8) | value, or 0 when the code is longer than LookupBits.
    private readonly ushort[] _lookup = new ushort[1 << LookupBits];
    private readonly int[] _maxCode = new int[18];
    private readonly int[] _valOffset = new int[17];
    private readonly byte[] _values;

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
                if (length <= LookupBits)
                {
                    int c = code - count + i;
                    int shift = LookupBits - length;
                    ushort entry = (ushort)((length << 8) | _values[k]);
                    _lookup.AsSpan(c << shift, 1 << shift).Fill(entry);
                }
            }

            code <<= 1;
        }

        _maxCode[17] = int.MaxValue; // sentinel: no code is longer than 16 bits
    }

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
