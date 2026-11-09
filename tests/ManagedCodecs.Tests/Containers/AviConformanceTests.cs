using System.Globalization;
using ManagedCodecs.Containers;

namespace ManagedCodecs.Tests.Containers;

/// <summary>
/// The audio of every samples/*.avi against ffprobe's packets (samples/*.packets.csv, <c>ffprobe -show_packets
/// -show_data_hash MD5</c>) and its extradata (samples/*.packets.framemd5): each packet's size and MD5, and its
/// timestamps and duration where FFmpeg gives them. MP3 in the CBR layout is timed at its frames' sample positions,
/// where FFmpeg's parser interpolates (docs/decisions/2026-11-08-avi-audio.md): its times are compared within 1.5 ms.
/// </summary>
public class AviConformanceTests
{
    public static TheoryData<string> Samples() =>
        new(TestSamples.WithReference(".packets.csv").Where(name => name.EndsWith(".avi", StringComparison.Ordinal)));

    [Theory]
    [MemberData(nameof(Samples))]
    public void AudioMatchesFfmpegPacketByPacket(string sampleName)
    {
        var expected = File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".packets.csv")).Select(Probed.Parse).ToList();
        using var demuxer = AviDemuxer.Open(TestSamples.PathOf(sampleName));
        var audio = demuxer.Audio;
        int videoIndex = demuxer.Video.StreamIndex;
        var theirs = expected.Where(e => e.StreamIndex != videoIndex).ToList();
        if (audio is null)
        {
            Assert.Empty(theirs);
            return;
        }

        Assert.All(theirs, e => Assert.Equal(audio.StreamIndex, e.StreamIndex));
        var extradata = FrameMd5.ParseExtradata(File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".packets.framemd5")), 1);
        Assert.Equal(extradata ?? (0, FrameMd5.Hash([])), (audio.CodecPrivate.Length, FrameMd5.Hash(audio.CodecPrivate.Span)));

        var ours = demuxer.ReadPackets().Where(p => p.StreamIndex == audio.StreamIndex).ToList();
        Assert.Equal(theirs.Count, ours.Count);

        // CBR MP3's frames are re-split: FFmpeg's times there count bytes (time base 1 / nAvgBytesPerSec), ours samples.
        int byteRate = AverageBytesPerSecond(TestSamples.PathOf(sampleName));
        bool resplit = audio.Codec == CodecIds.Mp3 && audio.TimeBase == new Rational(1, audio.SampleRate);
        for (int i = 0; i < ours.Count; i++)
        {
            var (p, e) = (ours[i], theirs[i]);
            Assert.Equal(i, p.Sequence);
            Assert.Equal(e.Size, p.Data.Length);
            Assert.Equal(e.Md5, "MD5:" + FrameMd5.Hash(p.Data.Span));
            if (resplit)
            {
                double ourTime = p.Pts / (double)audio.SampleRate;
                double theirTime = e.Pts / (double)byteRate;
                Assert.True(Math.Abs(ourTime - theirTime) < 1.5e-3, $"Packet {i}: {ourTime:F6} s, FFmpeg's {theirTime:F6} s.");
                continue;
            }

            Assert.Equal((e.Pts, e.Dts), (p.Pts, p.Dts));
            if (e.Duration != Probed.Unset)
            {
                Assert.Equal(e.Duration, p.Duration);
            }
        }
    }

    /// <summary>
    /// The video of every samples/*.avi against ffprobe's packets: each packet's size and MD5, its keyframe flag (the
    /// 'idx1' index's, which Seek starts from; MJPEG's every packet), and its timestamps where FFmpeg gives them (H.264
    /// in AVI has no pts in FFmpeg: its frames take their times from the decoder).
    /// </summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public void VideoMatchesFfmpegPacketByPacket(string sampleName)
    {
        var expected = File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".packets.csv")).Select(Probed.Parse).ToList();
        using var demuxer = AviDemuxer.Open(TestSamples.PathOf(sampleName));
        int index = demuxer.Video.StreamIndex;
        var theirs = expected.Where(e => e.StreamIndex == index).ToList();
        var ours = demuxer.ReadPackets().Where(p => p.StreamIndex == index).ToList();
        Assert.Equal(theirs.Count, ours.Count);
        for (int i = 0; i < ours.Count; i++)
        {
            var (p, e) = (ours[i], theirs[i]);
            Assert.Equal(e.Size, p.Data.Length);
            Assert.Equal(e.Md5, "MD5:" + FrameMd5.Hash(p.Data.Span));
            Assert.True(e.IsKeyframe == p.IsKeyframe, $"Packet {i}: keyframe {p.IsKeyframe}, FFmpeg's {e.IsKeyframe}.");
            Assert.True(e.Dts == Probed.Unset || e.Dts == p.Dts, $"Packet {i}: dts {p.Dts}, FFmpeg's {e.Dts}.");
            Assert.True(e.Pts == Probed.Unset || e.Pts == p.Pts, $"Packet {i}: pts {p.Pts}, FFmpeg's {e.Pts}.");
        }
    }

    // The audio stream's WAVEFORMATEX nAvgBytesPerSec: its 'strf' after the 'strh' of type 'auds'.
    private static int AverageBytesPerSecond(string path)
    {
        var data = File.ReadAllBytes(path);
        int auds = data.AsSpan().IndexOf("auds"u8);
        int strf = data.AsSpan(auds).IndexOf("strf"u8) + auds;
        return BitConverter.ToInt32(data, strf + 8 + 8);
    }

    private sealed record Probed(int StreamIndex, long Pts, long Dts, long Duration, int Size, string Md5, bool IsKeyframe)
    {
        public const long Unset = long.MinValue;

        public static Probed Parse(string line)
        {
            var f = line.Split(',');
            long Number(string field) => field == "N/A" ? Unset : long.Parse(field, CultureInfo.InvariantCulture);
            return new Probed(int.Parse(f[0], CultureInfo.InvariantCulture), Number(f[1]), Number(f[2]), Number(f[3]), int.Parse(f[4], CultureInfo.InvariantCulture), f[6], f[5].StartsWith('K'));
        }
    }
}
