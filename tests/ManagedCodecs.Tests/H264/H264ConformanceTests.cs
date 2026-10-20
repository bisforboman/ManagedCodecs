using ManagedCodecs.Containers.Mp4;
using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>
/// Decodes every Constrained Baseline H.264 sample (samples/*.mp4 with a .framemd5) and
/// compares each frame's MD5 with FFmpeg's. See samples/README.md.
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
        var expected = FrameMd5.Parse(lines);

        using var demuxer = Mp4Demuxer.Open(TestSamples.PathOf(sampleName));
        var decoder = new H264Decoder(demuxer.Video);

        int index = 0;
        foreach (var packet in demuxer.ReadPackets())
        {
            var frame = decoder.Decode(packet.Data.Span);
            Assert.True(index < expected.Count, $"More frames than the reference ({expected.Count}).");
            Assert.True(expected[index].Md5 == FrameMd5.Hash(frame), $"Frame {index} differs from FFmpeg.");
            index++;
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
        var avcC = demuxer.Video.CodecPrivate.Span;
        return demuxer.Video.Codec == CodecIds.H264 && avcC.Length > 1 && avcC[1] == 66;
    }
}
