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

        // MJPEG's two layouts H.264 has no chroma_format_idc for.
        Assert.True(seen.Add(PixelFormat.Yuv411P));
        Assert.True(seen.Add(PixelFormat.Yuv440P));
        Assert.Equal(Enum.GetValues<PixelFormat>().Length, seen.Count);
    }

    [Theory]
    [InlineData(PixelFormat.Yuv411P, 5, 5, 2, 5)]
    [InlineData(PixelFormat.Yuv411P, 8, 3, 2, 3)]
    [InlineData(PixelFormat.Yuv411P, 1, 1, 1, 1)]
    [InlineData(PixelFormat.Yuv440P, 5, 5, 5, 3)]
    [InlineData(PixelFormat.Yuv440P, 3, 4, 3, 2)]
    public void PlaneSize_RoundsMjpegsLayoutsUp(PixelFormat format, int width, int height, int chromaWidth, int chromaHeight)
    {
        Assert.Equal((8, 1, 3), (format.BitDepth(), format.BytesPerSample(), format.PlaneCount()));
        Assert.Equal((width, height), format.PlaneSize(0, width, height));
        Assert.Equal((chromaWidth, chromaHeight), format.PlaneSize(1, width, height));
        Assert.Equal((chromaWidth, chromaHeight), format.PlaneSize(2, width, height));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(16)]
    public void Of_RejectsDepthsWithoutAFormat(int depth) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelFormatExtensions.Of(1, depth));
}
