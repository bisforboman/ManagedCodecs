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
        bool fragmented = HasMovieFragments(TestSamples.PathOf(sampleName));

        // framemd5 numbers streams as FFmpeg maps them: video first, then audio.
        var streams = new List<(int OurIndex, long Count, ReadOnlyMemory<byte> CodecPrivate)>();
        if (demuxer.Video is { } video)
        {
            streams.Add((video.StreamIndex, video.FrameCount, video.CodecPrivate));
        }

        if (demuxer.Audio is { } audio)
        {
            streams.Add((audio.StreamIndex, audio.FrameCount, audio.CodecPrivate));
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

                // In fragmented files FFmpeg gives every sample a composition offset entry, so its
                // demuxer leaves durations unset and libavformat fills them in from the codec (1024
                // for AAC), not from the file. There the file's duration is FFmpeg's next dts minus
                // this one, which is what is compared; the last packet's has nothing to compare
                // with (ReadsTrackFragments covers fragment durations).
                long duration = !fragmented ? theirs[i].Duration
                    : i + 1 < theirs.Count ? theirs[i + 1].Dts - theirs[i].Dts
                    : p.Duration;
                Assert.Equal(theirs[i] with { Duration = duration }, new FrameMd5Entry(ffmpegIndex, p.Dts, p.Pts, p.Duration, p.Data.Length, FrameMd5.Hash(p.Data.Span)));
            }

            // Keyframe flags (ffprobe, video only: "K__" for a keyframe).
            if (ffmpegIndex == 0 && demuxer.Video is not null)
            {
                var flags = File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".packets.flags"));
                Assert.Equal(flags.Select(f => f.StartsWith('K')), ours.Select(p => p.IsKeyframe));
            }
        }
    }

    /// <summary>Whether the file has a top-level 'moof' box.</summary>
    private static bool HasMovieFragments(string path)
    {
        var data = File.ReadAllBytes(path);
        for (long pos = 0; pos + 8 <= data.Length;)
        {
            long size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)pos));
            if (System.Text.Encoding.ASCII.GetString(data, (int)pos + 4, 4) == "moof")
            {
                return true;
            }

            if (size < 8)
            {
                return false; // 64-bit or open-ended sizes: none of the samples need them here
            }

            pos += size;
        }

        return false;
    }
}
