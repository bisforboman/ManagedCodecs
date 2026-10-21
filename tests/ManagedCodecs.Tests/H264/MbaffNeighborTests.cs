using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>
/// Neighbouring locations in MBAFF frames (6.4.12.2, Table 6-4), case by case, on a frame two pairs
/// wide and two high (macroblocks 0..7, pair p holding 2p and 2p + 1). The expected values are
/// transcribed from the ITU reference decoder's getAffNeighbour (JM), not derived from the code
/// under test; the conformance samples check the same table in context.
/// </summary>
public class MbaffNeighborTests
{
    private const int Width = 2;

    public static TheoryData<bool, bool> Modes() => new() { { false, false }, { false, true }, { true, false }, { true, true } };

    /// <summary>A (-1, y) and D (-1, -1) of pair 3, whose left pair is 2 and upper-left pair 0.</summary>
    [Theory]
    [MemberData(nameof(Modes))]
    public void LeftAndUpperLeft(bool currentField, bool neighborField)
    {
        var field = Layout(current: 3, currentField, neighbors: [0, 2], neighborField);
        for (int bottom = 0; bottom < 2; bottom++)
        {
            int mb = 6 + bottom;
            for (int y = 0; y < 16; y++)
            {
                (int Mb, int Y) expected = (currentField, bottom == 1, neighborField) switch
                {
                    (false, false, false) => (4, y),
                    (false, false, true) => (4 + (y & 1), y >> 1),
                    (false, true, false) => (5, y),
                    (false, true, true) => (4 + (y & 1), (y + 16) >> 1),
                    (true, false, false) => y < 8 ? (4, y << 1) : (5, (y << 1) - 16),
                    (true, true, false) => y < 8 ? (4, (y << 1) + 1) : (5, (y << 1) + 1 - 16),
                    (true, false, true) => (4, y),
                    (true, true, true) => (5, y),
                };
                Assert.Equal((expected.Mb, 15, expected.Y), Neighbor(field, mb, -1, y));
            }

            // D: a frame macroblock at the bottom of its pair looks into the left pair (here also
            // field or frame by neighborField), the others into the upper-left pair.
            (int Mb, int Y) d = (currentField, bottom == 1, neighborField) switch
            {
                (false, false, _) => (1, 15),
                (false, true, false) => (4, 15),
                (false, true, true) => (5, 7),
                (true, false, false) => (1, 14),
                (true, false, true) => (0, 15),
                (true, true, _) => (1, 15),
            };
            Assert.Equal((d.Mb, 15, d.Y), Neighbor(field, mb, -1, -1));
        }
    }

    /// <summary>B (x, -1) of pair 3 under pair 1.</summary>
    [Theory]
    [MemberData(nameof(Modes))]
    public void Above(bool currentField, bool neighborField)
    {
        var field = Layout(current: 3, currentField, neighbors: [1], neighborField);
        (int Mb, int Y) top = currentField ? (neighborField ? (2, 15) : (3, 14)) : (3, 15);
        (int Mb, int Y) bottom = currentField ? (3, 15) : (6, 15);
        Assert.Equal((top.Mb, 5, top.Y), Neighbor(field, 6, 5, -1));
        Assert.Equal((bottom.Mb, 5, bottom.Y), Neighbor(field, 7, 5, -1));
    }

    /// <summary>C (16, -1) of pair 2 under pair 1; a frame macroblock at the bottom of its pair has none.</summary>
    [Theory]
    [MemberData(nameof(Modes))]
    public void AboveRight(bool currentField, bool neighborField)
    {
        var field = Layout(current: 2, currentField, neighbors: [1], neighborField);
        (int Mb, int Y) top = currentField ? (neighborField ? (2, 15) : (3, 14)) : (3, 15);
        Assert.Equal((top.Mb, 0, top.Y), Neighbor(field, 4, 16, -1));
        Assert.Equal(currentField ? (3, 0, 15) : (-1, 0, 0), Neighbor(field, 5, 16, -1));
    }

    /// <summary>Chroma (8x8 blocks): the same table with maxH 8.</summary>
    [Fact]
    public void ChromaFieldBelowFrame()
    {
        var field = Layout(current: 3, currentField: true, neighbors: [1, 2], neighborField: false);
        Assert.Equal((3, 3, 6), Neighbor(field, 6, 3, -1, 8));
        Assert.Equal((5, 7, (2 * 5) + 1 - 8), Neighbor(field, 7, -1, 5, 8));
    }

    /// <summary>Pairs outside the picture or in another slice are not available, except to the deblocking filter.</summary>
    [Fact]
    public void Availability()
    {
        var field = new bool[8];
        int[] slices = [0, 0, 0, 0, 1, 1, 1, 1];
        Assert.Equal(-1, PictureDecoder.AffNeighbor(field, slices, Width, 6, 0, -1, 16, 16).Mb);
        Assert.Equal(3, PictureDecoder.AffNeighbor(field, slices, Width, 6, 0, -1, 16, 16, anySlice: true).Mb);
        Assert.Equal(-1, PictureDecoder.AffNeighbor(field, slices, Width, 4, -1, 0, 16, 16, anySlice: true).Mb);
        Assert.Equal(-1, PictureDecoder.AffNeighbor(field, slices, Width, 6, 16, -1, 16, 16, anySlice: true).Mb);
    }

    private static bool[] Layout(int current, bool currentField, int[] neighbors, bool neighborField)
    {
        var field = new bool[8];
        field[2 * current] = field[(2 * current) + 1] = currentField;
        foreach (int pair in neighbors)
        {
            field[2 * pair] = field[(2 * pair) + 1] = neighborField;
        }

        return field;
    }

    private static (int Mb, int X, int Y) Neighbor(bool[] field, int mb, int x, int y, int size = 16) =>
        PictureDecoder.AffNeighbor(field, new int[8], Width, mb, x, y, size, size);
}
