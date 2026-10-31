using ManagedCodecs.Containers;
using ManagedCodecs.Fuzz;
using ManagedCodecs.H264;
using ManagedCodecs.Mjpeg;

namespace ManagedCodecs.Tests.Fuzz;

/// <summary>
/// The contract for malformed input (README, Malformed input): demuxers and decoders throw InvalidDataException or
/// NotSupportedException, nothing else, and finish. <see cref="Targets.Run"/> lets those two pass and any other
/// exception out. Inputs the fuzzer (tools/fuzz.sh) or the sweep below broke the contract with are kept in
/// Fuzz/Findings/&lt;target&gt;/ and replayed; a fixed-seed mutation sweep over a few samples runs the cheap version of
/// the fuzzer in every test run.
/// </summary>
public class MalformedInputTests
{
    private static readonly string FindingsDirectory = Path.Combine(AppContext.BaseDirectory, "Fuzz", "Findings");

    public static TheoryData<string, string> Findings()
    {
        var data = new TheoryData<string, string>();
        foreach (var target in Targets.Names)
        {
            string directory = Path.Combine(FindingsDirectory, target);
            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.GetFiles(directory).Order())
                {
                    data.Add(target, Path.GetFileName(file));
                }
            }
        }

        return data;
    }

#if !UNITY_BUILD
    /// <summary>
    /// The container findings through the thumbnail path too (the fuzzer's thumbnail target): probing, seeking, decoding
    /// with concealment, scaling and the PNG writer on files the demuxers and decoders once failed on.
    /// </summary>
    [Theory]
    [MemberData(nameof(Findings))]
    public async Task Finding_KeepsTheContractAsAThumbnail(string target, string name)
    {
        if (target != "file")
        {
            return;
        }

        var data = File.ReadAllBytes(Path.Combine(FindingsDirectory, target, name));
        await Task.Run(() => Targets.Run("thumbnail", data)).WaitAsync(TimeSpan.FromSeconds(30));
    }

#endif
    [Theory]
    [MemberData(nameof(Findings))]
    public async Task Finding_KeepsTheContract(string target, string name)
    {
        var data = File.ReadAllBytes(Path.Combine(FindingsDirectory, target, name));

        // WaitAsync throws TimeoutException for a hang.
        await Task.Run(() => Targets.Run(target, data)).WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// The findings again with H.264 on three threads (<c>H264Decoder.Threads</c>): an exception on a decoding thread
    /// must come out of the decoder as it is, and pictures waiting for a failed one must fail rather than wait.
    /// </summary>
    [Theory]
    [MemberData(nameof(Findings))]
    public async Task Finding_KeepsTheContractWithThreads(string target, string name)
    {
        var data = File.ReadAllBytes(Path.Combine(FindingsDirectory, target, name));
        await Task.Run(() => Targets.Run(target, data, threads: 3)).WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// The findings concealing errors (<c>H264Decoder.ConcealErrors</c>), on one thread and on three: decoding goes on
    /// past damaged slices, into what the first runs never reached.
    /// </summary>
    [Theory]
    [MemberData(nameof(Findings))]
    public async Task Finding_KeepsTheContractConcealing(string target, string name)
    {
        var data = File.ReadAllBytes(Path.Combine(FindingsDirectory, target, name));
        await Task.Run(() => Targets.Run(target, data, conceal: true)).WaitAsync(TimeSpan.FromSeconds(30));
        await Task.Run(() => Targets.Run(target, data, threads: 3, conceal: true)).WaitAsync(TimeSpan.FromSeconds(30));
    }

    // One sample per container and codec path: AVI MJPEG, MP4 with H.264 (CAVLC, CABAC with B frames, field
    // pictures) and AAC, fragmented MP4.
    public static TheoryData<string> SweepSamples() => new(new[]
    {
        "testsrc-420.avi", "h264-with-audio.mp4", "h264-main-p-refs.mp4", "h264-high-b.mp4", "jm-paff-b.mp4",
        "aac-stereo-48k.mp4", "fmp4-base-moof.mp4",
    }.Where(name => TestSamples.Directory is not null && File.Exists(TestSamples.PathOf(name))));

    /// <summary>
    /// Truncations and byte flips anywhere in the file (as the fuzzer would make them, with a fixed seed): each mutant
    /// must keep the contract, and finish within 30 s.
    /// </summary>
    /// <summary>
    /// The container findings through the asynchronous API (<see cref="IDemuxer.ReadPacketsAsync"/>,
    /// <see cref="IVideoDecoder.DecodeAsync"/>, H.264 on three threads): the exceptions must come out of the awaits as
    /// they are, not wrapped.
    /// </summary>
    [Theory]
    [MemberData(nameof(Findings))]
    public async Task Finding_KeepsTheContractAsynchronously(string target, string name)
    {
        if (target != "file")
        {
            return;
        }

        var data = File.ReadAllBytes(Path.Combine(FindingsDirectory, target, name));
        await DecodeAsynchronously(data).WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>The mutants through the asynchronous API, H.264 on three threads.</summary>
    [Theory]
    [MemberData(nameof(SweepSamples))]
    public async Task Mutants_KeepTheContractAsynchronously(string sampleName)
    {
        var original = File.ReadAllBytes(TestSamples.PathOf(sampleName));
        var random = new Random(sampleName.Length);
        for (int m = 0; m < 40; m++)
        {
            var data = (byte[])original.Clone();
            for (int k = 0, n = random.Next(1, 16); k < n; k++)
            {
                data[random.Next(data.Length)] = (byte)random.Next(256);
            }

            await DecodeAsynchronously(data).WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    // Demuxes and decodes the video of a file through the asynchronous API; only the contract's exceptions may come out.
    private static async Task DecodeAsynchronously(byte[] file)
    {
        try
        {
            using var demuxer = Demuxer.Open(new MemoryStream(file));
            var video = demuxer.Video;
            IVideoDecoder? decoder = video?.Codec switch
            {
                CodecIds.H264 => new H264Decoder(video) { Threads = 3 },
                CodecIds.Mjpeg => new MjpegDecoder(video),
                _ => null,
            };
            await foreach (var packet in demuxer.ReadPacketsAsync())
            {
                if (decoder is not null && packet.StreamIndex == video!.StreamIndex)
                {
                    foreach (var frame in await decoder.DecodeAsync(packet))
                    {
                        frame.Dispose();
                    }
                }
            }

            if (decoder is not null)
            {
                foreach (var frame in await decoder.FlushAsync())
                {
                    frame.Dispose();
                }
            }
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException)
        {
        }
    }

    [Theory]
    [MemberData(nameof(SweepSamples))]
    public Task Mutants_KeepTheContract(string sampleName) => SweepMutants(sampleName, threads: 1);

    /// <summary>The same mutants with H.264 on three threads.</summary>
    [Theory]
    [MemberData(nameof(SweepSamples))]
    public Task Mutants_KeepTheContractWithThreads(string sampleName) => SweepMutants(sampleName, threads: 3);

    /// <summary>The same mutants concealing errors, on three threads.</summary>
    [Theory]
    [MemberData(nameof(SweepSamples))]
    public Task Mutants_KeepTheContractConcealing(string sampleName) => SweepMutants(sampleName, threads: 3, conceal: true);

    private static async Task SweepMutants(string sampleName, int threads, bool conceal = false)
    {
        var original = File.ReadAllBytes(TestSamples.PathOf(sampleName));
        var random = new Random(sampleName.Length);
        for (int m = 0; m < 40; m++)
        {
            var data = (byte[])original.Clone();
            if (m % 3 == 0)
            {
                data = data[..random.Next(data.Length)];
            }
            else
            {
                for (int k = 0, n = random.Next(1, 16); k < n; k++)
                {
                    data[random.Next(data.Length)] = (byte)random.Next(256);
                }
            }

            await Task.Run(() => Targets.Run("file", data, threads, conceal)).WaitAsync(TimeSpan.FromSeconds(30));
        }
    }
}
