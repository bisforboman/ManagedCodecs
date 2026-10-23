namespace ManagedCodecs.Tests;

public class PixelFormatTests
{
    /// <summary>Of names every format once, and each format's depth, planes and chroma sizes are those it was named by.</summary>
    [Fact]
    public void Of_CoversEveryFormatWithItsDepthAndSampling()
    {
        var seen = new HashSet<PixelFormat>();
        foreach (int depth in new[] { 8, 9, 10, 12, 14 })
        {
            for (int chroma = 0; chroma < 4; chroma++)
            {
                var format = PixelFormatExtensions.Of(chroma, depth);
                Assert.True(seen.Add(format), $"{format} named twice");
                Assert.Equal(depth, format.BitDepth());
                Assert.Equal(depth > 8 ? 2 : 1, format.BytesPerSample());
                Assert.Equal(chroma == 0 ? 1 : 3, format.PlaneCount());
                if (chroma > 0)
                {
                    var expected = chroma switch { 1 => (3, 3), 2 => (3, 5), _ => (5, 5) };
                    Assert.Equal(expected, format.PlaneSize(1, 5, 5));
                }
            }
        }

        Assert.Equal(Enum.GetValues<PixelFormat>().Length, seen.Count);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(16)]
    public void Of_RejectsDepthsWithoutAFormat(int depth) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelFormatExtensions.Of(1, depth));
}
