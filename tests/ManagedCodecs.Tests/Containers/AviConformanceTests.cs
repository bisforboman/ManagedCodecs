using System.Globalization;
using ManagedCodecs.Containers;

namespace ManagedCodecs.Tests.Containers;

/// <summary>
/// Demuxes every samples/*.avi and compares each packet with FFmpeg's (samples/*.packets.csv,
/// <c>ffprobe -show_packets -show_data_hash MD5</c>): file order, then each stream's pts (its dts where FFmpeg gives no
/// pts: H.264, whose AVI stores none), size, MD5 and duration (where FFmpeg gives one), and the audio's keyframe flag;
/// and the audio stream's description with samples/*.streams (codec tag, rate, channels, time base, duration,
/// extradata). The audio's packets as FFmpeg's demuxer and MPEG audio parser give them: one a chunk, PCM in pieces of
/// 1024 samples, MP3 cut anywhere in a frame (avi-mp3-cbr) cut again into frames.
/// </summary>
public class AviConformanceTests
{
    public static TheoryData<string> Samples() =>
        new(TestSamples.WithReference(".packets.csv").Where(name => name.EndsWith(".avi", StringComparison.Ordinal)));

    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesFfmpegPacketByPacket(string sampleName)
    {
        var expected = File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".packets.csv")).Select(ProbedPacket.Parse).ToList();
        using var demuxer = AviDemuxer.Open(TestSamples.PathOf(sampleName));
        var packets = demuxer.ReadPackets().ToList();

        var streams = new List<int> { demuxer.Video.StreamIndex };
        if (demuxer.Audio is { } audio)
        {
            streams.Add(audio.StreamIndex);
        }

        Assert.Equal(expected.Select(e => e.StreamIndex).Distinct().Order(), streams.Order());
        Assert.Equal(expected.Select(e => e.StreamIndex), packets.Select(p => p.StreamIndex)); // file order
        foreach (int index in streams)
        {
            bool isAudio = index != demuxer.Video.StreamIndex;
            var theirs = expected.Where(e => e.StreamIndex == index).ToList();
            var ours = packets.Where(p => p.StreamIndex == index).ToList();
            Assert.Equal(theirs.Count, ours.Count);
            for (int i = 0; i < ours.Count; i++)
            {
                var (t, p) = (theirs[i], ours[i]);
                Assert.Equal(i, p.Sequence);
                Assert.Equal((t.Size, t.Md5), (p.Data.Length, FrameMd5.Hash(p.Data.Span)));
                if (t.Pts != Unset)
                {
                    Assert.True(t.Pts == p.Pts, $"Stream {index}, packet {i}: pts {p.Pts}, FFmpeg's {t.Pts}.");
                }
                else if (t.Dts != Unset)
                {
                    Assert.True(t.Dts == p.Dts, $"Stream {index}, packet {i}: dts {p.Dts}, FFmpeg's {t.Dts}.");
                }

                // FFmpeg leaves some durations unset (AAC's first packet's).
                if (t.Duration != Unset)
                {
                    Assert.True(t.Duration == p.Duration, $"Stream {index}, packet {i}: duration {p.Duration}, FFmpeg's {t.Duration}.");
                }

                if (isAudio)
                {
                    Assert.Equal(t.IsKeyframe, p.IsKeyframe);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void DescribesTheAudioAsFfmpeg(string sampleName)
    {
        var probed = Streams(File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".streams")));
        using var demuxer = AviDemuxer.Open(TestSamples.PathOf(sampleName));
        var theirs = probed.FirstOrDefault(s => s["codec_type"] == "audio");
        if (theirs is null)
        {
            Assert.Null(demuxer.Audio);
            return;
        }

        var audio = demuxer.Audio;
        Assert.NotNull(audio);
        Assert.Equal(int.Parse(theirs["index"], CultureInfo.InvariantCulture), audio.StreamIndex);
        int tag = Convert.ToInt32(theirs["codec_tag"], 16);
        string codec = tag switch { 0x55 => CodecIds.Mp3, 0xFF => CodecIds.Aac, _ => $"0x{tag:X4}" };
        Assert.Equal(codec, audio.Codec);
        Assert.Equal((int.Parse(theirs["sample_rate"], CultureInfo.InvariantCulture), int.Parse(theirs["channels"], CultureInfo.InvariantCulture)), (audio.SampleRate, audio.Channels));
        Assert.Equal(theirs["time_base"], audio.TimeBase.ToString());

        // ffprobe lists no extradata where there is none (PCM).
        int extradataSize = theirs.TryGetValue("extradata_size", out var listed) ? int.Parse(listed, CultureInfo.InvariantCulture) : 0;
        Assert.Equal(extradataSize, audio.CodecPrivate.Length);
        if (extradataSize > 0)
        {
            Assert.Equal(theirs["extradata_hash"]["MD5:".Length..], FrameMd5.Hash(audio.CodecPrivate.Span));
        }

        if (theirs["duration"] == "N/A")
        {
            Assert.Equal(0, audio.Duration);
        }
        else
        {
            double seconds = audio.Duration * audio.TimeBase.ToDouble();
            Assert.True(Math.Abs(seconds - double.Parse(theirs["duration"], CultureInfo.InvariantCulture)) < 1e-6, $"The audio lasts {seconds:F6} s, ffprobe's {theirs["duration"]} s.");
        }
    }

    // The sample exercises both of the CBR layout's timing rules (each against FFmpeg's in MatchesFfmpegPacketByPacket):
    // frames timed from the packet they start in, a tick or two off the frame before's end, and frames that follow on.
    [Fact]
    public void CutsCbrMp3IntoFrames()
    {
        const string name = "avi-mp3-cbr.avi";
        if (TestSamples.Directory is null || !File.Exists(TestSamples.PathOf(name)))
        {
            return;
        }

        using var demuxer = AviDemuxer.Open(TestSamples.PathOf(name));
        var audio = demuxer.Audio!;
        Assert.Equal(new Rational(1, 8000), audio.TimeBase); // dwScale 1, dwRate the bytes a second
        var frames = demuxer.ReadPackets().Where(p => p.StreamIndex == audio.StreamIndex).ToList();
        Assert.True(frames.Count > 30);
        Assert.All(frames, f => Assert.Equal(0xFF, f.Data.Span[0]));
        int followOn = frames.Skip(1).Zip(frames).Count(pair => pair.First.Pts == pair.Second.Pts + pair.Second.Duration);
        Assert.InRange(followOn, 5, frames.Count - 6);
    }

    private const long Unset = long.MinValue;

    // ffprobe's default output, one [STREAM] block (key=value lines) at a time: a new block at each "index=".
    private static List<Dictionary<string, string>> Streams(string[] lines)
    {
        var streams = new List<Dictionary<string, string>>();
        foreach (var line in lines)
        {
            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0)
            {
                continue;
            }

            string key = line[..equals];
            if (key == "index")
            {
                streams.Add([]);
            }

            if (streams.Count > 0)
            {
                streams[^1][key] = line[(equals + 1)..];
            }
        }

        return streams;
    }

    /// <summary>A line of <c>ffprobe -show_packets -of csv=p=0</c>: stream, pts, dts, duration, size, flags, MD5.</summary>
    private sealed record ProbedPacket(int StreamIndex, long Pts, long Dts, long Duration, int Size, bool IsKeyframe, string Md5)
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
                f[6]["MD5:".Length..]);
        }
    }
}
