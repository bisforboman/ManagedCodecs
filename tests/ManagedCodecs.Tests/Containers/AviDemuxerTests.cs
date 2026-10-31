using System.Buffers.Binary;
using System.Text;
using ManagedCodecs.Containers;

namespace ManagedCodecs.Tests.Containers;

public class AviDemuxerTests
{
    [Fact]
    public void ReadsHeaderAndVideoPackets()
    {
        using var demuxer = new AviDemuxer(new MemoryStream(TinyAvi()));

        Assert.Equal("MJPG", demuxer.Video.Codec);
        Assert.Equal(32, demuxer.Video.Width);
        Assert.Equal(16, demuxer.Video.Height);
        Assert.Equal(25.0, demuxer.Video.FramesPerSecond, precision: 6);
        Assert.Equal(2, demuxer.Video.FrameCount);
        Assert.Equal(new Rational(1, 25), demuxer.Video.TimeBase);

        var packets = demuxer.ReadPackets().ToList();

        Assert.Equal(2, packets.Count);
        Assert.Equal(new byte[] { 1, 2, 3 }, packets[0].Data.ToArray());          // odd size: padding skipped
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, packets[1].Data.ToArray());       // inside a 'rec ' list
        Assert.Equal(new long[] { 0, 1 }, packets.Select(p => p.Sequence).ToArray());
        Assert.Equal(new long[] { 0, 1 }, packets.Select(p => p.Pts).ToArray());
    }

    /// <summary>
    /// An empty video chunk is a dropped frame: no packet comes out for it, but it takes its tick, as in FFmpeg, so the
    /// packets after it are a frame later.
    /// </summary>
    [Fact]
    public void CountsAnEmptyChunkAsADroppedFrame()
    {
        using var demuxer = new AviDemuxer(new MemoryStream(TinyAvi(dropFrame: true)));

        var packets = demuxer.ReadPackets().ToList();

        Assert.Equal(2, packets.Count);
        Assert.Equal(new long[] { 0, 1 }, packets.Select(p => p.Sequence).ToArray());
        Assert.Equal(new long[] { 0, 2 }, packets.Select(p => p.Pts).ToArray());
        Assert.Equal(new long[] { 0, 2 }, packets.Select(p => p.Dts).ToArray());
    }

    /// <summary>
    /// The sample for it really has the empty chunk (its pts, against FFmpeg's, are checked by the MJPEG conformance
    /// tests): a muxer that stops writing one would leave them nothing to catch.
    /// </summary>
    [Fact]
    public void DroppedFrameSampleHasAGap()
    {
        if (TestSamples.Directory is null || !File.Exists(TestSamples.PathOf("avi-dropped-frame.avi")))
        {
            return;
        }

        using var demuxer = AviDemuxer.Open(TestSamples.PathOf("avi-dropped-frame.avi"));
        var pts = demuxer.ReadPackets().Select(p => p.Pts).ToList();

        Assert.True(pts.Count < pts[^1] + 1, $"No pts gap in {pts.Count} packets.");
    }

    [Fact]
    public void RejectsNonAvi()
    {
        var data = Encoding.ASCII.GetBytes("RIFF\u0004\0\0\0WAVE");

        Assert.Throws<InvalidDataException>(() => new AviDemuxer(new MemoryStream(data)));
    }

    // AVI with one MJPEG video stream (index 0) and one audio stream (index 1); with an empty video chunk (a dropped
    // frame) between the two packets if asked.
    /// <summary>
    /// The 'vprp' sample aspect ratio, as FFmpeg's avidec reads it: the picture's ratio (dwFrameAspectRatio, Y in its low
    /// word) over the frame's (dwFrameWidthInPixels, dwFrameHeightInLines), in lowest terms; none with a zero anywhere,
    /// a chunk of 36 bytes or fewer, or no chunk.
    /// </summary>
    [Theory]
    [InlineData(16, 9, 320, 240, 36 + 8, 4, 3)]
    [InlineData(4, 3, 720, 576, 36 + 8, 16, 15)]
    [InlineData(4, 3, 320, 240, 36 + 8, 1, 1)]
    [InlineData(0, 3, 320, 240, 36 + 8, 0, 1)]
    [InlineData(4, 3, 0, 240, 36 + 8, 0, 1)]
    [InlineData(4, 3, 320, 240, 36, 0, 1)]
    public void AspectRatio_FromVprp(int x, int y, int width, int height, int size, int numerator, int denominator)
    {
        var vprp = new byte[size];
        BinaryPrimitives.WriteUInt16LittleEndian(vprp.AsSpan(20), (ushort)y);
        BinaryPrimitives.WriteUInt16LittleEndian(vprp.AsSpan(22), (ushort)x);
        BinaryPrimitives.WriteUInt32LittleEndian(vprp.AsSpan(24), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(vprp.AsSpan(28), (uint)height);
        using var demuxer = new AviDemuxer(new MemoryStream(TinyAvi(vprp: vprp)));
        Assert.Equal(new Rational(numerator, denominator), demuxer.Video!.SampleAspectRatio);
        using var none = new AviDemuxer(new MemoryStream(TinyAvi()));
        Assert.Equal(new Rational(0, 1), none.Video!.SampleAspectRatio);
    }

    private static byte[] TinyAvi(bool dropFrame = false, byte[]? vprp = null)
    {
        var avih = new byte[56];
        BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(0), 40_000); // microseconds per frame = 25 fps
        BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(16), 2);     // total frames
        BinaryPrimitives.WriteInt32LittleEndian(avih.AsSpan(32), 32);     // width
        BinaryPrimitives.WriteInt32LittleEndian(avih.AsSpan(36), 16);     // height

        var videoStrh = new byte[56];
        Encoding.ASCII.GetBytes("vidsMJPG").CopyTo(videoStrh, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(videoStrh.AsSpan(20), 2);  // dwScale
        BinaryPrimitives.WriteUInt32LittleEndian(videoStrh.AsSpan(24), 50); // dwRate: 2/50 = 1/25

        var videoStrf = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(videoStrf.AsSpan(0), 40);
        Encoding.ASCII.GetBytes("MJPG").CopyTo(videoStrf, 16);

        var audioStrh = new byte[56];
        Encoding.ASCII.GetBytes("auds").CopyTo(audioStrh, 0);

        return Riff(
            "AVI ",
            List("hdrl",
                Chunk("avih", avih),
                List("strl", [.. Chunk("strh", videoStrh), .. Chunk("strf", videoStrf), .. vprp is null ? [] : Chunk("vprp", vprp)]),
                List("strl", Chunk("strh", audioStrh), Chunk("strf", new byte[16]))),
            List("movi",
                Chunk("00dc", [1, 2, 3]),
                Chunk("01wb", [9, 9]),
                dropFrame ? Chunk("00dc", []) : [],
                List("rec ", Chunk("00dc", [4, 5, 6, 7]))));
    }

    private static byte[] Riff(string type, params byte[][] children) => Container("RIFF", type, children);

    private static byte[] List(string type, params byte[][] children) => Container("LIST", type, children);

    private static byte[] Container(string id, string type, byte[][] children) =>
        Chunk(id, [.. Encoding.ASCII.GetBytes(type), .. children.SelectMany(c => c)]);

    private static byte[] Chunk(string id, byte[] data)
    {
        var chunk = new byte[8 + data.Length + (data.Length & 1)];
        Encoding.ASCII.GetBytes(id).CopyTo(chunk, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)data.Length);
        data.CopyTo(chunk, 8);
        return chunk;
    }
}
