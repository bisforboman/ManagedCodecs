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
            Transform.InverseTransformAddScalar(block, expected.AsSpan(), Stride, bitDepth: 8);

            var actual = (byte[])prediction.Clone();
            Transform.AddDc(dc, actual.AsSpan(), Stride, bitDepth: 8);

            Assert.True(expected.AsSpan().SequenceEqual(actual), $"DC {dc} differs.");
        }
    }

    /// <summary>
    /// The vector <see cref="Transform.Dequantize"/> against <see cref="Transform.DequantizeScalar"/>: random levels
    /// (large ones too), every QP, flat and random scaling lists, with and without the DC left alone.
    /// </summary>
    [Fact]
    public void Dequantize_MatchesScalar()
    {
        var random = new Random(53);
        var list = new byte[16];
        for (int n = 0; n < 20_000; n++)
        {
            if (n % 2 == 0)
            {
                list.AsSpan().Fill(16);
            }
            else
            {
                for (int i = 0; i < 16; i++)
                {
                    list[i] = (byte)random.Next(1, 256);
                }
            }

            var levelScale = Transform.LevelScale4x4Table(list);
            int qp = random.Next(0, 52);
            bool skipDc = random.Next(2) == 0;
            var levels = new int[16];
            for (int i = 0; i < 16; i++)
            {
                levels[i] = random.Next(4) == 0 ? random.Next(-2048, 2049) : random.Next(-8, 9);
            }

            var expected = (int[])levels.Clone();
            var actual = (int[])levels.Clone();
            Transform.DequantizeScalar(expected, qp, skipDc, levelScale);
            Transform.Dequantize(actual, qp, skipDc, levelScale);
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"Case {n}: QP {qp}, skipDc {skipDc}");
        }
    }

    /// <summary>
    /// The 8-bit vector add (with SSE2 both passes in 16-bit lanes for coefficients under 2^11, else the columns'
    /// transform four columns at a time) against the scalar transform, on random coefficients small and large (either
    /// side of that bound), and samples near both ends of the range.
    /// </summary>
    [Fact]
    public void InverseTransformAdd_MatchesScalar()
    {
        const int Stride = 9;
        var random = new Random(23);
        for (int n = 0; n < 20_000; n++)
        {
            int range = random.Next(4) switch { 0 => 64, 1 => 2047, 2 => 4096, _ => 1 << 20 };
            var coefficients = new int[16];
            for (int i = 0; i < 16; i++)
            {
                coefficients[i] = random.Next(4) == 0 ? 0 : random.Next(-range, range + 1);
            }

            var prediction = new byte[(3 * Stride) + 4];
            random.NextBytes(prediction);
            var expected = (byte[])prediction.Clone();
            Transform.InverseTransformAddScalar((int[])coefficients.Clone(), expected.AsSpan(), Stride, bitDepth: 8);
            var actual = (byte[])prediction.Clone();
            Transform.InverseTransformAdd((int[])coefficients.Clone(), actual.AsSpan(), Stride, bitDepth: 8);
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"Block {n} differs.");

            int dc = random.Next(-300_000, 300_001);
            expected = (byte[])prediction.Clone();
            Transform.AddDcScalar(dc, expected.AsSpan(), Stride, bitDepth: 8);
            actual = (byte[])prediction.Clone();
            Transform.AddDc(dc, actual.AsSpan(), Stride, bitDepth: 8);
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"DC {dc} differs.");
        }
    }

    /// <summary>The 8-bit 8x8 vector add against the scalar transform, on random coefficients and samples.</summary>
    [Fact]
    public void InverseTransform8x8Add_MatchesScalar()
    {
        const int Stride = 13;
        var random = new Random(29);
        for (int n = 0; n < 10_000; n++)
        {
            int range = random.Next(4) switch { 0 => 64, 1 => 2047, 2 => 4096, _ => 1 << 20 };
            var coefficients = new int[64];
            for (int i = 0; i < 64; i++)
            {
                coefficients[i] = random.Next(3) == 0 ? 0 : random.Next(-range, range + 1);
            }

            var prediction = new byte[(7 * Stride) + 8];
            random.NextBytes(prediction);
            var expected = (byte[])prediction.Clone();
            Transform.InverseTransform8x8AddScalar((int[])coefficients.Clone(), expected.AsSpan(), Stride, bitDepth: 8);
            var actual = (byte[])prediction.Clone();
            Transform.InverseTransform8x8Add((int[])coefficients.Clone(), actual.AsSpan(), Stride, bitDepth: 8);
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"Block {n} differs.");
        }
    }
}
