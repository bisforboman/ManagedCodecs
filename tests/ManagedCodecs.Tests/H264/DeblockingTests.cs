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

    /// <summary>
    /// A whole 8-bit 4:2:0 macroblock filtered at once (its parameters worked out once, the vector filters called
    /// directly) must match its edges filtered one by one with the sample-by-sample filter, in the same order: on
    /// random blocky planes, strengths (with and without the 8x8 transform's internal edges), QPs and offsets.
    /// </summary>
    [Fact]
    public void Macroblock420_MatchesEdgeByEdge()
    {
        const int LumaStride = 48;
        const int ChromaStride = 24;
        var random = new Random(29);
        var vertical = new byte[16];
        var horizontal = new byte[16];
        int filtered = 0;

        for (int n = 0; n < 5_000; n++)
        {
            var y = Blocks(random, LumaStride, 4);
            var cb = Blocks(random, ChromaStride, 2);
            var cr = Blocks(random, ChromaStride, 2);
            bool transform8x8 = random.Next(3) == 0;
            bool intra = random.Next(4) == 0;
            for (int i = 0; i < 16; i++)
            {
                int strength = i < 4 ? random.Next(0, 5) : intra ? 3 : random.Next(0, 3);
                vertical[i] = (byte)(random.Next(4) == 0 ? 0 : strength);
                horizontal[i] = (byte)(random.Next(4) == 0 ? 0 : (i < 4 ? random.Next(0, 5) : intra ? 3 : random.Next(0, 3)));
            }

            if (transform8x8)
            {
                vertical.AsSpan(4, 4).Clear();
                vertical.AsSpan(12, 4).Clear();
                horizontal.AsSpan(4, 4).Clear();
                horizontal.AsSpan(12, 4).Clear();
            }

            int qp = random.Next(0, 52);
            int qpLeft = random.Next(2) == 0 ? qp : random.Next(0, 52);
            int qpAbove = random.Next(2) == 0 ? qp : random.Next(0, 52);
            var slice = new SliceInfo(0, random.Next(-6, 7) * 2, random.Next(-6, 7) * 2, random.Next(-12, 13), random.Next(4) == 0 ? random.Next(-12, 13) : 0);
            const int LumaOrigin = (16 * LumaStride) + 16;
            const int ChromaOrigin = (8 * ChromaStride) + 8;

            var expectedY = (byte[])y.Clone();
            var expectedCb = (byte[])cb.Clone();
            var expectedCr = (byte[])cr.Clone();
            for (int edge = 0; edge < 4; edge++)
            {
                PictureDecoder<byte>.FilterEdge(expectedY, LumaOrigin + (edge * 4), 1, LumaStride, 16, vertical.AsSpan(edge * 4, 4), edge == 0 ? qpLeft : qp, qp, slice.AlphaOffset, slice.BetaOffset, chroma: false, vectorized: false);
            }

            for (int edge = 0; edge < 4; edge++)
            {
                PictureDecoder<byte>.FilterEdge(expectedY, LumaOrigin + (edge * 4 * LumaStride), LumaStride, 1, 16, horizontal.AsSpan(edge * 4, 4), edge == 0 ? qpAbove : qp, qp, slice.AlphaOffset, slice.BetaOffset, chroma: false, vectorized: false);
            }

            foreach (var (plane, offset) in new[] { (expectedCb, slice.CbQpOffset), (expectedCr, slice.CrQpOffset) })
            {
                int qpC = PictureDecoder<byte>.ChromaQp(qp, offset);
                for (int edge = 0; edge < 2; edge++)
                {
                    PictureDecoder<byte>.FilterEdge(plane, ChromaOrigin + (edge * 4), 1, ChromaStride, 8, vertical.AsSpan(edge * 8, 4), PictureDecoder<byte>.ChromaQp(edge == 0 ? qpLeft : qp, offset), qpC, slice.AlphaOffset, slice.BetaOffset, chroma: true, vectorized: false);
                }

                for (int edge = 0; edge < 2; edge++)
                {
                    PictureDecoder<byte>.FilterEdge(plane, ChromaOrigin + (edge * 4 * ChromaStride), ChromaStride, 1, 8, horizontal.AsSpan(edge * 8, 4), PictureDecoder<byte>.ChromaQp(edge == 0 ? qpAbove : qp, offset), qpC, slice.AlphaOffset, slice.BetaOffset, chroma: true, vectorized: false);
                }
            }

            var actualY = (byte[])y.Clone();
            var actualCb = (byte[])cb.Clone();
            var actualCr = (byte[])cr.Clone();
            PictureDecoder<byte>.FilterMacroblock420(actualY, actualCb, actualCr, LumaOrigin, LumaStride, ChromaOrigin, ChromaStride, vertical, horizontal, qpLeft, qpAbove, qp, slice);

            if (!expectedY.AsSpan().SequenceEqual(y) && !expectedCb.AsSpan().SequenceEqual(cb))
            {
                filtered++;
            }

            string context = $"Case {n}: bS [{string.Join(",", vertical)}] / [{string.Join(",", horizontal)}], qp {qpLeft}/{qpAbove}/{qp}";
            Assert.True(expectedY.AsSpan().SequenceEqual(actualY), "Y. " + context);
            Assert.True(expectedCb.AsSpan().SequenceEqual(actualCb), "Cb. " + context);
            Assert.True(expectedCr.AsSpan().SequenceEqual(actualCr), "Cr. " + context);
        }

        Assert.True(filtered > 2_000, $"Only {filtered} cases changed both luma and chroma; the test data is too rough.");
    }

    /// <summary>
    /// The internal edge strengths of a macroblock with motion from one list, in vectors, against 8.7.2.1 block pair by
    /// block pair: random motion vectors near each other (so both sides of the 4 and limitY thresholds come up),
    /// coefficients, the partitions' reference pictures, frame and field macroblocks (limitY 4 and 2).
    /// </summary>
    [Fact]
    public void OneListStrengths_MatchTheDefinition()
    {
        var random = new Random(31);
        var mvX = new short[16];
        var mvY = new short[16];
        var coefficients = new byte[16];
        var refs = new int[4];
        var vertical = new byte[16];
        var horizontal = new byte[16];
        var expectedVertical = new byte[16];
        var expectedHorizontal = new byte[16];
        for (int n = 0; n < 20_000; n++)
        {
            int baseX = random.Next(-2000, 2000);
            int baseY = random.Next(-2000, 2000);
            int spread = random.Next(1, 8);
            for (int i = 0; i < 16; i++)
            {
                mvX[i] = (short)(baseX + random.Next(-spread, spread + 1));
                mvY[i] = (short)(baseY + random.Next(-spread, spread + 1));
                coefficients[i] = (byte)(random.Next(3) == 0 ? random.Next(1, 17) : 0);
            }

            for (int i = 0; i < 4; i++)
            {
                refs[i] = random.Next(2);
            }

            int limitY = random.Next(2) == 0 ? 4 : 2;
            random.NextBytes(vertical);
            random.NextBytes(horizontal);
            vertical.AsSpan(0, 4).CopyTo(expectedVertical);
            horizontal.AsSpan(0, 4).CopyTo(expectedHorizontal);
            for (int edge = 1; edge < 4; edge++)
            {
                for (int k = 0; k < 4; k++)
                {
                    expectedVertical[(edge * 4) + k] = Strength((k * 4) + edge - 1, (k * 4) + edge);
                    expectedHorizontal[(edge * 4) + k] = Strength(((edge - 1) * 4) + k, (edge * 4) + k);
                }
            }

            int differ = (refs[0] != refs[1] ? 1 : 0) | (refs[2] != refs[3] ? 2 : 0) | (refs[0] != refs[2] ? 4 : 0) | (refs[1] != refs[3] ? 8 : 0);
            PictureDecoder<byte>.OneListStrengths(mvX, mvY, coefficients, differ, limitY, vertical, horizontal);
            Assert.Equal(expectedVertical, vertical);
            Assert.Equal(expectedHorizontal, horizontal);

            byte Strength(int p, int q)
            {
                if (coefficients[p] != 0 || coefficients[q] != 0)
                {
                    return 2;
                }

                int partP = ((p >> 3) * 2) + ((p & 3) >> 1);
                int partQ = ((q >> 3) * 2) + ((q & 3) >> 1);
                return (byte)(refs[partP] != refs[partQ] || Math.Abs(mvX[p] - mvX[q]) >= 4 || Math.Abs(mvY[p] - mvY[q]) >= limitY ? 1 : 0);
            }
        }
    }

    /// <summary>
    /// The internal edge strengths of a macroblock with motion from either list or both, in vectors, against 8.7.2.1
    /// block pair by block pair: each partition's reference pictures drawn from two pictures or none in each list (so one
    /// and two vectors, the same picture twice, and swapped lists all come up), random motion vectors near each other,
    /// coefficients, and frame and field limits.
    /// </summary>
    [Fact]
    public void TwoListStrengths_MatchTheDefinition()
    {
        var random = new Random(37);
        byte[] pictures = [1, 2];
        var mvX0 = new short[16];
        var mvY0 = new short[16];
        var mvX1 = new short[16];
        var mvY1 = new short[16];
        var coefficients = new byte[16];
        var refs0 = new byte[4];
        var refs1 = new byte[4];
        var vertical = new byte[16];
        var horizontal = new byte[16];
        var expectedVertical = new byte[16];
        var expectedHorizontal = new byte[16];
        int limitY = 4;
        for (int n = 0; n < 50_000; n++)
        {
            int spread = random.Next(1, 8);
            int baseX = random.Next(-2000, 2000);
            int baseY = random.Next(-2000, 2000);
            for (int i = 0; i < 16; i++)
            {
                mvX0[i] = (short)(baseX + random.Next(-spread, spread + 1));
                mvY0[i] = (short)(baseY + random.Next(-spread, spread + 1));
                mvX1[i] = (short)(baseX + random.Next(-spread, spread + 1));
                mvY1[i] = (short)(baseY + random.Next(-spread, spread + 1));
                coefficients[i] = (byte)(random.Next(4) == 0 ? 1 : 0);
            }

            // Mostly one motion per partition, as partitions predict (vectors within an 8x8 partition still differ with
            // 8x8 partitions split further).
            for (int k = 0; k < 4; k++)
            {
                do
                {
                    refs0[k] = random.Next(3) == 0 ? (byte)0 : pictures[random.Next(2)];
                    refs1[k] = random.Next(3) == 0 ? (byte)0 : pictures[random.Next(2)];
                }
                while (refs0[k] == 0 && refs1[k] == 0 && random.Next(8) != 0);
            }

            limitY = random.Next(2) == 0 ? 4 : 2;
            random.NextBytes(vertical);
            random.NextBytes(horizontal);
            vertical.AsSpan(0, 4).CopyTo(expectedVertical);
            horizontal.AsSpan(0, 4).CopyTo(expectedHorizontal);
            for (int edge = 1; edge < 4; edge++)
            {
                for (int k = 0; k < 4; k++)
                {
                    expectedVertical[(edge * 4) + k] = Strength((k * 4) + edge - 1, (k * 4) + edge);
                    expectedHorizontal[(edge * 4) + k] = Strength(((edge - 1) * 4) + k, (edge * 4) + k);
                }
            }

            PictureDecoder<byte>.TwoListStrengths(mvX0, mvY0, mvX1, mvY1, coefficients, refs0, refs1, limitY, vertical, horizontal);
            Assert.True(expectedVertical.AsSpan().SequenceEqual(vertical), $"Case {n}: vertical [{string.Join(",", vertical)}], expected [{string.Join(",", expectedVertical)}]");
            Assert.True(expectedHorizontal.AsSpan().SequenceEqual(horizontal), $"Case {n}: horizontal [{string.Join(",", horizontal)}], expected [{string.Join(",", expectedHorizontal)}]");
        }

        // 8.7.2.1 for blocks p and q of one macroblock, neither intra.
        byte Strength(int p, int q)
        {
            if (coefficients[p] != 0 || coefficients[q] != 0)
            {
                return 2;
            }

            int partP = ((p >> 3) * 2) + ((p & 3) >> 1);
            int partQ = ((q >> 3) * 2) + ((q & 3) >> 1);
            var (p0, p1, q0, q1) = (refs0[partP], refs1[partP], refs0[partQ], refs1[partQ]);
            bool Far(short[] xa, short[] ya, int a, short[] xb, short[] yb, int b) => Math.Abs(xa[a] - xb[b]) >= 4 || Math.Abs(ya[a] - yb[b]) >= limitY;

            // Different reference pictures, or a different number of motion vectors.
            int countP = (p0 == 0 ? 0 : 1) + (p1 == 0 ? 0 : 1);
            int countQ = (q0 == 0 ? 0 : 1) + (q1 == 0 ? 0 : 1);
            var picturesP = new[] { p0, p1 }.Where(r => r != 0).OrderBy(r => Array.IndexOf(pictures, r)).ToArray();
            var picturesQ = new[] { q0, q1 }.Where(r => r != 0).OrderBy(r => Array.IndexOf(pictures, r)).ToArray();
            if (countP != countQ || !picturesP.SequenceEqual(picturesQ))
            {
                return 1;
            }

            // The vectors that refer to the same picture, in some pairing.
            var vectorsP = new List<(short[] X, short[] Y)>();
            var vectorsQ = new List<(short[] X, short[] Y)>();
            if (p0 != 0)
            {
                vectorsP.Add((mvX0, mvY0));
            }

            if (p1 != 0)
            {
                vectorsP.Add((mvX1, mvY1));
            }

            if (q0 != 0)
            {
                vectorsQ.Add((mvX0, mvY0));
            }

            if (q1 != 0)
            {
                vectorsQ.Add((mvX1, mvY1));
            }

            var listsP = new[] { p0, p1 }.Where(r => r != 0).ToArray();
            var listsQ = new[] { q0, q1 }.Where(r => r != 0).ToArray();
            if (countP == 0)
            {
                return (byte)(Far(mvX0, mvY0, p, mvX0, mvY0, q) ? 1 : 0);
            }

            if (countP == 1)
            {
                return (byte)(Far(vectorsP[0].X, vectorsP[0].Y, p, vectorsQ[0].X, vectorsQ[0].Y, q) ? 1 : 0);
            }

            bool straight = listsP[0] == listsQ[0] && listsP[1] == listsQ[1] && !Far(vectorsP[0].X, vectorsP[0].Y, p, vectorsQ[0].X, vectorsQ[0].Y, q) && !Far(vectorsP[1].X, vectorsP[1].Y, p, vectorsQ[1].X, vectorsQ[1].Y, q);
            bool swapped = listsP[0] == listsQ[1] && listsP[1] == listsQ[0] && !Far(vectorsP[0].X, vectorsP[0].Y, p, vectorsQ[1].X, vectorsQ[1].Y, q) && !Far(vectorsP[1].X, vectorsP[1].Y, p, vectorsQ[0].X, vectorsQ[0].Y, q);
            return (byte)(straight || swapped ? 0 : 1);
        }
    }

    /// <summary>A plane of flat blocks (<paramref name="block"/> samples a side) a few levels apart, with small noise.</summary>
    private static byte[] Blocks(Random random, int stride, int block)
    {
        var plane = new byte[stride * stride];
        int baseValue = random.Next(4) switch { 0 => 3, 1 => 252, _ => random.Next(256) };
        int spread = random.Next(1, 16);
        int noise = random.Next(0, 4);
        var levels = new int[stride / block, stride / block];
        for (int by = 0; by < stride / block; by++)
        {
            for (int bx = 0; bx < stride / block; bx++)
            {
                levels[by, bx] = baseValue + random.Next(-spread, spread + 1);
            }
        }

        for (int y = 0; y < stride; y++)
        {
            for (int x = 0; x < stride; x++)
            {
                plane[(y * stride) + x] = (byte)Math.Clamp(levels[y / block, x / block] + random.Next(-noise, noise + 1), 0, 255);
            }
        }

        return plane;
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

    /// <summary>
    /// The Vector128 transposes of vertical edges (what WebAssembly runs; x64 and ARM64 take their own interleaves)
    /// against their definition on random planes: tap k, lane i is byte k of row i (rows 0-7 from the top start, 8-15
    /// from the bottom one), and writing the taps back changes those 8 bytes of 16 rows and nothing else.
    /// </summary>
    [Fact]
    public void VectorTransposes_MatchTheirDefinition()
    {
        const int Stride = 40;
        var random = new Random(23);
        for (int n = 0; n < 2_000; n++)
        {
            var plane = new byte[Stride * 40];
            random.NextBytes(plane);
            int top = random.Next(Stride - 8) + (random.Next(8) * Stride);
            int bottom = random.Next(2) == 0 ? top : top + (8 * Stride); // chroma: the same 8 rows twice
            var taps = new byte[8 * 16];
            PictureDecoder<byte>.TransposeInVector128(plane, top, plane, bottom, Stride, taps);
            for (int k = 0; k < 8; k++)
            {
                for (int i = 0; i < 16; i++)
                {
                    int row = i < 8 ? top + (i * Stride) : bottom + ((i - 8) * Stride);
                    Assert.Equal(plane[row + k], taps[(k * 16) + i]);
                }
            }

            if (bottom == top)
            {
                continue;
            }

            var newTaps = new byte[8 * 16];
            random.NextBytes(newTaps);
            var expected = (byte[])plane.Clone();
            for (int k = 0; k < 8; k++)
            {
                for (int i = 0; i < 16; i++)
                {
                    expected[(i < 8 ? top + (i * Stride) : bottom + ((i - 8) * Stride)) + k] = newTaps[(k * 16) + i];
                }
            }

            PictureDecoder<byte>.TransposeOutVector128(newTaps, plane, top, plane, bottom, Stride);
            Assert.Equal(expected, plane);
        }
    }
}
