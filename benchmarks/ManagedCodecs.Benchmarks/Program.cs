using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using ManagedCodecs;
using ManagedCodecs.Aac;
using ManagedCodecs.Containers;
using ManagedCodecs.H264;
using ManagedCodecs.Mjpeg;

// Run in Release: dotnet run -c Release --project benchmarks/ManagedCodecs.Benchmarks
// Sample files: MANAGEDCODECS_SAMPLE (MJPEG, default samples/testsrc-420.avi),
// MANAGEDCODECS_H264_SAMPLE (default samples/h264-bench-720p.mp4) and
// MANAGEDCODECS_AAC_SAMPLE (default samples/aac-5.1.mp4).
// Decodes packets already in memory; to compare with FFmpeg on the same file, see tools/compare.sh (README, Benchmarking).
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

/// <summary>Decodes a whole AAC track per iteration: each frame overlaps the previous one.</summary>
[MemoryDiagnoser]
public class AacDecodeBenchmark
{
    private byte[][] _packets = [];
    private AudioStreamInfo? _stream;

    [GlobalSetup]
    public void Setup()
    {
        var path = Environment.GetEnvironmentVariable("MANAGEDCODECS_AAC_SAMPLE")
            ?? Path.Combine(MjpegDecodeBenchmark.FindRepositoryRoot(), "samples", "aac-5.1.mp4");

        using var demuxer = Demuxer.Open(Path.GetFullPath(path));
        _stream = demuxer.Audio!;
        _packets = demuxer.ReadPackets().Where(p => p.StreamIndex == _stream.StreamIndex).Select(p => p.Data.ToArray()).ToArray();
    }

    [Benchmark]
    public int DecodeAllFrames()
    {
        var decoder = new AacDecoder(_stream!);
        int samples = 0;
        foreach (var packet in _packets)
        {
            samples += decoder.Decode(packet).SampleCount;
        }

        return samples;
    }
}
