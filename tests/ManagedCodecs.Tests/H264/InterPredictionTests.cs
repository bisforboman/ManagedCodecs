using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>The row-based interpolation must match the per-sample reference for every input.</summary>
public class InterPredictionTests
{
    private static readonly (int W, int H)[] BlockSizes = [(16, 16), (16, 8), (8, 16), (8, 8), (8, 4), (4, 8), (4, 4)];

    /// <summary>
    /// In 4:2:0 (<paramref name="mbHeightC"/> 8) and 4:2:2 (16), whose chroma blocks have the luma
    /// block's height and a vertical vector of twice the luma one in eighth samples.
    /// </summary>
    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void FastLumaAndChroma_MatchReference(int mbHeightC)
    {
        var random = new Random(42 + mbHeightC);
        var reference = new Picture(3, 2, mbHeightC); // 48x32 luma, small so many vectors cross the edges
        random.NextBytes(reference.Y);
        random.NextBytes(reference.Cb);
        random.NextBytes(reference.Cr);

        for (int n = 0; n < 4000; n++)
        {
            var (w, h) = BlockSizes[random.Next(BlockSizes.Length)];
            int x = (random.Next(0, 3) * 16) + (random.Next(0, 16 / w) * w);
            int y = (random.Next(0, 2) * 16) + (random.Next(0, 16 / h) * h);

            // Mostly small vectors, sometimes far outside the picture; every fourth one full sample
            // in both luma and chroma (a multiple of 8), which the fast path copies directly.
            int range = random.Next(4) == 0 ? 400 : 40;
            int mvX = random.Next(-range, range + 1);
            int mvY = random.Next(-range, range + 1);
            if (n % 4 == 0)
            {
                (mvX, mvY) = (mvX & ~7, mvY & ~7);
            }

            var expected = new Picture(3, 2, mbHeightC);
            var actual = new Picture(3, 2, mbHeightC);
            InterPrediction.PredictLumaReference(reference, expected.Y, expected.LumaStride, x, y, w, h, mvX, mvY);
            InterPrediction.PredictLuma(reference, actual.Y, actual.LumaStride, x, y, w, h, mvX, mvY);
            Assert.True(expected.Y.AsSpan().SequenceEqual(actual.Y), $"Luma {w}x{h} at ({x},{y}) mv ({mvX},{mvY})");

            bool tall = mbHeightC == 16;
            var (cy, ch, cmvY) = tall ? (y, h, mvY * 2) : (y / 2, h / 2, mvY);
            for (int c = 1; c <= 2; c++)
            {
                InterPrediction.PredictChromaReference(reference, c, expected.Plane(c), expected.ChromaStride, x / 2, cy, w / 2, ch, mvX, cmvY);
                InterPrediction.PredictChroma(reference, c, actual.Plane(c), actual.ChromaStride, x / 2, cy, w / 2, ch, mvX, cmvY);
                Assert.True(expected.Plane(c).AsSpan().SequenceEqual(actual.Plane(c)), $"Chroma {c} {w}x{h} at ({x},{y}) mv ({mvX},{mvY})");
            }
        }
    }

    /// <summary>
    /// The 16-bit paths (<see cref="InterPrediction.PredictLuma16"/>, <see cref="InterPrediction.PredictChroma16"/>)
    /// against the references on random 10-bit samples, in frames and field views, 4:2:0 and 4:2:2.
    /// </summary>
    [Theory]
    [InlineData(8, -1)]
    [InlineData(16, -1)]
    [InlineData(8, 0)]
    [InlineData(16, 1)]
    public void Fast16_MatchesReference(int mbHeightC, int parity)
    {
        var random = new Random(11 + mbHeightC + parity);
        var frame = new Picture(3, 2, mbHeightC, bitDepth: 10);
        for (int c = 0; c < 3; c++)
        {
            var plane = frame.Plane<ushort>(c);
            for (int i = 0; i < plane.Length; i++)
            {
                // Mostly anywhere in 0 .. 1023, sometimes pinned to the ends, so the clipping is exercised.
                plane[i] = (ushort)(random.Next(8) switch { 0 => 0, 1 => 1023, _ => random.Next(1024) });
            }
        }

        var reference = parity < 0 ? frame : frame.Field(parity);
        int pictureHeight = parity < 0 ? 32 : 16;
        Span<ushort> expected = stackalloc ushort[16 * 16];
        Span<ushort> actual = stackalloc ushort[16 * 16];
        for (int n = 0; n < 4000; n++)
        {
            var (w, h) = BlockSizes[random.Next(BlockSizes.Length)];
            int x = (random.Next(0, 3) * 16) + (random.Next(0, 16 / w) * w);
            int y = (random.Next(0, pictureHeight / 16) * 16) + (random.Next(0, 16 / h) * h);
            int range = random.Next(4) == 0 ? 400 : 40;
            int mvX = random.Next(-range, range + 1);
            int mvY = random.Next(-range, range + 1);

            expected.Clear();
            actual.Clear();
            InterPrediction.PredictLumaReference(reference, x, y, w, h, mvX, mvY, expected, 16);
            InterPrediction.PredictLuma16(reference, x, y, w, h, mvX, mvY, actual, 16);
            Assert.True(expected.SequenceEqual(actual), $"Luma {w}x{h} at ({x},{y}) mv ({mvX},{mvY})");

            bool tall = mbHeightC == 16;
            var (cy, ch, cmvY) = tall ? (y, h, mvY * 2) : (y / 2, h / 2, mvY);
            for (int c = 1; c <= 2; c++)
            {
                expected.Clear();
                actual.Clear();
                InterPrediction.PredictChromaReference(reference, c, x / 2, cy, w / 2, ch, mvX, cmvY, expected, 16);
                InterPrediction.PredictChroma16(reference, c, x / 2, cy, w / 2, ch, mvX, cmvY, actual, 16);
                Assert.True(expected.SequenceEqual(actual), $"Chroma {c} {w}x{h} at ({x},{y}) mv ({mvX},{cmvY})");
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

    /// <summary>
    /// The 8-bit vector weighted prediction (<see cref="InterPrediction.WeightOne"/>, <see cref="InterPrediction.WeightBoth"/>)
    /// against the definition, on random samples, weights and offsets over their whole ranges (explicit weights -128 to
    /// 127, implicit -64 to 128, denominators 0 to 7), every block width, and the extremes.
    /// </summary>
    [Fact]
    public void VectorWeights_MatchReference()
    {
        var random = new Random(7);
        var p0 = new byte[16 * 16];
        var p1 = new byte[16 * 16];
        var expected = new byte[16 * 20];
        var actual = new byte[16 * 20];
        for (int n = 0; n < 20000; n++)
        {
            bool extreme = n % 10 == 0;
            for (int i = 0; i < p0.Length; i++)
            {
                p0[i] = extreme ? (byte)(random.Next(2) * 255) : (byte)random.Next(256);
                p1[i] = extreme ? (byte)(random.Next(2) * 255) : (byte)random.Next(256);
            }

            int width = new[] { 2, 4, 8, 16 }[random.Next(4)];
            int height = new[] { 2, 4, 8, 16 }[random.Next(4)];
            int logWd = random.Next(8);
            int w0 = extreme ? (random.Next(2) == 0 ? -128 : 127) : random.Next(-128, 128);
            int w1 = extreme ? (random.Next(2) == 0 ? -64 : 128) : random.Next(-128, 129);
            int o = extreme ? (random.Next(2) == 0 ? -128 : 127) : random.Next(-128, 128);
            Array.Clear(expected);
            Array.Clear(actual);

            InterPrediction.WeightOneReference<byte>(p0, 16, width, height, expected, 20, w0, o, logWd, 255);
            InterPrediction.WeightOne(p0, 16, width, height, actual, 20, w0, o, logWd);
            Assert.Equal(expected, actual);

            InterPrediction.WeightBothReference<byte>(p0, p1, 16, width, height, expected, 20, w0, w1, o, logWd, 255);
            InterPrediction.WeightBoth(p0, p1, 16, width, height, actual, 20, w0, w1, o, logWd);
            Assert.Equal(expected, actual);
        }
    }
}
