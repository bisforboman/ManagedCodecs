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

    /// <summary>A cancelled token stops <see cref="H264Decoder.DecodeAsync"/> before it decodes anything.</summary>
    [Fact]
    public async Task DecodeAsync_Cancelled()
    {
        if (!ParameterSetRewriter.Exists("h264-high-b.mp4"))
        {
            return;
        }

        using var demuxer = Mp4Demuxer.Open(TestSamples.PathOf("h264-high-b.mp4"));
        var video = demuxer.Video!;
        var packet = demuxer.ReadPackets().First(p => p.StreamIndex == video.StreamIndex);
        var decoder = new H264Decoder(video) { Threads = 2 };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await decoder.DecodeAsync(packet, new CancellationToken(canceled: true)));

        // Nothing was decoded: the packet decodes as the first one still.
        var frames = new List<VideoFrame>(await decoder.DecodeAsync(packet));
        frames.AddRange(await decoder.FlushAsync());
        Assert.Single(frames);
    }

    /// <summary>
    /// <see cref="H264Decoder.DecodeAsync"/> awaits a picture being decoded (slowed down here) rather than blocking, so
    /// cancelling stops it; the packet is then not decoded at all, and the stream goes on to the frames of one thread.
    /// </summary>
    [Fact]
    public async Task DecodeAsync_CancelledWhileWaiting()
    {
        if (!ParameterSetRewriter.Exists("h264-high-b.mp4"))
        {
            return;
        }

        var expected = Decode("h264-high-b.mp4", threads: 1).Select(FrameMd5.Hash).ToList();

        using var demuxer = Mp4Demuxer.Open(TestSamples.PathOf("h264-high-b.mp4"));
        var video = demuxer.Video!;
        var packets = demuxer.ReadPackets().Where(p => p.StreamIndex == video.StreamIndex).ToList();
        var decoder = new H264Decoder(video) { Threads = 2, RowDelay = 30 };
        var frames = new List<VideoFrame>();
        frames.AddRange(await decoder.DecodeAsync(packets[0]));
        frames.AddRange(await decoder.DecodeAsync(packets[1]));

        // Both decoders are busy for a while (30 ms a row at most): the third packet waits for the first picture.
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await decoder.DecodeAsync(packets[2], cancellation.Token));

        foreach (var packet in packets.Skip(2))
        {
            frames.AddRange(await decoder.DecodeAsync(packet));
        }

        frames.AddRange(await decoder.FlushAsync());
        Assert.Equal(expected, frames.Select(FrameMd5.Hash));
    }
}
