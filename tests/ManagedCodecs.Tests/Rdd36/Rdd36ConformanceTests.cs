using ManagedCodecs.Containers;
using ManagedCodecs.Rdd36;

namespace ManagedCodecs.Tests.Rdd36;

/// <summary>
/// Decodes every SMPTE RDD 36 sample (samples/rdd36-*.mov: FFmpeg's encoders' streams, and those rewritten by
/// tools/rdd36-rewrite.py for what the encoders never write) and compares each frame's MD5 and pts, its picture type,
/// interlacing, colour properties and aspect ratio with what FFmpeg's decoder gives (tools/make-samples.sh;
/// docs/conformance.md).
/// </summary>
public class Rdd36ConformanceTests
{
    public static TheoryData<string> Samples() =>
        new(TestSamples.WithReference(".framemd5").Where(name => name.EndsWith(".mov", StringComparison.Ordinal) && TestSamples.CodecOf(name) == CodecIds.Rdd36));

    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesFfmpegFrameByFrame(string sampleName) => CompareWithFfmpeg(sampleName, dispose: false);

    /// <summary>
    /// The same with every frame disposed once hashed, so that later frames are decoded into its planes: a frame still
    /// read after its planes went back, or a sample left from the frame before, would show as a wrong frame.
    /// </summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesFfmpegWithDisposedFrames(string sampleName) => CompareWithFfmpeg(sampleName, dispose: true);

    [Fact]
    public void TheSamplesAreThere()
    {
        // The theories above pass with no rows: with samples generated, the RDD 36 ones must be among them.
        if (TestSamples.Directory is not null && File.Exists(TestSamples.PathOf("testsrc-420.avi")))
        {
            Assert.True(Samples().Count >= 30, $"{Samples().Count} RDD 36 samples with references; tools/make-samples.sh makes 31.");
        }
    }

    [Fact]
    public void TakesTheContainersTransferWhereTheFrameHeaderSaysUnknown()
    {
        // rdd36-aw-colour: prores_aw writes transfer_characteristic 0 for SMPTE 170M's, which RDD 36 calls
        // unknown/unspecified (6.1.1), beside a 'colr' that says 6. FFmpeg's decoder shows the 0 as H.273's
        // "reserved"; this one takes the container's, the one place its frames' properties are not FFmpeg's.
        if (TestSamples.Directory is null || !File.Exists(TestSamples.PathOf("rdd36-aw-colour.mov")))
        {
            return;
        }

        var colors = ColorReferences.Read("rdd36-aw-colour.mov")!;
        using var demuxer = Demuxer.Open(TestSamples.PathOf("rdd36-aw-colour.mov"));
        var video = demuxer.Video!;
        var packet = demuxer.ReadPackets().First(p => p.StreamIndex == video.StreamIndex);

        using var frame = new Rdd36Decoder(video).Decode(packet.Data.Span);
        using var alone = new Rdd36Decoder().Decode(packet.Data.Span);

        Assert.Equal("reserved", colors.Frames[0]["color_transfer"]);
        Assert.Equal((ColorPrimaries.Smpte170M, ColorTransfer.Smpte170M, ColorMatrix.Smpte170M), (frame.ColorPrimaries, frame.ColorTransfer, frame.ColorMatrix));
        Assert.Equal((ColorPrimaries.Smpte170M, ColorTransfer.Unspecified, ColorMatrix.Smpte170M), (alone.ColorPrimaries, alone.ColorTransfer, alone.ColorMatrix));
    }

    private static void CompareWithFfmpeg(string sampleName, bool dispose)
    {
        var lines = File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".framemd5"));
        var expected = FrameMd5.Parse(lines).Where(e => e.StreamIndex == 0).ToList(); // the reference maps the video alone
        var timeBase = FrameMd5.ParseTimeBase(lines); // FFmpeg's decoded output may use another time base

        using var demuxer = Demuxer.Open(TestSamples.PathOf(sampleName));
        var video = demuxer.Video!;
        var decoder = new Rdd36Decoder(video);
        var types = PictureTypes.Read(sampleName);
        var interlacing = Interlacings.Read(sampleName);

        // The frame header's unknown transfer beside a 'colr': TakesTheContainersTransferWhereTheFrameHeaderSaysUnknown.
        var colors = sampleName == "rdd36-aw-colour.mov" ? null : ColorReferences.Read(sampleName);
        Assert.NotNull(types);
        Assert.NotNull(interlacing);

        int index = 0;
        foreach (var packet in demuxer.ReadPackets().Where(p => p.StreamIndex == video.StreamIndex))
        {
            var frame = Assert.Single(decoder.Decode(packet));
            Assert.True(index < expected.Count, $"More frames than the reference ({expected.Count}).");
            Assert.True(expected[index].Md5 == FrameMd5.Hash(frame), $"Frame {index} differs from FFmpeg.");
            Assert.True(
                FrameMd5.SameTime(expected[index].Pts, timeBase, frame.Pts, video.TimeBase),
                $"Frame {index}: pts {frame.Pts} ({video.TimeBase}) is not FFmpeg's {expected[index].Pts} ({timeBase}).");
            Assert.True(types[index] == frame.PictureType, $"Frame {index}: picture type {frame.PictureType} is not FFmpeg's {types[index]}.");
            Assert.True(packet.IsKeyframe, $"Packet {index} is not a keyframe.");
            interlacing.Check(index, frame);
            colors?.Check(index, frame);
            Assert.Equal((video.Width, video.Height, PixelFormat.Yuv422P10), (frame.Width, frame.Height, frame.Format));
            index++;
            if (dispose)
            {
                frame.Dispose();
            }
        }

        Assert.Equal(expected.Count, index);
    }

    [Theory]
    [InlineData("apco")]
    [InlineData("apcs")]
    [InlineData("apcn")]
    [InlineData("apch")]
    [InlineData("ap4h")]
    [InlineData("ap4x")]
    public void TheDemuxerNamesTheCodecForEverySampleEntry(string fourcc)
    {
        // QuickTime's sample entries for the six profiles; the samples have the four 4:2:2 ones, and any of them
        // patched over a sample's shows the other two (whose 4:4:4 frames the decoder refuses itself).
        if (TestSamples.Directory is null || !File.Exists(TestSamples.PathOf("rdd36-ks-standard.mov")))
        {
            return;
        }

        byte[] file = File.ReadAllBytes(TestSamples.PathOf("rdd36-ks-standard.mov"));
        int stsd = file.AsSpan().IndexOf("stsd"u8);
        int entry = stsd + file.AsSpan(stsd).IndexOf("apcn"u8);
        System.Text.Encoding.ASCII.GetBytes(fourcc).CopyTo(file, entry);

        using var demuxer = Demuxer.Open(new MemoryStream(file));

        Assert.Equal(CodecIds.Rdd36, demuxer.Video!.Codec);
        Assert.Equal((320, 240), (demuxer.Video.Width, demuxer.Video.Height));
        Assert.True(demuxer.Video.CodecPrivate.IsEmpty);
    }
}
