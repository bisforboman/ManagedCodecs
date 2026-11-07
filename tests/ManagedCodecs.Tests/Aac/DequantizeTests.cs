using ManagedCodecs.Aac;

namespace ManagedCodecs.Tests.Aac;

/// <summary>
/// AAC's inverse quantization without branches (<see cref="AacDecoder.InverseQuantize"/>) against its definition,
/// sign(q) |q|^(4/3) gain (4.6.2) with the sign applied before the product, as the decoder did: the same bits for every
/// magnitude, both signs and zero, and the same failure past the largest magnitude an escape codes.
/// </summary>
public class DequantizeTests
{
    private static readonly double[] Powers = [.. Enumerable.Range(0, 8192).Select(AacDecoder.FourThirdsPower)];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Branchless_MatchesDefinition(int seed)
    {
        var random = new Random(seed);
        double[] gains = [.. Enumerable.Range(0, 256).Select(sf => AacDecoder.QuarterPowerOfTwo(sf - 100))];
        int[] quantized = [.. Enumerable.Range(-8191, 16383), .. Enumerable.Range(0, 4096).Select(_ => random.Next(-20, 21))];
        random.Shuffle(quantized);
        double[] actual = new double[quantized.Length];
        foreach (double gain in gains)
        {
            AacDecoder.InverseQuantize(quantized, actual, Powers, gain);
            for (int i = 0; i < quantized.Length; i++)
            {
                int q = quantized[i];
                double expected = q == 0 ? 0.0 : (q < 0 ? -Powers[-q] : Powers[q]) * gain;
                Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual[i]));
            }
        }
    }

    [Theory]
    [InlineData(8192)]
    [InlineData(-8192)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void OutOfRange_Throws(int q)
    {
        double[] spectrum = new double[3];
        Assert.Throws<InvalidDataException>(() => AacDecoder.InverseQuantize(new[] { 1, q, 1 }, spectrum, Powers, 1.0));
    }
}
