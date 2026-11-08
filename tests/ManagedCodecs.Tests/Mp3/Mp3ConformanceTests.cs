using ManagedCodecs.Containers;
using ManagedCodecs.Mp3;

namespace ManagedCodecs.Tests.Mp3;

/// <summary>
/// Decodes the MP3 track of every sample with a name.mp3.f32 reference (FFmpeg's float output, every packet whole, an
/// MP4 edit list ignored) or name.mp3.trim.f32 (as FFmpeg gives it by default, every sample has one) and compares sample
/// values; each sample also has to exercise the tool it is named for (<see cref="Mp3Statistics"/>).
/// </summary>
/// <remarks>
/// Both decoders work in floating point (FFmpeg's mp3float in single precision, this one in double, rounded at the end),
/// so the comparison takes a tolerance: 2^-17, a quarter of a 16-bit step, as AAC's. Correct decoding measures under
/// 2e-6; a wrong table entry or band edge shows at 1e-2 and above.
/// </remarks>
public class Mp3ConformanceTests
{
    private const double Tolerance = 1.0 / (1 << 17);

    public static TheoryData<string> Samples() => new(TestSamples.WithReference(".mp3.f32"));

    public static TheoryData<string> TrimmedSamples() => new(TestSamples.WithReference(".mp3.trim.f32"));

    /// <summary>Every packet decoded whole, priming included: every sample as FFmpeg's, no more and no fewer.</summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesFfmpeg(string sampleName)
    {
        var reference = File.ReadAllBytes(TestSamples.ReferenceOf(sampleName, ".mp3.f32"));
        using var demuxer = Demuxer.Open(TestSamples.PathOf(sampleName));
        var audio = demuxer.Audio!;
        Assert.Equal(CodecIds.Mp3, audio.Codec);
        var decoder = new Mp3Decoder(audio);

        int position = 0;
        foreach (var packet in demuxer.ReadPackets().Where(p => p.StreamIndex == audio.StreamIndex))
        {
            var decoded = decoder.Decode(packet.Data.Span);
            Assert.Equal((audio.SampleRate, audio.Channels), (decoded.SampleRate, decoded.Channels));
            Compare(reference, decoded, position);
            position += decoded.SampleCount;
        }

        Assert.Equal(reference.Length / 4 / decoder.Channels, position);
    }

    /// <summary>
    /// <see cref="Mp3Decoder.Decode(Packet)"/>: an MP4 edit list's priming skipped (the encoder's delay and the decoder's,
    /// 1105 samples for LAME), and a Matroska DiscardPadding's samples dropped from the end, as FFmpeg gives them.
    /// </summary>
    [Theory]
    [MemberData(nameof(TrimmedSamples))]
    public void MatchesFfmpegTrimmed(string sampleName)
    {
        var reference = File.ReadAllBytes(TestSamples.ReferenceOf(sampleName, ".mp3.trim.f32"));
        using var demuxer = Demuxer.Open(TestSamples.PathOf(sampleName));
        var audio = demuxer.Audio!;
        var decoder = new Mp3Decoder(audio);

        int position = 0;
        foreach (var packet in demuxer.ReadPackets().Where(p => p.StreamIndex == audio.StreamIndex))
        {
            var decoded = decoder.Decode(packet);
            Compare(reference, decoded, position);
            position += decoded.SampleCount;
        }

        Assert.Equal(reference.Length / 4 / decoder.Channels, position);
        CheckTool(sampleName, decoder.Statistics);
    }

    private static void Compare(byte[] reference, AudioFrame decoded, int position)
    {
        int channels = decoded.Channels;
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
    }

    // What each sample of make-samples.sh is for, by its name.
    private static void CheckTool(string name, Mp3Statistics stats)
    {
        Assert.True(stats.Frames > 0);
        if (name.Contains("joint", StringComparison.Ordinal))
        {
            Assert.True(stats.MidSideFrames > stats.Frames / 2, "Too few mid/side frames in the sample.");
            Assert.True(stats.ReservoirFrames > stats.Frames / 2, "Too few frames reach into the bit reservoir.");
        }

        if (name.Contains("320k", StringComparison.Ordinal))
        {
            Assert.True(stats.LinbitsGranules > 0, "No big-value table with linbits in the sample.");
        }

        if (name.Contains("mp3-stereo", StringComparison.Ordinal))
        {
            Assert.Equal(0, stats.MidSideFrames + stats.IntensityFrames);
            Assert.True(stats.Count1B > 0, "No count1 table B in the sample.");
        }

        if (name.Contains("transient", StringComparison.Ordinal))
        {
            Assert.True(stats.BlockTypes[2] > 10, "Too few short blocks in the sample.");
            Assert.True(stats.BlockTypes[1] > 0 && stats.BlockTypes[3] > 0, "No start or stop blocks in the sample.");
        }

        if (name.Contains("22k", StringComparison.Ordinal) || name.Contains("24k", StringComparison.Ordinal) || name.Contains("16k", StringComparison.Ordinal)
            || name.Contains("8k", StringComparison.Ordinal) || name.Contains("11k", StringComparison.Ordinal) || name.Contains("12k", StringComparison.Ordinal))
        {
            Assert.Equal(stats.Frames, stats.LsfFrames);
        }

        if (name.Contains("shine", StringComparison.Ordinal))
        {
            Assert.True(stats.DroppedQuadruples > 0, "No count1 quadruple reads past its granule's end in the sample.");
        }

        if (name.Contains("intensity", StringComparison.Ordinal))
        {
            Assert.True(stats.IntensityFrames == stats.Frames, "A frame without intensity stereo in the sample.");
        }

        if (name.Contains("mixed", StringComparison.Ordinal))
        {
            Assert.True(stats.MixedGranules > 0, "No mixed short block in the sample.");
            Assert.True(stats.MixedLongGranules > 0, "No mixed start or stop block in the sample.");
        }

        if (name.Contains("cut", StringComparison.Ordinal))
        {
            Assert.True(stats.MissingReservoir > 0, "No frame's main data starts before the stream's first.");
        }
    }
}
