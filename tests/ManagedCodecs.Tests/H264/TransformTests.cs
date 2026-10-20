using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>Shortcuts in the residual path must give what the full inverse transform gives.</summary>
public class TransformTests
{
    [Fact]
    public void AddDc_MatchesFullTransformOfADcOnlyBlock()
    {
        const int Stride = 7;
        var random = new Random(11);
        for (int n = 0; n < 20_000; n++)
        {
            // Scaled DC values as large as the 16x16 and chroma DC transforms produce, and prediction
            // samples near the ends of the range so the clipping matters.
            int dc = random.Next(4) == 0 ? random.Next(-64, 64) : random.Next(-300_000, 300_001);
            var prediction = new byte[(3 * Stride) + 4];
            random.NextBytes(prediction);

            var expected = (byte[])prediction.Clone();
            var block = new int[16];
            block[0] = dc;
            Transform.InverseTransformAdd(block, expected, Stride);

            var actual = (byte[])prediction.Clone();
            Transform.AddDc(dc, actual, Stride);

            Assert.True(expected.AsSpan().SequenceEqual(actual), $"DC {dc} differs.");
        }
    }
}
