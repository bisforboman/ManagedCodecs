using ManagedCodecs.Aac;
using ManagedCodecs.Containers.Mp4;

namespace ManagedCodecs.Tests.Aac;

/// <summary>
/// Decodes the AAC track of every sample with a .f32 reference (FFmpeg's float output with the
/// edit list ignored, so it holds 1024 samples for every packet) and compares sample values.
/// </summary>
/// <remarks>
/// AAC output is floating point, and FFmpeg computes its IMDCT differently, so the comparison
/// uses a tolerance: 2^-17, a quarter of a 16-bit step. Correct decoding measures around 3e-7;
/// mistakes show up at 1e-3 and above. Each sample also has to exercise the tool it is named for.
/// </remarks>
public class AacConformanceTests
{
    private const double Tolerance = 1.0 / (1 << 17);

    public static TheoryData<string> Samples() => new(TestSamples.WithReference(".f32"));

    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesFfmpeg(string sampleName)
    {
        var reference = File.ReadAllBytes(TestSamples.ReferenceOf(sampleName, ".f32"));
        using var demuxer = Mp4Demuxer.Open(TestSamples.PathOf(sampleName));
        var audio = demuxer.Audio!;
        var decoder = new AacDecoder(audio);
        int channels = decoder.Channels;
        Assert.Equal((audio.SampleRate, audio.Channels), (decoder.SampleRate, channels));

        int frame = 0;
        double worst = 0;
        foreach (var packet in demuxer.ReadPackets().Where(p => p.StreamIndex == audio.StreamIndex))
        {
            var decoded = decoder.Decode(packet.Data.Span);
            Assert.Equal(1024, decoded.SampleCount);
            for (int i = 0; i < 1024; i++)
            {
                for (int c = 0; c < channels; c++)
                {
                    int offset = ((((frame * 1024) + i) * channels) + c) * 4;
                    double diff = Math.Abs(BitConverter.ToSingle(reference, offset) - decoded.Samples[c][i]);
                    worst = Math.Max(worst, diff);
                    Assert.True(diff <= Tolerance, $"Frame {frame}, channel {c}, sample {i}: off by {diff:E3}.");
                }
            }

            frame++;
        }

        Assert.Equal(reference.Length, frame * 1024 * channels * 4);

        var stats = decoder.Statistics;
        if (sampleName.Contains("stereo", StringComparison.Ordinal))
        {
            Assert.True(stats.MsBands > 0 && stats.IntensityBands > 0, "No M/S or intensity stereo in the sample.");
        }

        if (sampleName.Contains("transients", StringComparison.Ordinal))
        {
            Assert.True(stats.ShortFrames > 10, "Too few short-window frames in the sample.");
        }

        if (sampleName.Contains("tns", StringComparison.Ordinal))
        {
            Assert.True(stats.TnsFilters > 10, "Too few TNS filters in the sample.");
        }

        if (sampleName.Contains("pns", StringComparison.Ordinal))
        {
            Assert.True(stats.NoiseBands > 0, "No noise substitution in the sample.");
        }
    }
}
