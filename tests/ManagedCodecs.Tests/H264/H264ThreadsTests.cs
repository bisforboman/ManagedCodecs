using ManagedCodecs.Containers;
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
        var decoder = new H264Decoder(video) { Threads = 2, RowDelay = 50 };
        var frames = new List<VideoFrame>();

        // With both decoders busy for a while (up to 50 ms a row), a packet waits for the oldest picture: DecodeAsync
        // returns still waiting (a blocking one never would). That one is cancelled (directly, not on a timer, which a
        // busy thread pool can fire late); should its picture have finished first after all, it gives its frames.
        using var cancellation = new CancellationTokenSource();
        int next = 0;
        ValueTask<IReadOnlyList<VideoFrame>>? waiting = null;
        while (next < packets.Count && waiting is null)
        {
            var decoding = decoder.DecodeAsync(packets[next++], cancellation.Token);
            if (decoding.IsCompleted)
            {
                frames.AddRange(await decoding);
            }
            else
            {
                waiting = decoding;
            }
        }

        Assert.True(waiting is not null, "DecodeAsync never waited for the busy decoders.");
        cancellation.Cancel();
        try
        {
            frames.AddRange(await waiting.Value);
        }
        catch (OperationCanceledException)
        {
            next--; // not decoded: decode it again
        }

        foreach (var packet in packets.Skip(next))
        {
            frames.AddRange(await decoder.DecodeAsync(packet));
        }

        frames.AddRange(await decoder.FlushAsync());
        Assert.Equal(expected, frames.Select(FrameMd5.Hash));
    }
}
