using System.Runtime.InteropServices;
using ManagedCodecs.Playback;

namespace ManagedCodecs.Tests.Playback;

/// <summary>
/// More than two channels to stereo (docs/decisions/2026-11-17-more-than-two-channels.md): the weights against FFmpeg's
/// <c>-ac 2</c> as measured for each layout (a tone of its own on each channel, least squares on the output), and the
/// players' samples against FFmpeg's downmix of every sample with a name.stereo.f32 reference.
/// </summary>
public class DownmixTests
{
    // AAC's samples differ from FFmpeg's decoder's by up to 2^-17 (AacConformanceTests); a downmix adds at most four
    // of them, each weighted 1 or less.
    private const double Tolerance = 4.0 / (1 << 17);

    private const float R = 0.70710678f;

    public static TheoryData<string> Samples() => [.. TestSamples.WithReference(".stereo.f32")];

    /// <summary>Each layout FFmpeg 6.1 was measured on: its mask, and each channel's weight into left, then right.</summary>
    public static TheoryData<string, ulong, float[], float[]> Layouts() => new()
    {
        { "3.0", 0x7, [1, 0, R], [0, 1, R] },
        { "3.0(back)", 0x103, [1, 0, 0.5f], [0, 1, 0.5f] },
        { "2.1", 0xB, [1, 0, 0], [0, 1, 0] },
        { "quad", 0x33, [1, 0, R, 0], [0, 1, 0, R] },
        { "quad(side)", 0x603, [1, 0, R, 0], [0, 1, 0, R] },
        { "4.0", 0x107, [1, 0, R, 0.5f], [0, 1, R, 0.5f] },
        { "3.1", 0xF, [1, 0, R, 0], [0, 1, R, 0] },
        { "5.0", 0x37, [1, 0, R, R, 0], [0, 1, R, 0, R] },
        { "4.1", 0x10F, [1, 0, R, 0, 0.5f], [0, 1, R, 0, 0.5f] },
        { "5.1", 0x3F, [1, 0, R, 0, R, 0], [0, 1, R, 0, 0, R] },
        { "5.1(side)", 0x60F, [1, 0, R, 0, R, 0], [0, 1, R, 0, 0, R] },
        { "6.0", 0x707, [1, 0, R, 0.5f, R, 0], [0, 1, R, 0.5f, 0, R] },
        { "hexagonal", 0x137, [1, 0, R, R, 0, 0.5f], [0, 1, R, 0, R, 0.5f] },
        { "6.1", 0x70F, [1, 0, R, 0, 0.5f, R, 0], [0, 1, R, 0, 0.5f, 0, R] },
        { "6.1(back)", 0x13F, [1, 0, R, 0, R, 0, 0.5f], [0, 1, R, 0, 0, R, 0.5f] },
        { "7.0", 0x637, [1, 0, R, R, 0, R, 0], [0, 1, R, 0, R, 0, R] },
        { "7.1", 0x63F, [1, 0, R, 0, R, 0, R, 0], [0, 1, R, 0, 0, R, 0, R] },
        { "7.1(wide)", 0xFF, [1, 0, R, 0, R, 0, 1, 0], [0, 1, R, 0, 0, R, 0, 1] },
        { "7.1(wide-side)", 0x6CF, [1, 0, R, 0, 1, 0, R, 0], [0, 1, R, 0, 0, 1, 0, R] },
        { "octagonal", 0x737, [1, 0, R, R, 0, 0.5f, R, 0], [0, 1, R, 0, R, 0.5f, 0, R] },
        { "5.1.2", 0x503F, [1, 0, R, 0, R, 0, R, 0], [0, 1, R, 0, 0, R, 0, R] },
        { "7.1.4", 0x2D63F, [1, 0, R, 0, R, 0, R, 0, R, 0, 0, 0], [0, 1, R, 0, 0, R, 0, R, 0, R, 0, 0] },
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public void WeighsEachSpeakerAsFfmpeg(string layout, ulong mask, float[] left, float[] right)
    {
        var weights = Downmix.Weights(mask, left.Length);
        Assert.NotNull(weights);
        Assert.Equal(left, weights.Select(w => w.Left));
        Assert.Equal(right, weights.Select(w => w.Right));
        Assert.NotEmpty(layout);
    }

    /// <summary>A channel count with no layout (or one whose mask names another count) is FFmpeg's default for it.</summary>
    [Theory]
    [InlineData(3, 0xBUL)]
    [InlineData(4, 0x107UL)]
    [InlineData(5, 0x37UL)]
    [InlineData(6, 0x3FUL)]
    [InlineData(7, 0x70FUL)]
    [InlineData(8, 0x63FUL)]
    public void TakesFfmpegsDefaultLayoutWhereNoneIsNamed(int channels, ulong layout)
    {
        Assert.Equal(Downmix.Weights(layout, channels), Downmix.Weights(0, channels));
        Assert.Equal(Downmix.Weights(layout, channels), Downmix.Weights(0x3, channels)); // a mask of two speakers
    }

    /// <summary>Two channels or fewer keep their rule (mono on both, stereo as it is), and so do more than eight with no layout.</summary>
    [Theory]
    [InlineData(1, 0x4UL)]
    [InlineData(2, 0x3UL)]
    [InlineData(9, 0UL)]
    [InlineData(16, 0UL)]
    public void LeavesTheseAsTheyWere(int channels, ulong mask) => Assert.Null(Downmix.Weights(mask, channels));

    /// <summary>A frame trimmed at its start or end (priming, Matroska's padding) keeps its layout.</summary>
    [Fact]
    public void ATrimmedFrameKeepsItsLayout()
    {
        var frame = new AudioFrame(48000, 3, 1024) { ChannelMask = 0x7 };
        Assert.Equal(0x7UL, frame.Drop(100, 0).ChannelMask);
        Assert.Equal(0x7UL, frame.Drop(0, 100).ChannelMask);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void PlaysAsFfmpegDownmixesIt(string name)
    {
        var bytes = File.ReadAllBytes(TestSamples.ReferenceOf(name, ".stereo.f32"));
        var expected = MemoryMarshal.Cast<byte, float>(bytes).ToArray();
        using var player = new AudioPlayer(TestSamples.PathOf(name));
        player.Start();
        var played = new List<float>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!player.IsFinished && DateTime.UtcNow < deadline)
        {
            if (player.TakeAudio() is { } chunk)
            {
                played.AddRange(chunk.Samples);
            }
            else
            {
                Thread.Sleep(1);
            }
        }

        while (player.TakeAudio() is { } rest)
        {
            played.AddRange(rest.Samples);
        }

        Assert.Null(player.Error);
        Assert.Equal(expected.Length, played.Count);
        double worst = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(expected[i] - played[i]));
        }

        Assert.True(worst <= Tolerance, $"Off by {worst:E3}.");
    }
}
