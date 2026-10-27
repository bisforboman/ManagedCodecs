namespace ManagedCodecs;

/// <summary>Decoded audio: one array of 32-bit float samples per channel, nominally in [-1, 1].</summary>
/// <remarks>Samples are not clipped; values slightly outside [-1, 1] are kept as decoded.</remarks>
public sealed class AudioFrame
{
    /// <summary>A frame of <paramref name="channels"/> channels of <paramref name="sampleCount"/> samples each, all 0.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The rate or channel count is not positive, or the sample count is negative.</exception>
    public AudioFrame(int sampleRate, int channels, int sampleCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfNegative(sampleCount);

        SampleRate = sampleRate;
        Samples = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            Samples[c] = new float[sampleCount];
        }
    }

    /// <summary>Samples per second (per channel).</summary>
    public int SampleRate { get; }

    /// <summary>Number of channels, in the order the decoder gives them (AAC: as FFmpeg orders them).</summary>
    public int Channels => Samples.Length;

    /// <summary>Samples per channel in this frame.</summary>
    public int SampleCount => Samples[0].Length;

    /// <summary>Samples[channel][index].</summary>
    public float[][] Samples { get; }

    /// <summary>
    /// Presentation timestamp of the first sample, in the stream's time base: the packet's, moved by the samples the
    /// decoder dropped from its start (priming, as the container says); 0 for frames decoded without a packet.
    /// </summary>
    public long Pts { get; set; }

    /// <summary>This frame without its first <paramref name="count"/> samples (all of them if it has fewer).</summary>
    internal AudioFrame DropStart(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
        {
            return this;
        }

        int start = Math.Min(count, SampleCount);
        var trimmed = new AudioFrame(SampleRate, Channels, SampleCount - start);
        for (int c = 0; c < Channels; c++)
        {
            Samples[c].AsSpan(start).CopyTo(trimmed.Samples[c]);
        }

        return trimmed;
    }
}
