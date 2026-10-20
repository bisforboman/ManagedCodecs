using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using ManagedCodecs.Containers;
using ManagedCodecs.Mjpeg;

// Run in Release: dotnet run -c Release --project benchmarks/ManagedCodecs.Benchmarks
// Sample file: MANAGEDCODECS_SAMPLE env var, or samples/testsrc-420.avi.
// Compare with FFmpeg: ffmpeg -benchmark -i samples/testsrc-420.avi -f null -
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
        _packets = demuxer.ReadPackets().Select(p => p.Data.ToArray()).ToArray();
    }

    // BenchmarkDotNet runs benchmarks in a child process with its own working directory.
    private static string FindRepositoryRoot()
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
