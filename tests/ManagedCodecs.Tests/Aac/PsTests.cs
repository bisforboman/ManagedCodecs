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
}
