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

            bool chroma = random.Next(3) == 0;
            int length = chroma ? 8 : 16;
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
            PictureDecoder.FilterEdge(expected, q0, acrossStep, alongStep, length, strengths, qpP, qpQ, alphaOffset, betaOffset, chroma, vectorized: false);
            PictureDecoder.FilterEdge(actual, q0, acrossStep, alongStep, length, strengths, qpP, qpQ, alphaOffset, betaOffset, chroma, vectorized: true);

            if (!expected.AsSpan().SequenceEqual(plane))
            {
                filtered++;
            }

            Assert.True(expected.AsSpan().SequenceEqual(actual), $"Case {n}: chroma {chroma}, vertical {vertical}, bS [{string.Join(",", strengths)}], qp {qpP}/{qpQ}");
        }

        Assert.True(filtered > 5_000, $"Only {filtered} cases changed any sample; the test data is too rough.");
    }
}
