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
            Transform.InverseTransformAdd(block, expected.AsSpan(), Stride, bitDepth: 8);

            var actual = (byte[])prediction.Clone();
            Transform.AddDc(dc, actual.AsSpan(), Stride, bitDepth: 8);

            Assert.True(expected.AsSpan().SequenceEqual(actual), $"DC {dc} differs.");
        }
    }

    /// <summary>
    /// The vector 8x8 inverse transform against the scalar one, on random coefficients as large as
    /// dequantization gives at 10 bits and predictions near both ends of the range (clipping).
    /// </summary>
    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    public void InverseTransform8x8_VectorMatchesReference(int bitDepth)
    {
        if (!System.Runtime.Intrinsics.X86.Sse2.IsSupported)
        {
            return;
        }

        const int Stride = 11;
        var random = new Random(bitDepth);
        int max = (1 << bitDepth) - 1;
        for (int n = 0; n < 20_000; n++)
        {
            int range = random.Next(3) switch { 0 => 64, 1 => 4096, _ => 1 << 20 };
            int[] coefficients = [.. Enumerable.Range(0, 64).Select(_ => random.Next(4) == 0 ? random.Next(-range, range + 1) : 0)];
            int[] prediction = [.. Enumerable.Range(0, (7 * Stride) + 8).Select(_ => random.Next(3) == 0 ? random.Next(2) * max : random.Next(max + 1))];
            if (bitDepth == 8)
            {
                byte[] expected = [.. prediction.Select(p => (byte)p)];
                byte[] actual = (byte[])expected.Clone();
                Transform.InverseTransform8x8AddReference((int[])coefficients.Clone(), expected.AsSpan(), Stride, bitDepth);
                Transform.InverseTransform8x8AddVector((int[])coefficients.Clone(), actual.AsSpan(), Stride, bitDepth);
                Assert.True(expected.AsSpan().SequenceEqual(actual), $"Block {n} differs.");
            }
            else
            {
                ushort[] expected = [.. prediction.Select(p => (ushort)p)];
                ushort[] actual = (ushort[])expected.Clone();
                Transform.InverseTransform8x8AddReference((int[])coefficients.Clone(), expected.AsSpan(), Stride, bitDepth);
                Transform.InverseTransform8x8AddVector((int[])coefficients.Clone(), actual.AsSpan(), Stride, bitDepth);
                Assert.True(expected.AsSpan().SequenceEqual(actual), $"Block {n} differs.");
            }
        }
    }

    /// <summary>The vector 4x4 inverse transform against the scalar one, as for 8x8.</summary>
    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    public void InverseTransform4x4_VectorMatchesReference(int bitDepth)
    {
        if (!System.Runtime.Intrinsics.X86.Sse2.IsSupported)
        {
            return;
        }

        const int Stride = 9;
        var random = new Random(40 + bitDepth);
        int max = (1 << bitDepth) - 1;
        for (int n = 0; n < 20_000; n++)
        {
            int range = random.Next(3) switch { 0 => 64, 1 => 4096, _ => 1 << 20 };
            int[] coefficients = [.. Enumerable.Range(0, 16).Select(_ => random.Next(3) == 0 ? random.Next(-range, range + 1) : 0)];
            int[] prediction = [.. Enumerable.Range(0, (3 * Stride) + 4).Select(_ => random.Next(3) == 0 ? random.Next(2) * max : random.Next(max + 1))];
            if (bitDepth == 8)
            {
                byte[] expected = [.. prediction.Select(p => (byte)p)];
                byte[] actual = (byte[])expected.Clone();
                Transform.InverseTransformAddReference((int[])coefficients.Clone(), expected.AsSpan(), Stride, bitDepth);
                Transform.InverseTransformAddVector((int[])coefficients.Clone(), actual.AsSpan(), Stride, bitDepth);
                Assert.True(expected.AsSpan().SequenceEqual(actual), $"Block {n} differs.");
            }
            else
            {
                ushort[] expected = [.. prediction.Select(p => (ushort)p)];
                ushort[] actual = (ushort[])expected.Clone();
                Transform.InverseTransformAddReference((int[])coefficients.Clone(), expected.AsSpan(), Stride, bitDepth);
                Transform.InverseTransformAddVector((int[])coefficients.Clone(), actual.AsSpan(), Stride, bitDepth);
                Assert.True(expected.AsSpan().SequenceEqual(actual), $"Block {n} differs.");
            }
        }
    }
}
