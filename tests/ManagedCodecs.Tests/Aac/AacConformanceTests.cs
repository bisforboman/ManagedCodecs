using ManagedCodecs.Aac;
using ManagedCodecs.Containers;

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

    public static TheoryData<string> TrimmedSamples() => new(TestSamples.WithReference(".trim.f32"));

    public static TheoryData<string> DelaySamples() => new(TestSamples.WithReference(".delay.f32"));

    /// <summary>
    /// The audio FFmpeg gives by default (name.trim.f32), with the edit list applied: the priming
    /// before its media time skipped, the packets after its end left out, and with several edits
    /// each one's packets in turn, those decoded before an edit only to prime it not shown. Every
    /// sample must come out, no more and no fewer.
    /// </summary>
    [Theory]
    [MemberData(nameof(TrimmedSamples))]
    public void MatchesFfmpegTrimmed(string sampleName)
    {
        var reference = File.ReadAllBytes(TestSamples.ReferenceOf(sampleName, ".trim.f32"));
        using var demuxer = Demuxer.Open(TestSamples.PathOf(sampleName));
        var audio = demuxer.Audio!;
        var decoder = new AacDecoder(audio);
        int channels = decoder.Channels;

        int position = 0;
        foreach (var packet in demuxer.ReadPackets().Where(p => p.StreamIndex == audio.StreamIndex))
        {
            var decoded = decoder.Decode(packet);
            Assert.Equal(packet.Discard ? 0 : Math.Max(0, 1024 - packet.SkipSamples), decoded.SampleCount);
            for (int i = 0; i < decoded.SampleCount; i++)
            {
                for (int c = 0; c < channels; c++)
                {
                    int offset = (((position + i) * channels) + c) * 4;
                    Assert.True(offset < reference.Length, $"More samples than FFmpeg's {reference.Length / 4 / channels}.");
                    double diff = Math.Abs(BitConverter.ToSingle(reference, offset) - decoded.Samples[c][i]);
                    Assert.True(diff <= Tolerance, $"Sample {position + i}, channel {c}: off by {diff:E3}.");
                }
            }

            position += decoded.SampleCount;
        }

        Assert.Equal(reference.Length / 4 / channels, position);
    }

    /// <summary>
    /// A stream whose first access unit names FAAC as its encoder (name.delay.f32: aac-faac-delay, FFmpeg's encoder name
    /// rewritten): FFmpeg drops FAAC's encoder delay, the first 1024 samples, which FAAC signals nowhere else, and so
    /// does <see cref="AacDecoder.Decode(Packet)"/>; every other sample comes out as FFmpeg's.
    /// </summary>
    [Theory]
    [MemberData(nameof(DelaySamples))]
    public void SkipsFaacEncoderDelayAsFfmpegDoes(string sampleName)
    {
        var reference = File.ReadAllBytes(TestSamples.ReferenceOf(sampleName, ".delay.f32"));
        using var demuxer = Demuxer.Open(TestSamples.PathOf(sampleName));
        var audio = demuxer.Audio!;
        var decoder = new AacDecoder(audio);
        int channels = decoder.Channels;

        int position = 0;
        int packets = 0;
        foreach (var packet in demuxer.ReadPackets().Where(p => p.StreamIndex == audio.StreamIndex))
        {
            var decoded = decoder.Decode(packet);
            Assert.Equal(packets++ == 0 ? 0 : 1024, decoded.SampleCount);
            for (int i = 0; i < decoded.SampleCount; i++)
            {
                for (int c = 0; c < channels; c++)
                {
                    int offset = (((position + i) * channels) + c) * 4;
                    Assert.True(offset < reference.Length, $"More samples than FFmpeg's {reference.Length / 4 / channels}.");
                    double diff = Math.Abs(BitConverter.ToSingle(reference, offset) - decoded.Samples[c][i]);
                    Assert.True(diff <= Tolerance, $"Sample {position + i}, channel {c}: off by {diff:E3}.");
                }
            }

            position += decoded.SampleCount;
        }

        Assert.Equal(reference.Length / 4 / channels, position);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesFfmpeg(string sampleName)
    {
        var reference = File.ReadAllBytes(TestSamples.ReferenceOf(sampleName, ".f32"));
        using var demuxer = Demuxer.Open(TestSamples.PathOf(sampleName));
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

        if (sampleName.Contains("pns-ms", StringComparison.Ordinal))
        {
            Assert.True(stats.CorrelatedNoiseBands > 0, "No M/S in bands both channels fill with noise in the sample.");
        }
    }
}
