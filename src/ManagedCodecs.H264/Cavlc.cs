using System.Numerics;
using System.Runtime.CompilerServices;

namespace ManagedCodecs.H264;

/// <summary>CAVLC residual_block parsing (H.264 clause 9.2).</summary>
internal static class Cavlc
{
    private static readonly Vlc CoeffToken0 = new(CavlcTables.CoeffToken0);
    private static readonly Vlc CoeffToken2 = new(CavlcTables.CoeffToken2);
    private static readonly Vlc CoeffToken4 = new(CavlcTables.CoeffToken4);
    private static readonly Vlc CoeffToken8 = new(CavlcTables.CoeffToken8);
    private static readonly Vlc CoeffTokenChromaDc = new(CavlcTables.CoeffTokenChromaDc);
    private static readonly Vlc CoeffTokenChromaDc422 = new(CavlcTables.CoeffTokenChromaDc422);
    private static readonly Vlc[] TotalZeros4x4 = [.. CavlcTables.TotalZeros4x4.Select(t => t.Length == 0 ? null! : new Vlc(t))];
    private static readonly Vlc[] TotalZerosChromaDc = [.. CavlcTables.TotalZerosChromaDc.Select(t => t.Length == 0 ? null! : new Vlc(t))];
    private static readonly Vlc[] TotalZerosChromaDc422 = [.. CavlcTables.TotalZerosChromaDc422.Select(t => t.Length == 0 ? null! : new Vlc(t))];
    private static readonly byte[] RunBefore = BuildRunBefore();

    /// <summary>nC value that selects the chroma DC table (4:2:0).</summary>
    public const int ChromaDcNc = -1;

    /// <summary>nC value that selects the chroma DC table of 4:2:2.</summary>
    public const int ChromaDc422Nc = -2;

    /// <summary>
    /// Parses one residual_block_cavlc and returns TotalCoeff. The nonzero levels go to
    /// <paramref name="block"/>[<paramref name="scan"/>[position]] for scan positions
    /// 0..maxNumCoeff-1; the caller clears the block first.
    /// </summary>
    public static int ReadResidualBlock(BitReader r, int nC, int maxNumCoeff, Span<int> block, ReadOnlySpan<byte> scan)
    {
        // The reads on a cursor in locals (BitReader.Begin), the position written back once.
        var bits = r.Begin();
        int totalCoeff = ReadResidualBlock(ref bits, nC, maxNumCoeff, block, scan);
        r.Commit(bits);
        return totalCoeff;
    }

    [SkipLocalsInit]
    private static int ReadResidualBlock(ref BitReader.Cursor r, int nC, int maxNumCoeff, Span<int> block, ReadOnlySpan<byte> scan)
    {
        var coeffTokenTable = nC switch
        {
            ChromaDcNc => CoeffTokenChromaDc,
            ChromaDc422Nc => CoeffTokenChromaDc422,
            < 2 => CoeffToken0,
            < 4 => CoeffToken2,
            < 8 => CoeffToken4,
            _ => CoeffToken8,
        };

        int token = r.Read(coeffTokenTable);
        int totalCoeff = token >> 2;
        int trailingOnes = token & 3;
        if (totalCoeff == 0)
        {
            return 0;
        }

        if (totalCoeff > maxNumCoeff)
        {
            throw new InvalidDataException($"TotalCoeff {totalCoeff} exceeds the block size {maxNumCoeff}.");
        }

        // The trailing ones' signs in one read, then the other levels from the next coefficient on.
        Unsafe.SkipInit(out Levels levelVal);
        uint signs = r.Peek(3);
        r.Skip(trailingOnes);
        for (int i = 0; i < trailingOnes; i++)
        {
            levelVal[i] = 1 - (int)(((signs << i) >> 1) & 2);
        }

        int suffixLength = totalCoeff > 10 && trailingOnes < 3 ? 1 : 0;
        for (int i = trailingOnes; i < totalCoeff; i++)
        {
            int levelPrefix = ReadLevelPrefix(ref r);
            int levelCode = Math.Min(15, levelPrefix) << suffixLength;
            if (suffixLength > 0 || levelPrefix >= 14)
            {
                int suffixSize = levelPrefix == 14 && suffixLength == 0 ? 4
                    : levelPrefix >= 15 ? levelPrefix - 3
                    : suffixLength;
                levelCode += (int)r.ReadBits(suffixSize);
            }

            if (levelPrefix >= 15 && suffixLength == 0)
            {
                levelCode += 15;
            }

            if (levelPrefix >= 16)
            {
                levelCode += (1 << (levelPrefix - 3)) - 4096;
            }

            if (i == trailingOnes && trailingOnes < 3)
            {
                levelCode += 2;
            }

            levelVal[i] = (levelCode & 1) == 0 ? (levelCode + 2) >> 1 : (-levelCode - 1) >> 1;

            if (suffixLength == 0)
            {
                suffixLength = 1;
            }

            if (Math.Abs(levelVal[i]) > (3 << (suffixLength - 1)) && suffixLength < 6)
            {
                suffixLength++;
            }
        }

        int zerosLeft = 0;
        if (totalCoeff < maxNumCoeff)
        {
            var table = nC switch
            {
                ChromaDcNc => TotalZerosChromaDc[totalCoeff],
                ChromaDc422Nc => TotalZerosChromaDc422[totalCoeff],
                _ => TotalZeros4x4[totalCoeff],
            };
            zerosLeft = r.Read(table);
            if (totalCoeff + zerosLeft > maxNumCoeff)
            {
                throw new InvalidDataException("total_zeros overruns the block.");
            }
        }

        // Place levels from the highest frequency down (9.2.4): each run_before is the number
        // of zeros directly below the coefficient just placed.
        int position = totalCoeff + zerosLeft - 1;
        for (int i = 0; i < totalCoeff; i++)
        {
            block[scan[position]] = levelVal[i];
            int run = 0;
            if (i < totalCoeff - 1 && zerosLeft > 0)
            {
                run = ReadRunBefore(ref r, zerosLeft);
                if (run > zerosLeft)
                {
                    throw new InvalidDataException("run_before exceeds the zeros left.");
                }

                zerosLeft -= run;
            }

            position -= run + 1;
        }

        return totalCoeff;
    }

    /// <summary>
    /// run_before (Table 9-10) for <paramref name="zerosLeft"/> zeros left: the codes of up to 3 bits from one
    /// lookup, the longer ones (zerosLeft above 6, runs above 6) from their leading zeros.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadRunBefore(ref BitReader.Cursor r, int zerosLeft)
    {
        uint peek = r.Peek(32);
        int entry = RunBefore[(Math.Min(zerosLeft, 7) << 3) | (int)(peek >> 29)];
        if (entry != 0)
        {
            r.Skip(entry & 3);
            return entry >> 2;
        }

        // 000 1 is a run of 7, each zero more one more, up to 0000 0000 001 (14).
        int zeros = BitOperations.LeadingZeroCount(peek);
        if (zeros > 10)
        {
            throw new InvalidDataException("Invalid run_before.");
        }

        r.Skip(zeros + 1);
        return zeros + 4;
    }

    /// <summary>
    /// The codes of Table 9-10 of up to 3 bits as (run &lt;&lt; 2) | length, at (min(zerosLeft, 7) &lt;&lt; 3) | the
    /// next 3 bits; 0 for none (the longer codes).
    /// </summary>
    private static byte[] BuildRunBefore()
    {
        var table = new byte[64];
        for (int zerosLeft = 1; zerosLeft <= 7; zerosLeft++)
        {
            string[] codes = CavlcTables.RunBefore[zerosLeft];
            for (int run = 0; run < codes.Length; run++)
            {
                string code = codes[run].Replace(" ", "", StringComparison.Ordinal);
                if (code.Length > 3)
                {
                    continue;
                }

                int shift = 3 - code.Length;
                int first = Convert.ToInt32(code, 2) << shift;
                for (int bits = first; bits < first + (1 << shift); bits++)
                {
                    table[(zerosLeft << 3) | bits] = (byte)((run << 2) | code.Length);
                }
            }
        }

        return table;
    }

    /// <summary>The levels of one block, on the stack without a stack allocation's probe.</summary>
#if NET
    [InlineArray(16)]
    private struct Levels
    {
        private int _element;
    }
#else
    // netstandard2.1's runtimes have no inline arrays: a fixed buffer, indexed as the inline array is.
    private unsafe struct Levels
    {
        private fixed int _elements[16];

        public int this[int index]
        {
            readonly get => _elements[index];
            set => _elements[index] = value;
        }
    }
#endif

    /// <summary>level_prefix (9.2.2.1): the number of leading zero bits before a one.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadLevelPrefix(ref BitReader.Cursor r)
    {
        uint peek = r.Peek(32);
        if (peek == 0)
        {
            throw new InvalidDataException("level_prefix is too long.");
        }

        int zeros = BitOperations.LeadingZeroCount(peek);
        r.Skip(zeros + 1);
        return zeros;
    }
}
