using ManagedCodecs.MonoGame;
using Microsoft.Xna.Framework;

namespace ManagedCodecs.Tests.MonoGame;

/// <summary>
/// VideoTexture's arithmetic, which needs no graphics device: where the picture is drawn (letterboxed, at its aspect
/// ratio, turned by its rotation) and the audio as 16-bit PCM. The texture and the sound need a window and an audio
/// device, which CI doesn't have: mcplay (tools/ManagedCodecs.MonoGame.Sample) runs them.
/// </summary>
public class VideoTextureTests
{
    [Fact]
    public void Placement_FitsAndCentres()
    {
        // 320x240 in 1000x600: the height decides (2.5), centred in the bounds, which start at (100, 50).
        var (position, rotation, origin, scale) = VideoTexture.Placement(320, 240, 1, 0, new Rectangle(100, 50, 1000, 600));
        Assert.Equal(new Vector2(600, 350), position);
        Assert.Equal(0, rotation);
        Assert.Equal(new Vector2(160, 120), origin);
        Assert.Equal(new Vector2(2.5f, 2.5f), scale);
    }

    [Fact]
    public void Placement_StretchesBySampleAspectRatio()
    {
        // 720x480 with 32:27 pixels shows as 853.3x480: in 1280x720, 1.5 times, the width stretched by 32/27 more.
        double aspect = VideoTexture.AspectOf(new Rational(32, 27));
        var (_, _, _, scale) = VideoTexture.Placement(720, 480, aspect, 0, new Rectangle(0, 0, 1280, 720));
        Assert.Equal(1.5f * 32 / 27, scale.X, 4);
        Assert.Equal(1.5f, scale.Y, 4);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public void Placement_AQuarterTurnSwapsTheSides(int degrees)
    {
        // A 1920x1080 phone video turned upright fits a 1080x1920 screen exactly.
        var (position, rotation, origin, scale) = VideoTexture.Placement(1920, 1080, 1, degrees, new Rectangle(0, 0, 1080, 1920));
        Assert.Equal(new Vector2(540, 960), position);
        Assert.Equal(-MathHelper.ToRadians(degrees), rotation);
        Assert.Equal(new Vector2(960, 540), origin);
        Assert.Equal(new Vector2(1, 1), scale);
    }

    [Fact]
    public void Placement_AHalfTurnKeepsTheSides()
    {
        var (_, rotation, _, scale) = VideoTexture.Placement(320, 240, 1, 180, new Rectangle(0, 0, 640, 480));
        Assert.Equal(-MathF.PI, rotation, 5);
        Assert.Equal(new Vector2(2, 2), scale);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void AspectOf_NoneIsSquare(int numerator, int denominator) =>
        Assert.Equal(1, VideoTexture.AspectOf(new Rational(numerator, denominator)));

    [Fact]
    public void ToPcm16_RoundsClampsAndIsLittleEndian()
    {
        var bytes = VideoTexture.ToPcm16([0f, 1f, -1f, 0.5f, 2f, -2f, 1f / 32767]);
        short[] values = [.. Enumerable.Range(0, bytes.Length / 2).Select(i => BitConverter.ToInt16(bytes, 2 * i))];
        Assert.Equal([0, 32767, -32767, 16384, 32767, -32768, 1], values);
        Assert.Equal([0xFF, 0x7F], bytes[2..4]);
    }
}
