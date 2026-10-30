using System.Globalization;
using ManagedCodecs.Containers;

namespace ManagedCodecs.Tests.Containers;

/// <summary>
/// Demuxes every samples/*.mp4 and compares each packet (dts, pts, duration, size, MD5, keyframe and discard flags)
/// with samples/*.packets.csv, written by <c>ffprobe -show_packets -show_data_hash MD5</c> (the demuxer's own
/// timestamps: <c>ffmpeg -c copy -f framemd5</c> makes decode timestamps go forward, which an edit list's discarded
/// packets don't), and the codec private data with samples/*.packets.framemd5. See samples/README.md.
/// </summary>
public class Mp4ConformanceTests
{
    public static TheoryData<string> Samples() =>
        new(TestSamples.WithReference(".packets.csv").Where(name => name.EndsWith(".mp4", StringComparison.Ordinal)));

    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesFfmpegPacketByPacket(string sampleName)
    {
        var expected = File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".packets.csv")).Select(ProbedPacket.Parse).ToList();
        var lines = File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".packets.framemd5"));

        using var demuxer = Mp4Demuxer.Open(TestSamples.PathOf(sampleName));
        var packets = demuxer.ReadPackets().ToList();
        bool fragmented = HasMovieFragments(TestSamples.PathOf(sampleName));

        // framemd5 numbers streams as FFmpeg maps them: video first, then audio; ffprobe by track.
        var streams = new List<(int Index, long Count, ReadOnlyMemory<byte> CodecPrivate)>();
        if (demuxer.Video is { } video)
        {
            streams.Add((video.StreamIndex, video.FrameCount, video.CodecPrivate));
        }

        if (demuxer.Audio is { } audio)
        {
            streams.Add((audio.StreamIndex, audio.FrameCount, audio.CodecPrivate));
        }

        Assert.Equal(expected.Select(e => e.StreamIndex).Distinct().Order(), streams.Select(s => s.Index).Order());
        for (int mapped = 0; mapped < streams.Count; mapped++)
        {
            var (index, count, codecPrivate) = streams[mapped];
            var extradata = FrameMd5.ParseExtradata(lines, mapped);
            if (extradata is null)
            {
                Assert.True(codecPrivate.IsEmpty, $"Stream {index}: FFmpeg reports no extradata but we found some.");
            }
            else
            {
                Assert.Equal(extradata.Value, (codecPrivate.Length, FrameMd5.Hash(codecPrivate.Span)));
            }

            var theirs = expected.Where(e => e.StreamIndex == index).ToList();
            var ours = packets.Where(p => p.StreamIndex == index).ToList();
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
                Assert.Equal(theirs[i] with { Duration = duration }, new ProbedPacket(index, p.Pts, p.Dts, p.Duration, p.Data.Length, p.IsKeyframe, p.Discard, FrameMd5.Hash(p.Data.Span)));
            }
        }
    }

    /// <summary>
    /// <see cref="Mp4Demuxer.ReadPacketInfo"/> gives <see cref="Mp4Demuxer.ReadPackets"/>'s packets (checked against
    /// FFmpeg's above) without their data, in the same order, and reads nothing from the file to do it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public void PacketInfo_IsThePacketsWithoutData(string sampleName)
    {
        var bytes = File.ReadAllBytes(TestSamples.PathOf(sampleName));
        using var stream = new ReadCountingStream(bytes);
        using var demuxer = new Mp4Demuxer(stream);
        var expected = demuxer.ReadPackets().Select(p => new PacketInfo(p.StreamIndex, p.Sequence, p.Data.Length, p.IsKeyframe, p.Dts, p.Pts, p.Duration, p.SkipSamples, p.Discard)).ToList();

        stream.Reads = 0;
        Assert.Equal(expected, demuxer.ReadPacketInfo());
        Assert.Equal(0, stream.Reads);
    }

    /// <summary>
    /// A demuxer asked for one track describes and gives that track's packets as one reading both does, and leaves the
    /// other out as if the file had none, in fragmented files too (whose fragments it still reads past).
    /// </summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public void OneTrack_IsThatTrackOfBoth(string sampleName)
    {
        using var both = Mp4Demuxer.Open(TestSamples.PathOf(sampleName));
        var packets = both.ReadPackets().Select(p => (p.StreamIndex, p.Sequence, FrameMd5.Hash(p.Data.Span), p.IsKeyframe, p.Dts, p.Pts, p.Duration, p.SkipSamples, p.Discard)).ToList();
        foreach (var tracks in new[] { MediaTypes.Video, MediaTypes.Audio })
        {
            using var one = new Mp4Demuxer(File.OpenRead(TestSamples.PathOf(sampleName)), tracks);
            var video = tracks == MediaTypes.Video ? both.Video : null;
            var audio = tracks == MediaTypes.Audio ? both.Audio : null;
            Assert.Equal(Described(video), Described(one.Video));
            Assert.Equal(Described(audio), Described(one.Audio));

            int?[] kept = [video?.StreamIndex, audio?.StreamIndex];
            Assert.Equal(
                packets.Where(p => kept.Contains(p.StreamIndex)),
                one.ReadPackets().Select(p => (p.StreamIndex, p.Sequence, FrameMd5.Hash(p.Data.Span), p.IsKeyframe, p.Dts, p.Pts, p.Duration, p.SkipSamples, p.Discard)));
        }
    }

    // A stream's description to compare: the records compare CodecPrivate by reference, so its bytes as a string.
    private static string? Described(VideoStreamInfo? info) => info is null ? null : $"{info with { CodecPrivate = default }} {Convert.ToHexString(info.CodecPrivate.Span)}";

    private static string? Described(AudioStreamInfo? info) => info is null ? null : $"{info with { CodecPrivate = default }} {Convert.ToHexString(info.CodecPrivate.Span)}";

    // Counts the synchronous reads, after the headers are read.
    private sealed class ReadCountingStream(byte[] data) : MemoryStream(data, writable: false)
    {
        public int Reads { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            Reads++;
            return base.Read(buffer);
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

    /// <summary>A line of <c>ffprobe -show_packets -show_data_hash MD5 -of csv=p=0</c>: stream, pts, dts, duration, size, flags, hash, then any side data.</summary>
    private sealed record ProbedPacket(int StreamIndex, long Pts, long Dts, long Duration, int Size, bool IsKeyframe, bool Discard, string Md5)
    {
        public static ProbedPacket Parse(string line)
        {
            var f = line.Split(',');
            long Number(string field) => field == "N/A" ? long.MinValue : long.Parse(field, CultureInfo.InvariantCulture); // unset (fragments' durations)
            return new ProbedPacket(
                int.Parse(f[0], CultureInfo.InvariantCulture),
                Number(f[1]),
                Number(f[2]),
                Number(f[3]),
                int.Parse(f[4], CultureInfo.InvariantCulture),
                f[5][0] == 'K',
                f[5][1] == 'D',
                f[6]["MD5:".Length..]);
        }
    }
}
