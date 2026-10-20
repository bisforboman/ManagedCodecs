using ManagedCodecs.Containers.Mp4;

namespace ManagedCodecs.Tests.Containers;

/// <summary>
/// Demuxes every samples/*.mp4 and compares each packet (dts, pts, duration, size, MD5) and the
/// codec private data with samples/*.packets.framemd5, written by
/// <c>ffmpeg -copyts -i x.mp4 -c copy -f framemd5</c>, and keyframe flags with
/// samples/*.packets.flags from ffprobe (framemd5 has no flags column). See samples/README.md.
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
        var packets = demuxer.ReadPackets().ToList();

        // framemd5 numbers streams as FFmpeg maps them: video first, then audio.
        var streams = new List<(int OurIndex, long Count, ReadOnlyMemory<byte> CodecPrivate)>();
        if (demuxer.Video is { } video)
        {
            streams.Add((video.StreamIndex, video.FrameCount, video.CodecPrivate));
        }

        if (demuxer.Audio is { } audio)
        {
            streams.Add((audio.StreamIndex, audio.PacketCount, audio.CodecPrivate));
        }

        Assert.Equal(expected.Select(e => e.StreamIndex).Distinct().Count(), streams.Count);
        for (int ffmpegIndex = 0; ffmpegIndex < streams.Count; ffmpegIndex++)
        {
            var (ourIndex, count, codecPrivate) = streams[ffmpegIndex];
            var extradata = FrameMd5.ParseExtradata(lines, ffmpegIndex);
            if (extradata is null)
            {
                Assert.True(codecPrivate.IsEmpty, $"Stream {ffmpegIndex}: FFmpeg reports no extradata but we found some.");
            }
            else
            {
                Assert.Equal(extradata.Value, (codecPrivate.Length, FrameMd5.Hash(codecPrivate.Span)));
            }

            var theirs = expected.Where(e => e.StreamIndex == ffmpegIndex).ToList();
            var ours = packets.Where(p => p.StreamIndex == ourIndex).ToList();
            Assert.Equal(theirs.Count, ours.Count);
            Assert.Equal(theirs.Count, count);

            for (int i = 0; i < ours.Count; i++)
            {
                var p = ours[i];
                Assert.Equal(theirs[i], new FrameMd5Entry(ffmpegIndex, p.Dts, p.Pts, p.Duration, p.Data.Length, FrameMd5.Hash(p.Data.Span)));
            }

            // Keyframe flags (ffprobe, video only: "K__" for a keyframe).
            if (ffmpegIndex == 0 && demuxer.Video is not null)
            {
                var flags = File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".packets.flags"));
                Assert.Equal(flags.Select(f => f.StartsWith('K')), ours.Select(p => p.IsKeyframe));
            }
        }
    }
}
