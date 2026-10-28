using ManagedCodecs.Aac;
using ManagedCodecs.Aac.Sbr;
using ManagedCodecs.Tests.H264;

namespace ManagedCodecs.Tests.Aac;

/// <summary>
/// Parametric stereo's arithmetic: the cos and sin it computes without Math (so its output is the same bits on every
/// runtime) against Math's, and the decorrelator's phase factors against libxaac's float tables (Apache-2.0).
/// </summary>
public class PsTests
{
    /// <summary>
    /// Mixing procedure Rb's matrix, by half-angle identities and square roots, against its definition by arctangents
    /// (8.6.4.6.2) through Math, for every fine IID (-50 to 50 dB, the coarse ones among them) and every ICC index.
    /// </summary>
    [Fact]
    public void RbMatrixMatchesItsDefinition()
    {
        double[] decibels = [-50, -45, -40, -35, -30, -25, -22, -19, -16, -13, -10, -8, -6, -4, -2, 0, 2, 4, 6, 8, 10, 13, 16, 19, 22, 25, 30, 35, 40, 45, 50];
        double[] rhos = [1, 0.937, 0.84118, 0.60092, 0.36764, 0, -0.589, -1];
        double worst = 0;
        foreach (double db in decibels)
        {
            double c = Math.Pow(10, db / 20);
            foreach (double sent in rhos)
            {
                double rho = Math.Max(sent, 0.05);
                double alpha = 0.5 * Math.Atan2(2 * c * rho, (c * c) - 1);
                if (alpha < 0)
                {
                    alpha += Math.PI / 2;
                }

                double mu = Math.Sqrt(1 + (((4 * rho * rho) - 4) / ((c + (1 / c)) * (c + (1 / c)))));
                double gamma = Math.Atan(Math.Sqrt((1 - mu) / (1 + mu)));
                double[] expected =
                [
                    Math.Sqrt(2) * Math.Cos(alpha) * Math.Cos(gamma), Math.Sqrt(2) * Math.Sin(alpha) * Math.Cos(gamma),
                    -Math.Sqrt(2) * Math.Sin(alpha) * Math.Sin(gamma), Math.Sqrt(2) * Math.Cos(alpha) * Math.Sin(gamma),
                ];
                PsProcessor.MatrixRb(Math.Sqrt(2 / (1 + (c * c))), sent, out double h11, out double h12, out double h21, out double h22);
                double[] actual = [h11, h12, h21, h22];
                for (int k = 0; k < 4; k++)
                {
                    worst = Math.Max(worst, Math.Abs(actual[k] - expected[k]));
                }
            }
        }

        Assert.True(worst < 1e-9, $"Off by {worst:E3}.");
    }

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

    /// <summary>
    /// The phase parameters' band count follows the iid_mode of the last header that enabled IID, as FFmpeg keeps it: none
    /// before such a header (the FDK encoder's first phases come under a header with IID disabled, and FFmpeg reads no
    /// values there), 11 after one with 20 IID bands, and still 11 under a later header without IID.
    /// </summary>
    [Fact]
    public void PhaseBandsFollowTheLastHeaderWithIid()
    {
        var data = new PsData();
        var statistics = new AacStatistics();
        ReadFrame(data, statistics, iid: false);
        Assert.True(data.EnableIpdOpd);
        Assert.Equal(0, data.PhaseBands);
        Assert.All(data.Ipd[0], v => Assert.Equal(0, v));

        ReadFrame(data, statistics, iid: true);
        Assert.Equal(11, data.PhaseBands);
        Assert.Equal([1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1], data.Ipd[0][..11]);
        Assert.Equal([2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2], data.Opd[0][..11]);

        ReadFrame(data, statistics, iid: false);
        Assert.Equal(11, data.PhaseBands);
        Assert.Equal(3, statistics.PsIpdOpdFrames);
    }

    // A frame with a header (IID in 20 bands, all 0, or none; no ICC) and one envelope, and its extension: the IPD's
    // deltas in frequency 1 then 0 (all 1), the OPD's 2 then 0 (all 2), as many as the reader takes.
    private static void ReadFrame(PsData data, AacStatistics statistics, bool iid)
    {
        int phaseBands = iid ? 11 : data.PhaseBands;
        var w = new BitWriter();
        w.Bits(0b1, 1); // enable_ps_header
        w.Bits(iid ? 1 : 0, 1);
        if (iid)
        {
            w.Bits(1, 3); // iid_mode: 20 bands
        }

        w.Bits(0, 1); // enable_icc
        w.Bits(1, 1); // enable_ext
        w.Bits(0, 1); // frame_class: fixed
        w.Bits(1, 2); // one envelope
        if (iid)
        {
            w.Bits(0, 1); // iid_dt
            for (int b = 0; b < 20; b++)
            {
                w.Bits(0, 1); // delta 0
            }
        }

        var extension = new BitWriter();
        extension.Bits(0, 2); // ps_extension_id
        extension.Bits(1, 1); // enable_ipdopd
        extension.Bits(0, 1); // ipd_dt
        for (int b = 0; b < phaseBands; b++)
        {
            Code(extension, PsTables.IpdFrequency[b == 0 ? 1 : 0]);
        }

        extension.Bits(0, 1); // opd_dt
        for (int b = 0; b < phaseBands; b++)
        {
            Code(extension, PsTables.OpdFrequency[b == 0 ? 2 : 0]);
        }

        extension.Bits(0, 1); // reserved_ps
        int bytes = (extension.Count + 7) / 8;
        w.Bits(bytes, 4);
        foreach (byte b in extension.ToBytes())
        {
            w.Bits(b, 8);
        }

        int bits = w.Count;
        var r = new AacBitReader(w.ToBytes());
        Assert.True(data.Read(r, bits, statistics));
        Assert.Equal(bits, r.Position);
    }

    private static void Code(BitWriter w, string code)
    {
        foreach (char c in code)
        {
            w.Bit(c - '0');
        }
    }

    [Fact]
    public void PhasesMatchLibxaac()
    {
        // exp(-i pi 0.39 f): the first hybrid sub-subband's (f = 0.125) and the first QMF band's (f = 0.5).
        Assert.Equal(0.9882950187, PsProcessor.Layout20.HybridPhase.Re[0], 1e-7);
        Assert.Equal(-0.1525546163, PsProcessor.Layout20.HybridPhase.Im[0], 1e-7);
        Assert.Equal(0.8181497455, PsProcessor.QmfPhase.Re[0], 1e-7);
    }

    /// <summary>
    /// The decorrelator's vector all-pass row does each subband's arithmetic in the scalar order: the same outputs, delay
    /// line and link states, to the bit, from the first subband it filters to one past the last, tails included.
    /// </summary>
    [Theory]
    [InlineData(3, 23, 3)]
    [InlineData(0, 22, 3)]
    [InlineData(5, 6, 3)]
    [InlineData(5, 23, 5)]
    public void PsAllPassRow_MatchesScalar(int first, int end, int cutoff)
    {
        var decays = PsProcessor.QmfDecays(cutoff);
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
                double decay = sb <= cutoff ? 1 : Math.Max(0, 1 + (cutoff * 0.05) - (0.05 * sb));
                Assert.Equal(decay, decays[sb]);
                (expectedRe[sb], expectedIm[sb]) = PsProcessor.AllPass(inRe[sb], inIm[sb], sb, decay, expectedDelayRe, expectedDelayIm, PsProcessor.QmfPhase, expectedLinkRe, expectedLinkIm, PsProcessor.QmfLinkPhase, linkIndex);
            }

            var outRe = new double[64];
            var outIm = new double[64];
            PsProcessor.AllPassRow(inRe, inIm, first, end, decays, delayRe, delayIm, linkRe, linkIm, linkIndex, outRe, outIm);
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
