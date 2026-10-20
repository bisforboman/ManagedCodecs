using ManagedCodecs.Containers.Avi;
using ManagedCodecs.Mjpeg;

namespace ManagedCodecs.Tests.Mjpeg;

/// <summary>
/// Decodes every samples/*.avi and compares each frame's MD5 with the matching
/// samples/*.framemd5 produced by FFmpeg. See samples/README.md for how to generate them.
/// </summary>
public class MjpegConformanceTests
{
    public static TheoryData<string> Samples()
    {
        var data = new TheoryData<string>();
        var dir = FindSamplesDirectory();
        if (dir is null)
        {
            return data;
        }

        foreach (var avi in Directory.EnumerateFiles(dir, "*.avi").Order())
        {
            if (File.Exists(Path.ChangeExtension(avi, ".framemd5")))
            {
                data.Add(Path.GetFileName(avi));
            }
        }

        return data;
    }

    [Theory(Skip = "Enable once MjpegDecoder decodes frames.")]
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
