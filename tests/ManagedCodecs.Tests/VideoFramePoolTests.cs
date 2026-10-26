using ManagedCodecs.Containers;
using ManagedCodecs.H264;

namespace ManagedCodecs.Tests;

/// <summary>Disposed frames give their planes back for the next frame of the same sizes, and only then.</summary>
public class VideoFramePoolTests
{
    [Fact]
    public void DisposedFramesPlanes_AreRentedAgain()
    {
        var pool = new VideoFramePool();
        var first = pool.Rent(33, 17, PixelFormat.Yuv420P);
        var planes = first.Planes;
        first.Dispose();

        var second = pool.Rent(33, 17, PixelFormat.Yuv420P);
        Assert.Equal(planes.Length, second.Planes.Length);
        Assert.All(Enumerable.Range(0, planes.Length), i => Assert.Same(planes[i], second.Planes[i]));
        Assert.Equal([33, 17, 17], second.Strides);
        Assert.Equal([0, 0, 0], second.Offsets);
    }

    [Fact]
    public void FramesNotDisposed_GetPlanesOfTheirOwn()
    {
        var pool = new VideoFramePool();
        var a = pool.Rent(16, 16, PixelFormat.Yuv420P);
        var b = pool.Rent(16, 16, PixelFormat.Yuv420P);
        Assert.All(Enumerable.Range(0, 3), i => Assert.NotSame(a.Planes[i], b.Planes[i]));
    }

    [Fact]
    public void PlanesOfOtherSizes_AreNotRented()
    {
        var pool = new VideoFramePool();
        var small = pool.Rent(16, 16, PixelFormat.Yuv420P);
        var planes = small.Planes;
        small.Dispose();

        var wide = pool.Rent(16, 16, PixelFormat.Yuv420P10);
        Assert.All(Enumerable.Range(0, 3), i => Assert.NotSame(planes[i], wide.Planes[i]));
        Assert.Equal(16 * 16 * 2, wide.Planes[0].Length);
    }

    [Fact]
    public void ThePool_KeepsAtMostItsCapacity()
    {
        var pool = new VideoFramePool(capacity: 1);
        var a = pool.Rent(8, 8, PixelFormat.Gray8);
        var b = pool.Rent(8, 8, PixelFormat.Gray8);
        var planeB = b.Planes[0];
        a.Dispose();
        b.Dispose(); // beyond the capacity: dropped

        var c = pool.Rent(8, 8, PixelFormat.Gray8);
        var d = pool.Rent(8, 8, PixelFormat.Gray8);
        Assert.NotSame(planeB, c.Planes[0]);
        Assert.NotSame(planeB, d.Planes[0]);
    }

    [Fact]
    public void ADisposedFrame_Throws_AndDisposesOnce()
    {
        var pool = new VideoFramePool();
        var frame = pool.Rent(8, 8, PixelFormat.Yuv444P);
        frame.Dispose();
        frame.Dispose();
        Assert.Throws<ObjectDisposedException>(() => frame.Planes);
        Assert.Throws<ObjectDisposedException>(() => frame.GetRow(0, 0));

        // Disposed twice, it went back once: two rents get two different sets.
        var a = pool.Rent(8, 8, PixelFormat.Yuv444P);
        var b = pool.Rent(8, 8, PixelFormat.Yuv444P);
        Assert.NotSame(a.Planes[0], b.Planes[0]);
    }

    [Fact]
    public void FramesOutsideAPool_JustEnd()
    {
        var frame = new VideoFrame(8, 8, PixelFormat.Yuv420P);
        frame.Dispose();
        Assert.Throws<ObjectDisposedException>(() => frame.Planes);
    }

    [Fact]
    public void H264Decoder_WritesTheNextFrameIntoADisposedFramesPlanes()
    {
        using var demuxer = Mp4Demuxer.Open(TestSamples.PathOf("h264-p-refs.mp4"));
        var video = demuxer.Video!;
        var decoder = new H264Decoder(video);
        byte[]? released = null;
        bool reused = false;
        foreach (var packet in demuxer.ReadPackets().Where(p => p.StreamIndex == video.StreamIndex))
        {
            foreach (var frame in decoder.Decode(packet))
            {
                reused |= released is not null && ReferenceEquals(frame.Planes[0], released);
                released = frame.Planes[0];
                frame.Dispose();
            }
        }

        Assert.True(reused, "No frame reused a disposed frame's planes.");
    }
}
