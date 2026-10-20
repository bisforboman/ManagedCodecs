using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>
/// Transcription checks for the CAVLC tables: building a <see cref="Vlc"/> rejects any table
/// that is not prefix-free, and total_zeros / run_before must use the whole code space except,
/// in some tables, the all-zero codeword of maximum length, which the spec leaves unused.
/// </summary>
public class CavlcTablesTests
{
    public static TheoryData<string> CoeffTokenTables() => new("0", "2", "4", "8", "ChromaDc");

    [Theory]
    [MemberData(nameof(CoeffTokenTables))]
    public void CoeffToken_IsPrefixFreeWithAllTokens(string name)
    {
        string[] table = name switch
        {
            "0" => CavlcTables.CoeffToken0,
            "2" => CavlcTables.CoeffToken2,
            "4" => CavlcTables.CoeffToken4,
            "8" => CavlcTables.CoeffToken8,
            _ => CavlcTables.CoeffTokenChromaDc,
        };

        _ = new Vlc(table);

        // Every (TotalCoeff, TrailingOnes) pair with TrailingOnes <= min(TotalCoeff, 3) has a code.
        int maxTotal = name == "ChromaDc" ? 4 : 16;
        Assert.Equal((maxTotal + 1) * 4, table.Length);
        for (int total = 0; total <= maxTotal; total++)
        {
            for (int ones = 0; ones < 4; ones++)
            {
                bool expected = ones <= Math.Min(total, 3);
                Assert.True(expected == (table[(total * 4) + ones].Length > 0), $"TotalCoeff {total}, TrailingOnes {ones}");
            }
        }

        Assert.True(KraftSum(table) <= 1);
    }

    [Fact]
    public void TotalZerosAndRunBefore_AreCompleteCodes()
    {
        foreach (var table in CavlcTables.TotalZeros4x4.Skip(1)
            .Concat(CavlcTables.TotalZerosChromaDc.Skip(1))
            .Concat(CavlcTables.RunBefore.Skip(1)))
        {
            var vlc = new Vlc(table);
            double unused = 1.0 - KraftSum(table);
            if (unused != 0)
            {
                // Then exactly the all-zero codeword of maximum length is the unused one.
                Assert.Equal(Math.Pow(2, -vlc.MaxLength), unused);
                Assert.DoesNotContain(new string('0', vlc.MaxLength), table.Select(c => c.Replace(" ", "", StringComparison.Ordinal)));
            }
        }

        // total_zeros can take every value from 0 to maxNumCoeff - TotalCoeff.
        for (int total = 1; total < 16; total++)
        {
            Assert.Equal(16 - total + 1, CavlcTables.TotalZeros4x4[total].Length);
        }
    }

    [Fact]
    public void CodedBlockPatternTables_ArePermutations()
    {
        Assert.Equal(Enumerable.Range(0, 48), CavlcTables.CodedBlockPatternIntra.ToArray().Select(b => (int)b).Order());
        Assert.Equal(Enumerable.Range(0, 48), CavlcTables.CodedBlockPatternInter.ToArray().Select(b => (int)b).Order());
    }

    private static double KraftSum(IEnumerable<string> codes) =>
        codes.Select(c => c.Replace(" ", "", StringComparison.Ordinal))
            .Where(c => c.Length > 0)
            .Sum(c => Math.Pow(2, -c.Length));
}
