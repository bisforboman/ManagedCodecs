using System.Runtime.CompilerServices;

namespace ManagedCodecs.H264;

/// <summary>
/// Decoder for one variable-length code table, built from codeword strings. Codes of up to
/// <see cref="FirstLevelBits"/> bits resolve in one lookup; longer ones go through a second
/// table chosen by their first bits.
/// </summary>
internal sealed class Vlc
{
    private const int FirstLevelBits = 8;

    // Entry: (value << 8) | code length; 0 = no codeword; negative = -(second-level table + 1).
    private readonly int[] _first;
    private readonly int[][] _second;
    private readonly int _maxLength;
    private readonly int _firstBits;

    /// <param name="codes">Codeword per value ("" for none); spaces are ignored.</param>
    public Vlc(IReadOnlyList<string> codes)
    {
        var entries = new List<(string Code, int Value)>();
        for (int value = 0; value < codes.Count; value++)
        {
            string code = codes[value].Replace(" ", string.Empty, StringComparison.Ordinal);
            if (code.Length > 0)
            {
                entries.Add((code, value));
            }
        }

        if (entries.Count == 0)
        {
            throw new ArgumentException("A VLC needs at least one codeword.", nameof(codes));
        }

        _maxLength = entries.Max(e => e.Code.Length);
        if (_maxLength > 16)
        {
            throw new ArgumentException("Codewords are limited to 16 bits.", nameof(codes));
        }

        _firstBits = Math.Min(FirstLevelBits, _maxLength);
        _first = new int[1 << _firstBits];
        var second = new List<int[]>();
        int secondBits = _maxLength - _firstBits;

        foreach (var (code, value) in entries)
        {
            int bits = Convert.ToInt32(code, 2);
            int length = code.Length;
            int entry = (value << 8) | length;

            if (length <= _firstBits)
            {
                int shift = _firstBits - length;
                Fill(_first, bits << shift, 1 << shift, entry);
                continue;
            }

            int prefix = bits >> (length - _firstBits);
            if (_first[prefix] > 0)
            {
                throw new ArgumentException($"Codeword {code} has another codeword as its prefix.", nameof(codes));
            }

            if (_first[prefix] == 0)
            {
                second.Add(new int[1 << secondBits]);
                _first[prefix] = -second.Count;
            }

            var table = second[-_first[prefix] - 1];
            int rest = bits & ((1 << (length - _firstBits)) - 1);
            int restShift = _maxLength - length;
            Fill(table, rest << restShift, 1 << restShift, entry);
        }

        _second = [.. second];
    }

    public int MaxLength => _maxLength;

    /// <summary>Reads one codeword and returns its value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Read(BitReader reader)
    {
        uint peek = reader.Peek(_maxLength);
        int entry = _first[peek >> (_maxLength - _firstBits)];
        if (entry < 0)
        {
            entry = _second[-entry - 1][peek & ((1u << (_maxLength - _firstBits)) - 1)];
        }

        if (entry == 0)
        {
            throw new InvalidDataException("Invalid VLC codeword.");
        }

        reader.Skip(entry & 0xFF);
        return entry >> 8;
    }

    private static void Fill(int[] table, int start, int count, int entry)
    {
        for (int i = start; i < start + count; i++)
        {
            if (table[i] != 0)
            {
                throw new ArgumentException("Codewords are not prefix-free.");
            }

            table[i] = entry;
        }
    }
}
