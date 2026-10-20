using ManagedCodecs.Aac;

namespace ManagedCodecs.Tests.Aac;

/// <summary>The FFT-based IMDCT must match the definition evaluated term by term.</summary>
public class ImdctTests
{
    [Theory]
    [InlineData(1024)]
    [InlineData(128)]
    public void FastImdct_MatchesDefinition(int coefficients)
    {
        var random = new Random(coefficients);
        var spec = new double[coefficients];
        var expected = new double[2 * coefficients];
        var actual = new double[2 * coefficients];
        for (int trial = 0; trial < 20; trial++)
        {
            for (int k = 0; k < coefficients; k++)
            {
                // Spectral values span the range real streams produce (up to 8191^(4/3) * gain).
                spec[k] = (random.NextDouble() - 0.5) * Math.Pow(10, random.Next(0, 6));
            }

            Imdct.InverseReference(spec, expected);
            Imdct.Inverse(spec, actual);
            double peak = expected.Max(Math.Abs);
            for (int n = 0; n < expected.Length; n++)
            {
                Assert.True(Math.Abs(expected[n] - actual[n]) <= peak * 1e-12, $"Sample {n}: {expected[n]} vs {actual[n]}");
            }
        }
    }
}
