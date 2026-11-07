using System.Runtime.CompilerServices;

namespace ManagedCodecs.Mjpeg;

/// <summary>
/// A canonical Huffman table (T.81 Annex C) with a lookup table for short codes and the
/// MAXCODE/VALPTR procedure of Figure F.16 for longer ones.
/// </summary>
/// <remarks>
/// A second table resolves a whole coefficient in one lookup: the Huffman code and the
/// magnitude bits after it (T.81 F.2.2.1), when both fit in <see cref="CoefficientBits"/>; where only the code fits,
/// it gives the symbol and the code's length, so a miss there needs no second lookup. Only codes longer than
/// <see cref="CoefficientBits"/> take the MAXCODE procedure.
/// </remarks>
internal sealed class HuffmanTable
{
    public const int CoefficientBits = 10;

    /// <summary>Marks a <see cref="_coefficients"/> entry that gives only the symbol and its code's length.</summary>
    private const int CodeOnly = 0x80;

    private readonly int[] _maxCode = new int[18];
    private readonly int[] _valOffset = new int[17];
    private readonly byte[] _values;

    // Entry: (coefficient value << 16) | (run << 8) | bits consumed (1..10), for symbols with a size of 1..11, EOB
    // (0x00) and ZRL (0xF0) whose magnitude bits fit too; else (code length << 16) | (symbol << 8) | CodeOnly, for any
    // other code of up to CoefficientBits bits; else 0, a longer code (or none).
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
        if (length > CoefficientBits)
        {
            return;
        }

        if (length + size > CoefficientBits || size > 11 || (size == 0 && symbol is not 0x00 and not 0xF0))
        {
            int shift = CoefficientBits - length;
            _coefficients.AsSpan(code << shift, 1 << shift).Fill((length << 16) | (symbol << 8) | CodeOnly);
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
    /// Decodes one AC symbol and its magnitude from <paramref name="buffer"/> (the next bits, left-aligned) and
    /// <paramref name="count"/> (how many are valid): the reader's state, held in the caller's locals
    /// (<see cref="JpegBitReader.Fill(ulong, int)"/>). Returns <c>(value &lt;&lt; 8) | run</c>; a value of 0 is EOB, or
    /// ZRL when the run is 15.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DecodeAc(ref JpegBitReader reader, ref ulong buffer, ref int count)
    {
        if (count < 16)
        {
            buffer = reader.Fill(buffer, count);
            count = JpegBitReader.FilledCount(count);
        }

        int entry = _coefficients[(int)(buffer >> (64 - CoefficientBits))];
        int consumed = entry & 0xFF;
        if ((uint)(consumed - 1) < CoefficientBits)
        {
            buffer <<= consumed;
            count -= consumed;
            return entry >> 8;
        }

        int code = consumed != 0 ? entry >> 8 : DecodeLong(buffer);
        int length = code >> 8;
        int symbol = code & 0xFF;
        buffer <<= length;
        count -= length;
        return (ReceiveExtend(ref reader, ref buffer, ref count, symbol & 0x0F) << 8) | (symbol >> 4);
    }

    /// <summary>Decodes one DC difference (T.81 F.2.2.1), the reader's state in the caller's locals.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DecodeDc(ref JpegBitReader reader, ref ulong buffer, ref int count)
    {
        if (count < 16)
        {
            buffer = reader.Fill(buffer, count);
            count = JpegBitReader.FilledCount(count);
        }

        int entry = _coefficients[(int)(buffer >> (64 - CoefficientBits))];
        int consumed = entry & 0xFF;
        if ((uint)(consumed - 1) < CoefficientBits && (entry & 0xFF00) == 0)
        {
            buffer <<= consumed;
            count -= consumed;
            return entry >> 16;
        }

        // A resolved entry with a run is a symbol above 11.
        int code = (consumed & CodeOnly) != 0 ? entry >> 8 : consumed == 0 ? DecodeLong(buffer) : 0xFF;
        int size = code & 0xFF;
        if (size > 11)
        {
            throw new InvalidDataException("Invalid DC coefficient size.");
        }

        int length = code >> 8;
        buffer <<= length;
        count -= length;
        return ReceiveExtend(ref reader, ref buffer, ref count, size);
    }

    /// <summary>
    /// Decodes one symbol by the procedure of T.81 Figure F.16 (MAXCODE, VALPTR), bit by bit: the reference the
    /// table lookups of <see cref="DecodeAc"/> and <see cref="DecodeDc"/> are tested against.
    /// </summary>
    public int Decode(ref JpegBitReader reader)
    {
        int code = 0;
        for (int length = 1; length <= 16; length++)
        {
            code = (code << 1) | reader.ReadBits(1);
            if (code <= _maxCode[length])
            {
                return _values[code + _valOffset[length]];
            }
        }

        throw new InvalidDataException("Invalid Huffman code.");
    }

    /// <summary>
    /// Decodes a code longer than <see cref="CoefficientBits"/> by MAXCODE (T.81 F.2.2.3) from the left-aligned
    /// <paramref name="buffer"/>, which holds at least 16 valid bits. Returns <c>(code length &lt;&lt; 8) | symbol</c>,
    /// consuming nothing.
    /// </summary>
    private int DecodeLong(ulong buffer)
    {
        int peek = (int)(buffer >> 48);
        for (int length = CoefficientBits + 1; length <= 16; length++)
        {
            int code = peek >> (16 - length);
            if (code <= _maxCode[length])
            {
                return (length << 8) | _values[code + _valOffset[length]];
            }
        }

        throw new InvalidDataException("Invalid Huffman code.");
    }

    /// <summary>Reads a <paramref name="size"/>-bit magnitude and sign-extends it (T.81 F.2.2.1, EXTEND).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReceiveExtend(ref JpegBitReader reader, ref ulong buffer, ref int count, int size)
    {
        if (size == 0)
        {
            return 0;
        }

        if (count < size)
        {
            buffer = reader.Fill(buffer, count);
            count = JpegBitReader.FilledCount(count);
        }

        int bits = (int)(buffer >> (64 - size));
        buffer <<= size;
        count -= size;
        return Extend(bits, size);
    }
}
