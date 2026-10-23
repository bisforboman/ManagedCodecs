using ManagedCodecs.Fuzz;

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

    [Theory]
    [MemberData(nameof(Findings))]
    public async Task Finding_KeepsTheContract(string target, string name)
    {
        var data = File.ReadAllBytes(Path.Combine(FindingsDirectory, target, name));

        // WaitAsync throws TimeoutException for a hang.
        await Task.Run(() => Targets.Run(target, data)).WaitAsync(TimeSpan.FromSeconds(30));
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
    [Theory]
    [MemberData(nameof(SweepSamples))]
    public async Task Mutants_KeepTheContract(string sampleName)
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

            await Task.Run(() => Targets.Run("file", data)).WaitAsync(TimeSpan.FromSeconds(30));
        }
    }
}
