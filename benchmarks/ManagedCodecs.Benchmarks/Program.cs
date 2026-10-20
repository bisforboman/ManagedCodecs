using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using ManagedCodecs.Containers.Avi;
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
            ?? Path.Combine("samples", "testsrc-420.avi");

        using var demuxer = AviDemuxer.Open(Path.GetFullPath(path));
        _packets = demuxer.ReadPackets().Select(p => p.Data.ToArray()).ToArray();
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
