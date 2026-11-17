namespace ManagedCodecs.Playback;

/// <summary>
/// More than two channels to stereo, as FFmpeg's <c>-ac 2</c> gives them in float (docs/decisions/2026-11-17-more-than-two-channels.md):
/// each speaker into left and right by a weight of its own, measured on FFmpeg 6.1, not scaled down (a loud passage
/// can exceed 1, as FFmpeg's float output does). A channel count with no layout is FFmpeg's default layout for it.
/// </summary>
internal static class Downmix
{
    private const float Half = 0.5f;

    // 1 / sqrt(2), -3 dB.
    private const float Root = 0.70710678f;

    // FFmpeg's default layouts for a channel count with none named, 3 to 8 channels: 2.1, 4.0, 5.0, 5.1, 6.1, 7.1.
    private static readonly ulong[] Defaults = [0xB, 0x107, 0x37, 0x3F, 0x70F, 0x63F];

    /// <summary>
    /// Each channel's weight into left and right, or null where the channels keep today's rule (the first two): two or
    /// fewer, or more than eight with no layout named (FFmpeg has no default layout for them).
    /// </summary>
    public static (float Left, float Right)[]? Weights(ulong mask, int channels)
    {
        if (channels <= 2)
        {
            return null;
        }

        if (Speakers(mask) != channels)
        {
            if (channels > 8)
            {
                return null;
            }

            mask = Defaults[channels - 3];
        }

        var weights = new (float, float)[channels];
        int channel = 0;
        for (int speaker = 0; speaker < 64; speaker++)
        {
            if ((mask & (1UL << speaker)) != 0)
            {
                weights[channel++] = Weight(speaker);
            }
        }

        return weights;
    }

    // The speakers a mask names (netstandard2.1, which the Unity build targets, has no PopCount).
    private static int Speakers(ulong mask)
    {
        int count = 0;
        for (; mask != 0; mask &= mask - 1)
        {
            count++;
        }

        return count;
    }

    /// <summary>A speaker's weight into left and right, by its bit in WAVE's channel mask.</summary>
    internal static (float Left, float Right) Weight(int speaker) => speaker switch
    {
        0 or 6 => (1, 0),        // front left, front left of centre
        1 or 7 => (0, 1),        // front right, front right of centre
        2 => (Root, Root),       // front centre
        4 or 9 or 12 => (Root, 0), // back left, side left, top front left
        5 or 10 or 14 => (0, Root), // back right, side right, top front right
        8 => (Half, Half),       // back centre

        // The LFE, and the speakers FFmpeg left out where measured (top back left and right) or that weren't measured
        // (top centre, top front and back centre, and the rest).
        _ => (0, 0),
    };

    /// <summary>The frame's channels as interleaved stereo by <paramref name="weights"/>.</summary>
    public static float[] Apply(AudioFrame frame, (float Left, float Right)[] weights)
    {
        int count = frame.SampleCount;
        var samples = new float[count * 2];
        for (int c = 0; c < frame.Channels; c++)
        {
            var (left, right) = weights[c];
            var input = frame.Samples[c];
            if (left != 0)
            {
                for (int i = 0; i < count; i++)
                {
                    samples[2 * i] += left * input[i];
                }
            }

            if (right != 0)
            {
                for (int i = 0; i < count; i++)
                {
                    samples[(2 * i) + 1] += right * input[i];
                }
            }
        }

        return samples;
    }
}
