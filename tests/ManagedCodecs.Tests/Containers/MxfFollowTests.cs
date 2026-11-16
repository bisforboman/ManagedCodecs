using ManagedCodecs.Containers;

namespace ManagedCodecs.Tests.Containers;

/// <summary>
/// <see cref="MxfDemuxer.Refresh"/> on a file still being written: each sample grown under the demuxer, a few kilobytes
/// at a time (a stream whose length rises over the finished file's bytes), gives through its calls every packet a read
/// of the finished file gives, once each and whole, and in the end the same listing and streams.
/// </summary>
public class MxfFollowTests
{
    [Theory]
    [InlineData("mxf-h264-pcm.mxf", 4093)]
    [InlineData("mxf-h264-pipe.mxf", 30011)]
    [InlineData("mxf-h264-index.mxf", 65537)]
    [InlineData("mxf-d10-pcm24.mxf", 100003)]
    [InlineData("mxf-h264-mono4.mxf", 4093)]
    [InlineData("mxf-h264-mono4.mxf", 4093, new[] { 4, 2 })]
    public void GivesEveryPacketOnceAsTheFileGrows(string sampleName, int step, int[]? tracks = null)
    {
        byte[] file = File.ReadAllBytes(TestSamples.PathOf(sampleName));
        using var finished = new MxfDemuxer(new MemoryStream(file));
        var growing = new GrowingStream(file, file.Length / 5);
        using var demuxer = new MxfDemuxer(growing);
        if (tracks is not null)
        {
            // Of several sound tracks, the selected ones are followed (unasked, the first).
            finished.SelectAudio(tracks);
            demuxer.SelectAudio(tracks);
        }

        var expected = finished.ReadPackets().ToList();
        var given = new List<Packet>();
        while (true)
        {
            var batch = demuxer.Refresh().ToList();
            given.AddRange(batch);

            // Between calls the listing holds what was given, nothing cut short.
            Assert.Equal(given.Count, demuxer.ReadPacketInfo().Count());
            if (growing.Length == file.Length)
            {
                break;
            }

            growing.Grow(step);
        }

        Assert.Empty(demuxer.Refresh());
        Assert.Equal(expected.Count, given.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            // Keyframe flags come from the footer's index, read last: earlier packets go out without them.
            var (e, g) = (expected[i], given[i]);
            Assert.Equal((e.StreamIndex, e.Sequence, e.Dts, e.Pts, e.Duration), (g.StreamIndex, g.Sequence, g.Dts, g.Pts, g.Duration));
            Assert.True(e.Data.Span.SequenceEqual(g.Data.Span), $"packet {i}: other bytes than the finished file's");
        }

        Assert.Equal(finished.ReadPacketInfo(), demuxer.ReadPacketInfo());
        Assert.Equal(finished.Video, demuxer.Video);
        Assert.Equal(finished.Audio, demuxer.Audio);
    }

    /// <summary>The element a file opened cut short ends in: <see cref="MxfDemuxer.ReadPackets"/> gives it as it is until Refresh, which holds it back until whole.</summary>
    [Fact]
    public void HoldsBackTheElementTheWriterIsIn()
    {
        byte[] file = File.ReadAllBytes(TestSamples.PathOf("mxf-h264-pcm.mxf"));
        var growing = new GrowingStream(file, file.Length / 2);
        using var demuxer = new MxfDemuxer(growing);
        int cut = demuxer.ReadPackets().Count();
        var given = demuxer.Refresh().ToList();
        Assert.Equal(cut - 1, given.Count);
        Assert.Equal(given.Count, demuxer.ReadPackets().Count());

        growing.Grow(file.Length);
        using var finished = new MxfDemuxer(new MemoryStream(file));
        var rest = demuxer.Refresh().ToList();
        Assert.Equal(finished.ReadPacketInfo().Count(), given.Count + rest.Count);
        Assert.Equal(finished.ReadPackets().ElementAt(given.Count).Data.ToArray(), rest[0].Data.ToArray());
    }

    [Fact]
    public void RefusesToFollowClipWrappedEssence()
    {
        using var demuxer = new MxfDemuxer(File.OpenRead(TestSamples.PathOf("mxf-opatom-h264.mxf")));
        Assert.Throws<NotSupportedException>(() => demuxer.Refresh());
    }

    // The finished file's bytes, of which only the first Length are there yet.
    private sealed class GrowingStream(byte[] file, int length) : Stream
    {
        private long _length = length;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position { get; set; }

        public void Grow(int bytes) => _length = Math.Min(file.Length, _length + bytes);

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Clamp(_length - Position, 0, count);
            Array.Copy(file, Position, buffer, offset, n);
            Position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            _ => _length + offset,
        };

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
