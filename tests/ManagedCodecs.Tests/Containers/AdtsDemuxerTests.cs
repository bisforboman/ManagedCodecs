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

    [Fact]
    public void SeveralRawDataBlocks_NotSupported()
    {
        if (!TryFrames(out var file, out var frames))
        {
            return;
        }

        using var demuxer = Demuxer.Open(new MemoryStream(file));
        var decoder = new AacDecoder(demuxer.Audio!);
        var frame = frames[0].ToArray();
        frame[6] |= 1; // number_of_raw_data_blocks_in_frame 1: two blocks
        Assert.Throws<NotSupportedException>(() => decoder.Decode(frame));
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

    private static List<float[]> Decode(byte[] file)
    {
        using var demuxer = Demuxer.Open(new MemoryStream(file));
        var decoder = new AacDecoder(demuxer.Audio!);
        return [.. demuxer.ReadPackets().SelectMany(p => decoder.Decode(p).Samples)];
    }
}
