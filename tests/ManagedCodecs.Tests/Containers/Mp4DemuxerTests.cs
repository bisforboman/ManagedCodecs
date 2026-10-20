using System.Buffers.Binary;
using System.Text;
using ManagedCodecs.Containers;
using ManagedCodecs.Containers.Mp4;

namespace ManagedCodecs.Tests.Containers;

public class Mp4DemuxerTests
{
    // Three samples, the first two in one chunk and the third in another after a gap byte.
    private static readonly byte[][] SampleData = [[1, 2, 3], [4, 5], [6, 7, 8, 9]];

    [Fact]
    public void ReadsSampleTablesAndTimestamps()
    {
        using var demuxer = new Mp4Demuxer(new MemoryStream(TinyMp4()));

        Assert.Equal(CodecIds.Mjpeg, demuxer.Video.Codec); // mp4v with object type 0x6C
        Assert.Equal((32, 16), (demuxer.Video.Width, demuxer.Video.Height));
        Assert.Equal(new Rational(1, 90_000), demuxer.Video.TimeBase);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, demuxer.Video.CodecPrivate.ToArray());
        Assert.Equal(30.0, demuxer.Video.FramesPerSecond, precision: 6);
        Assert.Equal(3, demuxer.Video.FrameCount);

        var packets = demuxer.ReadPackets().ToList();

        Assert.Equal(SampleData, packets.Select(p => p.Data.ToArray()));
        Assert.All(packets, p => Assert.Equal(1, p.StreamIndex)); // the audio track comes first
        Assert.Equal([true, false, true], packets.Select(p => p.IsKeyframe));
        Assert.Equal([3000L, 3000, 3000], packets.Select(p => p.Duration));

        // Raw dts 0, 3000, 6000 and pts 3000, 6000, 6000. The edit list starts with 500 ms of
        // nothing (45000 ticks) and then plays from media time 200, so everything moves by
        // 45000 - 200 = 44800.
        Assert.Equal([44_800L, 47_800, 50_800], packets.Select(p => p.Dts));
        Assert.Equal([47_800L, 50_800, 50_800], packets.Select(p => p.Pts));
    }

    [Fact]
    public void DemuxerOpen_RecognizesMp4()
    {
        using var demuxer = Demuxer.Open(new MemoryStream(TinyMp4()));

        Assert.IsType<Mp4Demuxer>(demuxer);
    }

    [Fact]
    public void RejectsFragmentedMp4()
    {
        var stbl = Box("stbl",
            SampleDescription(),
            FullBox("stts", 0, U32(0)),
            FullBox("stsc", 0, U32(0)),
            FullBox("stsz", 0, U32(0), U32(0)),
            FullBox("stco", 0, U32(0)));
        var moov = Box("moov",
            FullBox("mvhd", 0, new byte[8], U32(1000), new byte[84]),
            Box("trak", Box("mdia", MediaHeader(), Handler("vide"), Box("minf", stbl))),
            Box("mvex"));

        Assert.Throws<NotSupportedException>(() => new Mp4Demuxer(new MemoryStream([.. Box("ftyp", Ascii("iso5"), U32(0)), .. moov])));
    }

    [Fact]
    public void RejectsFileWithoutMoov()
    {
        Assert.Throws<InvalidDataException>(() => new Mp4Demuxer(new MemoryStream(Box("ftyp", Ascii("isom"), U32(0)))));
    }

    private static byte[] TinyMp4()
    {
        var ftyp = Box("ftyp", Ascii("isom"), U32(512), Ascii("isom"));

        // mdat payload: chunk 1 = samples 0 and 1, then a gap byte, then chunk 2 = sample 2.
        byte[] payload = [.. SampleData[0], .. SampleData[1], 0xEE, .. SampleData[2]];
        var mdat = Box("mdat", payload);
        long chunk1 = ftyp.Length + 8;
        long chunk2 = chunk1 + SampleData[0].Length + SampleData[1].Length + 1;

        var stbl = Box("stbl",
            SampleDescription(),
            FullBox("stts", 0, U32(1), U32(3), U32(3000)),
            FullBox("ctts", 0, U32(2), U32(2), U32(3000), U32(1), U32(0)),
            FullBox("stsc", 0, U32(2), U32(1), U32(2), U32(1), U32(2), U32(1), U32(1)),
            FullBox("stz2", 0, [0, 0, 0, 4], U32(3), [0x32, 0x40]), // 24 reserved bits, 4-bit sizes 3, 2, 4
            FullBox("co64", 0, U32(2), U64(chunk1), U64(chunk2)),
            FullBox("stss", 0, U32(2), U32(1), U32(3)));

        var video = Box("trak",
            Box("edts", FullBox("elst", 0, U32(2), U32(500), U32(uint.MaxValue), U32(0x0001_0000), U32(3000), U32(200), U32(0x0001_0000))),
            Box("mdia",
                MediaHeader(version: 1),
                Handler("vide"),
                Box("minf", stbl)));

        var audio = Box("trak", Box("mdia", MediaHeader(), Handler("soun")));

        var moov = Box("moov", FullBox("mvhd", 0, new byte[8], U32(1000), new byte[84]), audio, video);
        return [.. ftyp, .. mdat, .. moov];
    }

    private static byte[] MediaHeader(int version = 0) => version == 1
        ? FullBox("mdhd", 1, new byte[16], U32(90_000), U64(9000), new byte[4])
        : FullBox("mdhd", 0, new byte[8], U32(90_000), U32(9000), new byte[4]);

    private static byte[] Handler(string type) => FullBox("hdlr", 0, U32(0), Ascii(type), new byte[12], [0]);

    private static byte[] SampleDescription()
    {
        // VisualSampleEntry: 78 bytes with width and height at 24 and 26, then child boxes.
        var entry = new byte[78];
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(6), 1);  // data_reference_index
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(24), 32);
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(26), 16);

        byte[] decoderConfig = [0x04, 17, 0x6C, 0x11, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x05, 2, 0xAA, 0xBB];
        byte[] esDescriptor = [0x03, 0x80, 0x80, 0x80, (byte)(3 + decoderConfig.Length), 0, 1, 0, .. decoderConfig];

        return FullBox("stsd", 0, U32(1), Box("mp4v", entry, FullBox("esds", 0, esDescriptor)));
    }

    private static byte[] Box(string type, params byte[][] parts)
    {
        int size = 8 + parts.Sum(p => p.Length);
        byte[] box = [.. U32((uint)size), .. Ascii(type), .. parts.SelectMany(p => p)];
        return box;
    }

    private static byte[] FullBox(string type, byte version, params byte[][] parts) =>
        Box(type, [[version, 0, 0, 0], .. parts]);

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] U64(long value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
}
