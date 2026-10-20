using ManagedCodecs.Containers.Mp4;

namespace ManagedCodecs.Tests.Containers;

/// <summary>
/// Demuxes every samples/*.mp4 and compares each packet (dts, pts, duration, size, MD5) and the
/// codec private data with samples/*.packets.framemd5, written by
/// <c>ffmpeg -copyts -i x.mp4 -c copy -f framemd5</c>. See samples/README.md.
/// </summary>
public class Mp4ConformanceTests
{
    public static TheoryData<string> Samples() =>
        new(TestSamples.WithReference(".packets.framemd5").Where(name => name.EndsWith(".mp4", StringComparison.Ordinal)));

    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesFfmpegPacketByPacket(string sampleName)
    {
        var lines = File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".packets.framemd5"));
        var expected = FrameMd5.Parse(lines);

        using var demuxer = Mp4Demuxer.Open(TestSamples.PathOf(sampleName));

        var extradata = FrameMd5.ParseExtradata(lines);
        var codecPrivate = demuxer.Video.CodecPrivate.Span;
        if (extradata is null)
        {
            Assert.True(codecPrivate.IsEmpty, "FFmpeg reports no extradata but we found some.");
        }
        else
        {
            Assert.Equal(extradata.Value, (codecPrivate.Length, FrameMd5.Hash(codecPrivate)));
        }

        var packets = demuxer.ReadPackets().ToList();
        Assert.Equal(expected.Count, packets.Count);
        Assert.Equal(expected.Count, demuxer.Video.FrameCount);

        for (int i = 0; i < packets.Count; i++)
        {
            var p = packets[i];
            var actual = new FrameMd5Entry(0, p.Dts, p.Pts, p.Duration, p.Data.Length, FrameMd5.Hash(p.Data.Span));
            Assert.Equal(expected[i], actual);
        }
    }
}
