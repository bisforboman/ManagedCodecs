using ManagedCodecs.Aac;
using ManagedCodecs.Containers;
using ManagedCodecs.Containers.Adts;

namespace ManagedCodecs.Tests.Containers;

/// <summary>
/// Raw AAC (ADTS): every samples/*.aac demuxed packet by packet against FFmpeg's (samples/*.packets.framemd5, whose
/// timestamps are in another time base, so they are compared as instants), and the cases FFmpeg's samples don't
/// have built from samples/aac-adts.aac: junk between frames, tags around them, a truncated end, frames with a CRC.
/// </summary>
public class AdtsDemuxerTests
{
    private const string Sample = "aac-adts.aac";

    public static TheoryData<string> Samples() =>
        new(TestSamples.WithReference(".packets.framemd5").Where(name => name.EndsWith(".aac", StringComparison.Ordinal)));

    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesFfmpegPacketByPacket(string sampleName)
    {
        var lines = File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".packets.framemd5"));
        var expected = FrameMd5.Parse(lines);
        var timeBase = FrameMd5.ParseTimeBase(lines);
        Assert.Null(FrameMd5.ParseExtradata(lines));

        using var demuxer = Demuxer.Open(TestSamples.PathOf(sampleName));
        Assert.IsType<AdtsDemuxer>(demuxer);
        var audio = demuxer.Audio!;
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

        // The configuration the headers stand for is the decoder's.
        var decoder = new AacDecoder(audio);
        Assert.Equal((audio.SampleRate, audio.Channels), (decoder.SampleRate, decoder.Channels));
    }

    /// <summary>Bytes that are no frame between two frames are skipped, as FFmpeg does: the same packets.</summary>
    [Fact]
    public void SkipsJunkBetweenFrames()
    {
        if (!TryFrames(out var file, out var frames))
        {
            return;
        }

        int cut = frames[0].Length + frames[1].Length;
        byte[] junk = [.. file[..cut], .. Enumerable.Repeat(new byte[] { 0x12, 0x34, 0xFF, 0x78 }, 5).SelectMany(b => b), .. file[cut..]];
        Assert.Equal(frames, Packets(junk));
    }

    /// <summary>ID3v2 tags before the frames and an ID3v1 tag after them are no packets.</summary>
    [Fact]
    public void SkipsTags()
    {
        if (!TryFrames(out var file, out var frames))
        {
            return;
        }

        byte[] id3v2 = [.. "ID3"u8, 4, 0, 0, 0, 0, 0, 3, 1, 2, 3];
        byte[] id3v1 = [.. "TAG"u8, .. new byte[125]];
        Assert.Equal(frames, Packets([.. id3v2, .. id3v2, .. file, .. id3v1]));
    }

    /// <summary>A file that breaks off inside a frame gives the frames before it, then <see cref="InvalidDataException"/>.</summary>
    [Fact]
    public void TruncatedFrame_Throws()
    {
        if (!TryFrames(out var file, out var frames))
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
    /// Frames with a CRC after the header (protection_absent 0, which FFmpeg's muxer never writes) decode as the same
    /// frames without it; like FFmpeg by default, the CRC is not checked.
    /// </summary>
    [Fact]
    public void FramesWithCrc_DecodeTheSame()
    {
        if (!TryFrames(out var file, out var frames))
        {
            return;
        }

        var withCrc = new List<byte>();
        foreach (var frame in frames)
        {
            var header = frame[..7];
            header[1] &= 0xFE; // protection_absent 0
            int length = frame.Length + 2;
            header[3] = (byte)((header[3] & 0xFC) | (length >> 11));
            header[4] = (byte)(length >> 3);
            header[5] = (byte)((header[5] & 0x1F) | ((length & 7) << 5));
            withCrc.AddRange([.. header, 0xAB, 0xCD, .. frame[7..]]);
        }

        Assert.Equal(Decode(file), Decode([.. withCrc]));
    }

    /// <summary>
    /// Channel configuration 0: the AudioSpecificConfig the demuxer builds from the program config element at the start
    /// of the first frame is the one FFmpeg's ADTS-to-MP4 filter writes for the same stream, byte for byte (FFmpeg's
    /// element ends with a comment, after the bits realigned at their new place in the config; the FDK encoder's has
    /// none).
    /// </summary>
    [Theory]
    [InlineData("aac-adts-pce")]
    [InlineData("aac-fdk-pce")]
    public void ProgramConfig_IsFfmpegsMp4Config(string name)
    {
        if (TestSamples.Directory is null || !File.Exists(TestSamples.PathOf(name + ".aac")))
        {
            return;
        }

        using var adts = Demuxer.Open(TestSamples.PathOf(name + ".aac"));
        using var mp4 = Demuxer.Open(TestSamples.PathOf(name + "-remux.mp4"));
        Assert.Equal(mp4.Audio!.CodecPrivate.ToArray(), adts.Audio!.CodecPrivate.ToArray());
        Assert.Equal((6, 6), (adts.Audio.Channels, new AacDecoder(adts.Audio).Channels));
    }

    /// <summary>
    /// Channel configuration 0 without a program config element at the start of the first frame (FFmpeg writes it in
    /// the first frame only, so a file cut after that one) is refused.
    /// </summary>
    [Fact]
    public void ProgramConfigMissing_NotSupported()
    {
        if (TestSamples.Directory is null || !File.Exists(TestSamples.PathOf("aac-adts-pce.aac")))
        {
            return;
        }

        var file = File.ReadAllBytes(TestSamples.PathOf("aac-adts-pce.aac"));
        var frames = Packets(file);
        Assert.Throws<NotSupportedException>(() => new AdtsDemuxer(new MemoryStream(file[frames[0].Length..])));
    }

    /// <summary>
    /// Frames of several raw data blocks (which neither FFmpeg's muxer nor the FDK encoder writes, and FFmpeg's demuxer
    /// refuses), built from a sample's frames by joining 1 to 4 consecutive ones under one header: the same samples as
    /// the frames one by one, in packets as long as their blocks, also with CRCs (the header's then gives the blocks'
    /// positions, and each block has its own after it) and with SBR, whose state runs on across blocks as across frames.
    /// </summary>
    [Theory]
    [InlineData(Sample, false)]
    [InlineData(Sample, true)]
    [InlineData("heaac-adts.aac", false)]
    [InlineData("heaac-adts.aac", true)]
    public void SeveralRawDataBlocks_DecodeAsTheirFrames(string name, bool crc)
    {
        if (TestSamples.Directory is null || !File.Exists(TestSamples.PathOf(name)))
        {
            return;
        }

        var file = File.ReadAllBytes(TestSamples.PathOf(name));
        var frames = Packets(file);
        var joined = new List<byte>();
        var blocks = new List<int>();
        for (int i = 0; i < frames.Count; i += blocks[^1])
        {
            blocks.Add(Math.Min((blocks.Count % 4) + 1, frames.Count - i));
            joined.AddRange(Join(frames.GetRange(i, blocks[^1]), crc));
        }

        using var demuxer = Demuxer.Open(new MemoryStream([.. joined]));
        var packets = demuxer.ReadPackets().ToList();
        Assert.Equal(blocks.Select(n => 1024L * n), packets.Select(p => p.Duration));
        Assert.Equal(blocks.Select((_, i) => 1024L * blocks.Take(i).Sum()), packets.Select(p => p.Pts));
        Assert.Equal(DecodeJoined(file), DecodeJoined([.. joined]));
    }

    /// <summary>A frame that says it has more raw data blocks than it holds is malformed.</summary>
    [Fact]
    public void MissingRawDataBlock_IsInvalid()
    {
        if (!TryFrames(out var file, out var frames))
        {
            return;
        }

        using var demuxer = Demuxer.Open(new MemoryStream(file));
        var decoder = new AacDecoder(demuxer.Audio!);
        var frame = Join(frames.GetRange(0, 2), crc: false);
        frame[6] = (byte)((frame[6] & 0xFC) | 2); // three blocks
        Assert.Throws<InvalidDataException>(() => decoder.Decode(frame));
    }

    [Fact]
    public void NotAdts_IsNotRecognized()
    {
        // A sync word, but the frame it claims is not followed by another one.
        byte[] data = [0xFF, 0xF1, 0x50, 0x80, 0x02, 0x1F, 0xFC, .. new byte[20]];
        Assert.Throws<NotSupportedException>(() => Demuxer.Open(new MemoryStream(data)));
        Assert.Throws<InvalidDataException>(() => new AdtsDemuxer(new MemoryStream(data)));
    }

    private static bool TryFrames(out byte[] file, out List<byte[]> frames)
    {
        file = [];
        frames = [];
        if (TestSamples.Directory is null || !File.Exists(TestSamples.PathOf(Sample)))
        {
            return false;
        }

        file = File.ReadAllBytes(TestSamples.PathOf(Sample));
        frames = Packets(file);
        Assert.True(frames.Count > 10);
        return true;
    }

    private static List<byte[]> Packets(byte[] file)
    {
        using var demuxer = Demuxer.Open(new MemoryStream(file));
        return [.. demuxer.ReadPackets().Select(p => p.Data.ToArray())];
    }

    // ADTS frames (without CRCs) as one frame of their raw data blocks, with CRCs (of no value: they are not checked) if
    // asked: the header's after the positions of the blocks but the first, and one after each block.
    private static byte[] Join(List<byte[]> frames, bool crc)
    {
        var data = new List<byte>();
        foreach (var frame in frames)
        {
            data.AddRange(frame[7..]);
            if (crc && frames.Count > 1)
            {
                data.AddRange([0x12, 0x34]);
            }
        }

        int headerLength = crc ? 7 + (2 * frames.Count) : 7;
        var header = frames[0][..7];
        int length = headerLength + data.Count;
        header[1] = (byte)(crc ? header[1] & 0xFE : header[1] | 1);
        header[3] = (byte)((header[3] & 0xFC) | (length >> 11));
        header[4] = (byte)(length >> 3);
        header[5] = (byte)((header[5] & 0x1F) | ((length & 7) << 5));
        header[6] = (byte)((header[6] & 0xFC) | (frames.Count - 1));
        return [.. header, .. Enumerable.Repeat((byte)0xAB, headerLength - 7), .. data];
    }

    // Every channel's samples, all frames joined.
    private static float[][] DecodeJoined(byte[] file)
    {
        using var demuxer = Demuxer.Open(new MemoryStream(file));
        var decoder = new AacDecoder(demuxer.Audio!);
        var frames = demuxer.ReadPackets().Select(p => decoder.Decode(p)).ToList();
        return [.. Enumerable.Range(0, decoder.Channels).Select(c => frames.SelectMany(f => f.Samples[c]).ToArray())];
    }

    private static List<float[]> Decode(byte[] file)
    {
        using var demuxer = Demuxer.Open(new MemoryStream(file));
        var decoder = new AacDecoder(demuxer.Audio!);
        return [.. demuxer.ReadPackets().SelectMany(p => decoder.Decode(p).Samples)];
    }
}
