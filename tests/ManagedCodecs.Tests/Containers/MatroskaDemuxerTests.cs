using System.Buffers.Binary;
using System.Text;
using ManagedCodecs.Containers;

namespace ManagedCodecs.Tests.Containers;

/// <summary>
/// <see cref="MatroskaDemuxer"/> on files built here, for what FFmpeg's muxer never writes (MatroskaConformanceTests
/// checks the samples against FFmpeg): header stripping, Xiph lace sizes of 255 and more, a file broken off, malformed
/// and unsupported files, and the old AAC Codec IDs without a CodecPrivate.
/// </summary>
public class MatroskaDemuxerTests
{
    [Fact]
    public void ReadsTracksAndBlocks()
    {
        var file = File(
            Tracks(Video(1, "V_MPEG4/ISO/AVC", [1, 2, 3], defaultDuration: 40_000_000), Audio(2, "A_OPUS", 48000, 2)),
            Cluster(1000, SimpleBlock(1, 0, keyframe: true, [10, 11]), SimpleBlock(2, -5, keyframe: true, [20]), SimpleBlock(1, 40, keyframe: false, [12])));
        using var demuxer = new MatroskaDemuxer(new MemoryStream(file));

        Assert.Equal((CodecIds.H264, 64, 48, 25.0, 2L, new Rational(1, 1000), 0), (demuxer.Video!.Codec, demuxer.Video.Width, demuxer.Video.Height, demuxer.Video.FramesPerSecond, demuxer.Video.FrameCount, demuxer.Video.TimeBase, demuxer.Video.StreamIndex));
        Assert.Equal(new byte[] { 1, 2, 3 }, demuxer.Video.CodecPrivate.ToArray());
        Assert.Equal(new Rational(1, 1), demuxer.Video.SampleAspectRatio); // DisplayWidth and DisplayHeight default to the pixel size
        Assert.Equal(("A_OPUS", 48000, 2, 1), (demuxer.Audio!.Codec, demuxer.Audio.SampleRate, demuxer.Audio.Channels, demuxer.Audio.StreamIndex));

        var packets = demuxer.ReadPackets().ToList();
        Assert.Equal(
            [(0, 0L, "0A0B", true, 1000L, 40L), (1, 0, "14", true, 995, 0), (0, 1, "0C", false, 1040, 40)],
            packets.Select(p => (p.StreamIndex, p.Sequence, Convert.ToHexString(p.Data.Span), p.IsKeyframe, p.Pts, p.Duration)));
    }

    /// <summary>Header stripping (ContentCompAlgo 3): the bytes taken off each frame's start are put back.</summary>
    [Fact]
    public void PutsBackStrippedHeaders()
    {
        var encodings = Element(0x6D80, Element(0x6240, Element(0x5034, UInt(0x4254, 3), Element(0x4255, [0xAA, 0xBB]))));
        var file = File(Tracks(Video(1, "V_MJPEG", [], extra: encodings)), Cluster(0, SimpleBlock(1, 0, keyframe: true, [1, 2])));
        using var demuxer = new MatroskaDemuxer(new MemoryStream(file));

        var packet = Assert.Single(demuxer.ReadPackets());
        Assert.Equal(new byte[] { 0xAA, 0xBB, 1, 2 }, packet.Data.ToArray());
        Assert.Equal(4, Assert.Single(demuxer.ReadPacketInfo()).Size);
    }

    /// <summary>bzlib and LZO compression and encryption of a track it exposes are refused as not supported.</summary>
    [Theory]
    [InlineData(0, 1)] // bzlib
    [InlineData(0, 2)] // LZO
    [InlineData(1, 3)] // encrypted
    public void RefusesOtherContentEncodings(int type, int algorithm)
    {
        var encodings = Element(0x6D80, Element(0x6240, UInt(0x5033, type), Element(0x5034, UInt(0x4254, algorithm))));
        var file = File(Tracks(Video(1, "V_MJPEG", [], extra: encodings)), Cluster(0, SimpleBlock(1, 0, keyframe: true, [1])));
        Assert.Throws<NotSupportedException>(() => new MatroskaDemuxer(new MemoryStream(file)));
    }

    /// <summary>zlib (ContentCompAlgo 0) of the frames and the CodecPrivate (scope 3) is inflated; ReadPacketInfo gives the inflated sizes.</summary>
    [Fact]
    public void InflatesZlib()
    {
        var encodings = Element(0x6D80, Element(0x6240, UInt(0x5032, 3), Element(0x5034, UInt(0x4254, 0))));
        byte[] frame = [.. Filled(300, 9), 1, 2, 3];
        var file = File(Tracks(Video(1, "V_MJPEG", Zlib([7, 8, 9]), extra: encodings)), Cluster(0, SimpleBlock(1, 0, keyframe: true, Zlib(frame))));
        using var demuxer = new MatroskaDemuxer(new MemoryStream(file));

        Assert.Equal(new byte[] { 7, 8, 9 }, demuxer.Video!.CodecPrivate.ToArray());
        Assert.Equal(frame, Assert.Single(demuxer.ReadPackets()).Data.ToArray());
        Assert.Equal(frame.Length, Assert.Single(demuxer.ReadPacketInfo()).Size);
    }

    /// <summary>A zlib frame that isn't one (no zlib header, or deflate data cut short) is malformed: InvalidDataException.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefusesBrokenZlib(bool cut)
    {
        var encodings = Element(0x6D80, Element(0x6240, Element(0x5034, UInt(0x4254, 0))));
        byte[] data = cut ? Zlib(Filled(5000, 3))[..10] : [1, 2, 3, 4];
        var file = File(Tracks(Video(1, "V_MJPEG", [], extra: encodings)), Cluster(0, SimpleBlock(1, 0, keyframe: true, data)));
        using var demuxer = new MatroskaDemuxer(new MemoryStream(file));
        Assert.Throws<InvalidDataException>(() => demuxer.ReadPackets().ToList());
    }

    /// <summary>
    /// The Projection's roll, as FFmpeg writes a display rotation, is the rotation counter-clockwise (-90 is 270); another
    /// angle, a yaw or pitch (a mirror) or another projection type gives 0.
    /// </summary>
    [Theory]
    [InlineData(0, 0.0, 90.0, 90)]
    [InlineData(0, 0.0, -90.0, 270)]
    [InlineData(0, 0.0, 180.0, 180)]
    [InlineData(0, 0.0, 45.0, 0)]
    [InlineData(0, 180.0, 0.0, 0)]
    [InlineData(1, 0.0, 90.0, 0)]
    public void ReadsTheProjectionsRotation(int type, double yaw, double roll, int rotation)
    {
        var projection = Element(0x7670, UInt(0x7671, type), Float(0x7673, yaw), Float(0x7675, roll));
        var video = Element(0xE0, UInt(0xB0, 64), UInt(0xBA, 48), projection);
        var file = File(Tracks(Element(0xAE, UInt(0xD7, 1), UInt(0x83, 1), Text(0x86, "V_MJPEG"), video)), Cluster(0, SimpleBlock(1, 0, keyframe: true, [1])));
        using var demuxer = new MatroskaDemuxer(new MemoryStream(file));
        Assert.Equal(rotation, demuxer.Video!.Rotation);
    }

    /// <summary>
    /// Xiph lacing writes each size as bytes of 255 and the byte that ends it (255 is 255, 0); the frames share the
    /// DefaultDuration out, each one's timestamp the one before plus its share.
    /// </summary>
    [Fact]
    public void ReadsXiphLacesOfLargeFrames()
    {
        byte[][] frames = [Filled(255, 1), Filled(600, 2), Filled(3, 3)];
        byte[] lace = [2, 255, 0, 255, 255, 90, .. frames.SelectMany(f => f)];
        var file = File(Tracks(Audio(1, "A_VORBIS", 44100, 1, defaultDuration: 10_000_000)), Cluster(100, Block(0xA3, 1, 0, 0x80 | 0x02, lace)));
        using var demuxer = new MatroskaDemuxer(new MemoryStream(file));

        var packets = demuxer.ReadPackets().ToList();
        Assert.Equal(frames.Select(f => f.Length), packets.Select(p => p.Data.Length));
        Assert.Equal(frames.Select(f => f[0]), packets.Select(p => p.Data.Span[0]));
        Assert.Equal([100L, 110, 120], packets.Select(p => p.Pts));
        Assert.Equal([10L, 10, 10], packets.Select(p => p.Duration));
    }

    /// <summary>
    /// EBML lacing: the first size, then signed differences (here -2 and +300, in one and two bytes). The lace's duration
    /// is the DefaultDuration times its frames in nanoseconds, then in ticks (85 for 4 of 21.33 ms); each frame's share
    /// rounds down, the remainder going to the later ones (21, 21, 21, 22), as FFmpeg shares it.
    /// </summary>
    [Fact]
    public void ReadsEbmlLaces()
    {
        int[] sizes = [5, 3, 303, 1];
        byte[] lace = [3, 0x85, 0x80 - 2 + 63, (byte)(0x40 | ((300 + 8191) >> 8)), (byte)((300 + 8191) & 0xFF), .. sizes.SelectMany((s, i) => Filled(s, (byte)i))];
        var file = File(Tracks(Audio(1, "A_AAC", 48000, 1, defaultDuration: 21_333_333, codecPrivate: [0x11, 0x88])), Cluster(0, Block(0xA3, 1, 0, 0x80 | 0x06, lace)));
        using var demuxer = new MatroskaDemuxer(new MemoryStream(file));
        var packets = demuxer.ReadPackets().ToList();
        Assert.Equal(sizes, packets.Select(p => p.Data.Length));
        Assert.Equal([0L, 21, 42, 63], packets.Select(p => p.Pts));
        Assert.Equal([21L, 21, 21, 22], packets.Select(p => p.Duration));
    }

    /// <summary>A BlockGroup with a ReferenceBlock is no keyframe, one without is; its BlockDuration is the duration.</summary>
    [Fact]
    public void ReadsBlockGroups()
    {
        var file = File(
            Tracks(Video(1, "V_VP9", [])),
            Cluster(0, Element(0xA0, Block(0xA1, 1, 0, 0, [1]), UInt(0x9B, 33)), Element(0xA0, Block(0xA1, 1, 33, 0, [2]), UInt(0x9B, 34), Element(0xFB, [0xDF]))));
        using var demuxer = new MatroskaDemuxer(new MemoryStream(file));
        Assert.Equal([(true, 0L, 33L), (false, 33, 34)], demuxer.ReadPackets().Select(p => (p.IsKeyframe, p.Pts, p.Duration)));
    }

    /// <summary>
    /// DiscardPadding in nanoseconds, at the track's rate and rounded to nearest: positive drops samples from the end of
    /// the block's last frame, negative from the start of its first (FFmpeg's skip-samples side data).
    /// </summary>
    [Theory]
    [InlineData(13_500_000, 0, 648)]
    [InlineData(-10_000_000, 480, 0)]
    [InlineData(10_400, 0, 0)] // 0.4992 samples: rounds to none
    [InlineData(10_500, 0, 1)] // 0.504 samples: rounds to one
    public void ReadsDiscardPadding(long nanoseconds, int start, int end)
    {
        byte[] padding = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(padding, nanoseconds);
        var file = File(
            Tracks(Audio(1, "A_OPUS", 48000, 2)),
            Cluster(0, SimpleBlock(1, 0, keyframe: true, [1]), Element(0xA0, Block(0xA1, 1, 20, 0x80, [2]), Element(0x75A2, padding))));
        using var demuxer = new MatroskaDemuxer(new MemoryStream(file));
        var packets = demuxer.ReadPackets().ToList();
        Assert.Equal([(0, 0), (start, end)], packets.Select(p => (p.SkipSamples, p.SkipEndSamples)));
        Assert.Equal(packets.Select(ReadCountingStream.InfoOf), demuxer.ReadPacketInfo());
    }

    /// <summary>A file that breaks off inside a block ends at the block before, as FFmpeg's does.</summary>
    [Fact]
    public void EndsAtTheLastWholeBlock()
    {
        var file = File(Tracks(Video(1, "V_MJPEG", [])), Cluster(0, SimpleBlock(1, 0, keyframe: true, [1, 2, 3]), SimpleBlock(1, 40, keyframe: true, Filled(100, 7))));
        using var demuxer = new MatroskaDemuxer(new MemoryStream(file[..^10]));
        Assert.Equal(1, demuxer.Video!.FrameCount);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(demuxer.ReadPackets()).Data.ToArray());
    }

    /// <summary>A Segment and Clusters of unknown size: each Cluster ends where the next top-level element starts.</summary>
    [Fact]
    public void ReadsUnknownSizes()
    {
        var file = File(
            [.. Tracks(Video(1, "V_MJPEG", [])), .. Cluster(0, unknownSize: true, SimpleBlock(1, 0, keyframe: true, [1])), .. Cluster(40, unknownSize: true, SimpleBlock(1, 0, keyframe: true, [2]))],
            unknownSize: true);
        using var demuxer = new MatroskaDemuxer(new MemoryStream(file));
        Assert.Equal([(0L, (byte)1), (40, 2)], demuxer.ReadPackets().Select(p => (p.Pts, p.Data.Span[0])));
    }

    /// <summary>
    /// The old AAC Codec IDs carry no CodecPrivate: the AudioSpecificConfig is made from the name (object type, rate
    /// index, channels; LC/SBR with an SBR sync extension at the output rate, which is then the rate).
    /// </summary>
    [Theory]
    [InlineData("A_AAC/MPEG4/LC", 44100, 2, 0, "1210", 44100)]
    [InlineData("A_AAC/MPEG2/MAIN", 48000, 1, 0, "0988", 48000)]
    [InlineData("A_AAC/MPEG4/LC/SBR", 22050, 2, 44100, "139056E5A0", 44100)]
    [InlineData("A_AAC/MPEG4/LTP", 12345, 2, 0, "", 12345)] // a rate without an index: no config
    public void MakesTheAacConfigFromTheName(string codecId, int rate, int channels, int outputRate, string config, int reportedRate)
    {
        var file = File(Tracks(Audio(1, codecId, rate, channels, outputRate: outputRate)), Cluster(0, SimpleBlock(1, 0, keyframe: true, [1])));
        using var demuxer = new MatroskaDemuxer(new MemoryStream(file));
        Assert.Equal((CodecIds.Aac, config, reportedRate), (demuxer.Audio!.Codec, Convert.ToHexString(demuxer.Audio.CodecPrivate.Span), demuxer.Audio.SampleRate));
    }

    /// <summary>The display size over the pixel size, in any unit but unknown (4), as the sample aspect ratio.</summary>
    [Theory]
    [InlineData(0, 128, 48, "2:1")]
    [InlineData(3, 16, 9, "4:3")]
    [InlineData(4, 16, 9, "0:1")]
    public void ReadsTheDisplaySize(int unit, int width, int height, string ratio)
    {
        var video = Element(0xE0, UInt(0xB0, 64), UInt(0xBA, 48), UInt(0x54B0, width), UInt(0x54BA, height), UInt(0x54B2, unit));
        var file = File(Tracks(Element(0xAE, UInt(0xD7, 1), UInt(0x83, 1), Text(0x86, "V_MJPEG"), video)), Cluster(0, SimpleBlock(1, 0, keyframe: true, [1])));
        using var demuxer = new MatroskaDemuxer(new MemoryStream(file));
        Assert.Equal(ratio, $"{demuxer.Video!.SampleAspectRatio.Numerator}:{demuxer.Video.SampleAspectRatio.Denominator}");
    }

    [Fact]
    public void RefusesWhatIsNotMatroska()
    {
        byte[] other = [.. Element(0x1A45DFA3, Text(0x4282, "other")), .. Element(0x18538067)];
        Assert.Throws<InvalidDataException>(() => new MatroskaDemuxer(new MemoryStream(other)));
        Assert.Throws<InvalidDataException>(() => new MatroskaDemuxer(new MemoryStream("RIFF0000AVI "u8.ToArray())));
        Assert.Throws<InvalidDataException>(() => new MatroskaDemuxer(new MemoryStream(File(Tracks(Element(0xAE, UInt(0xD7, 1), UInt(0x83, 17)))))));
    }

    [Fact]
    public void RefusesABlockBeforeItsClusterTimestamp()
    {
        var file = File([.. Tracks(Video(1, "V_MJPEG", [])), .. Element(0x1F43B675, SimpleBlock(1, 0, keyframe: true, [1]))]);
        Assert.Throws<InvalidDataException>(() => new MatroskaDemuxer(new MemoryStream(file)));
    }

    [Fact]
    public void RefusesTracksAfterTheClusters()
    {
        var file = File([.. Cluster(0, SimpleBlock(1, 0, keyframe: true, [1])), .. Tracks(Video(1, "V_MJPEG", []))]);
        Assert.Throws<NotSupportedException>(() => new MatroskaDemuxer(new MemoryStream(file)));
    }

    /// <summary>Every cut of a file, and every byte of it changed, opens and reads or fails with InvalidDataException (or NotSupportedException).</summary>
    [Fact]
    public void FailsOnMalformedFilesWithInvalidData()
    {
        var file = File(
            Tracks(Video(1, "V_MPEG4/ISO/AVC", [1], defaultDuration: 40_000_000), Audio(2, "A_AAC", 48000, 2, codecPrivate: [0x11, 0x90])),
            Cluster(0, SimpleBlock(1, 0, keyframe: true, [1, 2]), Block(0xA3, 2, 0, 0x86, [1, 0x81, 7, 8])));
        for (int cut = 0; cut < file.Length; cut++)
        {
            Survives(file[..cut]);
        }

        for (int i = 0; i < file.Length; i++)
        {
            foreach (byte value in new byte[] { 0x00, 0xFF, 0x01, 0x80 })
            {
                var changed = (byte[])file.Clone();
                changed[i] = value;
                Survives(changed);
            }
        }

        static void Survives(byte[] data)
        {
            try
            {
                using var demuxer = new MatroskaDemuxer(new MemoryStream(data));
                _ = demuxer.ReadPackets().Count();
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException)
            {
            }
        }
    }

    [Fact]
    public void OpensByTheEbmlHeader()
    {
        var file = File(Tracks(Video(1, "V_MJPEG", [])), Cluster(0, SimpleBlock(1, 0, keyframe: true, [1])));
        using var demuxer = Demuxer.Open(new MemoryStream(file));
        Assert.IsType<MatroskaDemuxer>(demuxer);
    }

    private static byte[] File(byte[] segment, bool unknownSize = false) =>
        [.. Element(0x1A45DFA3, Text(0x4282, "matroska")), .. unknownSize ? [0x18, 0x53, 0x80, 0x67, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, .. segment] : Element(0x18538067, segment)];

    private static byte[] File(byte[] tracks, byte[] cluster) => File([.. Element(0x1549A966, UInt(0x2AD7B1, 1_000_000)), .. tracks, .. cluster]);

    private static byte[] Tracks(params byte[][] entries) => Element(0x1654AE6B, entries);

    private static byte[] Video(int number, string codec, byte[] codecPrivate, long defaultDuration = 0, byte[]? extra = null) => Element(
        0xAE,
        UInt(0xD7, number),
        UInt(0x83, 1),
        Text(0x86, codec),
        codecPrivate.Length > 0 ? Element(0x63A2, codecPrivate) : [],
        defaultDuration > 0 ? UInt(0x23E383, defaultDuration) : [],
        Element(0xE0, UInt(0xB0, 64), UInt(0xBA, 48)),
        extra ?? []);

    private static byte[] Audio(int number, string codec, double rate, int channels, long defaultDuration = 0, byte[]? codecPrivate = null, double outputRate = 0) => Element(
        0xAE,
        UInt(0xD7, number),
        UInt(0x83, 2),
        Text(0x86, codec),
        codecPrivate is null ? [] : Element(0x63A2, codecPrivate),
        defaultDuration > 0 ? UInt(0x23E383, defaultDuration) : [],
        Element(0xE1, Float(0xB5, rate), UInt(0x9F, channels), outputRate > 0 ? Float(0x78B5, outputRate) : []));

    private static byte[] Cluster(long time, params byte[][] blocks) => Cluster(time, unknownSize: false, blocks);

    private static byte[] Cluster(long time, bool unknownSize, params byte[][] blocks)
    {
        byte[] body = [.. UInt(0xE7, time), .. blocks.SelectMany(b => b)];
        return unknownSize ? [0x1F, 0x43, 0xB6, 0x75, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, .. body] : Element(0x1F43B675, body);
    }

    private static byte[] SimpleBlock(int track, short time, bool keyframe, byte[] frame) => Block(0xA3, track, time, keyframe ? 0x80 : 0, frame);

    private static byte[] Block(uint id, int track, short time, int flags, byte[] payload)
    {
        var header = new byte[4];
        header[0] = (byte)(0x80 | track);
        BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(1), time);
        header[3] = (byte)flags;
        return Element(id, [.. header, .. payload]);
    }

    private static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.Optimal))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] Filled(int count, byte value) => Enumerable.Repeat(value, count).ToArray();

    private static byte[] UInt(uint id, long value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        int skip = 0;
        while (skip < 7 && bytes[skip] == 0)
        {
            skip++;
        }

        return Element(id, bytes[skip..]);
    }

    private static byte[] Float(uint id, double value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, BitConverter.DoubleToInt64Bits(value));
        return Element(id, bytes);
    }

    private static byte[] Text(uint id, string value) => Element(id, Encoding.ASCII.GetBytes(value));

    private static byte[] Element(uint id, params byte[][] children)
    {
        byte[] payload = [.. children.SelectMany(c => c)];
        int idLength = id > 0xFFFFFF ? 4 : id > 0xFFFF ? 3 : id > 0xFF ? 2 : 1;
        var head = new List<byte>();
        for (int i = idLength - 1; i >= 0; i--)
        {
            head.Add((byte)(id >> (8 * i)));
        }

        // The size in 8 bytes (0x01 marker): always room, and a length the parser must handle.
        head.Add(0x01);
        for (int i = 6; i >= 0; i--)
        {
            head.Add((byte)((long)payload.Length >> (8 * i)));
        }

        return [.. head, .. payload];
    }
}
