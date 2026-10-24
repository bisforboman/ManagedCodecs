using ManagedCodecs.Aac;
using ManagedCodecs.Containers;

namespace ManagedCodecs.Tests.Aac;

/// <summary>
/// HE-AAC decoded with spectral band replication against FFmpeg's output (name.sbr.f32: at the SBR rate, without the
/// encoder delay of an MP4 file's iTunSMPB tag, a mono stream in stereo).
/// </summary>
/// <remarks>
/// <para>
/// FFmpeg runs SBR in single precision, and the HF generator's prediction coefficients solve a 2x2 system that is
/// nearly singular where a subband holds one sinusoid (as the samples' test tones do): there FFmpeg's coefficients, and
/// so its output, move by up to about 5e-3 of the signal's peak around noise bursts, as much as this decoder's own output
/// moves when the coefficients are computed in single precision instead (the same frames, the same size). Elsewhere the
/// two agree to about 1e-6. So the comparison bounds the largest difference loosely (1e-2 of the channel's peak, or of
/// full scale) and the mean one tightly: a wrong formula, even a small one, moves the output in every frame it touches. An LFE, which SBR leaves without a high band, goes through
/// the QMF banks alone and agrees to 1e-6.
/// </para>
/// <para>
/// Parametric stereo (HE-AAC v2) comes out as stereo, as FFmpeg's does, and is held to the same bounds (it agrees to
/// about 3e-4 at most, 3e-6 on average); its mean is held tighter, since a decay slope of 0.06 instead of 0.05 stays
/// within the SBR bound.
/// </para>
/// </remarks>
public class SbrConformanceTests
{
    private const double MaxTolerance = 1e-2;
    private const double MeanTolerance = 1e-4;

    // Parametric stereo agrees to 3e-6 on average; a decay slope of 0.06 instead of 0.05 gives 1e-4.
    private const double ParametricStereoMeanTolerance = 2e-5;
    private const double QmfOnlyTolerance = 1e-6;

    public static TheoryData<string> Samples() => new(TestSamples.WithReference(".sbr.f32"));

    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesFfmpeg(string sampleName)
    {
        var bytes = File.ReadAllBytes(TestSamples.ReferenceOf(sampleName, ".sbr.f32"));
        var reference = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, reference, 0, bytes.Length);

        using var demuxer = Demuxer.Open(TestSamples.PathOf(sampleName));
        var audio = demuxer.Audio!;
        var decoder = new AacDecoder(audio);
        int channels = decoder.Channels;
        if (sampleName.EndsWith(".mp4", StringComparison.Ordinal))
        {
            // The config signals SBR, so the track reports the output's rate, as FFmpeg does.
            Assert.Equal(decoder.SampleRate, audio.SampleRate);
            Assert.Equal(decoder.Channels, audio.Channels);
        }

        int referenceChannels = channels == 1 ? 2 : channels; // FFmpeg's mono HE-AAC is stereo

        var worst = new double[channels];
        var sum = new double[channels];
        var peak = new double[channels];
        int position = 0;
        int packets = 0;
        foreach (var packet in demuxer.ReadPackets().Where(p => p.StreamIndex == audio.StreamIndex))
        {
            var decoded = decoder.Decode(packet);
            packets++;
            Assert.Equal(decoder.SampleRate, decoded.SampleRate);
            for (int i = 0; i < decoded.SampleCount; i++)
            {
                int offset = (position + i) * referenceChannels;
                Assert.True(offset < reference.Length, $"More samples than FFmpeg's {reference.Length / referenceChannels}.");
                for (int c = 0; c < channels; c++)
                {
                    double diff = Math.Abs(reference[offset + c] - decoded.Samples[c][i]);
                    worst[c] = Math.Max(worst[c], diff);
                    sum[c] += diff;
                    peak[c] = Math.Max(peak[c], Math.Abs(reference[offset + c]));
                }
            }

            position += decoded.SampleCount;
        }

        Assert.Equal(reference.Length / referenceChannels, position);
        var stats = decoder.Statistics;
        for (int c = 0; c < channels; c++)
        {
            Assert.True(worst[c] <= MaxTolerance * Math.Max(1, peak[c]), $"Channel {c}: off by up to {worst[c]:E3} (peak {peak[c]:F3}).");
            Assert.True(sum[c] / position <= (channels == 2 && decoder.Statistics.PsAppliedFrames > 0 ? ParametricStereoMeanTolerance : MeanTolerance), $"Channel {c}: off by {sum[c] / position:E3} on average.");
        }

        if (sampleName.StartsWith("heaacv2", StringComparison.Ordinal))
        {
            // A mono core made stereo, every frame by its own parameters, transients ducked.
            Assert.Equal(2, channels);
            Assert.Equal(stats.PsFrames, stats.PsAppliedFrames);
            Assert.Equal(packets, stats.PsAppliedFrames);
            Assert.True(stats.PsDuckedSlots > 0, "No transients ducked.");
        }

        if (channels == 1)
        {
            for (int i = 0; i < reference.Length; i += 2)
            {
                Assert.Equal(reference[i], reference[i + 1]);
            }
        }

        if (sampleName.Contains("5.1", StringComparison.Ordinal))
        {
            Assert.True(worst[3] <= QmfOnlyTolerance, $"LFE: off by up to {worst[3]:E3}.");
        }

        Assert.True(stats.SbrChirpBands > 0, "The HF generator never filtered.");
        Assert.True(stats.SbrLimitedGains > 0, "The limiter never cut a gain.");
        if (sampleName.Contains("header2", StringComparison.Ordinal))
        {
            // The header's second part, with settings other than the defaults.
            Assert.True(stats.SbrSmoothedEnvelopes > 0, "No gains smoothed over time.");
            Assert.True(stats.SbrUninterpolatedHeaders > 0, "No headers without frequency interpolation.");
            Assert.Equal((1 << 3, 1 << 0), (stats.SbrLimiterBands, stats.SbrLimiterGains));
        }
        else
        {
            Assert.Equal((0, 1 << 2, 1 << 2), (stats.SbrSmoothedEnvelopes, stats.SbrLimiterBands, stats.SbrLimiterGains));
        }

        if (sampleName.Contains("stereo-44k", StringComparison.Ordinal))
        {
            Assert.True(stats.SbrTransientEnvelopes > 0, "No transient envelopes.");
            Assert.True(stats.SbrSinusoids > 0, "No sinusoids added.");
        }
    }
}
