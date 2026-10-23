using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>The vectorized deblocking filter must match the sample-by-sample one exactly.</summary>
public class DeblockingTests
{
    [Fact]
    public void VectorFilter_MatchesScalar()
    {
        const int Stride = 32;
        var random = new Random(7);
        var strengths = new byte[4];
        int filtered = 0;

        for (int n = 0; n < 20_000; n++)
        {
            // A smooth ramp with small noise and a step across the edge, so that the
            // |p0 - q0| < alpha and |p1 - p0| < beta conditions often pass; sometimes pinned
            // near 0 or 255 to exercise the clipping.
            var plane = new byte[Stride * Stride];
            int baseValue = random.Next(4) switch { 0 => 3, 1 => 252, _ => random.Next(256) };
            int step = random.Next(-12, 13);
            int noise = random.Next(1, 6);
            bool vertical = random.Next(2) == 0;
            for (int y = 0; y < Stride; y++)
            {
                for (int x = 0; x < Stride; x++)
                {
                    int across = vertical ? x : y;
                    int value = baseValue + (across >= 16 ? step : 0) + random.Next(-noise, noise + 1);
                    plane[(y * Stride) + x] = (byte)Math.Clamp(value, 0, 255);
                }
            }

            // Chroma edges of 8 samples, or of 16 with four to a luma segment (vertical edges of 4:2:2).
            bool chroma = random.Next(3) == 0;
            bool tallChroma = chroma && random.Next(2) == 0;
            int length = chroma && !tallChroma ? 8 : 16;
            int segmentShift = tallChroma ? 2 : 0;
            for (int i = 0; i < 4; i++)
            {
                strengths[i] = (byte)random.Next(0, 5);
            }

            int qpP = random.Next(0, 52);
            int qpQ = random.Next(0, 52);
            int alphaOffset = random.Next(-6, 7) * 2;
            int betaOffset = random.Next(-6, 7) * 2;

            // q0 of the first position: column 16 (vertical edge) or row 16 (horizontal edge).
            int q0 = vertical ? (8 * Stride) + 16 : (16 * Stride) + 8;
            int acrossStep = vertical ? 1 : Stride;
            int alongStep = vertical ? Stride : 1;

            var expected = (byte[])plane.Clone();
            var actual = (byte[])plane.Clone();
            PictureDecoder<byte>.FilterEdge(expected, q0, acrossStep, alongStep, length, strengths, qpP, qpQ, alphaOffset, betaOffset, chroma, vectorized: false, segmentShift);
            PictureDecoder<byte>.FilterEdge(actual, q0, acrossStep, alongStep, length, strengths, qpP, qpQ, alphaOffset, betaOffset, chroma, vectorized: true, segmentShift);

            if (!expected.AsSpan().SequenceEqual(plane))
            {
                filtered++;
            }

            Assert.True(expected.AsSpan().SequenceEqual(actual), $"Case {n}: chroma {chroma} ({length}), vertical {vertical}, bS [{string.Join(",", strengths)}], qp {qpP}/{qpQ}");
        }

        Assert.True(filtered > 5_000, $"Only {filtered} cases changed any sample; the test data is too rough.");
    }

    /// <summary>
    /// The 16-bit vector filter (10-bit samples) against the sample-by-sample one, on the same
    /// kind of edges as <see cref="VectorFilter_MatchesScalar"/> scaled to 9, 10 and 12 bits, where alpha,
    /// beta and tC0 are 2, 4 and 16 times as large.
    /// </summary>
    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(12)] // the deepest the 16-bit lanes take: the strong filter's sums reach 8 * 4095 + 4
    public void VectorFilter16_MatchesScalar(int bitDepth)
    {
        const int Stride = 32;
        var random = new Random(17);
        int scale = 1 << (bitDepth - 8);
        int max = (1 << bitDepth) - 1;
        var strengths = new byte[4];
        int filtered = 0;

        for (int n = 0; n < 20_000; n++)
        {
            var plane = new ushort[Stride * Stride];
            int baseValue = random.Next(4) switch { 0 => 3 * scale, 1 => max - (3 * scale), _ => random.Next(max + 1) };
            int step = random.Next(-12 * scale, (12 * scale) + 1);
            int noise = random.Next(1, (5 * scale) + 1);
            bool vertical = random.Next(2) == 0;
            for (int y = 0; y < Stride; y++)
            {
                for (int x = 0; x < Stride; x++)
                {
                    int across = vertical ? x : y;
                    int value = baseValue + (across >= 16 ? step : 0) + random.Next(-noise, noise + 1);
                    plane[(y * Stride) + x] = (ushort)Math.Clamp(value, 0, max);
                }
            }

            bool chroma = random.Next(3) == 0;
            bool tallChroma = chroma && random.Next(2) == 0;
            int length = chroma && !tallChroma ? 8 : 16;
            int segmentShift = tallChroma ? 2 : 0;
            for (int i = 0; i < 4; i++)
            {
                strengths[i] = (byte)random.Next(0, 5);
            }

            int qpP = random.Next(-6 * (bitDepth - 8), 52);
            int qpQ = random.Next(-6 * (bitDepth - 8), 52);
            int alphaOffset = random.Next(-6, 7) * 2;
            int betaOffset = random.Next(-6, 7) * 2;
            int q0 = vertical ? (8 * Stride) + 16 : (16 * Stride) + 8;
            int acrossStep = vertical ? 1 : Stride;
            int alongStep = vertical ? Stride : 1;

            var expected = (ushort[])plane.Clone();
            var actual = (ushort[])plane.Clone();
            PictureDecoder<ushort>.FilterEdge(expected, q0, acrossStep, alongStep, length, strengths, qpP, qpQ, alphaOffset, betaOffset, chroma, vectorized: false, segmentShift, bitDepth);
            PictureDecoder<ushort>.FilterEdge(actual, q0, acrossStep, alongStep, length, strengths, qpP, qpQ, alphaOffset, betaOffset, chroma, vectorized: true, segmentShift, bitDepth);

            if (!expected.AsSpan().SequenceEqual(plane))
            {
                filtered++;
            }

            Assert.True(expected.AsSpan().SequenceEqual(actual), $"Case {n}: chroma {chroma} ({length}), vertical {vertical}, bS [{string.Join(",", strengths)}], qp {qpP}/{qpQ}");
        }

        Assert.True(filtered > 5_000, $"Only {filtered} cases changed any sample; the test data is too rough.");
    }

    /// <summary>
    /// Cb and Cr filtered together (lanes 0-7 and 8-15) must match the sample-by-sample filter on
    /// each plane, including different QPs for the two (second_chroma_qp_index_offset).
    /// </summary>
    [Fact]
    public void ChromaPair_MatchesScalarOnEachPlane()
    {
        const int Stride = 32;
        var random = new Random(11);
        var strengths = new byte[4];
        int filtered = 0;

        for (int n = 0; n < 20_000; n++)
        {
            bool vertical = random.Next(2) == 0;
            var cb = Plane(random, Stride, vertical);
            var cr = Plane(random, Stride, vertical);
            for (int i = 0; i < 4; i++)
            {
                strengths[i] = (byte)random.Next(0, 5);
            }

            int qpPCb = random.Next(0, 52);
            int qpQCb = random.Next(0, 52);
            int qpPCr = random.Next(4) == 0 ? qpPCb : random.Next(0, 52);
            int qpQCr = random.Next(4) == 0 ? qpQCb : random.Next(0, 52);
            int alphaOffset = random.Next(-6, 7) * 2;
            int betaOffset = random.Next(-6, 7) * 2;
            int q0 = vertical ? (8 * Stride) + 16 : (16 * Stride) + 8;
            int acrossStep = vertical ? 1 : Stride;
            int alongStep = vertical ? Stride : 1;

            var expectedCb = (byte[])cb.Clone();
            var expectedCr = (byte[])cr.Clone();
            PictureDecoder<byte>.FilterEdge(expectedCb, q0, acrossStep, alongStep, 8, strengths, qpPCb, qpQCb, alphaOffset, betaOffset, chroma: true, vectorized: false);
            PictureDecoder<byte>.FilterEdge(expectedCr, q0, acrossStep, alongStep, 8, strengths, qpPCr, qpQCr, alphaOffset, betaOffset, chroma: true, vectorized: false);
            var actualCb = (byte[])cb.Clone();
            var actualCr = (byte[])cr.Clone();
            PictureDecoder<byte>.FilterChromaEdge(actualCb, actualCr, q0, acrossStep, alongStep, strengths, qpPCb, qpQCb, qpPCr, qpQCr, alphaOffset, betaOffset);

            if (!expectedCb.AsSpan().SequenceEqual(cb) || !expectedCr.AsSpan().SequenceEqual(cr))
            {
                filtered++;
            }

            string context = $"Case {n}: vertical {vertical}, bS [{string.Join(",", strengths)}], qp Cb {qpPCb}/{qpQCb}, Cr {qpPCr}/{qpQCr}";
            Assert.True(expectedCb.AsSpan().SequenceEqual(actualCb), "Cb. " + context);
            Assert.True(expectedCr.AsSpan().SequenceEqual(actualCr), "Cr. " + context);
        }

        Assert.True(filtered > 5_000, $"Only {filtered} cases changed any sample; the test data is too rough.");
    }

    /// <summary>A ramp with a step across the edge at 16 and small noise, as in <see cref="VectorFilter_MatchesScalar"/>.</summary>
    private static byte[] Plane(Random random, int stride, bool vertical)
    {
        var plane = new byte[stride * stride];
        int baseValue = random.Next(4) switch { 0 => 3, 1 => 252, _ => random.Next(256) };
        int step = random.Next(-12, 13);
        int noise = random.Next(1, 6);
        for (int y = 0; y < stride; y++)
        {
            for (int x = 0; x < stride; x++)
            {
                int across = vertical ? x : y;
                int value = baseValue + (across >= 16 ? step : 0) + random.Next(-noise, noise + 1);
                plane[(y * stride) + x] = (byte)Math.Clamp(value, 0, 255);
            }
        }

        return plane;
    }
}
