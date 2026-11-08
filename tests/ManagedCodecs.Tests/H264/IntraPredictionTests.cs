using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>
/// The row-at-a-time intra prediction (vertical, horizontal and DC of 4x4 and 8x8, every 16x16 and chroma mode) against the sample by
/// sample references, on random planes, for every mode, every combination of available neighbours and the sample
/// types and depths the decoder uses: the whole plane must come out the same, so nothing outside the block is written
/// either, and a mode that needs a missing neighbour must fail the same way.
/// </summary>
public class IntraPredictionTests
{
    private const int Stride = 48;
    private const int Rows = 40;

    public static TheoryData<int> BitDepths => [8, 10, 14];

    [Theory]
    [MemberData(nameof(BitDepths))]
    public void Fast4x4_MatchesReference(int bitDepth) =>
        Compare(bitDepth, 9, 4, 4, IntraPrediction.Predict4x4, IntraPrediction.Predict4x4Reference, IntraPrediction.Predict4x4, IntraPrediction.Predict4x4Reference);

    [Theory]
    [MemberData(nameof(BitDepths))]
    public void Fast8x8_MatchesReference(int bitDepth) =>
        Compare(bitDepth, 9, 8, 8, IntraPrediction.Predict8x8, IntraPrediction.Predict8x8Reference, IntraPrediction.Predict8x8, IntraPrediction.Predict8x8Reference);

    [Theory]
    [MemberData(nameof(BitDepths))]
    public void Fast16x16_MatchesReference(int bitDepth) =>
        Compare(bitDepth, 4, 16, 16, IntraPrediction.Predict16x16, IntraPrediction.Predict16x16Reference, IntraPrediction.Predict16x16, IntraPrediction.Predict16x16Reference);

    /// <summary>Chroma's horizontal, vertical and plane modes (1 to 3) in 4:2:0 (8x8) and 4:2:2 (8x16).</summary>
    [Theory]
    [InlineData(8, 8)]
    [InlineData(8, 16)]
    [InlineData(10, 8)]
    [InlineData(10, 16)]
    [InlineData(14, 16)]
    public void FastChroma_MatchesReference(int bitDepth, int height) =>
        Compare(
            bitDepth,
            3,
            8,
            height,
            (mode, plane, offset, stride, n, depth) => IntraPrediction.PredictChroma(mode + 1, plane, offset, stride, n, depth, height: height),
            (mode, plane, offset, stride, n, depth) => IntraPrediction.PredictChromaReference(mode + 1, plane, offset, stride, n, depth, height),
            (mode, plane, offset, stride, n, depth) => IntraPrediction.PredictChroma(mode + 1, plane, offset, stride, n, depth, height: height),
            (mode, plane, offset, stride, n, depth) => IntraPrediction.PredictChromaReference(mode + 1, plane, offset, stride, n, depth, height));

    /// <summary>Chroma DC in 4:2:0 (8x8) and 4:2:2 (8x16), with the per-row-of-blocks left availability MBAFF uses.</summary>
    [Theory]
    [InlineData(8, 8)]
    [InlineData(8, 16)]
    [InlineData(10, 8)]
    [InlineData(10, 16)]
    [InlineData(14, 16)]
    public void FastChromaDc_MatchesReference(int bitDepth, int height)
    {
        var random = new Random((bitDepth * 100) + height);
        for (int trial = 0; trial < 200; trial++)
        {
            var n = Neighbors(random.Next(16));
            int leftBlocks = random.Next(3) switch { 0 => -1, 1 => n.Left ? 15 : 0, _ => random.Next(16) };
            int offset = Position(random, 8, height);
            int referenceLeft = leftBlocks < 0 ? (n.Left ? 15 : 0) : leftBlocks;
            if (bitDepth == 8)
            {
                var expected = Plane<byte>(random, bitDepth);
                var actual = (byte[])expected.Clone();
                IntraPrediction.PredictChromaDcReference(expected, offset, Stride, n, referenceLeft, height, bitDepth);
                IntraPrediction.PredictChroma(0, actual, offset, Stride, n, bitDepth, leftBlocks, height);
                Assert.Equal(expected, actual);
            }
            else
            {
                var expected = Plane<ushort>(random, bitDepth);
                var actual = (ushort[])expected.Clone();
                IntraPrediction.PredictChromaDcReference(expected, offset, Stride, n, referenceLeft, height, bitDepth);
                IntraPrediction.PredictChroma(0, actual, offset, Stride, n, bitDepth, leftBlocks, height);
                Assert.Equal(expected, actual);
            }
        }
    }

    private delegate void Predict<TPixel>(int mode, TPixel[] plane, int offset, int stride, IntraNeighbors n, int bitDepth)
        where TPixel : unmanaged;

    private static void Compare(int bitDepth, int modes, int width, int height, Predict<byte> fast8, Predict<byte> reference8, Predict<ushort> fast16, Predict<ushort> reference16)
    {
        var random = new Random((bitDepth * 10) + width);
        for (int mode = 0; mode < modes; mode++)
        {
            for (int available = 0; available < 16; available++)
            {
                for (int trial = 0; trial < 20; trial++)
                {
                    var n = Neighbors(available);
                    int offset = Position(random, width, height);
                    if (bitDepth == 8)
                    {
                        Check(Plane<byte>(random, bitDepth), mode, offset, n, bitDepth, fast8, reference8);
                    }
                    else
                    {
                        Check(Plane<ushort>(random, bitDepth), mode, offset, n, bitDepth, fast16, reference16);
                    }
                }
            }
        }
    }

    private static void Check<TPixel>(TPixel[] plane, int mode, int offset, IntraNeighbors n, int bitDepth, Predict<TPixel> fast, Predict<TPixel> reference)
        where TPixel : unmanaged
    {
        var expected = (TPixel[])plane.Clone();
        var actual = (TPixel[])plane.Clone();
        var expectedError = Record.Exception(() => reference(mode, expected, offset, Stride, n, bitDepth));
        var actualError = Record.Exception(() => fast(mode, actual, offset, Stride, n, bitDepth));
        Assert.Equal(expectedError?.GetType(), actualError?.GetType());
        if (expectedError is null)
        {
            Assert.Equal(expected, actual);
        }
    }

    private static IntraNeighbors Neighbors(int bits) => new((bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0, (bits & 8) != 0);

    /// <summary>A block position with its neighbours (and 4x4's top right) inside the plane, now and then at its first column or row of blocks.</summary>
    private static int Position(Random random, int width, int height)
    {
        int x = 1 + (random.Next(((Stride - (2 * width) - 1) / width) + 1) * width);
        int y = 1 + (random.Next(((Rows - height - 1) / height) + 1) * height);
        return (y * Stride) + Math.Min(x, Stride - (2 * width));
    }

    private static TPixel[] Plane<TPixel>(Random random, int bitDepth)
        where TPixel : unmanaged
    {
        var plane = new TPixel[Stride * Rows];
        for (int i = 0; i < plane.Length; i++)
        {
            int value = random.Next(1 << bitDepth);
            plane[i] = typeof(TPixel) == typeof(byte) ? (TPixel)(object)(byte)value : (TPixel)(object)(ushort)value;
        }

        return plane;
    }
}
