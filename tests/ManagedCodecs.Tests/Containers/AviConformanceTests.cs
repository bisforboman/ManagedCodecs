using System.Globalization;
using ManagedCodecs.Containers;

namespace ManagedCodecs.Tests.Containers;

/// <summary>
/// The audio of every samples/*.avi, each of its audio streams read alone (<see cref="AviDemuxer.SelectAudio"/>), against ffprobe's packets (samples/*.packets.csv, <c>ffprobe -show_packets
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
        int videoIndex = demuxer.Video.StreamIndex;
        var listed = demuxer.AudioTracks.Select(t => t.StreamIndex).ToList();
        Assert.Equal(expected.Where(e => e.StreamIndex != videoIndex).Select(e => e.StreamIndex).Distinct().Order(), listed);
        Assert.Equal(listed.FirstOrDefault(-1), demuxer.Audio?.StreamIndex ?? -1);
        foreach (var audio in demuxer.AudioTracks)
        {
            demuxer.SelectAudio(audio.StreamIndex);
            Assert.Same(audio, demuxer.Audio);
            Assert.DoesNotContain(demuxer.ReadPackets(), p => p.StreamIndex != videoIndex && p.StreamIndex != audio.StreamIndex);
            CompareAudio(sampleName, demuxer, audio, [.. expected.Where(e => e.StreamIndex == audio.StreamIndex)]);
        }
    }

    /// <summary>
    /// avi-mjpeg-three-audio's streams all read in one pass: the packets interleaved in file order (ffprobe's, the
    /// video's included), each stream's as it is read alone.
    /// </summary>
    [Fact]
    public void ReadsSeveralAudioStreamsInFileOrder()
    {
        const string Name = "avi-mjpeg-three-audio.avi";
        var expected = File.ReadAllLines(TestSamples.ReferenceOf(Name, ".packets.csv")).Select(Probed.Parse).ToList();
        using var demuxer = AviDemuxer.Open(TestSamples.PathOf(Name));
        Assert.Equal([(1, CodecIds.Mp3), (2, CodecIds.PcmS16Le), (3, CodecIds.Aac)], demuxer.AudioTracks.Select(t => (t.StreamIndex, t.Codec)));

        demuxer.SelectAudio(3, 1, 2);
        Assert.Equal(3, demuxer.Audio!.StreamIndex);
        var all = demuxer.ReadPackets().ToList();
        Assert.Equal(expected.Select(e => e.StreamIndex), all.Select(p => p.StreamIndex));
        Assert.Equal(expected.Select(e => e.Md5), all.Select(p => "MD5:" + FrameMd5.Hash(p.Data.Span)));
        Assert.Equal(demuxer.ReadPacketInfo(), all.Select(p => new PacketInfo(p.StreamIndex, p.Sequence, p.Data.Length, p.IsKeyframe, p.Dts, p.Pts, p.Duration)));

        demuxer.SelectAudio(2);
        var alone = demuxer.ReadPackets().Where(p => p.StreamIndex == 2).ToList();
        Assert.Equal(all.Where(p => p.StreamIndex == 2).Select(p => (p.Sequence, p.Pts, p.Data.ToArray())), alone.Select(p => (p.Sequence, p.Pts, p.Data.ToArray())));

        Assert.Throws<ArgumentException>(() => demuxer.SelectAudio());
        Assert.Throws<ArgumentException>(() => demuxer.SelectAudio(1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => demuxer.SelectAudio(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => demuxer.SelectAudio(4));
        Assert.Equal(2, demuxer.Audio!.StreamIndex);
    }

    /// <summary>
    /// A seek by a stream not selected, then a choice of others: every selected stream starts at its last packet at or
    /// before the time (a packet a chunk for MP3 and AAC, pieces timed by bytes for PCM), the video at its frame then.
    /// </summary>
    [Fact]
    public void SeeksEveryAudioStream()
    {
        using var demuxer = AviDemuxer.Open(TestSamples.PathOf("avi-mjpeg-three-audio.avi"));
        var tracks = demuxer.AudioTracks;

        // About half a second, in the AAC stream's ticks (dwScale / dwRate: a frame of 1024 samples each).
        var aac = tracks[2].TimeBase;
        long pts = (long)Math.Floor(0.5 / aac.ToDouble());
        double time = pts * aac.ToDouble();
        demuxer.Seek(pts, 3);
        demuxer.SelectAudio(1, 2, 3);
        var after = demuxer.ReadPackets().ToList();

        // The video's frame then, by its time (FFmpeg's muxer leaves out the second frame of an AVI with audio: a gap).
        Assert.Equal((long)Math.Floor(time * 25), after.First(p => p.StreamIndex == 0).Pts);
        foreach (var track in tracks)
        {
            var first = after.First(p => p.StreamIndex == track.StreamIndex);
            double at = first.Pts * track.TimeBase.ToDouble();
            double end = (first.Pts + first.Duration) * track.TimeBase.ToDouble();
            Assert.True(at <= time && end > time, $"Stream {track.StreamIndex} starts at {at:F4} s to {end:F4} s, not around {time:F4} s.");
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => demuxer.Seek(0, 4));
    }

    private static void CompareAudio(string sampleName, AviDemuxer demuxer, AudioStreamInfo audio, List<Probed> theirs)
    {
        var extradata = FrameMd5.ParseExtradata(File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".packets.framemd5")), audio.StreamIndex);
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
