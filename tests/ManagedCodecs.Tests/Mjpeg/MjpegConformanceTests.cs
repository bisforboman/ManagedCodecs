using ManagedCodecs.Containers;
using ManagedCodecs.Mjpeg;

namespace ManagedCodecs.Tests.Mjpeg;

/// <summary>
/// Decodes every MJPEG sample (samples/*.avi and *.mp4) and compares each frame's MD5 and pts
/// with the matching samples/*.framemd5 produced by FFmpeg. See samples/README.md.
/// </summary>
public class MjpegConformanceTests
{
    public static TheoryData<string> Samples() => new(SampleNames());

    private static IEnumerable<string> SampleNames() =>
        TestSamples.WithReference(".framemd5").Where(name => TestSamples.CodecOf(name) == CodecIds.Mjpeg);

    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesFfmpegFrameByFrame(string sampleName) => CompareWithFfmpeg(sampleName, zeroCopy: false, dispose: false);

    /// <summary>
    /// The same with every frame disposed once hashed, so that later frames are decoded into its planes: a frame still
    /// read after its planes went back would show as a wrong frame.
    /// </summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesFfmpegWithDisposedFrames(string sampleName) => CompareWithFfmpeg(sampleName, zeroCopy: false, dispose: true);

    /// <summary>
    /// The same with <see cref="MjpegDecoder.ZeroCopy"/>: frames decoded into their own MCU-padded planes, read through
    /// their strides, and disposed, so that the next ones reuse the planes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesFfmpegWithZeroCopy(string sampleName) => CompareWithFfmpeg(sampleName, zeroCopy: true, dispose: true);

    private static void CompareWithFfmpeg(string sampleName, bool zeroCopy, bool dispose)
    {
        var lines = File.ReadAllLines(TestSamples.ReferenceOf(sampleName, ".framemd5"));
        var expected = FrameMd5.Parse(lines).Where(e => e.StreamIndex == 0).ToList(); // FFmpeg lists video first
        var timeBase = FrameMd5.ParseTimeBase(lines); // FFmpeg's decoded output may use another time base

        using var demuxer = Demuxer.Open(TestSamples.PathOf(sampleName));
        var video = demuxer.Video!;
        var decoder = new MjpegDecoder(video) { ZeroCopy = zeroCopy }; // the stream's height tells interlaced packets (two fields) apart

        int index = 0;
        foreach (var packet in demuxer.ReadPackets().Where(p => p.StreamIndex == video.StreamIndex))
        {
            var frame = decoder.Decode(packet.Data.Span);
            Assert.True(index < expected.Count, $"More frames than the reference ({expected.Count}).");
            Assert.True(expected[index].Md5 == FrameMd5.Hash(frame), $"Frame {index} differs from FFmpeg.");
            Assert.True(
                FrameMd5.SameTime(expected[index].Pts, timeBase, packet.Pts, video.TimeBase),
                $"Frame {index}: pts {packet.Pts} ({video.TimeBase}) is not FFmpeg's {expected[index].Pts} ({timeBase}).");
            index++;
            if (dispose)
            {
                frame.Dispose();
            }
        }

        Assert.Equal(expected.Count, index);
    }

    public static TheoryData<string> StandardHuffmanSamples() =>
        new(SampleNames().Where(name => name.Contains("stdhuff", StringComparison.Ordinal)));

    /// <summary>
    /// Samples encoded with <c>-huffman default</c> carry the Annex K tables in DHT. With every
    /// DHT removed, as many camera streams do, the built-in tables must give the same frames.
    /// </summary>
    [Theory]
    [MemberData(nameof(StandardHuffmanSamples))]
    public void MatchesFfmpegWithoutDht(string sampleName)
    {
        var expected = FrameMd5.Parse(File.ReadLines(TestSamples.ReferenceOf(sampleName, ".framemd5"))).Where(e => e.StreamIndex == 0).ToList();

        using var demuxer = Demuxer.Open(TestSamples.PathOf(sampleName));
        var decoder = new MjpegDecoder();

        int index = 0;
        foreach (var packet in demuxer.ReadPackets().Where(p => p.StreamIndex == demuxer.Video!.StreamIndex))
        {
            var stripped = RemoveSegments(packet.Data.Span, JpegMarker.Dht);
            Assert.True(stripped.Length < packet.Data.Length, "Sample has no DHT to remove.");
            var frame = decoder.Decode(stripped);
            Assert.True(expected[index].Md5 == FrameMd5.Hash(frame), $"Frame {index} differs from FFmpeg.");
            index++;
        }

        Assert.Equal(expected.Count, index);
    }

    private static byte[] RemoveSegments(ReadOnlySpan<byte> jpeg, byte marker)
    {
        var output = new List<byte>(jpeg.Length);
        int copied = 0;
        foreach (var segment in JpegMarkerReader.ReadSegments(jpeg))
        {
            if (segment.Marker != marker)
            {
                continue;
            }

            // The segment starts at its 0xFF, two bytes of marker and two of length before the payload.
            int start = segment.PayloadOffset - 4;
            output.AddRange(jpeg[copied..start]);
            copied = segment.PayloadOffset + segment.PayloadLength;
        }

        output.AddRange(jpeg[copied..]);
        return [.. output];
    }
}
