using System.Globalization;
using ManagedCodecs.Containers;

namespace ManagedCodecs.Tests.Containers;

/// <summary>
/// Demuxes every samples/*.mxf and compares each packet of its video and first audio stream with FFmpeg's: pts, size,
/// MD5, keyframe flag and duration with samples/*.packets.csv (<c>ffprobe -show_packets -show_data_hash MD5</c>), the
/// decode times with *.packets.nofillin.csv (FFmpeg's MXF demuxer's own, before libavformat moves MPEG-2's back a frame;
/// audio has none there, and its dts is its pts), and the streams' facts with the header metadata FFmpeg reads.
/// </summary>
public class MxfConformanceTests
{
    public static TheoryData<string> Samples() =>
        new(TestSamples.Directory is null
            ? Enumerable.Empty<string>()
            : Directory.EnumerateFiles(TestSamples.Directory, "*.mxf").Order()
                .Where(f => File.Exists(Path.ChangeExtension(f, ".packets.csv")))
                .Select(f => Path.GetFileName(f)));

    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesFfmpegPacketByPacket(string sampleName)
    {
        using var demuxer = Demuxer.Open(TestSamples.PathOf(sampleName));
        var mxf = Assert.IsType<MxfDemuxer>(demuxer);
        var streams = new[] { mxf.Video?.StreamIndex, mxf.Audio?.StreamIndex }.OfType<int>().ToHashSet();

        var expected = Parse(TestSamples.ReferenceOf(sampleName, ".packets.csv")).Where(p => streams.Contains(p.StreamIndex)).ToList();
        var own = Parse(TestSamples.ReferenceOf(sampleName, ".packets.nofillin.csv")).Where(p => streams.Contains(p.StreamIndex)).ToList();
        var packets = mxf.ReadPackets().ToList();

        Assert.Equal(expected.Select(p => p.StreamIndex), packets.Select(p => p.StreamIndex)); // file order
        Assert.Equal(mxf.ReadPacketInfo(), packets.Select(Info));
        var sequences = new Dictionary<int, long>();
        for (int i = 0; i < expected.Count; i++)
        {
            var e = expected[i];
            var p = packets[i];
            string at = $"packet {i} (stream {p.StreamIndex})";

            // Without an index table it reads (mxf-h264-pipe-norip: the footer's, which it doesn't reach), FFmpeg gives
            // video no times at all; ours come from the edit units, the index's found by walking to the footer.
            Assert.True((e.Pts ?? p.Pts) == p.Pts, $"{at}: pts {p.Pts}, FFmpeg's {e.Pts}");
            Assert.True(e.Size == p.Data.Length, $"{at}: size {p.Data.Length}, FFmpeg's {e.Size}");
            Assert.True(e.Md5 == FrameMd5.Hash(p.Data.Span), $"{at}: other data than FFmpeg's");
            Assert.True(e.IsKeyframe == p.IsKeyframe, $"{at}: keyframe {p.IsKeyframe}, FFmpeg's {e.IsKeyframe}");
            Assert.True(e.Duration == p.Duration, $"{at}: duration {p.Duration}, FFmpeg's {e.Duration}");

            // FFmpeg's demuxer's own decode times; where it gives none (audio; DNxHR's constant-size index, which has no
            // entries) the times libavformat fills in, which count edit units too.
            Assert.True((own[i].Dts ?? e.Dts ?? p.Dts) == p.Dts, $"{at}: dts {p.Dts}, FFmpeg's demuxer's {own[i].Dts}, filled in {e.Dts}");

            long sequence = sequences.GetValueOrDefault(p.StreamIndex);
            Assert.Equal(sequence, p.Sequence);
            sequences[p.StreamIndex] = sequence + 1;
        }

        if (mxf.Video is { } video)
        {
            Assert.Equal(sequences[video.StreamIndex], video.FrameCount);
        }

        if (mxf.Audio is { } audio)
        {
            Assert.Equal(sequences[audio.StreamIndex], audio.FrameCount);
            Assert.Equal(new Rational(1, audio.SampleRate), audio.TimeBase);
            var last = packets.Last(p => p.StreamIndex == audio.StreamIndex);
            Assert.Equal(last.Pts + last.Duration, audio.Duration);
        }
    }

    [Fact]
    public void NamesTheCodecsThisLibraryDecodes()
    {
        using var h264 = Demuxer.Open(TestSamples.PathOf("mxf-h264-pcm.mxf"));
        Assert.Equal(CodecIds.H264, h264.Video!.Codec);
        Assert.Equal(CodecIds.PcmS24Le, h264.Audio!.Codec);

        // The sampled height (120), not the stored one with the macroblock row's padding (128) FFmpeg's demuxer reports.
        Assert.Equal((160, 120), (h264.Video.Width, h264.Video.Height));
        Assert.Equal(new Rational(1, 25), h264.Video.TimeBase);
        Assert.Equal(25, h264.Video.FramesPerSecond);

        using var prores = Demuxer.Open(TestSamples.PathOf("mxf-prores-pcm16.mxf"));
        Assert.Equal("prores", prores.Video!.Codec);
        Assert.Equal(CodecIds.PcmS16Le, prores.Audio!.Codec);
        Assert.Equal(2, prores.Audio.Channels);

        using var interlaced = Demuxer.Open(TestSamples.PathOf("mxf-h264-interlaced.mxf"));
        Assert.Equal(new Rational(1001, 30000), interlaced.Video!.TimeBase);
        Assert.Equal(120, interlaced.Video.Height); // two sampled fields of 60
    }

    [Fact]
    public void AppliesTheIndexTablesTemporalOffsetsAndFlags()
    {
        // FFmpeg's muxer writes H.264 unreordered (PosTableIndex 0, temporal offsets 0): rewritten here, the first delta
        // entry says reordered (-1), edit unit 1 shows two later and 3 two earlier (11.2.3, Table 28), and edit unit
        // 0 loses its random access flag while 2 gains it.
        byte[] file = File.ReadAllBytes(TestSamples.PathOf("mxf-h264-pcm.mxf"));
        var (deltas, entries, entryLength) = IndexArrays(file);
        file[deltas + 8] = 0xFF;
        file[entries + 8 + entryLength] = 2;
        file[entries + 8 + (3 * entryLength)] = unchecked((byte)-2);
        file[entries + 8 + 2] &= 0x7F;
        file[entries + 8 + (2 * entryLength) + 2] |= 0x80;

        using var demuxer = new MxfDemuxer(new MemoryStream(file));
        var video = demuxer.ReadPacketInfo().Where(p => p.StreamIndex == demuxer.Video!.StreamIndex).ToList();
        Assert.Equal([0L, 3, 2, 1, 4], video.Take(5).Select(p => p.Pts));
        Assert.Equal([0L, 1, 2, 3, 4], video.Take(5).Select(p => p.Dts));
        Assert.Equal([false, false, true, false], video.Take(4).Select(p => p.IsKeyframe));

        // The same offsets without the reordering flag are not applied (they belong to another element).
        file[deltas + 8] = 0;
        using var unordered = new MxfDemuxer(new MemoryStream(file));
        Assert.Equal([0L, 1, 2, 3], unordered.ReadPacketInfo().Where(p => p.StreamIndex == unordered.Video!.StreamIndex).Take(4).Select(p => p.Pts));
    }

    [Fact]
    public void RefusesAnotherStreamAndSeeksEachStreamToItsKeyframe()
    {
        using var demuxer = new MxfDemuxer(File.OpenRead(TestSamples.PathOf("mxf-h264-index.mxf")));
        Assert.Throws<ArgumentOutOfRangeException>(() => demuxer.Seek(0, 7));

        // 12 s in: the video from its keyframe at 12 s (a GOP of 25), the audio from 12 s exactly (every packet a keyframe).
        demuxer.Seek(300, demuxer.Video!.StreamIndex);
        var read = demuxer.ReadPacketInfo().ToList();
        Assert.Equal(300, read.First(p => p.StreamIndex == demuxer.Video.StreamIndex).Pts);
        Assert.Equal(300 * 1920, read.First(p => p.StreamIndex == demuxer.Audio!.StreamIndex).Pts);
    }

    [Fact]
    public void ThrowsOnlyInvalidDataOnAFileCutShort()
    {
        byte[] file = File.ReadAllBytes(TestSamples.PathOf("mxf-h264-pcm.mxf"));
        foreach (int length in new[] { 20, 100, 600, 3000, 8000, file.Length / 2, file.Length - 70, file.Length - 1 })
        {
            try
            {
                using var demuxer = new MxfDemuxer(new MemoryStream(file[..length]));
                _ = demuxer.ReadPackets().Count();
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException)
            {
            }
        }
    }

    // Where the first index table segment's Delta Entry Array and Index Entry Array values start, and an entry's length.
    private static (int Deltas, int Entries, int EntryLength) IndexArrays(byte[] file)
    {
        byte[] key = Convert.FromHexString("060E2B34025301010D01020101100100");
        int at = file.AsSpan().IndexOf(key);
        Assert.True(at >= 0, "No index table segment.");
        int length = file[at + 16] == 0x83 ? (file[at + 17] << 16) | (file[at + 18] << 8) | file[at + 19] : throw new InvalidOperationException("Another BER length form.");
        int position = at + 20;
        int end = position + length;
        int deltas = -1;
        int entries = -1;
        while (position < end)
        {
            int tag = (file[position] << 8) | file[position + 1];
            int size = (file[position + 2] << 8) | file[position + 3];
            if (tag == 0x3F09)
            {
                deltas = position + 4;
            }
            else if (tag == 0x3F0A)
            {
                entries = position + 4;
            }

            position += 4 + size;
        }

        int entryLength = (file[entries + 4] << 24) | (file[entries + 5] << 16) | (file[entries + 6] << 8) | file[entries + 7];
        return (deltas, entries, entryLength);
    }

    [Fact]
    public void RejectsWhatIsNotMxf()
    {
        Assert.Throws<InvalidDataException>(() => new MxfDemuxer(new MemoryStream(new byte[200])));
        Assert.Throws<ArgumentException>(() => new MxfDemuxer(new NonSeekableStream()));
    }

    private static PacketInfo Info(Packet p) =>
        new(p.StreamIndex, p.Sequence, p.Data.Length, p.IsKeyframe, p.Dts, p.Pts, p.Duration, p.SkipSamples, p.Discard);

    private static List<Probed> Parse(string path) =>
    [
        .. File.ReadAllLines(path).Where(l => l.Length > 0).Select(line =>
        {
            string[] f = line.Split(',');
            long? Number(string s) => s == "N/A" ? null : long.Parse(s, CultureInfo.InvariantCulture);
            return new Probed(
                int.Parse(f[0], CultureInfo.InvariantCulture),
                Number(f[1]),
                Number(f[2]),
                Number(f[3]) ?? 0,
                int.Parse(f[4], CultureInfo.InvariantCulture),
                f[5].StartsWith('K'),
                f.Length > 6 ? f[6]["MD5:".Length..] : null);
        }),
    ];

    private sealed record Probed(int StreamIndex, long? Pts, long? Dts, long Duration, int Size, bool IsKeyframe, string? Md5);

    private sealed class NonSeekableStream : MemoryStream
    {
        public override bool CanSeek => false;
    }
}
