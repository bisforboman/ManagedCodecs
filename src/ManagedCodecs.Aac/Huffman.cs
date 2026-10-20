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

    /// <summary>
    /// Reads one codeword of spectrum codebook <paramref name="book"/> (1..11) and its sign and
    /// escape bits, writing 4 (books 1-4) or 2 (books 5-11) quantized values.
    /// </summary>
    public static void ReadSpectrum(AacBitReader r, int book, Span<int> values)
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
