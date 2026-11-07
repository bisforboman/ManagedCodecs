using System.Numerics;
using System.Runtime.CompilerServices;

namespace ManagedCodecs.Aac;

/// <summary>Scalefactor and spectrum Huffman decoding (ISO/IEC 14496-3 4.6.3).</summary>
internal static class Huffman
{
    /// <summary>The ESC codebook's escape value.</summary>
    public const int EscapeFlag = 16;

    private static readonly Vlc Scalefactors = new(AacTables.ScalefactorCodebook);
    private static readonly Vlc[] Spectrum = [.. AacTables.SpectrumCodebooks.Select(t => t.Length == 0 ? null! : new Vlc(t))];

    /// <summary>A scalefactor difference, -60 .. 60.</summary>
    public static int ReadScalefactorDelta(AacBitReader r) => r.Read(Scalefactors) - 60;

    // Values of every codeword, 4 per index (books 5-11 use the first 2), from Table 4.A.x's
    // index formulas: signed books are offset by the LAV, unsigned ones get sign bits later.
    private static readonly sbyte[][] Values = [.. Enumerable.Range(0, 12).Select(BuildValues)];

    // Bits of the first level of a packed table (fewer for a book whose codewords are all shorter): 97.8% of book 11's
    // codewords, the most used one in loud music, and more of the others' resolve there.
    private const int PackedFirstBits = 10;

    /// <summary>Spectrum codebooks 1 to 11 as packed tables (<see cref="PackedBook"/>), by number.</summary>
    private static readonly PackedBook[] Packed = [.. Enumerable.Range(0, 12).Select(b => b == 0 ? null! : new PackedBook(b))];

    /// <summary>
    /// Reads the codewords of codebook <paramref name="book"/> (1..11) for quantized[start..end) with their sign and
    /// escape bits (4.6.3.3): the same values as <see cref="ReadSpectrumByDefinition"/>, each written straight into
    /// <paramref name="quantized"/>, through the cursor <paramref name="r"/> came from (<paramref name="cursor"/>), which
    /// reads past the end as zeros: a codeword error checks the end first, so that a truncated unit fails as with
    /// <see cref="AacBitReader.Skip"/>, and the caller commits the cursor, which checks it too.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReadSpectrum(AacBitReader r, ref AacBitReader.Cursor cursor, int book, int[] quantized, int start, int end)
    {
        var packed = Packed[book];
        int[] table = packed.Table;
        int firstBits = packed.FirstBits;
        int maxLength = packed.MaxLength;
        switch (book)
        {
            case 1 or 2:
                ReadQuads(r, ref cursor, table, firstBits, maxLength, quantized, start, end, unsigned: false);
                break;
            case 3 or 4:
                ReadQuads(r, ref cursor, table, firstBits, maxLength, quantized, start, end, unsigned: true);
                break;
            case 5 or 6:
                ReadPairs(r, ref cursor, table, firstBits, maxLength, quantized, start, end, unsigned: false);
                break;
            case < 11:
                ReadPairs(r, ref cursor, table, firstBits, maxLength, quantized, start, end, unsigned: true);
                break;
            default:
                for (int k = start; k < end; k += 2)
                {
                    int entry = ReadPacked(r, ref cursor, table, firstBits, maxLength, out uint signs);
                    int bit = (entry >> 5) & 7;
                    int v0 = Value(entry, 0, signs, ref bit, unsigned: true);
                    int v1 = Value(entry, 1, signs, ref bit, unsigned: true);
                    if ((entry & (EscapeMask << 8)) != 0)
                    {
                        v0 = ReadPackedEscape(ref cursor, v0);
                        v1 = ReadPackedEscape(ref cursor, v1);
                    }

                    quantized[k] = v0;
                    quantized[k + 1] = v1;
                }

                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReadQuads(AacBitReader r, ref AacBitReader.Cursor cursor, int[] table, int firstBits, int maxLength, int[] quantized, int start, int end, bool unsigned)
    {
        for (int k = start; k < end; k += 4)
        {
            int entry = ReadPacked(r, ref cursor, table, firstBits, maxLength, out uint signs);
            int bit = (entry >> 5) & 7;
            quantized[k] = Value(entry, 0, signs, ref bit, unsigned);
            quantized[k + 1] = Value(entry, 1, signs, ref bit, unsigned);
            quantized[k + 2] = Value(entry, 2, signs, ref bit, unsigned);
            quantized[k + 3] = Value(entry, 3, signs, ref bit, unsigned);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReadPairs(AacBitReader r, ref AacBitReader.Cursor cursor, int[] table, int firstBits, int maxLength, int[] quantized, int start, int end, bool unsigned)
    {
        for (int k = start; k < end; k += 2)
        {
            int entry = ReadPacked(r, ref cursor, table, firstBits, maxLength, out uint signs);
            int bit = (entry >> 5) & 7;
            quantized[k] = Value(entry, 0, signs, ref bit, unsigned);
            quantized[k + 1] = Value(entry, 1, signs, ref bit, unsigned);
        }
    }

    // A first value of 16 (an escape) in a packed entry: its 6-bit field reads 010000 (bits 8-13), the second (bits
    // 14-19) likewise; the mask tests both fields' bit 4 (no other book 11 value, 0..15, sets it).
    private const int EscapeMask = (1 << 4) | (1 << 10);

    /// <summary>
    /// One codeword's packed entry, consumed with the sign bits that follow it, which <paramref name="signs"/> holds in
    /// its low bits (one per nonzero value of an unsigned book, the first value's highest; none for a signed book).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadPacked(AacBitReader r, ref AacBitReader.Cursor cursor, int[] table, int firstBits, int maxLength, out uint signs)
    {
        uint bits = cursor.Peek(32);
        int entry = table[bits >> (32 - firstBits)];
        if ((entry & 31) == 0)
        {
            int secondBits = maxLength - firstBits;
            entry = entry == 0 ? 0 : table[(entry >> 8) + (int)((bits << firstBits) >> (32 - secondBits))];
            if (entry == 0)
            {
                r.ThrowIfPastEnd(cursor.Position);
                throw new InvalidDataException("Invalid VLC codeword.");
            }
        }

        int length = entry & 31;
        int count = (entry >> 5) & 7;

        // At most 16 + 4 bits: the shifts stay below 32 but for count 0, whose (garbage) signs no value uses.
        signs = (bits << length) >> (32 - count);
        cursor.Skip(length + count);
        return entry;
    }

    /// <summary>
    /// Value <paramref name="i"/> of a packed entry; in an unsigned book negated if it is nonzero and its sign bit (the
    /// next of <paramref name="signs"/>' low <paramref name="bit"/> bits, highest first) is set.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Value(int entry, int i, uint signs, ref int bit, bool unsigned)
    {
        int value = (entry << (18 - (6 * i))) >> 26;
        if (!unsigned)
        {
            return value;
        }

        int nonzero = value != 0 ? 1 : 0;
        bit -= nonzero;
        int negative = -(int)((signs >> bit) & (uint)nonzero);
        return (value ^ negative) - negative;
    }

    /// <summary><see cref="ReadEscape"/> through the cursor: N ones, a zero, then N + 4 bits, at most 21 in all.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadPackedEscape(ref AacBitReader.Cursor cursor, int value)
    {
        if (Math.Abs(value) != EscapeFlag)
        {
            return value;
        }

        uint bits = cursor.Peek(32);
        int n = BitOperations.LeadingZeroCount(~bits);
        if (n > 8)
        {
            // As ReadEscape, at the ninth one: never past the end, where the cursor reads zeros.
            throw new InvalidDataException("Escape sequence is too long.");
        }

        int escaped = (1 << (n + 4)) + (int)((bits << (n + 1)) >> (32 - (n + 4)));
        cursor.Skip((2 * n) + 5);
        return value < 0 ? -escaped : escaped;
    }

    /// <summary>
    /// A spectrum codebook as one table of packed entries, the codewords of up to <see cref="FirstBits"/> bits in its
    /// first level and the longer ones in second-level tables after it, each of 2^(<see cref="MaxLength"/> -
    /// FirstBits) entries. A value entry is the codeword's length (bits 0-4), the number of sign bits after it (bits
    /// 5-7: the nonzero values of an unsigned book, 0 for a signed one) and its 2 or 4 values (6-bit two's complement,
    /// from bit 8 on); a first-level entry that leads to a second level is its offset in the table (from bit 8 on);
    /// 0 is no codeword.
    /// </summary>
    private sealed class PackedBook
    {
        public PackedBook(int book)
        {
            var codes = AacTables.SpectrumCodebooks[book].Select(c => c.Replace(" ", "", StringComparison.Ordinal)).ToArray();
            MaxLength = codes.Max(c => c.Length);
            FirstBits = Math.Min(PackedFirstBits, MaxLength);
            int secondBits = MaxLength - FirstBits;
            var table = new List<int>(new int[1 << FirstBits]);
            var values = Values[book];
            bool unsigned = book is 3 or 4 or >= 7;

            for (int index = 0; index < codes.Length; index++)
            {
                string code = codes[index];
                int length = code.Length;
                int entry = length;
                int nonzero = 0;
                for (int i = 0; i < 4; i++)
                {
                    int value = values[(index * 4) + i];
                    nonzero += value != 0 ? 1 : 0;
                    entry |= (value & 63) << (8 + (6 * i));
                }

                if (unsigned)
                {
                    entry |= nonzero << 5;
                }

                int bits = Convert.ToInt32(code, 2);
                if (length <= FirstBits)
                {
                    int shift = FirstBits - length;
                    Fill(table, bits << shift, 1 << shift, entry);
                    continue;
                }

                int prefix = bits >> (length - FirstBits);
                if (table[prefix] == 0)
                {
                    table[prefix] = table.Count << 8;
                    table.AddRange(new int[1 << secondBits]);
                }

                int rest = bits & ((1 << (length - FirstBits)) - 1);
                int restShift = MaxLength - length;
                Fill(table, (table[prefix] >> 8) + (rest << restShift), 1 << restShift, entry);
            }

            Table = [.. table];
        }

        public int[] Table { get; }

        public int FirstBits { get; }

        public int MaxLength { get; }

        private static void Fill(List<int> table, int start, int count, int entry)
        {
            for (int i = start; i < start + count; i++)
            {
                if (table[i] != 0)
                {
                    throw new InvalidOperationException("Codewords are not prefix-free.");
                }

                table[i] = entry;
            }
        }
    }

    /// <summary>
    /// Reads one codeword of spectrum codebook <paramref name="book"/> (1..11) and its sign and
    /// escape bits, writing 4 (books 1-4) or 2 (books 5-11) quantized values, by the definition: the reference the
    /// packed reader is tested against.
    /// </summary>
    public static void ReadSpectrumByDefinition(AacBitReader r, int book, Span<int> values)
    {
        int index = r.Read(Spectrum[book]);
        var table = Values[book];
        int dimension = book <= 4 ? 4 : 2;
        for (int i = 0; i < dimension; i++)
        {
            values[i] = table[(index * 4) + i];
        }

        bool unsigned = book is 3 or 4 or >= 7;
        if (unsigned)
        {
            ReadSigns(r, values[..dimension]);
        }

        if (book == 11)
        {
            values[0] = ReadEscape(r, values[0]);
            values[1] = ReadEscape(r, values[1]);
        }
    }

    private static sbyte[] BuildValues(int book)
    {
        int count = book switch { 0 => 0, <= 6 => 81, <= 8 => 64, <= 10 => 169, _ => 289 };
        var values = new sbyte[count * 4];
        Span<int> v = stackalloc int[4];
        for (int index = 0; index < count; index++)
        {
            v.Clear();
            switch (book)
            {
                case 1 or 2: // signed quads, values -1..1
                    v[0] = (index / 27) - 1;
                    v[1] = ((index / 9) % 3) - 1;
                    v[2] = ((index / 3) % 3) - 1;
                    v[3] = (index % 3) - 1;
                    break;
                case 3 or 4: // unsigned quads, 0..2
                    v[0] = index / 27;
                    v[1] = (index / 9) % 3;
                    v[2] = (index / 3) % 3;
                    v[3] = index % 3;
                    break;
                case 5 or 6: // signed pairs, -4..4
                    v[0] = (index / 9) - 4;
                    v[1] = (index % 9) - 4;
                    break;
                case 7 or 8: // unsigned pairs, 0..7
                    v[0] = index / 8;
                    v[1] = index % 8;
                    break;
                case 9 or 10: // unsigned pairs, 0..12
                    v[0] = index / 13;
                    v[1] = index % 13;
                    break;
                default: // book 11: unsigned pairs, 0..16 where 16 is an escape
                    v[0] = index / 17;
                    v[1] = index % 17;
                    break;
            }

            for (int i = 0; i < 4; i++)
            {
                values[(index * 4) + i] = (sbyte)v[i];
            }
        }

        return values;
    }

    /// <summary>One sign bit per non-zero value, in order (1 = negative).</summary>
    private static void ReadSigns(AacBitReader r, Span<int> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] != 0 && r.ReadFlag())
            {
                values[i] = -values[i];
            }
        }
    }

    /// <summary>escape_sequence (4.6.3.3): N ones, a zero, then N + 4 bits; the value is 2^(N+4) + bits.</summary>
    private static int ReadEscape(AacBitReader r, int value)
    {
        int magnitude = Math.Abs(value);
        if (magnitude != EscapeFlag)
        {
            return value;
        }

        int n = 0;
        while (r.ReadFlag())
        {
            if (++n > 8)
            {
                throw new InvalidDataException("Escape sequence is too long.");
            }
        }

        int escaped = (1 << (n + 4)) + r.Read(n + 4);
        return value < 0 ? -escaped : escaped;
    }
}
