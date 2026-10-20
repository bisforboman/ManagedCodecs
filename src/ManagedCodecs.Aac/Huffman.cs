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

    /// <summary>
    /// Reads one codeword of spectrum codebook <paramref name="book"/> (1..11) and its sign and
    /// escape bits, writing 4 (books 1-4) or 2 (books 5-11) quantized values.
    /// </summary>
    public static void ReadSpectrum(AacBitReader r, int book, Span<int> values)
    {
        int index = r.Read(Spectrum[book]);
        switch (book)
        {
            case 1 or 2: // signed quads, values -1..1
                values[0] = (index / 27) - 1;
                values[1] = ((index / 9) % 3) - 1;
                values[2] = ((index / 3) % 3) - 1;
                values[3] = (index % 3) - 1;
                return;

            case 3 or 4: // unsigned quads, 0..2
                values[0] = index / 27;
                values[1] = (index / 9) % 3;
                values[2] = (index / 3) % 3;
                values[3] = index % 3;
                ReadSigns(r, values[..4]);
                return;

            case 5 or 6: // signed pairs, -4..4
                values[0] = (index / 9) - 4;
                values[1] = (index % 9) - 4;
                return;

            case 7 or 8: // unsigned pairs, 0..7
                values[0] = index / 8;
                values[1] = index % 8;
                ReadSigns(r, values[..2]);
                return;

            case 9 or 10: // unsigned pairs, 0..12
                values[0] = index / 13;
                values[1] = index % 13;
                ReadSigns(r, values[..2]);
                return;

            case 11: // unsigned pairs, 0..16 where 16 is an escape
                values[0] = index / 17;
                values[1] = index % 17;
                ReadSigns(r, values[..2]);
                values[0] = ReadEscape(r, values[0]);
                values[1] = ReadEscape(r, values[1]);
                return;

            default:
                throw new InvalidDataException($"Codebook {book} carries no spectral data.");
        }
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
