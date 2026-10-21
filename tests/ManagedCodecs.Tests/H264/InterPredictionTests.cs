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
            int x = (random.Next(0, 3) * 16) + (random.Next(0, 16 / w) * w);
            int y = (random.Next(0, 2) * 16) + (random.Next(0, 16 / h) * h);

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

    /// <summary>
    /// Field views (MBAFF field macroblocks): every other row of the frame, half its height, with
    /// the edges repeated within the field; vectors cross the field's top and bottom edges.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void FastFieldPrediction_MatchesReference(int parity)
    {
        var random = new Random(7 + parity);
        var frame = new Picture(3, 2);
        random.NextBytes(frame.Y);
        random.NextBytes(frame.Cb);
        random.NextBytes(frame.Cr);
        var field = frame.Field(parity);
        Span<byte> expected = stackalloc byte[16 * 16];
        Span<byte> actual = stackalloc byte[16 * 16];

        for (int n = 0; n < 4000; n++)
        {
            var (w, h) = BlockSizes[random.Next(BlockSizes.Length)];
            int x = (random.Next(0, 3) * 16) + (random.Next(0, 16 / w) * w);
            int y = random.Next(0, 16 / h) * h; // the field is 16 rows high
            int range = random.Next(4) == 0 ? 200 : 40;
            int mvX = random.Next(-range, range + 1);
            int mvY = random.Next(-range, range + 1);

            expected.Clear();
            actual.Clear();
            InterPrediction.PredictLumaReference(field, x, y, w, h, mvX, mvY, expected, 16);
            InterPrediction.PredictLuma(field, x, y, w, h, mvX, mvY, actual, 16);
            Assert.True(expected.SequenceEqual(actual), $"Field {parity} luma {w}x{h} at ({x},{y}) mv ({mvX},{mvY})");

            for (int c = 1; c <= 2; c++)
            {
                expected.Clear();
                actual.Clear();
                InterPrediction.PredictChromaReference(field, c, x / 2, y / 2, w / 2, h / 2, mvX, mvY, expected, 16);
                InterPrediction.PredictChroma(field, c, x / 2, y / 2, w / 2, h / 2, mvX, mvY, actual, 16);
                Assert.True(expected.SequenceEqual(actual), $"Field {parity} chroma {c} {w}x{h} at ({x},{y}) mv ({mvX},{mvY})");
            }
        }
    }

    /// <summary>A field view reads only its own rows: the other field's samples never reach it.</summary>
    [Fact]
    public void FieldView_ReadsOnlyItsRows()
    {
        var frame = new Picture(1, 2);
        for (int row = 0; row < 32; row++)
        {
            frame.Y.AsSpan(row * 16, 16).Fill((byte)(row % 2 == 0 ? 50 : 200));
        }

        Span<byte> output = stackalloc byte[16 * 16];
        for (int parity = 0; parity < 2; parity++)
        {
            InterPrediction.PredictLuma(frame.Field(parity), 0, 0, 16, 16, 6, -70, output, 16); // fractional, past the top
            Assert.All(output.ToArray(), sample => Assert.Equal(parity == 0 ? 50 : 200, sample));
        }
    }
}
