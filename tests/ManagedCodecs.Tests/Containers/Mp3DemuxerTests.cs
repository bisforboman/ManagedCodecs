using System.Buffers.Binary;
using System.Globalization;
using ManagedCodecs.Containers;

namespace ManagedCodecs.Tests.Containers;

/// <summary>
/// Raw MP3: every samples/*.mp3 demuxed packet by packet against FFmpeg's (samples/*.packets.framemd5, whose timestamps
/// are in another time base, so they are compared as instants; FFmpeg's demuxer leaves the info frame out too), the
/// gapless trim from the LAME tag (the decoded samples are checked in Mp3ConformanceTests), and the cases FFmpeg's
/// samples don't have, built from them: junk between frames, tags around them, a truncated end, other layers. The rules
/// FFmpeg's demuxer follows that libmp3lame's files don't reach (a first frame unlike the next, tag frames that count
/// nothing, padding over more than one packet) on frames built here, whose contents don't matter to the demuxer; each
/// was found by altering libmp3lame's files and reading ffprobe's packets.
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

        // ffprobe's packets with their side data: the skip samples at both ends, and the discard flag.
        var probed = File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".packets.csv")).Select(SkipsOf).ToList();
        Assert.Equal(expected.Count, probed.Count);
        for (int i = 0; i < packets.Count; i++)
        {
            var p = packets[i];
            Assert.Equal((expected[i].Size, expected[i].Md5), (p.Data.Length, FrameMd5.Hash(p.Data.Span)));
            Assert.True(FrameMd5.SameTime(p.Dts, audio.TimeBase, expected[i].Dts, timeBase), $"Packet {i}: dts {p.Dts}.");
            Assert.True(FrameMd5.SameTime(p.Pts, audio.TimeBase, expected[i].Pts, timeBase), $"Packet {i}: pts {p.Pts}.");
            Assert.True(FrameMd5.SameTime(p.Duration, audio.TimeBase, expected[i].Duration, timeBase), $"Packet {i}: duration {p.Duration}.");
            Assert.True(probed[i] == (p.SkipSamples, p.SkipEndSamples, p.Discard), $"Packet {i}: {(p.SkipSamples, p.SkipEndSamples, p.Discard)}, FFmpeg's {probed[i]}.");
        }
    }

    /// <summary>
    /// A first frame whose header differs from the next one's in the mode, copyright, original or emphasis is no packet
    /// to FFmpeg, and the next one neither while it differs from its own next; a frame differing later is kept, and so
    /// is a first frame differing only where frames of a stream may (private bit, mode extension, protection, padding).
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
        var frames = BuiltFrames(5);
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
    /// A first frame with a Xing or Info tag (after the side information) is no packet when the tag counts the frames or
    /// the bytes; one that counts neither (no fields, or a table of contents alone) is a packet, as FFmpeg keeps it.
    /// </summary>
    [Theory]
    [InlineData("Info", 1, true)]
    [InlineData("Xing", 1, true)]
    [InlineData("Info", 2, true)]
    [InlineData("Info", 0, false)]
    [InlineData("Info", 4, false)]
    public void TagFrame_IsNoPacketWhenItCounts(string name, int flags, bool skipped)
    {
        var frames = BuiltFrames(5);
        var tag = frames[0];
        name.Select(c => (byte)c).ToArray().CopyTo(tag, 4 + 17);
        BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(4 + 17 + 4), (uint)flags);
        if (flags is 1 or 2)
        {
            BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(4 + 17 + 8), 4);
        }

        Assert.Equal(skipped ? frames[1..] : frames, Packets(Join(frames)));
    }

    /// <summary>A VBRI tag (32 bytes after the header): the same, by its byte and frame counts; its delay isn't taken.</summary>
    [Theory]
    [InlineData(4, true)]
    [InlineData(0, false)]
    public void VbriFrame_IsNoPacketWhenItCounts(int frameCount, bool skipped)
    {
        var frames = BuiltFrames(5);
        var tag = frames[0];
        "VBRI"u8.CopyTo(tag.AsSpan(4 + 32));
        BinaryPrimitives.WriteUInt16BigEndian(tag.AsSpan(4 + 32 + 6), 576);
        BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(4 + 32 + 10), 0);
        BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(4 + 32 + 14), (uint)frameCount);
        using var demuxer = new Mp3Demuxer(new MemoryStream(Join(frames)));
        var packets = demuxer.ReadPackets().ToList();
        Assert.Equal(skipped ? frames[1..] : frames, packets.Select(p => p.Data.ToArray()));
        Assert.All(packets, p => Assert.Equal((0, 0), (p.SkipSamples, p.SkipEndSamples)));
    }

    /// <summary>
    /// The LAME extension's padding less 529 over as many packets as it covers, a packet covered whole skipping all its
    /// samples rather than discarded (FFmpeg: padding 2000 over 40 frames gives 319 and then 1152); none below 529, none
    /// without the frame count, none for an encoder name FFmpeg doesn't take.
    /// </summary>
    [Theory]
    [InlineData("LAME", 576, 1404, 1, 1105, new[] { 0, 0, 0, 875 })]
    [InlineData("Lavf", 0, 2000, 1, 529, new[] { 0, 0, 319, 1152 })]
    [InlineData("LAME", 576, 300, 1, 1105, new[] { 0, 0, 0, 0 })]
    [InlineData("LAME", 576, 1404, 2, 1105, new[] { 0, 0, 0, 0 })]
    [InlineData("Xxxx", 576, 1404, 1, 0, new[] { 0, 0, 0, 0 })]
    public void LamePadding_IsSkippedOverThePacketsItCovers(string encoder, int delay, int padding, int flags, int startSkip, int[] endSkips)
    {
        var frames = BuiltFrames(5);
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
        Assert.DoesNotContain(packets, p => p.Discard);
        Assert.Equal(packets.Select(ReadCountingStream.InfoOf), demuxer.ReadPacketInfo());
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

    // A line of the .packets.csv (stream, pts, dts, duration, size, flags, hash, then any side data): its skip samples
    // at the start and the end, and whether it is discarded.
    private static (int Start, int End, bool Discard) SkipsOf(string line)
    {
        var f = line.Split(',');
        bool skips = f.Length > 9 && f[7] == "Skip Samples";
        return (skips ? int.Parse(f[8], CultureInfo.InvariantCulture) : 0, skips ? int.Parse(f[9], CultureInfo.InvariantCulture) : 0, f[5].Contains('D', StringComparison.Ordinal));
    }

    // MPEG-1 Layer III frames, 128 kbit/s at 44.1 kHz, single channel, original, no CRC (417 bytes, 1152 samples), each
    // its own bytes after the header.
    private static List<byte[]> BuiltFrames(int count) =>
        [.. Enumerable.Range(0, count).Select(i => (byte[])[0xFF, 0xFB, 0x90, 0xC4, .. Enumerable.Repeat((byte)(i + 1), 417 - 4)])];

    private static byte[] Join(IEnumerable<byte[]> frames) => [.. frames.SelectMany(f => f)];

    private static List<byte[]> Packets(byte[] file)
    {
        using var demuxer = Demuxer.Open(new MemoryStream(file));
        return [.. demuxer.ReadPackets().Select(p => p.Data.ToArray())];
    }
}
