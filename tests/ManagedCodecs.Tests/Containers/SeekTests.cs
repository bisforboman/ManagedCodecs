using ManagedCodecs.Containers;

namespace ManagedCodecs.Tests.Containers;

/// <summary>
/// <see cref="IDemuxer.Seek"/> of the MP4 and AVI demuxers: after a seek, each stream's packets are the tail of a read
/// from the start, from the last keyframe whose pts is at or before the time (the other stream's at the same instant),
/// for every sample, at each keyframe's time, just after it, between keyframes, before the first and past the last.
/// The decoded frame at a time is checked against FFmpeg's <c>-ss</c> in <see cref="SeekFrameTests"/>.
/// </summary>
public class SeekTests
{
    public static TheoryData<string> Files()
    {
        var files = new TheoryData<string>();
        if (TestSamples.Directory is not { } directory)
        {
            return files;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order())
        {
            if (Path.GetExtension(file) is ".avi" or ".mp4")
            {
                files.Add(Path.GetRelativePath(directory, file));
            }
        }

        return files;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void ReadsTheTailFromTheKeyframeBefore(string name)
    {
        using var demuxer = Demuxer.Open(TestSamples.PathOf(name));
        if (demuxer is AviDemuxer && demuxer.Video?.Codec != CodecIds.Mjpeg)
        {
            return; // SeeksOnlyMjpegInAvi
        }

        var all = Infos(demuxer).ToList();
        foreach (var (stream, timeBase) in Streams(demuxer))
        {
            var packets = all.Where(p => p.StreamIndex == stream).ToList();
            foreach (long pts in Targets(packets))
            {
                demuxer.Seek(pts, stream);
                var read = Infos(demuxer).ToList();
                foreach (var (other, otherBase) in Streams(demuxer))
                {
                    long time = other == stream ? pts : Rescale(pts, timeBase, otherBase);
                    var expected = Tail(all.Where(p => p.StreamIndex == other).ToList(), time);
                    var actual = read.Where(p => p.StreamIndex == other).ToList();
                    Assert.True(expected.SequenceEqual(actual), $"Seek to {pts} in stream {stream}: stream {other}'s packets are not the tail from its keyframe at or before {time} ({actual.Count} packets, {expected.Count} expected).");
                }
            }
        }
    }

    [Theory]
    [InlineData("h264-bframes.mp4")]
    [InlineData("testsrc-420.avi")]
    [InlineData("fmp4-base-moof.mp4")]
    [InlineData("h264-edits.mp4")]
    public async Task GivesThePacketsOfAReadFromTheStart(string name)
    {
        using var demuxer = Demuxer.Open(TestSamples.PathOf(name));
        var video = demuxer.Video!;
        var all = demuxer.ReadPackets().ToList();
        var keyframes = all.Where(p => p.StreamIndex == video.StreamIndex && p.IsKeyframe).ToList();
        Assert.True(keyframes.Count > 1, "The sample needs keyframes after its first.");
        var target = keyframes[keyframes.Count / 2];

        demuxer.Seek(target.Pts, video.StreamIndex);
        var read = demuxer.ReadPackets().ToList();
        var asynchronous = new List<Packet>();
        await foreach (var packet in demuxer.ReadPacketsAsync())
        {
            asynchronous.Add(packet);
        }

        Assert.Equal(target.Sequence, read.First(p => p.StreamIndex == video.StreamIndex).Sequence);
        foreach (var packets in new[] { read, asynchronous })
        {
            Assert.Equal(read.Count, packets.Count);
            foreach (var packet in packets)
            {
                var original = all.Single(p => p.StreamIndex == packet.StreamIndex && p.Sequence == packet.Sequence);
                Assert.Equal(original with { Data = default }, packet with { Data = default });
                Assert.True(original.Data.Span.SequenceEqual(packet.Data.Span), $"Packet {packet.Sequence} of stream {packet.StreamIndex} has other data.");
            }
        }
    }

    [Fact]
    public void SeekingBackToTheStartReadsEverything()
    {
        using var demuxer = Demuxer.Open(TestSamples.PathOf("h264-bframes.mp4"));
        var all = Infos(demuxer).ToList();
        var video = demuxer.Video!;
        demuxer.Seek(all.Where(p => p.StreamIndex == video.StreamIndex).Max(p => p.Pts), video.StreamIndex);
        Assert.NotEqual(all.Count, Infos(demuxer).Count());
        demuxer.Seek(long.MinValue / 4, video.StreamIndex);
        Assert.Equal(all, Infos(demuxer));
    }

    [Fact]
    public void RefusesAnotherStream()
    {
        using var mp4 = Demuxer.Open(TestSamples.PathOf("h264-bframes.mp4"));
        Assert.Throws<ArgumentOutOfRangeException>(() => mp4.Seek(0, 7));
        using var avi = Demuxer.Open(TestSamples.PathOf("testsrc-420.avi"));
        Assert.Throws<ArgumentOutOfRangeException>(() => avi.Seek(0, avi.Video!.StreamIndex + 1));
    }

    [Fact]
    public void AdtsCannotSeek()
    {
        using var adts = Demuxer.Open(TestSamples.PathOf("aac-pulse.aac"));
        Assert.Throws<NotSupportedException>(() => adts.Seek(0, adts.Audio!.StreamIndex));
    }

    // The packets from the last keyframe in decode order whose pts is at or before the time, or all of them.
    private static List<PacketInfo> Tail(List<PacketInfo> packets, long pts)
    {
        int start = 0;
        for (int i = 0; i < packets.Count; i++)
        {
            if (packets[i].IsKeyframe && packets[i].Pts <= pts)
            {
                start = i;
            }
        }

        return packets[start..];
    }

    // Each keyframe's pts, one after it and one before it, a time between keyframes, before the first and past the last.
    private static IEnumerable<long> Targets(List<PacketInfo> packets)
    {
        if (packets.Count == 0)
        {
            yield break;
        }

        foreach (var keyframe in packets.Where(p => p.IsKeyframe).Take(8))
        {
            yield return keyframe.Pts;
            yield return keyframe.Pts + 1;
            yield return keyframe.Pts - 1;
        }

        yield return packets[packets.Count / 2].Pts;
        yield return packets.Min(p => p.Pts) - 1000;
        yield return packets.Max(p => p.Pts) + 1000;
    }

    private static IEnumerable<(int Stream, Rational TimeBase)> Streams(IDemuxer demuxer)
    {
        if (demuxer.Video is { } video)
        {
            yield return (video.StreamIndex, video.TimeBase);
        }

        if (demuxer.Audio is { } audio)
        {
            yield return (audio.StreamIndex, audio.TimeBase);
        }
    }

    private static IEnumerable<PacketInfo> Infos(IDemuxer demuxer) => demuxer switch
    {
        Mp4Demuxer mp4 => mp4.ReadPacketInfo(),
        AviDemuxer avi => avi.ReadPacketInfo(),
        _ => throw new ArgumentException("An MP4 or AVI demuxer.", nameof(demuxer)),
    };

    // Rounded down, by the definition, in BigInteger.
    private static long Rescale(long value, Rational from, Rational to)
    {
        var n = (System.Numerics.BigInteger)value * from.Numerator * to.Denominator;
        var d = (System.Numerics.BigInteger)from.Denominator * to.Numerator;
        var q = System.Numerics.BigInteger.DivRem(n, d, out var r);
        return (long)(r.IsZero || (n.Sign < 0) == (d.Sign < 0) ? q : q - 1);
    }
}
