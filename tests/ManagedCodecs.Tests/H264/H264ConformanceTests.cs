using ManagedCodecs.Containers.Mp4;
using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>
/// Decodes every Constrained Baseline H.264 sample (samples/*.mp4 with a .framemd5) and
/// compares each frame's MD5 and timestamp, in output order, with FFmpeg's. See samples/README.md.
/// </summary>
public class H264ConformanceTests
{
    public static TheoryData<string> Samples() =>
        new(TestSamples.WithReference(".framemd5").Where(IsBaseline));

    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesFfmpegFrameByFrame(string sampleName)
    {
        var lines = File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".framemd5"));
        var expected = FrameMd5.Parse(lines).Where(e => e.StreamIndex == 0).ToList(); // FFmpeg lists video first
        var timeBase = FrameMd5.ParseTimeBase(lines);

        using var demuxer = Mp4Demuxer.Open(TestSamples.PathOf(sampleName));
        var video = demuxer.Video!;
        var decoder = new H264Decoder(video);

        int index = 0;
        void Check(VideoFrame frame)
        {
            Assert.True(index < expected.Count, $"More frames than the reference ({expected.Count}).");
            Assert.True(expected[index].Md5 == FrameMd5.Hash(frame), $"Frame {index} differs from FFmpeg.");
            Assert.True(
                FrameMd5.SameTime(expected[index].Pts, timeBase, frame.Pts, video.TimeBase),
                $"Frame {index}: pts {frame.Pts} ({video.TimeBase}) is not FFmpeg's {expected[index].Pts} ({timeBase}).");
            index++;
        }

        foreach (var packet in demuxer.ReadPackets().Where(p => p.StreamIndex == video.StreamIndex))
        {
            foreach (var frame in decoder.Decode(packet))
            {
                Check(frame);
            }
        }

        foreach (var frame in decoder.Flush())
        {
            Check(frame);
        }

        Assert.Equal(expected.Count, index);
    }

    /// <summary>H.264 in MP4 whose avcC says profile_idc 66 (Baseline).</summary>
    private static bool IsBaseline(string name)
    {
        if (!name.EndsWith(".mp4", StringComparison.Ordinal))
        {
            return false;
        }

        using var demuxer = Mp4Demuxer.Open(TestSamples.PathOf(name));
        if (demuxer.Video is not { Codec: CodecIds.H264 } video)
        {
            return false;
        }

        var avcC = video.CodecPrivate.Span;
        return avcC.Length > 1 && avcC[1] == 66;
    }
}
