using System.Numerics;

namespace ManagedCodecs.H264;

/// <summary>CAVLC residual_block parsing (H.264 clause 9.2).</summary>
internal static class Cavlc
{
    private static readonly Vlc CoeffToken0 = new(CavlcTables.CoeffToken0);
    private static readonly Vlc CoeffToken2 = new(CavlcTables.CoeffToken2);
    private static readonly Vlc CoeffToken4 = new(CavlcTables.CoeffToken4);
    private static readonly Vlc CoeffToken8 = new(CavlcTables.CoeffToken8);
    private static readonly Vlc CoeffTokenChromaDc = new(CavlcTables.CoeffTokenChromaDc);
    private static readonly Vlc[] TotalZeros4x4 = [.. CavlcTables.TotalZeros4x4.Select(t => t.Length == 0 ? null! : new Vlc(t))];
    private static readonly Vlc[] TotalZerosChromaDc = [.. CavlcTables.TotalZerosChromaDc.Select(t => t.Length == 0 ? null! : new Vlc(t))];
    private static readonly Vlc[] RunBefore = [.. CavlcTables.RunBefore.Select(t => t.Length == 0 ? null! : new Vlc(t))];

    /// <summary>nC value that selects the chroma DC table (4:2:0).</summary>
    public const int ChromaDcNc = -1;

    /// <summary>
    /// Parses one residual_block_cavlc into <paramref name="levels"/>, indexed by scan position
    /// within the block (0..maxNumCoeff-1), and returns TotalCoeff.
    /// </summary>
    public static int ReadResidualBlock(BitReader r, int nC, int maxNumCoeff, Span<int> levels)
    {
        levels[..maxNumCoeff].Clear();

        var coeffTokenTable = nC switch
        {
            ChromaDcNc => CoeffTokenChromaDc,
            < 2 => CoeffToken0,
            < 4 => CoeffToken2,
            < 8 => CoeffToken4,
            _ => CoeffToken8,
        };

        int token = coeffTokenTable.Read(r);
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

        Span<int> levelVal = stackalloc int[16];
        int suffixLength = totalCoeff > 10 && trailingOnes < 3 ? 1 : 0;
        for (int i = 0; i < totalCoeff; i++)
        {
            if (i < trailingOnes)
            {
                levelVal[i] = r.ReadFlag() ? -1 : 1;
                continue;
            }

            int levelPrefix = ReadLevelPrefix(r);
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
            var table = nC == ChromaDcNc ? TotalZerosChromaDc[totalCoeff] : TotalZeros4x4[totalCoeff];
            zerosLeft = table.Read(r);
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
            levels[position] = levelVal[i];
            int run = 0;
            if (i < totalCoeff - 1 && zerosLeft > 0)
            {
                run = RunBefore[Math.Min(zerosLeft, 7)].Read(r);
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

    /// <summary>level_prefix (9.2.2.1): the number of leading zero bits before a one.</summary>
    private static int ReadLevelPrefix(BitReader r)
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
