using ManagedCodecs.Aac;

namespace ManagedCodecs.Tests.Aac;

/// <summary>The FFT-based IMDCT (in float) must match the definition evaluated term by term in double, and its vector paths the scalar ones.</summary>
public class ImdctTests
{
    /// <summary>
    /// The IMDCT, in single precision, is within float rounding of the definition in double: 2e-6 of the block's peak
    /// (in double it was within 1e-12; the decoders' outputs against FFmpeg's are in docs/conformance.md).
    /// </summary>
    [Theory]
    [InlineData(1024)]
    [InlineData(128)]
    public void FastImdct_MatchesDefinition(int coefficients)
    {
        var random = new Random(coefficients + 2);
        var spec = new double[coefficients];
        var expected = new double[2 * coefficients];
        var actual = new double[2 * coefficients];
        for (int trial = 0; trial < 20; trial++)
        {
            for (int k = 0; k < coefficients; k++)
            {
                spec[k] = (random.NextDouble() - 0.5) * Math.Pow(10, random.Next(0, 6));
            }

            Imdct.InverseReference(spec, expected);
            Imdct.Inverse(spec, actual);
            double peak = expected.Max(Math.Abs);
            for (int n = 0; n < expected.Length; n++)
            {
                Assert.True(Math.Abs(expected[n] - actual[n]) <= peak * 2e-6, $"Sample {n}: {expected[n]} vs {actual[n]}");
            }
        }
    }

    /// <summary>
    /// The vector IMDCT and FFT do the scalar arithmetic in its order, without fused multiply-adds: the same floats, to the
    /// bit. The FFT (its bit reversal fused with the first stage) gives the book's FFT's floats too, but for a zero's sign.
    /// </summary>
    [Theory]
    [InlineData(1024)]
    [InlineData(128)]
    public void VectorImdct_MatchesScalarExactly(int coefficients)
    {
        var random = new Random(coefficients + 3);
        var spec = new double[coefficients];
        var expected = new double[2 * coefficients];
        var actual = new double[2 * coefficients];
        var re = new float[coefficients / 2];
        var im = new float[coefficients / 2];
        for (int trial = 0; trial < 50; trial++)
        {
            for (int k = 0; k < coefficients; k++)
            {
                spec[k] = (random.NextDouble() - 0.5) * Math.Pow(10, random.Next(0, 6));
            }

            Imdct.InverseScalar(spec, expected);
            Imdct.Inverse(spec, actual);
            Assert.Equal(expected.Select(BitConverter.DoubleToInt64Bits), actual.Select(BitConverter.DoubleToInt64Bits));

            for (int k = 0; k < re.Length; k++)
            {
                re[k] = (float)spec[k];
                im[k] = (float)spec[coefficients - 1 - k];
            }

            // Every other trial with zeros, as silent bands give: the reference multiplies the first stage by 1 - 0i, so
            // against it only a zero's sign may differ (equal floats, but for 0 and -0).
            if (trial % 2 == 1)
            {
                for (int k = 0; k < re.Length; k += 3)
                {
                    re[k] = 0;
                    im[(k + 1) % im.Length] = 0;
                }
            }

            var scalarRe = (float[])re.Clone();
            var scalarIm = (float[])im.Clone();
            var referenceRe = (float[])re.Clone();
            var referenceIm = (float[])im.Clone();
            Imdct.Fft(scalarRe, scalarIm, vector: false);
            Imdct.Fft(referenceRe, referenceIm, vector: false, reference: true);
            Imdct.Fft(re, im, vector: true);
            Assert.Equal(scalarRe.Select(BitConverter.SingleToInt32Bits), re.Select(BitConverter.SingleToInt32Bits));
            Assert.Equal(scalarIm.Select(BitConverter.SingleToInt32Bits), im.Select(BitConverter.SingleToInt32Bits));
            Assert.Equal(referenceRe, re);
            Assert.Equal(referenceIm, im);
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
