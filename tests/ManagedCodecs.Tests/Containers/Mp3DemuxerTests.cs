using System.Buffers.Binary;
using System.Globalization;
using ManagedCodecs.Containers;

namespace ManagedCodecs.Tests.Containers;

/// <summary>
/// Raw MP3: every samples/*.mp3 demuxed packet by packet against FFmpeg's (samples/*.packets.framemd5 for the data and
/// times, as instants in another time base, and *.packets.csv for the skip-samples side data), and the rules FFmpeg's
/// demuxer follows that libmp3lame's files don't reach, on frames built here (their contents don't matter to the
/// demuxer): junk and tags around the frames, a truncated end, a first frame unlike the next, the Xing, Info and VBRI
/// tags and LAME's delay and padding, CRCs, and the lower sampling frequencies. Mp3ConformanceTests decodes the samples
/// against FFmpeg's output, the delay and padding trimmed.
/// </summary>
public class Mp3DemuxerTests
{
    // MPEG-1 Layer III, no CRC, 128 kbit/s, 44.1 kHz, no padding, single channel, original: 417 bytes, 1152 samples.
    private static readonly byte[] Header = [0xFF, 0xFB, 0x90, 0xC4];

    public static TheoryData<string> Samples() =>
        new(TestSamples.WithReference(".packets.framemd5").Where(name => name.EndsWith(".mp3", StringComparison.Ordinal)));

    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesFfmpegPacketByPacket(string sampleName)
    {
        var lines = File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".packets.framemd5"));
        var expected = FrameMd5.Parse(lines);
        var timeBase = FrameMd5.ParseTimeBase(lines);
        var skips = File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".packets.csv")).Select(SkipsOf).ToList();
        Assert.Null(FrameMd5.ParseExtradata(lines));

        using var demuxer = Demuxer.Open(TestSamples.PathOf(sampleName));
        Assert.IsType<Mp3Demuxer>(demuxer);
        var audio = demuxer.Audio!;
        Assert.Null(demuxer.Video);
        Assert.Equal(CodecIds.Mp3, audio.Codec);
        var packets = demuxer.ReadPackets().ToList();
        Assert.Equal(expected.Count, packets.Count);
        Assert.Equal(expected.Count, audio.FrameCount);
        Assert.Equal(expected.Count, skips.Count);
        for (int i = 0; i < packets.Count; i++)
        {
            var p = packets[i];
            Assert.Equal((expected[i].Size, expected[i].Md5), (p.Data.Length, FrameMd5.Hash(p.Data.Span)));
            Assert.True(FrameMd5.SameTime(p.Dts, audio.TimeBase, expected[i].Dts, timeBase), $"Packet {i}: dts {p.Dts}.");
            Assert.True(FrameMd5.SameTime(p.Pts, audio.TimeBase, expected[i].Pts, timeBase), $"Packet {i}: pts {p.Pts}.");
            Assert.True(FrameMd5.SameTime(p.Duration, audio.TimeBase, expected[i].Duration, timeBase), $"Packet {i}: duration {p.Duration}.");
            Assert.True(skips[i] == (p.SkipSamples, p.SkipEndSamples), $"Packet {i}: skips {(p.SkipSamples, p.SkipEndSamples)}, FFmpeg's {skips[i]}.");
        }
    }

    /// <summary>
    /// <see cref="Mp3Demuxer.ReadPacketInfo"/> gives <see cref="Mp3Demuxer.ReadPackets"/>'s packets (checked against
    /// FFmpeg's above) without their data, in the same order, and reads nothing from the file to do it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public void PacketInfo_IsThePacketsWithoutData(string sampleName)
    {
        using var stream = new ReadCountingStream(File.ReadAllBytes(TestSamples.PathOf(sampleName)));
        using var demuxer = new Mp3Demuxer(stream);
        var expected = demuxer.ReadPackets().Select(ReadCountingStream.InfoOf).ToList();

        stream.Reads.Clear();
        Assert.Equal(expected, demuxer.ReadPacketInfo());
        Assert.Empty(stream.Reads);
    }

    [Fact]
    public void Frames_AreThePackets()
    {
        var frames = Frames(5);
        using var demuxer = Demuxer.Open(new MemoryStream(Join(frames)));
        var mp3 = Assert.IsType<Mp3Demuxer>(demuxer);
        Assert.Equal((CodecIds.Mp3, 44100, 1, 5L, new Rational(1, 44100), 0L), (mp3.Audio.Codec, mp3.Audio.SampleRate, mp3.Audio.Channels, mp3.Audio.FrameCount, mp3.Audio.TimeBase, mp3.Audio.Duration));
        var packets = mp3.ReadPackets().ToList();
        Assert.Equal(frames, packets.Select(p => p.Data.ToArray()));
        Assert.Equal([0L, 1152, 2304, 3456, 4608], packets.Select(p => p.Pts));
        Assert.All(packets, p => Assert.Equal((1152L, true, 0, 0), (p.Duration, p.IsKeyframe, p.SkipSamples, p.SkipEndSamples)));
    }

    /// <summary>Bytes that are no frame between two frames are skipped, as FFmpeg does.</summary>
    [Fact]
    public void SkipsJunkBetweenFrames()
    {
        var frames = Frames(5);
        byte[] junk = [.. Enumerable.Repeat(new byte[] { 0x12, 0x34, 0xFF, 0x78 }, 5).SelectMany(b => b)];
        Assert.Equal(frames, Packets([.. frames[0], .. frames[1], .. junk, .. Join(frames[2..])]));
    }

    /// <summary>ID3v2 tags before the frames and an ID3v1 tag after them are no packets.</summary>
    [Fact]
    public void SkipsTags()
    {
        var frames = Frames(5);
        byte[] id3v2 = [.. "ID3"u8, 4, 0, 0, 0, 0, 0, 3, 1, 2, 3];
        byte[] id3v1 = [.. "TAG"u8, .. new byte[125]];
        Assert.Equal(frames, Packets([.. id3v2, .. id3v2, .. Join(frames), .. id3v1]));
    }

    /// <summary>A file that breaks off inside a frame gives the frames before it, then <see cref="InvalidDataException"/>.</summary>
    [Fact]
    public void TruncatedFrame_Throws()
    {
        var frames = Frames(5);
        using var demuxer = new Mp3Demuxer(new MemoryStream(Join(frames)[..^5]));
        Assert.Equal(4, demuxer.Audio.FrameCount);
        var packets = new List<byte[]>();
        Assert.Throws<InvalidDataException>(() =>
        {
            foreach (var packet in demuxer.ReadPackets())
            {
                packets.Add(packet.Data.ToArray());
            }
        });
        Assert.Equal(frames[..4], packets);
        Assert.Throws<InvalidDataException>(() => demuxer.ReadPacketInfo().ToList());
    }

    /// <summary>
    /// A first frame whose header differs from the next one's in the mode, copyright, original or emphasis is no packet
    /// to FFmpeg, and the next one neither while it differs from its own next; a frame differing later is kept, and so
    /// is a first frame differing only where frames of a stream may (private bit, mode extension, protection, bitrate,
    /// padding). (By FFmpeg's output on libmp3lame's frames, so altered.)
    /// </summary>
    [Theory]
    [InlineData(new[] { 0 }, 3, 0x04, 1)] // original
    [InlineData(new[] { 0 }, 3, 0x08, 1)] // copyright
    [InlineData(new[] { 0 }, 3, 0x01, 1)] // emphasis
    [InlineData(new[] { 0 }, 3, 0xC0, 1)] // mode: stereo
    [InlineData(new[] { 1 }, 3, 0x04, 2)] // the second frame: the first is unlike it, it unlike the third
    [InlineData(new[] { 0, 1 }, 3, 0x04, 0)] // the first two alike
    [InlineData(new[] { 2 }, 3, 0x04, 0)] // later: kept
    [InlineData(new[] { 0 }, 2, 0x01, 0)] // private bit
    [InlineData(new[] { 0 }, 3, 0x30, 0)] // mode extension
    [InlineData(new[] { 0 }, 2, 0x02, 0)] // padding (one byte longer)
    [InlineData(new[] { 0 }, 1, 0x01, 0)] // protection (a CRC)
    public void FirstFrameUnlikeTheNext_IsSkipped(int[] changed, int headerByte, int bits, int skipped)
    {
        var frames = Frames(5);
        foreach (int i in changed)
        {
            frames[i][headerByte] ^= (byte)bits;
            if (headerByte == 2 && bits == 2)
            {
                frames[i] = [.. frames[i], 0];
            }
        }

        Assert.Equal(frames[skipped..], Packets(Join(frames)));
    }

    /// <summary>
    /// A first frame with a Xing or Info tag (after the side information) or a VBRI tag (32 bytes after the header) is no
    /// packet when it counts the frames or the bytes, as FFmpeg leaves it out; one counting neither is a packet.
    /// </summary>
    [Theory]
    [InlineData("Info", 1, true)]
    [InlineData("Xing", 1, true)]
    [InlineData("Info", 2, true)]
    [InlineData("Info", 0, false)]
    [InlineData("Info", 4, false)] // a table of contents alone
    public void TagFrame_IsNoPacket(string name, int flags, bool skipped)
    {
        var frames = Frames(5);
        var tag = frames[0];
        name.Select(c => (byte)c).ToArray().CopyTo(tag, 4 + 17);
        BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(4 + 17 + 4), (uint)flags);
        if (flags is 1 or 2)
        {
            BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(4 + 17 + 8), 4);
        }

        Assert.Equal(skipped ? frames[1..] : frames, Packets(Join(frames)));
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(0, false)]
    public void VbriFrame_IsNoPacket(int frameCount, bool skipped)
    {
        var frames = Frames(5);
        var tag = frames[0];
        "VBRI"u8.CopyTo(tag.AsSpan(4 + 32));
        BinaryPrimitives.WriteUInt16BigEndian(tag.AsSpan(4 + 32 + 6), 576); // a delay, which FFmpeg doesn't take
        BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(4 + 32 + 10), 0); // no byte count
        BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(4 + 32 + 14), (uint)frameCount);
        using var demuxer = new Mp3Demuxer(new MemoryStream(Join(frames)));
        var packets = demuxer.ReadPackets().ToList();
        Assert.Equal(skipped ? frames[1..] : frames, packets.Select(p => p.Data.ToArray()));
        Assert.All(packets, p => Assert.Equal((0, 0), (p.SkipSamples, p.SkipEndSamples)));
    }

    /// <summary>
    /// LAME's extension of an Info tag: the delay plus the decoder's 529 samples skipped from the start; with the frame
    /// count, the padding less 529 from the end, over as many packets as it covers (FFmpeg drops the samples from the
    /// tag's frames times 1152 less that, to the frames' end); nothing for an encoder name FFmpeg doesn't take, or
    /// without the frame count at the end.
    /// </summary>
    [Theory]
    [InlineData("LAME", 576, 1404, 1, 1105, new[] { 0, 0, 0, 875 })]
    [InlineData("Lavc", 576, 1404, 1, 1105, new[] { 0, 0, 0, 875 })]
    [InlineData("Lavf", 0, 2000, 1, 529, new[] { 0, 0, 319, 1152 })]
    [InlineData("LAME", 576, 300, 1, 1105, new[] { 0, 0, 0, 0 })] // padding below 529
    [InlineData("LAME", 576, 1404, 2, 1105, new[] { 0, 0, 0, 0 })] // a byte count, no frame count
    [InlineData("Xxxx", 576, 1404, 1, 0, new[] { 0, 0, 0, 0 })]
    public void LameDelayAndPadding_AreSkipped(string encoder, int delay, int padding, int flags, int startSkip, int[] endSkips)
    {
        var frames = Frames(5);
        var tag = frames[0];
        int at = 4 + 17;
        "Info"u8.CopyTo(tag.AsSpan(at));
        BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(at + 4), (uint)flags);
        BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(at + 8), 4); // the frames after this one, or the bytes
        at += 12;
        encoder.Select(c => (byte)c).ToArray().CopyTo(tag, at);
        tag[at + 21] = (byte)(delay >> 4);
        tag[at + 22] = (byte)((delay << 4) | (padding >> 8));
        tag[at + 23] = (byte)padding;

        using var demuxer = new Mp3Demuxer(new MemoryStream(Join(frames)));
        var packets = demuxer.ReadPackets().ToList();
        Assert.Equal(frames[1..], packets.Select(p => p.Data.ToArray()));
        Assert.Equal([startSkip, 0, 0, 0], packets.Select(p => p.SkipSamples));
        Assert.Equal(endSkips, packets.Select(p => p.SkipEndSamples));
        Assert.Equal(flags == 1 ? 4 * 1152 : 0, demuxer.Audio.Duration);
        Assert.Equal(packets.Select(ReadCountingStream.InfoOf), demuxer.ReadPacketInfo());
    }

    /// <summary>With a CRC after the header, the Xing tag is after it too, where the side information starts.</summary>
    [Fact]
    public void TagAfterCrc_IsRead()
    {
        var frames = Frames(5);
        foreach (var frame in frames)
        {
            frame[1] &= 0xFE; // protection_bit 0: a CRC follows
        }

        "Info"u8.CopyTo(frames[0].AsSpan(4 + 2 + 17));
        BinaryPrimitives.WriteUInt32BigEndian(frames[0].AsSpan(4 + 2 + 17 + 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(frames[0].AsSpan(4 + 2 + 17 + 8), 4);
        Assert.Equal(frames[1..], Packets(Join(frames)));
    }

    /// <summary>The headers' frame lengths, rates and frame durations: MPEG-1, MPEG-2's lower and MPEG-2.5's lowest sampling frequencies.</summary>
    [Theory]
    [InlineData(0xFF, 0xFB, 0x90, 0x00, 1, 44100, 128000, 2, 417, 1152)] // padding 0, stereo
    [InlineData(0xFF, 0xFB, 0x92, 0x00, 1, 44100, 128000, 2, 418, 1152)] // padding 1
    [InlineData(0xFF, 0xFB, 0xE4, 0xC0, 1, 48000, 320000, 1, 960, 1152)]
    [InlineData(0xFF, 0xFB, 0x18, 0x00, 1, 32000, 32000, 2, 144, 1152)]
    [InlineData(0xFF, 0xF3, 0x80, 0xC0, 2, 22050, 64000, 1, 208, 576)]
    [InlineData(0xFF, 0xF3, 0xE8, 0x00, 2, 16000, 160000, 2, 720, 576)]
    [InlineData(0xFF, 0xE3, 0x38, 0x00, 25, 8000, 24000, 2, 216, 576)]
    [InlineData(0xFF, 0xE3, 0x10, 0xC0, 25, 11025, 8000, 1, 52, 576)]
    public void Header_IsRead(int b0, int b1, int b2, int b3, int version, int rate, int bitrate, int channels, int length, int samples)
    {
        Assert.True(MpegAudioHeader.TryParse([(byte)b0, (byte)b1, (byte)b2, (byte)b3], out var h));
        Assert.Equal((version, rate, bitrate, channels, length, samples), (h.Version, h.SampleRate, h.Bitrate, h.Channels, h.FrameLength, h.SamplesPerFrame));
    }

    /// <summary>The headers that aren't Layer III frames this demuxer reads: the reserved version, Layers I and II, the free format, bitrate 15, the reserved rate, ADTS.</summary>
    [Theory]
    [InlineData(0xFF, 0xEB, 0x90, 0x00)] // reserved version
    [InlineData(0xFF, 0xFD, 0x90, 0x00)] // Layer II
    [InlineData(0xFF, 0xFF, 0x90, 0x00)] // Layer I
    [InlineData(0xFF, 0xF9, 0x90, 0x00)] // layer 0 (ADTS's)
    [InlineData(0xFF, 0xFB, 0x00, 0x00)] // free format
    [InlineData(0xFF, 0xFB, 0xF0, 0x00)] // bitrate 15
    [InlineData(0xFF, 0xFB, 0x9C, 0x00)] // rate 3
    [InlineData(0xFF, 0xDB, 0x90, 0x00)] // no sync
    public void Header_IsNotLayer3(int b0, int b1, int b2, int b3) =>
        Assert.False(MpegAudioHeader.TryParse([(byte)b0, (byte)b1, (byte)b2, (byte)b3], out _));

    [Fact]
    public void NotMp3_IsNotRecognized()
    {
        // A header, but the frame it claims is not followed by another one.
        byte[] data = [.. Header, .. new byte[500]];
        Assert.Throws<NotSupportedException>(() => Demuxer.Open(new MemoryStream(data)));
        Assert.Throws<InvalidDataException>(() => new Mp3Demuxer(new MemoryStream(data)));
    }

    // A line of the .packets.csv (stream, pts, dts, duration, size, flags, hash, then any side data): its skip samples
    // at the start and the end.
    private static (int Start, int End) SkipsOf(string line)
    {
        var f = line.Split(',');
        return f.Length > 9 && f[7] == "Skip Samples"
            ? (int.Parse(f[8], CultureInfo.InvariantCulture), int.Parse(f[9], CultureInfo.InvariantCulture))
            : (0, 0);
    }

    // Frames of Header's kind, each its own bytes after the header.
    private static List<byte[]> Frames(int count) =>
        [.. Enumerable.Range(0, count).Select(i => (byte[])[.. Header, .. Enumerable.Repeat((byte)(i + 1), 417 - 4)])];

    private static byte[] Join(IEnumerable<byte[]> frames) => [.. frames.SelectMany(f => f)];

    private static List<byte[]> Packets(byte[] file)
    {
        using var demuxer = Demuxer.Open(new MemoryStream(file));
        Assert.IsType<Mp3Demuxer>(demuxer);
        return [.. demuxer.ReadPackets().Select(p => p.Data.ToArray())];
    }
}
