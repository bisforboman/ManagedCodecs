using System.Numerics;
using ManagedCodecs.Aac;

namespace ManagedCodecs.Tests.Aac;

/// <summary>Transcription and construction checks for the AAC tables and windows.</summary>
public class AacTablesTests
{
    [Fact]
    public void FourThirdsPower_IsWithinTwoUnitsOfTheExactValue()
    {
        // The reference in integers: floor(x^(4/3) 2^60) = floor(cbrt(x^4 2^180)), by Newton's method from above.
        for (int x = 1; x < 8192; x++)
        {
            var n = BigInteger.Pow(x, 4) << 180;
            var root = BigInteger.One << (int)((n.GetBitLength() + 2) / 3);
            while (((2 * root) + (n / (root * root))) / 3 is var next && next < root)
            {
                root = next;
            }

            double exact = (double)root * Math.ScaleB(1.0, -60);
            double value = AacDecoder.FourThirdsPower(x);
            Assert.True(Math.Abs(value - exact) <= 2 * (Math.BitIncrement(exact) - exact), $"{x}^(4/3): {value} against {exact}");
        }

        Assert.Equal(16.0, AacDecoder.FourThirdsPower(8));
    }

    [Fact]
    public void QuarterPowerOfTwo_IsTwoToTheQuarter()
    {
        // Exact at whole powers; elsewhere within one unit in the last place of Math.Pow (which may itself be off by one).
        for (int quarters = -400; quarters <= 400; quarters++)
        {
            double value = AacDecoder.QuarterPowerOfTwo(quarters);
            if (quarters % 4 == 0)
            {
                Assert.Equal(Math.ScaleB(1.0, quarters / 4), value);
            }

            double reference = Math.Pow(2, quarters / 4.0);
            Assert.True(Math.Abs(value - reference) <= Math.BitIncrement(reference) - reference, $"2^({quarters}/4): {value} against {reference}");
        }
    }

    [Fact]
    public void Codebooks_ArePrefixFreeAndComplete()
    {
        // Building a Vlc rejects any table that is not prefix-free; a Kraft sum of exactly 1
        // means the codebook uses its whole code space, as all AAC codebooks do.
        int[] sizes = [0, 81, 81, 81, 81, 81, 81, 64, 64, 169, 169, 289];
        for (int book = 1; book <= 11; book++)
        {
            var table = AacTables.SpectrumCodebooks[book];
            Assert.Equal(sizes[book], table.Length);
            _ = new Vlc(table);
            Assert.Equal(1.0, KraftSum(table), precision: 12);
        }

        Assert.Equal(121, AacTables.ScalefactorCodebook.Length);
        _ = new Vlc(AacTables.ScalefactorCodebook);
        Assert.Equal(1.0, KraftSum(AacTables.ScalefactorCodebook), precision: 12);
        Assert.Equal("0", AacTables.ScalefactorCodebook[60]); // a difference of 0 is the 1-bit code
    }

    [Fact]
    public void ScalefactorBands_CoverTheWindowInOrder()
    {
        int[] longBands = [41, 41, 47, 49, 49, 51, 47, 47, 43, 43, 43, 40, 40];
        int[] shortBands = [12, 12, 12, 14, 14, 14, 15, 15, 15, 15, 15, 15, 15];
        for (int rate = 0; rate < 13; rate++)
        {
            Check(AacTables.SwbOffsetLong[rate], longBands[rate], 1024);
            Check(AacTables.SwbOffsetShort[rate], shortBands[rate], 128);
            Assert.True(AacTables.TnsMaxBandsLong[rate] <= longBands[rate]);
            Assert.True(AacTables.TnsMaxBandsShort[rate] <= shortBands[rate]);
        }

        static void Check(short[] offsets, int bands, int length)
        {
            Assert.Equal(bands + 1, offsets.Length);
            Assert.Equal(0, offsets[0]);
            Assert.Equal(length, offsets[^1]);
            for (int i = 1; i < offsets.Length; i++)
            {
                Assert.True(offsets[i] > offsets[i - 1] && offsets[i] % 4 == 0);
            }
        }
    }

    [Fact]
    public void Windows_SatisfyPrincenBradley()
    {
        // w[n]^2 + w[n + N/2]^2 = 1, i.e. rise[n]^2 + rise[N/2 - 1 - n]^2 = 1 for a symmetric window.
        foreach (var rise in new[] { Filterbank.SineLong, Filterbank.SineShort, Filterbank.KbdLong, Filterbank.KbdShort })
        {
            for (int n = 0; n < rise.Length; n++)
            {
                double fall = rise[rise.Length - 1 - n];
                Assert.Equal(1.0, (rise[n] * rise[n]) + (fall * fall), precision: 12);
            }
        }
    }

    private static double KraftSum(IEnumerable<string> codes) =>
        codes.Select(c => c.Replace(" ", "", StringComparison.Ordinal)).Sum(c => Math.Pow(2, -c.Length));
}
