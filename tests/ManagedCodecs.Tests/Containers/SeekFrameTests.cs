using System.Text.RegularExpressions;
using ManagedCodecs.Containers;
using ManagedCodecs.H264;
using ManagedCodecs.Mjpeg;

namespace ManagedCodecs.Tests.Containers;

/// <summary>
/// The frame shown at a time after <see cref="IDemuxer.Seek"/>, against FFmpeg's accurate seek
/// (<c>ffmpeg -seek_timestamp 1 -ss t -i ... -frames:v 1</c>, make-samples.sh's name.seekNNNN.framemd5): a fresh decoder
/// on the packets from the seek, and the first frame it shows at or after the time.
/// </summary>
public partial class SeekFrameTests
{
    public static TheoryData<string> References()
    {
        var references = new TheoryData<string>();
        if (TestSamples.Directory is { } directory)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.framemd5").Where(f => SeekReference().IsMatch(Path.GetFileName(f))).Order())
            {
                references.Add(Path.GetFileName(file));
            }
        }

        return references;
    }

    [Theory]
    [MemberData(nameof(References))]
    public void ShowsFfmpegsFrameAtTheTime(string reference)
    {
        var match = SeekReference().Match(reference);
        string stem = match.Groups[1].Value;
        long milliseconds = long.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        string name = File.Exists(TestSamples.PathOf(stem + ".mp4")) ? stem + ".mp4" : stem + ".avi";
        var expected = FrameMd5.Parse(File.ReadLines(TestSamples.PathOf(reference))).Single(e => e.StreamIndex == 0);

        using var demuxer = Demuxer.Open(TestSamples.PathOf(name));
        var video = demuxer.Video!;

        // The time in the stream's time base, rounded up: the first frame at or after it is the one shown.
        long pts = (long)Math.Ceiling(milliseconds * (double)video.TimeBase.Denominator / (1000.0 * video.TimeBase.Numerator));
        demuxer.Seek(pts, video.StreamIndex);

        IVideoDecoder decoder = video.Codec == CodecIds.H264 ? new H264Decoder(video) : new MjpegDecoder(video);
        VideoFrame? shown = null;
        var packets = demuxer.ReadPackets().Where(p => p.StreamIndex == video.StreamIndex).ToList();
        Assert.True(packets[0].IsKeyframe, "The read after the seek does not start at a keyframe.");
        foreach (var packet in packets)
        {
            shown = decoder.Decode(packet).FirstOrDefault(f => f.Pts >= pts);
            if (shown is not null)
            {
                break;
            }
        }

        shown ??= decoder.Flush().FirstOrDefault(f => f.Pts >= pts);
        Assert.NotNull(shown);
        Assert.Equal(expected.Md5, FrameMd5.Hash(shown));
    }

    [GeneratedRegex(@"^(.+)\.seek(\d+)\.framemd5$")]
    private static partial Regex SeekReference();
}
