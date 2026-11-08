using ManagedCodecs.Containers;

namespace ManagedCodecs.Tests.Containers;

/// <summary>
/// Raw MP3: every samples/*.mp3 demuxed packet by packet against FFmpeg's (samples/*.packets.framemd5, whose timestamps
/// are in another time base, so they are compared as instants; FFmpeg's demuxer leaves the info frame out too), the
/// gapless trim from the LAME tag (the decoded samples are checked in Mp3ConformanceTests), and the cases FFmpeg's
/// samples don't have, built from them: junk between frames, tags around them, a truncated end, other layers.
/// </summary>
public class Mp3DemuxerTests
{
    private const string Sample = "mp3-raw-noxing.mp3";

    public static TheoryData<string> Samples() =>
        new(TestSamples.WithReference(".packets.framemd5").Where(name => name.EndsWith(".mp3", StringComparison.Ordinal)));

    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesFfmpegPacketByPacket(string sampleName)
    {
        var lines = File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".packets.framemd5"));
        var expected = FrameMd5.Parse(lines);
        var timeBase = FrameMd5.ParseTimeBase(lines);

        using var demuxer = Demuxer.Open(TestSamples.PathOf(sampleName));
        Assert.IsType<Mp3Demuxer>(demuxer);
        var audio = demuxer.Audio!;
        Assert.Equal(CodecIds.Mp3, audio.Codec);
        Assert.Null(demuxer.Video);
        var packets = demuxer.ReadPackets().ToList();
        Assert.Equal(expected.Count, packets.Count);
        Assert.Equal(expected.Count, audio.FrameCount);
        for (int i = 0; i < packets.Count; i++)
        {
            var p = packets[i];
            Assert.Equal((expected[i].Size, expected[i].Md5), (p.Data.Length, FrameMd5.Hash(p.Data.Span)));
            Assert.True(FrameMd5.SameTime(p.Dts, audio.TimeBase, expected[i].Dts, timeBase), $"Packet {i}: dts {p.Dts}.");
            Assert.True(FrameMd5.SameTime(p.Pts, audio.TimeBase, expected[i].Pts, timeBase), $"Packet {i}: pts {p.Pts}.");
            Assert.True(FrameMd5.SameTime(p.Duration, audio.TimeBase, expected[i].Duration, timeBase), $"Packet {i}: duration {p.Duration}.");
        }
    }

    /// <summary>
    /// The LAME tag's delay plus the decoder's 529 samples are skipped from the start, its padding less 529 cut from the
    /// end (LAME's delay is 576 and FFmpeg's padding 1404 here); without a tag nothing is.
    /// </summary>
    [Theory]
    [InlineData("mp3-raw-cbr.mp3", 1105, 875)]
    [InlineData("mp3-raw-vbr.mp3", 1105, 875)]
    [InlineData("mp3-raw-noxing.mp3", 0, 0)]
    public void TrimsByTheLameTag(string name, int skip, int skipEnd)
    {
        if (TestSamples.Directory is null || !File.Exists(TestSamples.PathOf(name)))
        {
            return;
        }

        using var demuxer = Demuxer.Open(TestSamples.PathOf(name));
        var packets = demuxer.ReadPackets().ToList();
        Assert.Equal(skip, packets[0].SkipSamples);
        Assert.Equal(skipEnd, packets[^1].SkipEndSamples);
        Assert.All(packets.Skip(1), p => Assert.Equal(0, p.SkipSamples));
        Assert.All(packets.SkipLast(1), p => Assert.Equal(0, p.SkipEndSamples));
        Assert.DoesNotContain(packets, p => p.Discard);
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

    /// <summary>Bytes that are no frame between two frames are skipped, as FFmpeg does: the same packets.</summary>
    [Fact]
    public void SkipsJunkBetweenFrames()
    {
        if (!TryFrames(out var file, out var frames, out int start))
        {
            return;
        }

        int cut = start + frames[0].Length + frames[1].Length;
        byte[] junk = [.. file[..cut], .. Enumerable.Repeat(new byte[] { 0x12, 0x34, 0xFF, 0x78 }, 5).SelectMany(b => b), .. file[cut..]];
        Assert.Equal(frames, Packets(junk));
    }

    /// <summary>ID3v2 tags before the frames and an ID3v1 tag after them are no packets.</summary>
    [Fact]
    public void SkipsTags()
    {
        if (!TryFrames(out var file, out var frames, out int start))
        {
            return;
        }

        byte[] id3v2 = [.. "ID3"u8, 4, 0, 0, 0, 0, 0, 3, 1, 2, 3];
        byte[] id3v1 = [.. "TAG"u8, .. new byte[125]];
        Assert.Equal(frames, Packets([.. id3v2, .. id3v2, .. file[start..], .. id3v1]));
    }

    /// <summary>A file that breaks off inside a frame gives the frames before it, then <see cref="InvalidDataException"/>.</summary>
    [Fact]
    public void TruncatedFrame_Throws()
    {
        if (!TryFrames(out var file, out var frames, out _))
        {
            return;
        }

        using var demuxer = Demuxer.Open(new MemoryStream(file[..^5]));
        Assert.Equal(frames.Count - 1, demuxer.Audio!.FrameCount);
        var packets = new List<byte[]>();
        Assert.Throws<InvalidDataException>(() =>
        {
            foreach (var packet in demuxer.ReadPackets())
            {
                packets.Add(packet.Data.ToArray());
            }
        });
        Assert.Equal(frames[..^1], packets);
    }

    /// <summary>
    /// Only Layer III: the same frames marked Layer II (an .mp2 file) or with a free-format bitrate are no MP3 file, and
    /// <see cref="Demuxer.Open(Stream, bool)"/> takes them for no format.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OtherLayersAndFreeFormat_AreNotMp3(bool layer2)
    {
        if (!TryFrames(out var file, out var frames, out int start))
        {
            return;
        }

        var changed = (byte[])file.Clone();
        for (int at = start, i = 0; i < frames.Count; at += frames[i].Length, i++)
        {
            if (layer2)
            {
                changed[at + 1] = (byte)((changed[at + 1] & 0xF9) | (2 << 1)); // layer bits 10: Layer II
            }
            else
            {
                changed[at + 2] &= 0x0F; // bitrate_index 0: free format
            }
        }

        Assert.Throws<NotSupportedException>(() => Demuxer.Open(new MemoryStream(changed)));
        Assert.Throws<InvalidDataException>(() => new Mp3Demuxer(new MemoryStream(changed)));
    }

    // The sample's file, its packets, and where its first frame starts (after FFmpeg's ID3v2 tag).
    private static bool TryFrames(out byte[] file, out List<byte[]> frames, out int start)
    {
        file = [];
        frames = [];
        start = 0;
        if (TestSamples.Directory is null || !File.Exists(TestSamples.PathOf(Sample)))
        {
            return false;
        }

        file = File.ReadAllBytes(TestSamples.PathOf(Sample));
        frames = Packets(file);
        Assert.True(frames.Count > 10);
        start = file.AsSpan().IndexOf(frames[0]);
        return true;
    }

    private static List<byte[]> Packets(byte[] file)
    {
        using var demuxer = Demuxer.Open(new MemoryStream(file));
        return [.. demuxer.ReadPackets().Select(p => p.Data.ToArray())];
    }
}
