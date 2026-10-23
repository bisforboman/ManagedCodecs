using ManagedCodecs.Containers.Mp4;
using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>
/// The options around frame threading (<see cref="H264Decoder.Threads"/>); its frames are checked against FFmpeg by
/// the conformance tests, with and without random delays.
/// </summary>
public class H264ThreadsTests
{
    [Fact]
    public void Threads_AtLeastOne()
    {
        if (!ParameterSetRewriter.Exists("h264-high-b.mp4"))
        {
            return;
        }

        var config = Config("h264-high-b.mp4");
        Assert.Throws<ArgumentOutOfRangeException>(() => new H264Decoder(config) { Threads = 0 });
    }

    [Fact]
    public void BorrowedFrames_NotWithThreads()
    {
        if (!ParameterSetRewriter.Exists("h264-high-b.mp4"))
        {
            return;
        }

        using var demuxer = Mp4Demuxer.Open(TestSamples.PathOf("h264-high-b.mp4"));
        var video = demuxer.Video!;
        var decoder = new H264Decoder(video) { BorrowFrames = true, Threads = 2 };
        var packet = demuxer.ReadPackets().First(p => p.StreamIndex == video.StreamIndex);
        Assert.Throws<NotSupportedException>(() => decoder.Decode(packet));
    }

    /// <summary>With threads, frames come later, but all of them, in the same order and with the same timestamps.</summary>
    [Fact]
    public void Threads_GiveTheSameFramesInTheSameOrder()
    {
        if (!ParameterSetRewriter.Exists("h264-high-b.mp4"))
        {
            return;
        }

        var one = Decode("h264-high-b.mp4", threads: 1);
        var three = Decode("h264-high-b.mp4", threads: 3);
        Assert.Equal(one.Count, three.Count);
        for (int i = 0; i < one.Count; i++)
        {
            Assert.Equal(one[i].Pts, three[i].Pts);
            Assert.Equal(FrameMd5.Hash(one[i]), FrameMd5.Hash(three[i]));
        }
    }

    private static byte[] Config(string sampleName)
    {
        using var demuxer = Mp4Demuxer.Open(TestSamples.PathOf(sampleName));
        return demuxer.Video!.CodecPrivate.ToArray();
    }

    private static List<VideoFrame> Decode(string sampleName, int threads)
    {
        using var demuxer = Mp4Demuxer.Open(TestSamples.PathOf(sampleName));
        var video = demuxer.Video!;
        var decoder = new H264Decoder(video) { Threads = threads };
        var frames = new List<VideoFrame>();
        foreach (var packet in demuxer.ReadPackets().Where(p => p.StreamIndex == video.StreamIndex))
        {
            frames.AddRange(decoder.Decode(packet));
        }

        frames.AddRange(decoder.Flush());
        return frames;
    }
}
