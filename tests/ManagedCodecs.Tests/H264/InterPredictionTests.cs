using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>The row-based interpolation must match the per-sample reference for every input.</summary>
public class InterPredictionTests
{
    private static readonly (int W, int H)[] BlockSizes = [(16, 16), (16, 8), (8, 16), (8, 8), (8, 4), (4, 8), (4, 4)];

    [Fact]
    public void FastLumaAndChroma_MatchReference()
    {
        var random = new Random(42);
        var reference = new Picture(3, 2); // 48x32 luma, small so many vectors cross the edges
        random.NextBytes(reference.Y);
        random.NextBytes(reference.Cb);
        random.NextBytes(reference.Cr);

        for (int n = 0; n < 4000; n++)
        {
            var (w, h) = BlockSizes[random.Next(BlockSizes.Length)];
            int x = random.Next(0, 3) * 16 + (random.Next(0, 16 / w) * w);
            int y = random.Next(0, 2) * 16 + (random.Next(0, 16 / h) * h);

            // Mostly small vectors, sometimes far outside the picture.
            int range = random.Next(4) == 0 ? 400 : 40;
            int mvX = random.Next(-range, range + 1);
            int mvY = random.Next(-range, range + 1);

            var expected = new Picture(3, 2);
            var actual = new Picture(3, 2);
            InterPrediction.PredictLumaReference(reference, expected.Y, expected.LumaStride, x, y, w, h, mvX, mvY);
            InterPrediction.PredictLuma(reference, actual.Y, actual.LumaStride, x, y, w, h, mvX, mvY);
            Assert.True(expected.Y.AsSpan().SequenceEqual(actual.Y), $"Luma {w}x{h} at ({x},{y}) mv ({mvX},{mvY})");

            for (int c = 1; c <= 2; c++)
            {
                InterPrediction.PredictChromaReference(reference, c, expected.Plane(c), expected.ChromaStride, x / 2, y / 2, w / 2, h / 2, mvX, mvY);
                InterPrediction.PredictChroma(reference, c, actual.Plane(c), actual.ChromaStride, x / 2, y / 2, w / 2, h / 2, mvX, mvY);
                Assert.True(expected.Plane(c).AsSpan().SequenceEqual(actual.Plane(c)), $"Chroma {c} {w}x{h} at ({x},{y}) mv ({mvX},{mvY})");
            }
        }
    }
}
