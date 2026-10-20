using ManagedCodecs.Containers.Avi;
using ManagedCodecs.Mjpeg;

namespace ManagedCodecs.Tests.Mjpeg;

/// <summary>
/// Decodes every samples/*.avi and compares each frame's MD5 with the matching
/// samples/*.framemd5 produced by FFmpeg. See samples/README.md for how to generate them.
/// </summary>
public class MjpegConformanceTests
{
    public static TheoryData<string> Samples() => new(SampleNames());

    private static IEnumerable<string> SampleNames()
    {
        var dir = FindSamplesDirectory();
        if (dir is null)
        {
            yield break;
        }

        foreach (var avi in Directory.EnumerateFiles(dir, "*.avi").Order())
        {
            if (File.Exists(Path.ChangeExtension(avi, ".framemd5")))
            {
                yield return Path.GetFileName(avi);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesFfmpegFrameByFrame(string sampleName)
    {
        var dir = FindSamplesDirectory()!;
        var avi = Path.Combine(dir, sampleName);
        var expected = FrameMd5.Parse(File.ReadLines(Path.ChangeExtension(avi, ".framemd5")));

        using var demuxer = AviDemuxer.Open(avi);
        var decoder = new MjpegDecoder();

        int index = 0;
        foreach (var packet in demuxer.ReadPackets())
        {
            var frame = decoder.Decode(packet.Data.Span);
            Assert.True(index < expected.Count, $"More frames than the reference ({expected.Count}).");
            Assert.True(expected[index].Md5 == FrameMd5.Hash(frame), $"Frame {index} differs from FFmpeg.");
            index++;
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
        var dir = FindSamplesDirectory()!;
        var avi = Path.Combine(dir, sampleName);
        var expected = FrameMd5.Parse(File.ReadLines(Path.ChangeExtension(avi, ".framemd5")));

        using var demuxer = AviDemuxer.Open(avi);
        var decoder = new MjpegDecoder();

        int index = 0;
        foreach (var packet in demuxer.ReadPackets())
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

    private static string? FindSamplesDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "samples");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(dir.FullName, "ManagedCodecs.slnx")))
            {
                return candidate;
            }
        }

        return null;
    }
}
