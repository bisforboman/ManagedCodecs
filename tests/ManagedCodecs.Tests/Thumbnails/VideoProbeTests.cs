using System.Globalization;
using System.Text.Json;
using ManagedCodecs.Thumbnails;

namespace ManagedCodecs.Tests.Thumbnails;

/// <summary>
/// <see cref="VideoProbe"/> against ffprobe (<c>-show_entries format=duration:stream=...</c>, make-samples.sh's
/// name.probe.json) on every MP4 and AVI sample whose video this library decodes: duration, size, aspect ratio, rotation,
/// frame rate and the audio stream's codec, rate and channels.
/// </summary>
public class VideoProbeTests
{
    [Theory]
    [InlineData(0.2002, 2_002_000)] // Mono's TimeSpan.FromSeconds: 2_000_000, rounded to the millisecond
    [InlineData(7.999999999999999, 79_999_999)] // truncated, as on .NET; Mono's: 80_000_000
    [InlineData(0.0, 0)]
    [InlineData(-1.5, -15_000_000)]
    public void TimeTruncatesTicksOnEveryRuntime(double seconds, long ticks)
    {
        Assert.Equal(ticks, VideoProbe.Time(seconds).Ticks);
        Assert.Equal(TimeSpan.FromSeconds(seconds), VideoProbe.Time(seconds)); // .NET 10's own, which the tests run on
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1e300)]
    public void TimeOverflowsAsTimeSpanDoes(double seconds)
    {
        Assert.Throws<OverflowException>(() => VideoProbe.Time(seconds));
        Assert.Throws<OverflowException>(() => TimeSpan.FromSeconds(seconds));
    }

    public static TheoryData<string> Files()
    {
        var files = new TheoryData<string>();
        if (TestSamples.Directory is not { } directory)
        {
            return files;
        }

        foreach (var file in Directory.EnumerateFiles(directory).Order())
        {
            if (Path.GetExtension(file) is ".avi" or ".mp4" && File.Exists(Path.ChangeExtension(file, ".probe.json"))
                && TestSamples.CodecOf(Path.GetRelativePath(directory, file)) is CodecIds.H264 or CodecIds.Mjpeg)
            {
                files.Add(Path.GetRelativePath(directory, file));
            }
        }

        return files;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void MatchesFfprobe(string name)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(TestSamples.PathOf(name), ".probe.json")));
        var root = json.RootElement;
        var streams = root.GetProperty("streams").EnumerateArray().ToList();
        var video = streams.First(s => s.GetProperty("codec_type").GetString() == "video");
        var info = VideoProbe.Read(TestSamples.PathOf(name));

        // AVI's audio is not read, so an AVI file lasts as long as its video stream (ffprobe's format duration also
        // counts the audio: avi-dropped-frame's MP3 runs half a second past the video).
        var durationOf = name.EndsWith(".avi", StringComparison.Ordinal) ? video : root.GetProperty("format");
        double duration = double.Parse(durationOf.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        Assert.True(Math.Abs(info.Duration.TotalSeconds - duration) < 1.5e-6, $"Duration {info.Duration.TotalSeconds:F6} s, ffprobe's {duration:F6} s.");
        Assert.Equal(video.GetProperty("width").GetInt32(), info.Width);
        Assert.Equal(video.GetProperty("height").GetInt32(), info.Height);
        Assert.Equal(Ratio(video, "sample_aspect_ratio") is { Numerator: > 0 } sar ? sar : new Rational(1, 1), info.SampleAspectRatio);
        Assert.Equal(Rotation(video), info.Rotation);
        var rate = Ratio(video, "avg_frame_rate");
        Assert.True(Math.Abs(info.FramesPerSecond - rate.ToDouble()) < 1e-6, $"{info.FramesPerSecond} frames a second, ffprobe's average {rate}.");

        var (width, height) = VideoProbe.DisplaySize(info.Width, info.Height, info.SampleAspectRatio, info.Rotation);
        Assert.Equal((width, height), (info.DisplayWidth, info.DisplayHeight));

        var audio = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "audio");
        if (name.EndsWith(".avi", StringComparison.Ordinal) || audio.ValueKind == JsonValueKind.Undefined)
        {
            Assert.Null(info.Audio);
        }
        else
        {
            Assert.NotNull(info.Audio);
            Assert.Equal(int.Parse(audio.GetProperty("sample_rate").GetString()!, CultureInfo.InvariantCulture), info.Audio.SampleRate);
            Assert.Equal(audio.GetProperty("channels").GetInt32(), info.Audio.Channels);
        }
    }

    // Each stream's Duration (VideoStreamInfo, AudioStreamInfo) against ffprobe's stream duration, where it lists one.
    [Theory]
    [MemberData(nameof(Files))]
    public void StreamDurationsMatchFfprobe(string name)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(TestSamples.PathOf(name), ".probe.json")));
        using var demuxer = ManagedCodecs.Containers.Demuxer.Open(TestSamples.PathOf(name));
        foreach (var stream in json.RootElement.GetProperty("streams").EnumerateArray())
        {
            if (!stream.TryGetProperty("duration", out var listed) || listed.GetString() is not { } text)
            {
                continue;
            }

            double expected = double.Parse(text, CultureInfo.InvariantCulture);
            string type = stream.GetProperty("codec_type").GetString()!;
            (long Duration, Rational TimeBase)? actual = type switch
            {
                "video" when demuxer.Video is { } v => (v.Duration, v.TimeBase),
                "audio" when demuxer.Audio is { } a => (a.Duration, a.TimeBase),
                _ => null,
            };

            if (actual is { } d)
            {
                double seconds = d.Duration * d.TimeBase.ToDouble();
                Assert.True(Math.Abs(seconds - expected) < 1.5e-6, $"The {type} stream lasts {seconds:F6} s, ffprobe's {expected:F6} s.");
            }
        }
    }

    [Theory]
    [InlineData(320, 240, 1, 1, 0, 320, 240)]
    [InlineData(320, 240, 4, 3, 0, 427, 240)] // wider pixels: the width grows, rounded to nearest
    [InlineData(720, 576, 16, 15, 0, 768, 576)]
    [InlineData(352, 480, 10, 11, 0, 352, 528)] // narrower pixels: the height grows
    [InlineData(320, 240, 0, 1, 0, 320, 240)] // unknown: square
    [InlineData(320, 240, 1, 1, 90, 240, 320)]
    [InlineData(320, 240, 4, 3, 270, 240, 427)]
    [InlineData(320, 240, 1, 1, 180, 320, 240)]
    public void DisplaySizeStretchesThenTurns(int width, int height, int sarNum, int sarDen, int rotation, int displayWidth, int displayHeight) =>
        Assert.Equal((displayWidth, displayHeight), VideoProbe.DisplaySize(width, height, new Rational(sarNum, sarDen), rotation));

    [Fact]
    public void RefusesACodecItCannotDecode()
    {
        Assert.Throws<NotSupportedException>(() => VideoProbe.Read(TestSamples.PathOf("mpeg4-bframes.mp4")));
    }

    [Fact]
    public void RefusesAFileWithoutVideo()
    {
        Assert.Throws<InvalidDataException>(() => VideoProbe.Read(TestSamples.PathOf("aac-stereo-48k.mp4")));
    }

    // ffprobe's display matrix rotation ("-90"), as the library gives it: 0 to 270 (DisplayTests).
    private static int Rotation(JsonElement stream)
    {
        if (!stream.TryGetProperty("side_data_list", out var sideData))
        {
            return 0;
        }

        foreach (var entry in sideData.EnumerateArray())
        {
            if (entry.TryGetProperty("rotation", out var rotation))
            {
                return ((rotation.GetInt32() % 360) + 360) % 360;
            }
        }

        return 0;
    }

    private static Rational Ratio(JsonElement stream, string property)
    {
        if (!stream.TryGetProperty(property, out var value) || value.GetString() is not { } text)
        {
            return new Rational(0, 1);
        }

        var parts = text.Split(':', '/');
        return new Rational(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
    }
}
