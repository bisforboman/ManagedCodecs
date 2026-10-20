using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using ManagedCodecs;
using ManagedCodecs.Containers;
using ManagedCodecs.H264;
using ManagedCodecs.Mjpeg;

// Run in Release: dotnet run -c Release --project benchmarks/ManagedCodecs.Benchmarks
// Sample files: MANAGEDCODECS_SAMPLE (MJPEG, default samples/testsrc-420.avi) and
// MANAGEDCODECS_H264_SAMPLE (default samples/h264-bench-720p.mp4).
// Compare with FFmpeg: ffmpeg -benchmark -threads 1 -i <sample> -f null -
BenchmarkSwitcher.FromAssembly(typeof(MjpegDecodeBenchmark).Assembly).Run(args);

[MemoryDiagnoser]
public class MjpegDecodeBenchmark
{
    private readonly MjpegDecoder _decoder = new();
    private byte[][] _packets = [];

    [GlobalSetup]
    public void Setup()
    {
        var path = Environment.GetEnvironmentVariable("MANAGEDCODECS_SAMPLE")
            ?? Path.Combine(FindRepositoryRoot(), "samples", "testsrc-420.avi");

        using var demuxer = Demuxer.Open(Path.GetFullPath(path));
        int stream = demuxer.Video!.StreamIndex;
        _packets = demuxer.ReadPackets().Where(p => p.StreamIndex == stream).Select(p => p.Data.ToArray()).ToArray();
    }

    // BenchmarkDotNet runs benchmarks in a child process with its own working directory.
    internal static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ManagedCodecs.slnx")))
            {
                return dir.FullName;
            }
        }

        return Directory.GetCurrentDirectory();
    }

    [Benchmark]
    public int DecodeAllFrames()
    {
        int pixels = 0;
        foreach (var packet in _packets)
        {
            pixels += _decoder.Decode(packet).Width;
        }

        return pixels;
    }
}

/// <summary>Decodes a whole H.264 file per iteration: frames depend on earlier ones.</summary>
[MemoryDiagnoser]
public class H264DecodeBenchmark
{
    private byte[][] _packets = [];
    private VideoStreamInfo? _stream;

    [GlobalSetup]
    public void Setup()
    {
        var path = Environment.GetEnvironmentVariable("MANAGEDCODECS_H264_SAMPLE")
            ?? Path.Combine(MjpegDecodeBenchmark.FindRepositoryRoot(), "samples", "h264-bench-720p.mp4");

        using var demuxer = Demuxer.Open(Path.GetFullPath(path));
        _stream = demuxer.Video!;
        _packets = demuxer.ReadPackets().Where(p => p.StreamIndex == _stream.StreamIndex).Select(p => p.Data.ToArray()).ToArray();
    }

    [Benchmark]
    public int DecodeAllFrames()
    {
        var decoder = new H264Decoder(_stream!);
        int pixels = 0;
        foreach (var packet in _packets)
        {
            pixels += decoder.Decode(packet).Width;
        }

        return pixels;
    }
}
