using ManagedCodecs.Aac.Sbr;

namespace ManagedCodecs.Tests.Aac;

/// <summary>
/// Parametric stereo's arithmetic: the cos and sin it computes without Math (so its output is the same bits on every
/// runtime) against Math's, and the decorrelator's phase factors against libxaac's float tables (Apache-2.0).
/// </summary>
public class PsTests
{
    [Fact]
    public void CosAndSinMatchMath()
    {
        var random = new Random(7);
        double worst = 0;
        for (int i = 0; i < 100_000; i++)
        {
            double x = (random.NextDouble() - 0.5) * 40;
            worst = Math.Max(worst, Math.Abs(PsMath.Cos(x) - Math.Cos(x)));
            worst = Math.Max(worst, Math.Abs(PsMath.Sin(x) - Math.Sin(x)));
        }

        foreach (double x in new[] { 0, Math.PI / 4, Math.PI / 2, Math.PI, -Math.PI / 2, 3 * Math.PI / 4 })
        {
            worst = Math.Max(worst, Math.Abs(PsMath.Cos(x) - Math.Cos(x)));
            worst = Math.Max(worst, Math.Abs(PsMath.Sin(x) - Math.Sin(x)));
        }

        Assert.True(worst < 1e-15, $"Off by {worst:E3}.");
    }

    [Fact]
    public void PhasesMatchLibxaac()
    {
        // exp(-i pi 0.39 f): the first hybrid sub-subband's (f = 0.125) and the first QMF band's (f = 0.5).
        Assert.Equal(0.9882950187, PsProcessor.HybridPhase.Re[0], 1e-7);
        Assert.Equal(-0.1525546163, PsProcessor.HybridPhase.Im[0], 1e-7);
        Assert.Equal(0.8181497455, PsProcessor.QmfPhase.Re[0], 1e-7);
    }

    /// <summary>
    /// The decorrelator's vector all-pass row does each subband's arithmetic in the scalar order: the same outputs, delay
    /// line and link states, to the bit, from the first subband it filters to one past the last, tails included.
    /// </summary>
    [Theory]
    [InlineData(3, 23)]
    [InlineData(0, 22)]
    [InlineData(5, 6)]
    public void PsAllPassRow_MatchesScalar(int first, int end)
    {
        var random = new Random((first * 64) + end);
        double Next() => (random.NextDouble() - 0.5) * Math.Pow(10, random.Next(-3, 4));
        double[] Row() => [.. Enumerable.Range(0, 64).Select(_ => Next())];
        for (int trial = 0; trial < 20; trial++)
        {
            var inRe = Row();
            var inIm = Row();
            var delayRe = Row();
            var delayIm = Row();
            double[][][] linkRe = [.. new[] { 3, 4, 5 }.Select(n => Enumerable.Range(0, n).Select(_ => Row()).ToArray())];
            double[][][] linkIm = [.. new[] { 3, 4, 5 }.Select(n => Enumerable.Range(0, n).Select(_ => Row()).ToArray())];
            int[] linkIndex = [random.Next(3), random.Next(4), random.Next(5)];

            var expectedDelayRe = (double[])delayRe.Clone();
            var expectedDelayIm = (double[])delayIm.Clone();
            double[][][] expectedLinkRe = [.. linkRe.Select(l => l.Select(r => (double[])r.Clone()).ToArray())];
            double[][][] expectedLinkIm = [.. linkIm.Select(l => l.Select(r => (double[])r.Clone()).ToArray())];
            var expectedRe = new double[64];
            var expectedIm = new double[64];
            for (int sb = first; sb < end; sb++)
            {
                double decay = sb <= 3 ? 1 : Math.Max(0, 1 + (3 * 0.05) - (0.05 * sb));
                (expectedRe[sb], expectedIm[sb]) = PsProcessor.AllPass(inRe[sb], inIm[sb], sb, decay, expectedDelayRe, expectedDelayIm, PsProcessor.QmfPhase, expectedLinkRe, expectedLinkIm, PsProcessor.QmfLinkPhase, linkIndex);
            }

            var outRe = new double[64];
            var outIm = new double[64];
            PsProcessor.AllPassRow(inRe, inIm, first, end, delayRe, delayIm, linkRe, linkIm, linkIndex, outRe, outIm);
            Assert.Equal(expectedRe, outRe);
            Assert.Equal(expectedIm, outIm);
            Assert.Equal(expectedDelayRe, delayRe);
            Assert.Equal(expectedDelayIm, delayIm);
            for (int link = 0; link < 3; link++)
            {
                Assert.Equal(expectedLinkRe[link], linkRe[link]);
                Assert.Equal(expectedLinkIm[link], linkIm[link]);
            }
        }
    }
}
