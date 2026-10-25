using System.Buffers.Binary;
using System.Text;
using ManagedCodecs.Containers;
using ManagedCodecs.Containers.Mp4;

namespace ManagedCodecs.Tests.Containers;

public class Mp4DemuxerTests
{
    // Three samples, the first two in one chunk and the third in another after a gap byte.
    private static readonly byte[][] SampleData = [[1, 2, 3], [4, 5], [6, 7, 8, 9]];

    // One AAC packet after the video samples.
    private static readonly byte[] AudioData = [0x21, 0x10, 0x05];

    [Fact]
    public void ReadsSampleTablesAndTimestamps()
    {
        using var demuxer = new Mp4Demuxer(new MemoryStream(TinyMp4()));

        var video = demuxer.Video!;
        Assert.Equal(CodecIds.Mjpeg, video.Codec); // mp4v with object type 0x6C
        Assert.Equal((32, 16), (video.Width, video.Height));
        Assert.Equal(new Rational(1, 90_000), video.TimeBase);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, video.CodecPrivate.ToArray());
        Assert.Equal(30.0, video.FramesPerSecond, precision: 6);
        Assert.Equal(3, video.FrameCount);

        var packets = demuxer.ReadPackets().Where(p => p.StreamIndex == demuxer.Video!.StreamIndex).ToList();

        Assert.Equal(1, demuxer.Video!.StreamIndex); // the audio track comes first
        Assert.Equal(SampleData, packets.Select(p => p.Data.ToArray()));
        Assert.Equal([true, false, true], packets.Select(p => p.IsKeyframe));
        Assert.Equal([3000L, 3000, 3000], packets.Select(p => p.Duration));

        // Raw dts 0, 3000, 6000 and pts 3000, 6000, 6000. The edit list starts with 500 ms of
        // nothing (45000 ticks) and then plays from media time 200, so everything moves by
        // 45000 - 200 = 44800.
        Assert.Equal([44_800L, 47_800, 50_800], packets.Select(p => p.Dts));
        Assert.Equal([47_800L, 50_800, 50_800], packets.Select(p => p.Pts));
    }

    [Fact]
    public void ReadingTwiceGivesTheSamePackets()
    {
        using var demuxer = new Mp4Demuxer(new MemoryStream(TinyMp4()));

        string Describe(Packet p) => $"{p.StreamIndex} {p.Sequence} {p.Dts} {p.Pts} {Convert.ToHexString(p.Data.Span)}";
        var first = demuxer.ReadPackets().Select(Describe).ToList();

        Assert.Equal(first, demuxer.ReadPackets().Select(Describe));
        Assert.Equal([0L, 1, 2, 0], demuxer.ReadPackets().Select(p => p.Sequence)); // each track's from 0, video first in the file
    }

    [Fact]
    public void ReadsAudioTrackFromAudioSpecificConfig()
    {
        using var demuxer = new Mp4Demuxer(new MemoryStream(TinyMp4()));

        // The mp4a entry claims 44.1 kHz mono; the AudioSpecificConfig (normative) says 48 kHz stereo.
        var audio = demuxer.Audio!;
        Assert.Equal(CodecIds.Aac, audio.Codec);
        Assert.Equal((48_000, 2), (audio.SampleRate, audio.Channels));
        Assert.Equal(new byte[] { 0x11, 0x90 }, audio.CodecPrivate.ToArray());
        Assert.Equal(0, audio.StreamIndex);
        Assert.Equal(new Rational(1, 48_000), audio.TimeBase);

        var packets = demuxer.ReadPackets().ToList();
        var audioPacket = Assert.Single(packets, p => p.StreamIndex == 0);
        Assert.Equal(AudioData, audioPacket.Data.ToArray());
        Assert.Equal(1024, audioPacket.Duration);

        // Interleaved in file order: the audio sample sits after the video samples.
        Assert.Equal(0, packets[^1].StreamIndex);
    }

    [Fact]
    public void DemuxerOpen_RecognizesMp4()
    {
        using var demuxer = Demuxer.Open(new MemoryStream(TinyMp4()));

        Assert.IsType<Mp4Demuxer>(demuxer);
    }

    /// <summary>
    /// A fragmented file built by hand for what FFmpeg's muxer never writes: track fragments without
    /// a base offset (the first starts at its 'moof', the next where the previous one's data
    /// ended), no 'tfdt' (samples continue from the track's last one), 'trex' defaults, a second
    /// 'trun' without a data offset, per-sample flags, negative composition offsets in a version 1
    /// 'trun', and a track fragment of a track that is not exposed.
    /// </summary>
    [Fact]
    public void ReadsTrackFragments()
    {
        using var demuxer = new Mp4Demuxer(new MemoryStream(FragmentedMp4()));
        var video = demuxer.ReadPackets().Where(p => p.StreamIndex == demuxer.Video!.StreamIndex).ToList();
        var audio = demuxer.ReadPackets().Where(p => p.StreamIndex == demuxer.Audio!.StreamIndex).ToList();

        Assert.Equal(4, demuxer.Video!.FrameCount);

        // The composition offset of -3000 moves every video pts forward by 3000, as in FFmpeg.
        Assert.Equal(
            [(0L, 3000L, 3000L, true, 1), (3000, 6000, 3000, false, 2), (9000, 9000, 3000, false, 3), (12000, 15000, 3000, true, 4)],
            video.Select(p => (p.Dts, p.Pts, p.Duration, p.IsKeyframe, (int)p.Data.Span[0])));
        Assert.Equal([3, 2, 4, 1], video.Select(p => p.Data.Length));
        Assert.All(video, p => Assert.All(p.Data.ToArray(), b => Assert.Equal(p.Data.Span[0], b)));

        var packet = Assert.Single(audio);
        Assert.Equal((0L, 0L, 1024L, true), (packet.Dts, packet.Pts, packet.Duration, packet.IsKeyframe));
        Assert.Equal(new byte[] { 9, 9, 9, 9, 9 }, packet.Data.ToArray());
    }

    /// <summary>
    /// A track fragment without a base offset after one of a track that is not read: where its data
    /// starts is not known, so the file is refused rather than misread.
    /// </summary>
    [Fact]
    public void RefusesBaselessFragmentAfterUnreadTrack()
    {
        var error = Assert.Throws<NotSupportedException>(() => new Mp4Demuxer(new MemoryStream(FragmentedMp4(unreadTrackFirst: true))));
        Assert.Contains("not read", error.Message);
    }

    private static byte[] FragmentedMp4(bool unreadTrackFirst = false)
    {
        var ftyp = Box("ftyp", Ascii("iso5"), U32(0));
        byte[] Stbl(byte[] description) => Box("stbl",
            description,
            FullBox("stts", 0, U32(0)),
            FullBox("stsc", 0, U32(0)),
            FullBox("stsz", 0, U32(0), U32(0)),
            FullBox("stco", 0, U32(0)));
        byte[] TrackHeader(uint id) => FullBox("tkhd", 0, new byte[8], U32(id), new byte[68]);
        var moov = Box("moov",
            FullBox("mvhd", 0, new byte[8], U32(1000), new byte[84]),
            Box("trak", TrackHeader(1), Box("mdia", MediaHeader(), Handler("vide"), Box("minf", Stbl(SampleDescription())))),
            Box("trak", TrackHeader(2), Box("mdia", MediaHeader(timescale: 48_000), Handler("soun"), Box("minf", Stbl(AudioSampleDescription())))),
            Box("mvex",
                FullBox("trex", 0, U32(1), U32(1), U32(3000), U32(0), U32(0x0001_0000)), // video: non-sync by default
                FullBox("trex", 0, U32(2), U32(1), U32(1024), U32(5), U32(0))));

        // Fragment 1: video samples of 3 and 2 bytes (the first a sync sample by first_sample_flags),
        // then the audio sample (5 bytes, all from 'trex') where the video data ends, and a fragment
        // of track 9, which does not exist.
        var unread = Box("traf",
            Flagged("tfhd", 0, 0, U32(9)),
            Flagged("trun", 0, 0x000200, U32(1), U32(7)));
        var videoFragment = Box("traf",
            Flagged("tfhd", 0, 0, U32(1)),
            Flagged("trun", 0, 0x000205, U32(2), U32(0), U32(0), U32(3), U32(2)));
        byte[] Moof1(int dataOffset)
        {
            BinaryPrimitives.WriteUInt32BigEndian(videoFragment.AsSpan(videoFragment.Length - 16), (uint)dataOffset);
            var audioFragment = Box("traf",
                Flagged("tfhd", 0, 0, U32(2)),
                Flagged("trun", 0, 0, U32(1)));
            return unreadTrackFirst
                ? Box("moof", FullBox("mfhd", 0, U32(1)), videoFragment, unread, audioFragment)
                : Box("moof", FullBox("mfhd", 0, U32(1)), videoFragment, audioFragment, unread);
        }

        int moof1Size = Moof1(0).Length;
        var moof1 = Moof1(moof1Size + 8);
        var mdat1 = Box("mdat", [1, 1, 1, 2, 2, 9, 9, 9, 9, 9]);

        // Fragment 2, data based at the 'moof': decode time 9000, one sample with duration, size and a
        // composition offset of -3000 (version 1), then a second 'trun' with no data offset whose one
        // sample follows on and is a sync sample by its own flags.
        byte[] Moof2(int dataOffset) => Box("moof",
            FullBox("mfhd", 0, U32(2)),
            Box("traf",
                Flagged("tfhd", 0, 0x020000, U32(1)),
                FullBox("tfdt", 1, U64(9000)),
                Flagged("trun", 1, 0x000B01, U32(1), U32((uint)dataOffset), U32(3000), U32(4), U32(unchecked((uint)-3000))),
                Flagged("trun", 0, 0x000600, U32(1), U32(1), U32(0))));
        int moof2Size = Moof2(0).Length;
        var moof2 = Moof2(moof2Size + 8);
        var mdat2 = Box("mdat", [3, 3, 3, 3, 4]);

        return [.. ftyp, .. moov, .. moof1, .. mdat1, .. moof2, .. mdat2];
    }

    /// <summary>A FullBox with flags.</summary>
    private static byte[] Flagged(string type, byte version, uint flags, params byte[][] parts) =>
        Box(type, [[version, (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags], .. parts]);

    [Fact]
    public void RejectsFileWithoutMoov()
    {
        Assert.Throws<InvalidDataException>(() => new Mp4Demuxer(new MemoryStream(Box("ftyp", Ascii("isom"), U32(0)))));
    }

    private static byte[] TinyMp4()
    {
        var ftyp = Box("ftyp", Ascii("isom"), U32(512), Ascii("isom"));

        // mdat payload: chunk 1 = samples 0 and 1, then a gap byte, then chunk 2 = sample 2.
        byte[] payload = [.. SampleData[0], .. SampleData[1], 0xEE, .. SampleData[2], .. AudioData];
        var mdat = Box("mdat", payload);
        long chunk1 = ftyp.Length + 8;
        long chunk2 = chunk1 + SampleData[0].Length + SampleData[1].Length + 1;
        long audioChunk = chunk2 + SampleData[2].Length;

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

        var audio = Box("trak", Box("mdia", MediaHeader(timescale: 48_000), Handler("soun"), Box("minf", Box("stbl",
            AudioSampleDescription(),
            FullBox("stts", 0, U32(1), U32(1), U32(1024)),
            FullBox("stsc", 0, U32(1), U32(1), U32(1), U32(1)),
            FullBox("stsz", 0, U32(0), U32(1), U32((uint)AudioData.Length)),
            FullBox("stco", 0, U32(1), U32((uint)audioChunk))))));

        var moov = Box("moov", FullBox("mvhd", 0, new byte[8], U32(1000), new byte[84]), audio, video);
        return [.. ftyp, .. mdat, .. moov];
    }

    private static byte[] MediaHeader(int version = 0, uint timescale = 90_000) => version == 1
        ? FullBox("mdhd", 1, new byte[16], U32(timescale), U64(9000), new byte[4])
        : FullBox("mdhd", 0, new byte[8], U32(timescale), U32(9000), new byte[4]);

    private static byte[] AudioSampleDescription()
    {
        // AudioSampleEntry: channelcount at 16, samplesize at 18, 16.16 samplerate at 24.
        var entry = new byte[28];
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(18), 16);
        BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(24), 44_100u << 16);

        // AudioSpecificConfig: AAC LC (2), 48 kHz (index 3), 2 channels.
        byte[] decoderConfig = [0x04, 17, 0x40, 0x15, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x05, 2, 0x11, 0x90];
        byte[] esDescriptor = [0x03, (byte)(3 + decoderConfig.Length), 0, 2, 0, .. decoderConfig];
        return FullBox("stsd", 0, U32(1), Box("mp4a", entry, FullBox("esds", 0, esDescriptor)));
    }

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
