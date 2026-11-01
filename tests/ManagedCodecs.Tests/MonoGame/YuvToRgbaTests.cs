using ManagedCodecs.MonoGame;

namespace ManagedCodecs.Tests.MonoGame;

/// <summary>
/// mcplay's RGBA conversion: the 8-bit path (tables, a pixel a word, two pixels a chroma sample) gives what the
/// formula sample by sample gives, byte for byte, on random samples of every 8-bit layout, odd sizes included.
/// </summary>
public class YuvToRgbaTests
{
    public static TheoryData<PixelFormat, int, int> Layouts()
    {
        var data = new TheoryData<PixelFormat, int, int>();
        foreach (var format in new[] { PixelFormat.Gray8, PixelFormat.Yuv420P, PixelFormat.Yuv422P, PixelFormat.Yuv444P, PixelFormat.Yuv411P, PixelFormat.Yuv440P })
        {
            data.Add(format, 64, 32);
            data.Add(format, 37, 19);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void EightBitPath_MatchesTheReference(PixelFormat format, int width, int height)
    {
        var random = new Random(((int)format * 1000) + width);
        using var frame = new VideoFrame(width, height, format);
        for (int plane = 0; plane < format.PlaneCount(); plane++)
        {
            var (_, rows) = plane == 0 ? (width, height) : format.PlaneSize(plane, width, height);
            for (int y = 0; y < rows; y++)
            {
                random.NextBytes(frame.GetRow(plane, y));
            }
        }

        // BT.709 and BT.601, limited and full range, and BT.2020's, whose Cb term in blue is the largest.
        foreach (var (kr, kb, full) in new[] { (0.2126, 0.0722, false), (0.299, 0.114, true), (0.2627, 0.0593, true), (0.299, 0.114, false) })
        {
            var converter = new YuvToRgba(kr, kb, full);
            var fast = new byte[width * height * 4];
            var reference = new byte[width * height * 4];
            converter.Convert(frame, fast);
            converter.ConvertReference(frame, reference);
            Assert.Equal(reference, fast);
        }
    }
}
