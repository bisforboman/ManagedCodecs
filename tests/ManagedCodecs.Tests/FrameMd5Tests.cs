namespace ManagedCodecs.Tests;

public class FrameMd5Tests
{
    [Fact]
    public void Parse_SkipsHeaderAndReadsEntries()
    {
        string[] lines =
        [
            "#format: frame checksums",
            "#version: 2",
            "#tb 0: 1/25",
            "#stream#, dts,        pts, duration,     size, hash",
            "0,          0,          0,        1,   115200, 9e0F8a3b1c2d4e5f60718293a4b5c6d7",
            "0,          1,          1,        1,   115200, 0123456789abcdef0123456789abcdef",
        ];

        var entries = FrameMd5.Parse(lines);

        Assert.Equal(2, entries.Count);
        Assert.Equal(new FrameMd5Entry(0, 0, 0, 1, 115200, "9e0f8a3b1c2d4e5f60718293a4b5c6d7"), entries[0]);
        Assert.Equal(1L, entries[1].Pts);
    }

    [Fact]
    public void ParseExtradata_ReadsHeaderLine()
    {
        string[] lines =
        [
            "#format: frame checksums",
            "#extradata 0,                              46, 8CB85D22C5BABB0D597CD76F06BD3B8F",
            "0,      -1024,          0,      512,     4932, 8e9b82f671e4da149eb08b2988f15c7a",
        ];

        Assert.Equal((46, "8cb85d22c5babb0d597cd76f06bd3b8f"), FrameMd5.ParseExtradata(lines));
        Assert.Null(FrameMd5.ParseExtradata(lines, streamIndex: 1));
        Assert.Equal(-1024L, FrameMd5.Parse(lines)[0].Dts);
    }

    [Fact]
    public void Hash_Gray8_HashesPlaneBytes()
    {
        var frame = new VideoFrame(2, 2, PixelFormat.Gray8);

        Assert.Equal("f1d3ff8443297732862df21dc4e57262", FrameMd5.Hash(frame)); // MD5 of 4 zero bytes
    }

    [Fact]
    public void Hash_Yuv420P_PacksPlanesInOrder()
    {
        var frame = new VideoFrame(2, 2, PixelFormat.Yuv420P);
        for (byte i = 0; i < 4; i++)
        {
            frame.Planes[0][i] = i;
        }

        frame.Planes[1][0] = 4;
        frame.Planes[2][0] = 5;

        Assert.Equal("d15ae53931880fd7b724dd7888b4b4ed", FrameMd5.Hash(frame)); // MD5 of bytes 0..5
    }
}
