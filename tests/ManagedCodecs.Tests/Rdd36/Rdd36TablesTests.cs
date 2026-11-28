using ManagedCodecs.Rdd36;

namespace ManagedCodecs.Tests.Rdd36;

/// <summary>The decoder's tables against RDD 36's as printed (typed apart, in <see cref="Rdd36FrameWriter"/> and here).</summary>
public class Rdd36TablesTests
{
    [Fact]
    public void TheScanIsFigure4TheOtherWayRound()
    {
        // QF[v][u] = QFS[scan[v][u]] (7.2.2): the decoder's table gives 8 * v + u for each scanned frequency index.
        var seen = new bool[64];
        for (int v = 0; v < 8; v++)
        {
            for (int u = 0; u < 8; u++)
            {
                int index = Rdd36FrameWriter.ProgressiveScan[v][u];
                Assert.Equal((8 * v) + u, Rdd36Tables.ProgressiveScan[index]);
                Assert.False(seen[index]);
                seen[index] = true;
            }
        }
    }

    [Fact]
    public void TheCodebooksAreTables9To11()
    {
        static (int, int, int) Combo(int lastRiceQ, int riceOrder, int expOrder) => (lastRiceQ, riceOrder, expOrder);
        static (int, int, int) Exp(int order) => (0, order, order + 1);
        static (int, int, int)[] Of(Rdd36Codebook[] books) => [.. books.Select(b => ((int)b.LastRiceQ, (int)b.RiceOrder, (int)b.ExpOrder))];

        Assert.Equal([Exp(0), Exp(1), Combo(1, 2, 3), Exp(3)], Of(Rdd36Tables.DcDifference));
        Assert.Equal(
            [
                Combo(2, 0, 1), Combo(2, 0, 1), Combo(1, 0, 1), Combo(1, 0, 1), Exp(0), Combo(1, 1, 2), Combo(1, 1, 2), Combo(1, 1, 2), Combo(1, 1, 2),
                Exp(1), Exp(1), Exp(1), Exp(1), Exp(1), Exp(1), Exp(2),
            ],
            Of(Rdd36Tables.Run));
        Assert.Equal([Combo(2, 0, 2), Combo(1, 0, 1), Combo(2, 0, 1), Exp(0), Exp(1), Exp(1), Exp(1), Exp(1), Exp(2)], Of(Rdd36Tables.Level));
    }
}
