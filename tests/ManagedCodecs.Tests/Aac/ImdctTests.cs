using ManagedCodecs.Aac;

namespace ManagedCodecs.Tests.Aac;

/// <summary>The FFT-based IMDCT must match the definition evaluated term by term, and its vector FFT the scalar one.</summary>
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

    /// <summary>The vector FFT does each butterfly's arithmetic in the scalar order: the same doubles, to the bit.</summary>
    [Theory]
    [InlineData(512)]
    [InlineData(64)]
    public void VectorFft_MatchesScalarExactly(int length)
    {
        var random = new Random(length);
        var re = new double[length];
        var im = new double[length];
        for (int trial = 0; trial < 20; trial++)
        {
            for (int k = 0; k < length; k++)
            {
                re[k] = (random.NextDouble() - 0.5) * Math.Pow(10, random.Next(0, 6));
                im[k] = (random.NextDouble() - 0.5) * Math.Pow(10, random.Next(0, 6));
            }

            var expectedRe = (double[])re.Clone();
            var expectedIm = (double[])im.Clone();
            Imdct.Fft(expectedRe, expectedIm, scalar: true);
            Imdct.Fft(re, im, scalar: false);
            Assert.Equal(expectedRe, re);
            Assert.Equal(expectedIm, im);
        }
    }

    /// <summary>The vector window, overlap and conversion loops give the scalar results, tails included.</summary>
    [Fact]
    public void VectorLoops_MatchScalarExactly()
    {
        var random = new Random(7);
        for (int length = 0; length < 40; length++)
        {
            double[] a = [.. Enumerable.Range(0, length).Select(_ => (random.NextDouble() - 0.5) * 1e5)];
            double[] b = [.. Enumerable.Range(0, length).Select(_ => (random.NextDouble() - 0.5) * 1e5)];
            double[] w = [.. Enumerable.Range(0, length).Select(_ => random.NextDouble())];

            var product = (double[])a.Clone();
            Vectors.Multiply(product, w);
            Assert.Equal(a.Select((x, n) => x * w[n]), product);

            var sum = (double[])a.Clone();
            Vectors.MultiplyAdd(sum, b, w);
            Assert.Equal(a.Select((x, n) => x + (b[n] * w[n])), sum);

            var output = new float[length];
            Vectors.AddScaleToSingle(a, b, 1.0 / 32768, output);
            Assert.Equal(a.Select((x, n) => (float)((x + b[n]) * (1.0 / 32768))), output);
        }
    }
}
