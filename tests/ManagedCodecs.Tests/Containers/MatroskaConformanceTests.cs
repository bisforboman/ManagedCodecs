using System.Globalization;
using ManagedCodecs.Containers;

namespace ManagedCodecs.Tests.Containers;

/// <summary>
/// Demuxes every samples/*.mkv and *.webm and compares each packet with FFmpeg's: pts, size, MD5 and keyframe flag with
/// samples/*.packets.csv (<c>ffprobe -show_packets -show_data_hash MD5</c>), durations with *.packets.nofillin.csv (the
/// same with <c>-fflags +nofillin</c>: the file's own, N/A where it gives none), the codec private data with
/// *.packets.framemd5, and the streams with *.streams (<c>ffprobe -show_streams</c>). Decode times, which Matroska
/// doesn't store, are checked for what they must be.
/// </summary>
public class MatroskaConformanceTests
{
    public static TheoryData<string> Samples() =>
        new(TestSamples.WithReference(".packets.nofillin.csv").Where(name => name.EndsWith(".mkv", StringComparison.Ordinal) || name.EndsWith(".webm", StringComparison.Ordinal)));

    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesFfmpegPacketByPacket(string sampleName)
    {
        var expected = File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".packets.csv")).Select(ProbedPacket.Parse).ToList();
        var own = File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".packets.nofillin.csv")).Select(ProbedPacket.Parse).ToList();
        var lines = File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".packets.framemd5"));

        using var demuxer = MatroskaDemuxer.Open(TestSamples.PathOf(sampleName));
        var packets = demuxer.ReadPackets().ToList();

        // framemd5 numbers streams as FFmpeg maps them: video first, then audio.
        var streams = new List<(int Index, long Count, ReadOnlyMemory<byte> CodecPrivate)>();
        if (demuxer.Video is { } video)
        {
            streams.Add((video.StreamIndex, video.FrameCount, video.CodecPrivate));
        }

        if (demuxer.Audio is { } audio)
        {
            streams.Add((audio.StreamIndex, audio.FrameCount, audio.CodecPrivate));
        }

        Assert.Equal(expected.Select(e => e.StreamIndex).Distinct().Order(), streams.Select(s => s.Index).Order());
        Assert.Equal(expected.Select(e => e.StreamIndex), packets.Select(p => p.StreamIndex)); // file order
        for (int mapped = 0; mapped < streams.Count; mapped++)
        {
            var (index, count, codecPrivate) = streams[mapped];
            var extradata = FrameMd5.ParseExtradata(lines, mapped);
            if (extradata is null)
            {
                Assert.True(codecPrivate.IsEmpty, $"Stream {index}: FFmpeg reports no extradata but we found some.");
            }
            else
            {
                Assert.Equal(extradata.Value, (codecPrivate.Length, FrameMd5.Hash(codecPrivate.Span)));
            }

            var theirs = expected.Where(e => e.StreamIndex == index).ToList();
            var filed = own.Where(e => e.StreamIndex == index).ToList();
            var ours = packets.Where(p => p.StreamIndex == index).ToList();
            Assert.Equal(theirs.Count, ours.Count);
            Assert.Equal(theirs.Count, count);

            for (int i = 0; i < ours.Count; i++)
            {
                var p = ours[i];
                Assert.Equal(i, p.Sequence);
                Assert.Equal(
                    (theirs[i].Pts, theirs[i].Size, theirs[i].IsKeyframe, theirs[i].Md5),
                    (p.Pts, p.Data.Length, p.IsKeyframe, FrameMd5.Hash(p.Data.Span)));

                // A duration the file gives (a BlockDuration, the DefaultDuration); else, for audio, up to the next
                // frame, and the last frame's as the one before.
                long duration = filed[i].Duration != Unset ? filed[i].Duration
                    : i + 1 < theirs.Count ? theirs[i + 1].Pts - theirs[i].Pts
                    : ours[i - 1].Duration;
                Assert.True(duration == p.Duration, $"Stream {index}, packet {i}: duration {p.Duration}, expected {duration}.");

                // Decode times (none in the file): at or before the pts, never going back, and at or after FFmpeg's where
                // it derives one (it delays the sorted presentation times by the reorder depth the codec declares, at
                // least the fewest frames that keep them at or before the pts, which this demuxer takes).
                Assert.True(p.Dts <= p.Pts && (i == 0 || ours[i - 1].Dts <= p.Dts), $"Stream {index}, packet {i}: dts {p.Dts} with pts {p.Pts}.");
                Assert.True(theirs[i].Dts == Unset || theirs[i].Dts <= p.Dts, $"Stream {index}, packet {i}: dts {p.Dts}, FFmpeg's {theirs[i].Dts}.");
            }
        }
    }

    /// <summary>The streams' facts as ffprobe reports them: size, aspect ratio, time base, rate, channels, the file's duration.</summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public void DescribesTheStreamsAsFfprobe(string sampleName)
    {
        var fields = new Dictionary<int, Dictionary<string, string>>();
        string? fileDuration = null;
        int current = -1;
        foreach (string line in File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".streams")))
        {
            var (key, value) = (line[..line.IndexOf('=', StringComparison.Ordinal)], line[(line.IndexOf('=', StringComparison.Ordinal) + 1)..]);
            if (key == "index")
            {
                current = int.Parse(value, CultureInfo.InvariantCulture);
                fields[current] = [];
            }
            else if (key == "duration")
            {
                fileDuration = value;
            }
            else
            {
                fields[current][key] = value;
            }
        }

        using var demuxer = MatroskaDemuxer.Open(TestSamples.PathOf(sampleName));
        long expectedDuration = fileDuration is null or "N/A" ? 0 : (long)Math.Round(double.Parse(fileDuration, CultureInfo.InvariantCulture) * 1000);
        if (demuxer.Video is { } video)
        {
            var f = fields[video.StreamIndex];
            Assert.Equal("video", f["codec_type"]);
            Assert.Equal((f["width"], f["height"]), (video.Width.ToString(CultureInfo.InvariantCulture), video.Height.ToString(CultureInfo.InvariantCulture)));
            Assert.Equal(f["sample_aspect_ratio"], $"{video.SampleAspectRatio.Numerator}:{video.SampleAspectRatio.Denominator}");
            Assert.Equal(f["time_base"], video.TimeBase.ToString());
            Assert.Equal(expectedDuration, video.Duration);
        }

        if (demuxer.Audio is { } audio)
        {
            var f = fields[audio.StreamIndex];
            Assert.Equal("audio", f["codec_type"]);
            Assert.Equal(f["sample_rate"], audio.SampleRate.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(f["channels"], audio.Channels.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(f["time_base"], audio.TimeBase.ToString());
            Assert.Equal(expectedDuration, audio.Duration);
        }
    }

    /// <summary>
    /// <see cref="MatroskaDemuxer.ReadPacketInfo"/> gives <see cref="MatroskaDemuxer.ReadPackets"/>'s packets (checked
    /// against FFmpeg's above) without their data, in the same order, and reads nothing from the file to do it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public void PacketInfo_IsThePacketsWithoutData(string sampleName)
    {
        var bytes = File.ReadAllBytes(TestSamples.PathOf(sampleName));
        using var stream = new ReadCountingStream(bytes);
        using var demuxer = new MatroskaDemuxer(stream);
        var expected = demuxer.ReadPackets().Select(ReadCountingStream.InfoOf).ToList();

        stream.Reads.Clear();
        Assert.Equal(expected, demuxer.ReadPacketInfo());
        Assert.Empty(stream.Reads);
    }

    /// <summary>The codec each sample's tracks map to: CodecIds where there is one, Matroska's Codec ID otherwise.</summary>
    [Theory]
    [InlineData("mkv-h264-aac.mkv", CodecIds.H264, CodecIds.Aac)]
    [InlineData("webm-vp9-opus.webm", "V_VP9", "A_OPUS")]
    [InlineData("mkv-mjpeg-sar.mkv", CodecIds.Mjpeg, null)]
    [InlineData("mkv-lace-fixed.mkv", CodecIds.H264, "A_PCM/INT/LIT")]
    public void NamesTheCodecs(string sampleName, string video, string? audio)
    {
        if (TestSamples.Directory is null || !File.Exists(TestSamples.PathOf(sampleName)))
        {
            return;
        }

        using var demuxer = Demuxer.Open(TestSamples.PathOf(sampleName));
        Assert.IsType<MatroskaDemuxer>(demuxer);
        Assert.Equal(video, demuxer.Video?.Codec);
        Assert.Equal(audio, demuxer.Audio?.Codec);
    }

    /// <summary>
    /// The Colour element FFmpeg's muxer wrote (BT.709 primaries, transfer and matrix, full range; ffprobe's stream shows
    /// the MJPEG decoder's matrix instead), and DefaultDuration's frame rate.
    /// </summary>
    [Fact]
    public void ReadsTheColourElement()
    {
        if (TestSamples.Directory is null || !File.Exists(TestSamples.PathOf("mkv-mjpeg-sar.mkv")))
        {
            return;
        }

        using var demuxer = MatroskaDemuxer.Open(TestSamples.PathOf("mkv-mjpeg-sar.mkv"));
        var video = demuxer.Video!;
        Assert.Equal(
            (ColorRange.Full, ColorPrimaries.Bt709, ColorTransfer.Bt709, ColorMatrix.Bt709, 25.0),
            (video.ColorRange, video.ColorPrimaries, video.ColorTransfer, video.ColorMatrix, video.FramesPerSecond));
    }

    private const long Unset = long.MinValue;

    /// <summary>A line of <c>ffprobe -show_packets -of csv=p=0</c>: stream, pts, dts, duration, size, flags, then the hash if asked.</summary>
    private sealed record ProbedPacket(int StreamIndex, long Pts, long Dts, long Duration, int Size, bool IsKeyframe, string? Md5)
    {
        public static ProbedPacket Parse(string line)
        {
            var f = line.Split(',');
            long Number(string field) => field == "N/A" ? Unset : long.Parse(field, CultureInfo.InvariantCulture);
            return new ProbedPacket(
                int.Parse(f[0], CultureInfo.InvariantCulture),
                Number(f[1]),
                Number(f[2]),
                Number(f[3]),
                int.Parse(f[4], CultureInfo.InvariantCulture),
                f[5][0] == 'K',
                f.Length > 6 ? f[6]["MD5:".Length..] : null);
        }
    }
}
