namespace ManagedCodecs;

/// <summary>Decoded audio: one array of 32-bit float samples per channel, nominally in [-1, 1].</summary>
/// <remarks>Samples are not clipped; values slightly outside [-1, 1] are kept as decoded.</remarks>
public sealed class AudioFrame
{
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

    public int SampleRate { get; }

    public int Channels => Samples.Length;

    public int SampleCount => Samples[0].Length;

    /// <summary>Samples[channel][index].</summary>
    public float[][] Samples { get; }
}
