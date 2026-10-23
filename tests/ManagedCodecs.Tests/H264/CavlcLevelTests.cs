using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>
/// Levels coded with level_prefix 16 and more (9.2.2.1), which only High profiles allow and only very large levels
/// need: no encoder sample reaches them (JM codes such macroblocks as I_PCM, and crashes with EnableIPCM=0).
/// </summary>
public class CavlcLevelTests
{
    // One level after coeff_token (TotalCoeff 1, TrailingOnes 0, nC 0) and before total_zeros 0. suffixLength is 0,
    // so levelCode = 15 + level_suffix + 15 + (1 << (level_prefix - 3)) - 4096 + 2 (the first level after fewer
    // than three trailing ones), and the level is (levelCode + 2) / 2, or -(levelCode + 1) / 2 for odd levelCode.
    [Theory]
    [InlineData(15, 0, 17)]
    [InlineData(15, 4095, -2064)]
    [InlineData(16, 0, 2065)]
    [InlineData(16, 1, -2065)]
    [InlineData(16, 8191, -6160)]
    [InlineData(17, 5, -6163)]
    [InlineData(20, 0, 63505)]
    public void ReadResidualBlock_LargeLevel(int levelPrefix, int levelSuffix, int expected)
    {
        var w = new BitWriter();
        w.Bits(0b000101, 6); // coeff_token: TotalCoeff 1, TrailingOnes 0 (Table 9-5, 0 <= nC < 2)
        w.Bits(1, levelPrefix + 1); // level_prefix: that many zeros, then a one
        w.Bits(levelSuffix, levelPrefix - 3); // level_suffix: levelSuffixSize = level_prefix - 3
        w.Bit(1); // total_zeros 0
        w.Trailing();
        var bytes = w.ToBytes();
        byte[] padded = [.. bytes, .. new byte[8]]; // the reader's padding

        Span<int> block = stackalloc int[16];
        byte[] scan = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];
        int totalCoeff = Cavlc.ReadResidualBlock(new BitReader(padded, bytes.Length), 0, 16, block, scan);

        Assert.Equal(1, totalCoeff);
        Assert.Equal(expected, block[0]);
    }
}
