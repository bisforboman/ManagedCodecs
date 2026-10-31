using ManagedCodecs.Containers;
using ManagedCodecs.Mjpeg;

namespace ManagedCodecs.Tests.Mjpeg;

/// <summary>
/// The frames' colour properties and aspect ratio where FFmpeg's encoder writes nothing to compare (the samples'
/// name.color references check the rest): images without a JFIF header, a density of 0, and what FFmpeg keeps from
/// earlier images, each found with patched samples against ffprobe (docs/conformance.md, MJPEG).
/// </summary>
public class MjpegColorTests
{
    /// <summary>The JFIF density whatever its units (0, 1 dots an inch, 2 a centimetre), in lowest terms; a zero unknown.</summary>
    [Theory]
    [InlineData(1, 1, 1, 1)]
    [InlineData(3, 2, 3, 2)]
    [InlineData(300, 150, 2, 1)]
    [InlineData(72, 72, 1, 1)]
    [InlineData(0, 5, 0, 1)]
    [InlineData(5, 0, 0, 1)]
    [InlineData(65535, 1, 65535, 1)]
    public void AspectRatio_IsTheDensity(int x, int y, int numerator, int denominator)
    {
        Assert.Equal(new Rational(numerator, denominator), MjpegDecoder.AspectRatioOf(x, y));
    }

    /// <summary>A gray image: BT.470BG and centred chroma as for any image, the range unspecified; no JFIF, no ratio.</summary>
    [Fact]
    public void Gray_WithoutJfif()
    {
        var frame = new MjpegDecoder().Decode(GrayJpeg());
        Assert.Equal((ColorRange.Unspecified, ColorMatrix.Bt470BG, ChromaLocation.Center, new Rational(0, 1)), Describe(frame));
        Assert.Equal((ColorPrimaries.Unspecified, ColorTransfer.Unspecified), (frame.ColorPrimaries, frame.ColorTransfer));
    }

    /// <summary>
    /// A JFIF density stays for later images without one, and a later JFIF segment replaces it (FFmpeg keeps it in its
    /// decoder's context: an MP4 whose first image alone had JFIF 3:2 printed 3:2 for every frame); a JFIF segment too
    /// short to hold a density says nothing.
    /// </summary>
    [Fact]
    public void AspectRatio_IsKeptUntilTheNextJfif()
    {
        var decoder = new MjpegDecoder();
        Assert.Equal(new Rational(3, 2), decoder.Decode(GrayJpeg(Jfif(units: 0, 3, 2))).SampleAspectRatio);
        Assert.Equal(new Rational(3, 2), decoder.Decode(GrayJpeg()).SampleAspectRatio);
        Assert.Equal(new Rational(2, 1), decoder.Decode(GrayJpeg(Jfif(units: 1, 300, 150))).SampleAspectRatio);
        Assert.Equal(new Rational(0, 1), decoder.Decode(GrayJpeg(Jfif(units: 0, 0, 0))).SampleAspectRatio);
        Assert.Equal(new Rational(0, 1), new MjpegDecoder().Decode(GrayJpeg([0xFF, 0xE0, 0x00, 0x0A, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0])).SampleAspectRatio);
    }

    /// <summary>
    /// YCbCr is full range unless a "CS=ITU601" comment came, which holds for every later image (an AVI whose first image
    /// alone had it gave yuv420p, tv, for all); another comment, or the text with more after it, says nothing.
    /// </summary>
    [Fact]
    public void LimitedRange_FromTheComment_ForGood()
    {
        if (TestSamples.Directory is null || !File.Exists(TestSamples.PathOf("testsrc-420.avi")))
        {
            return;
        }

        using var demuxer = Demuxer.Open(TestSamples.PathOf("testsrc-420.avi"));
        var packets = demuxer.ReadPackets().Take(3).Select(p => p.Data.ToArray()).ToList();
        var decoder = new MjpegDecoder();
        Assert.Equal((ColorRange.Full, ColorMatrix.Bt470BG, ChromaLocation.Center, new Rational(1, 1)), Describe(decoder.Decode(packets[0])));
        Assert.Equal(ColorRange.Full, decoder.Decode(WithSegment(packets[0], Comment("CS=ITU601 and more"))).ColorRange);
        Assert.Equal(ColorRange.Full, decoder.Decode(WithSegment(packets[0], Comment("Lavc"))).ColorRange);
        Assert.Equal(ColorRange.Limited, decoder.Decode(WithSegment(packets[1], Comment("CS=ITU601\0"))).ColorRange);
        Assert.Equal(ColorRange.Limited, decoder.Decode(packets[2]).ColorRange);
        Assert.Equal(ColorRange.Limited, new MjpegDecoder().Decode(WithSegment(packets[2], Comment("CS=ITU601"))).ColorRange);
    }

    private static (ColorRange, ColorMatrix, ChromaLocation, Rational) Describe(VideoFrame f) => (f.ColorRange, f.ColorMatrix, f.ChromaLocation, f.SampleAspectRatio);

    /// <summary>An APP0 JFIF 1.01 segment with the given density.</summary>
    private static byte[] Jfif(int units, int x, int y) =>
        [0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, (byte)units, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y, 0, 0];

    /// <summary>A COM segment holding the text.</summary>
    private static byte[] Comment(string text)
    {
        int length = text.Length + 2;
        return [0xFF, 0xFE, (byte)(length >> 8), (byte)length, .. System.Text.Encoding.ASCII.GetBytes(text)];
    }

    /// <summary>The JPEG with a segment inserted right after SOI.</summary>
    private static byte[] WithSegment(byte[] jpeg, byte[] segment) => [.. jpeg[..2], .. segment, .. jpeg[2..]];

    /// <summary>An 8x8 flat gray JPEG (one block, Annex K's tables), with the given segments after SOI.</summary>
    private static byte[] GrayJpeg(params byte[][] segments)
    {
        var data = new List<byte> { 0xFF, 0xD8 };
        foreach (var segment in segments)
        {
            data.AddRange(segment);
        }

        data.AddRange([0xFF, 0xDB, 0x00, 0x43, 0x00]); // DQT, table 0
        data.AddRange(Enumerable.Repeat((byte)8, 64));
        data.AddRange([0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x08, 0x00, 0x08, 0x01, 0x01, 0x11, 0x00]); // SOF0: 8x8 gray
        data.AddRange([0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00, 0x5A, 0xFF, 0xD9]); // SOS, one block, EOI
        return [.. data];
    }
}
