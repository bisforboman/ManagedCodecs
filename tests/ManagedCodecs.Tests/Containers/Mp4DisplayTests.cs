using ManagedCodecs.Containers;

namespace ManagedCodecs.Tests.Containers;

/// <summary>
/// <see cref="VideoStreamInfo.SampleAspectRatio"/> and <see cref="VideoStreamInfo.Rotation"/> against ffprobe's stream
/// (name.display; tools/make-samples.sh). ffprobe's ratio is the container's 'pasp' where there is one, else the
/// decoder's, so a stream without 'pasp' (0:1 here) is not compared; its rotation, -180 to 180, is ours less 360 above
/// 180. The colour properties of 'colr' reach ffprobe only through the frames (its stream shows the decoder's once it
/// has decoded some): name.color checks them where the VUI says nothing (h264-colr-only, mjpeg-colr).
/// </summary>
public class Mp4DisplayTests
{
    public static TheoryData<string> Samples() => [.. TestSamples.WithReference(".display").Where(name => name.EndsWith(".mp4", StringComparison.Ordinal))];

    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesFfprobe(string sampleName)
    {
        var fields = File.ReadLines(TestSamples.ReferenceOf(sampleName, ".display"))
            .Select(line => line.Split('=', 2))
            .Where(kv => kv.Length == 2)
            .ToDictionary(kv => kv[0], kv => kv[1]);
        using var demuxer = Mp4Demuxer.Open(TestSamples.PathOf(sampleName));
        var video = demuxer.Video!;

        int expectedRotation = fields.TryGetValue("rotation", out var rotation) ? (int.Parse(rotation, System.Globalization.CultureInfo.InvariantCulture) + 360) % 360 : 0;
        Assert.Equal(expectedRotation, video.Rotation);
        if (video.SampleAspectRatio.Numerator != 0)
        {
            Assert.Equal(fields["sample_aspect_ratio"], $"{video.SampleAspectRatio.Numerator}:{video.SampleAspectRatio.Denominator}");
        }
    }

    /// <summary>The samples that need a container ratio and a rotation do have them, so the theory above compares them.</summary>
    [Theory]
    [InlineData("h264-pasp-conflict.mp4", 4, 3, 0)]
    [InlineData("h264-rotate-90.mp4", 1, 1, 90)]
    [InlineData("h264-rotate-180.mp4", 1, 1, 180)]
    [InlineData("h264-rotate-270.mp4", 1, 1, 270)]
    public void SamplesSayIt(string sampleName, int numerator, int denominator, int rotation)
    {
        if (TestSamples.Directory is null || !File.Exists(TestSamples.PathOf(sampleName)))
        {
            return;
        }

        using var demuxer = Mp4Demuxer.Open(TestSamples.PathOf(sampleName));
        Assert.Equal((new Rational(numerator, denominator), rotation), (demuxer.Video!.SampleAspectRatio, demuxer.Video.Rotation));
    }
}
