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

            var magnitudes = (double[])w.Clone();
            Vectors.AddMagnitudes(magnitudes, a, b);
            Assert.Equal(w.Select((x, n) => x + ((a[n] * a[n]) + (b[n] * b[n]))), magnitudes);

            var output = new float[length];
            Vectors.AddScaleToSingle(a, b, 1.0 / 32768, output);
            Assert.Equal(a.Select((x, n) => (float)((x + b[n]) * (1.0 / 32768))), output);
        }
    }

    /// <summary>
    /// The fused windows' sums give what clearing and one <see cref="Vectors.MultiplyAdd"/> a term give, bit for bit:
    /// the terms in their order from zero, tails included, at the QMF banks' offsets and at scattered ones.
    /// </summary>
    [Fact]
    public void SumOfProducts_MatchesMultiplyAddATerm()
    {
        var random = new Random(11);
        for (int length = 0; length < 70; length++)
        {
            int terms = random.Next(0, 11);
            int[] bOffsets = [.. Enumerable.Range(0, terms).Select(_ => random.Next(0, 100))];
            int[] wOffsets = [.. Enumerable.Range(0, terms).Select(_ => random.Next(0, 100))];
            double[] b = [.. Enumerable.Range(0, length + 100).Select(_ => (random.NextDouble() - 0.5) * 1e5)];
            double[] w = [.. Enumerable.Range(0, length + 100).Select(_ => random.NextDouble() - 0.5)];

            var expected = new double[length];
            for (int t = 0; t < terms; t++)
            {
                Vectors.MultiplyAdd(expected, b.AsSpan(bOffsets[t], length), w.AsSpan(wOffsets[t], length));
            }

            var sum = new double[length];
            Array.Fill(sum, double.NaN);
            Vectors.SumOfProducts(sum, b, bOffsets, w, wOffsets);
            Assert.Equal(expected.Select(BitConverter.DoubleToInt64Bits), sum.Select(BitConverter.DoubleToInt64Bits));
        }
    }

    /// <summary>A term that would read past its input is refused, before anything is written.</summary>
    [Fact]
    public void SumOfProducts_RejectsATermPastItsInput()
    {
        var sum = new double[8];
        Assert.Throws<ArgumentOutOfRangeException>(() => Vectors.SumOfProducts(sum, new double[16], [9], new double[16], [0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Vectors.SumOfProducts(sum, new double[16], [0], new double[16], [-1]));
        Assert.Throws<ArgumentException>(() => Vectors.SumOfProducts(sum, new double[16], [0, 1], new double[16], [0]));
    }
}
