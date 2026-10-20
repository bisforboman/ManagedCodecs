using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>Display-order output of decoded frames.</summary>
public class OutputQueueTests
{
    [Fact]
    public void ReordersBFramesWithinTheDepth()
    {
        // Decoding order I0 P6 B2 B4 P12 B8 B10: two B frames between P frames, which libx264
        // signals with max_num_reorder_frames 2. The order counts give the display order.
        int[] pocs = [0, 6, 2, 4, 12, 8, 10];
        var queue = new OutputQueue();
        var output = new List<long>();
        foreach (int poc in pocs)
        {
            output.AddRange(queue.Add(Frame(poc), poc, restart: poc == 0, depth: 2).Select(f => f.Pts));
        }

        output.AddRange(queue.Flush().Select(f => f.Pts));
        Assert.Equal([0, 2, 4, 6, 8, 10, 12], output);
    }

    [Fact]
    public void DepthZeroOutputsEachFrameAtOnce()
    {
        var queue = new OutputQueue();
        Assert.Equal([0L], queue.Add(Frame(0), 0, restart: true, depth: 0).Select(f => f.Pts));
        Assert.Equal([2L], queue.Add(Frame(2), 2, restart: false, depth: 0).Select(f => f.Pts));
        Assert.Empty(queue.Flush());
    }

    [Fact]
    public void ARestartReleasesEverythingHeldFirst()
    {
        var queue = new OutputQueue();
        queue.Add(Frame(0), 0, restart: true, depth: 2);
        queue.Add(Frame(4), 4, restart: false, depth: 2);

        // The next IDR starts again at 0 but is shown after both held frames.
        var ready = queue.Add(Frame(100), 0, restart: true, depth: 2);
        Assert.Equal([0L, 4], ready.Select(f => f.Pts));
        Assert.Equal([100L], queue.Flush().Select(f => f.Pts));
    }

    private static VideoFrame Frame(long pts) => new(16, 16, PixelFormat.Yuv420P) { Pts = pts };
}
