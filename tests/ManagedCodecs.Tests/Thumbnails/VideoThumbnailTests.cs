using System.Text.RegularExpressions;
using ManagedCodecs.Containers;
using ManagedCodecs.Mjpeg;
using ManagedCodecs.Thumbnails;

namespace ManagedCodecs.Tests.Thumbnails;

/// <summary>
/// <see cref="VideoThumbnail"/> end to end against FFmpeg: at the start of every sample (make-samples.sh's
/// name.thumb.rgba) and at times into a few (name.atNNNN.thumb.rgba, <c>ffmpeg -ss</c>), 3/8 of the size as shown, within
/// <see cref="RgbaImageTests"/>' band; and its choices (dark frames, sizes, limits) on their own.
/// </summary>
public class VideoThumbnailTests
{
    private static readonly Regex AtReference = new(@"^(.+)\.at(\d+)\.thumb\.rgba$", RegexOptions.CultureInvariant);

    public static TheoryData<string> AtTheStart()
    {
        var files = new TheoryData<string>();
        foreach (var file in TestSamples.WithReference(".thumb.rgba").Where(f => File.Exists(TestSamples.ReferenceOf(f, ".framemd5"))))
        {
            files.Add(file);
        }

        return files;
    }

    public static TheoryData<string> AtTimes()
    {
        var references = new TheoryData<string>();
        if (TestSamples.Directory is { } directory)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.thumb.rgba").Select(Path.GetFileName).Where(f => AtReference.IsMatch(f!)).Order())
            {
                references.Add(file!);
            }
        }

        return references;
    }

    [Theory]
    [MemberData(nameof(AtTheStart))]
    public void AtTheStartMatchesFfmpeg(string file)
    {
        var info = VideoProbe.Read(TestSamples.PathOf(file));
        if (info.SampleAspectRatio != new Rational(1, 1))
        {
            return; // FFmpeg's reference scales the coded size, not the size as shown
        }

        Compare(file, TestSamples.ReferenceOf(file, ".thumb.rgba"), TimeSpan.Zero, info);
    }

    [Theory]
    [MemberData(nameof(AtTimes))]
    public void AtTheTimeMatchesFfmpeg(string reference)
    {
        var match = AtReference.Match(reference);
        string stem = match.Groups[1].Value;
        string file = File.Exists(TestSamples.PathOf(stem + ".mp4")) ? stem + ".mp4" : stem + ".avi";
        var at = TimeSpan.FromMilliseconds(int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
        var thumbnail = Compare(file, TestSamples.PathOf(reference), at, VideoProbe.Read(TestSamples.PathOf(file)));
        Assert.True(thumbnail.Time >= at - TimeSpan.FromMilliseconds(1), $"Shown at {thumbnail.Time}, before {at}.");
    }

    [Fact]
    public void SkipDarkStepsPastTheFadeIn()
    {
        if (TestSamples.Directory is null)
        {
            return;
        }

        // A keyframe every 0.2 s; the mean luma reaches 10% of white between 0.2 and 0.4 s of the fade.
        string path = TestSamples.PathOf("h264-fade-in.mp4");
        var plain = VideoThumbnail.Create(path, new ThumbnailOptions { At = TimeSpan.Zero });
        var stepped = VideoThumbnail.Create(path, new ThumbnailOptions { At = TimeSpan.Zero, SkipDark = true });
        Assert.Equal(TimeSpan.Zero, plain.Time);
        Assert.Equal(TimeSpan.FromSeconds(0.4), stepped.Time);

        // From a bright frame on, SkipDark changes nothing.
        var bright = VideoThumbnail.Create(path, new ThumbnailOptions { At = TimeSpan.FromSeconds(1.5), SkipDark = true });
        Assert.Equal(VideoThumbnail.Create(path, new ThumbnailOptions { At = TimeSpan.FromSeconds(1.5) }).Pixels, bright.Pixels);
    }

    [Fact]
    public void DefaultsToTenPercentInAtMostTenSeconds()
    {
        if (TestSamples.Directory is null)
        {
            return;
        }

        // h264-fade-in lasts 2 s: 10% is 0.2 s, its second keyframe. The default size fits 320 x 320.
        var thumbnail = VideoThumbnail.Create(TestSamples.PathOf("h264-fade-in.mp4"));
        Assert.Equal(TimeSpan.FromSeconds(0.2), thumbnail.Time);
        Assert.Equal((320, 240), (thumbnail.Width, thumbnail.Height));
        Assert.Equal(320 * 240 * 4, thumbnail.Pixels.Length);
    }

    [Fact]
    public void RefusesFramesOverTheLimit()
    {
        if (TestSamples.Directory is null)
        {
            return;
        }

        foreach (var file in new[] { "h264-bframes.mp4", "testsrc-420.avi" })
        {
            var tooMany = new ThumbnailOptions { MaxPixels = (320 * 240) - 1 };
            Assert.Throws<NotSupportedException>(() => VideoThumbnail.Create(TestSamples.PathOf(file), tooMany));
            VideoThumbnail.Create(TestSamples.PathOf(file), new ThumbnailOptions { MaxPixels = 320 * 240 });
        }
    }

    [Fact]
    public void ReadsAJpegsSizeBeforeDecodingIt()
    {
        if (TestSamples.Directory is null)
        {
            return;
        }

        // An AVI whose header ('avih') says 16 x 16 around 320 x 240 JPEGs: refused by the JPEG's own header, before the
        // decoder makes the frame (the frame's size would be refused only after).
        byte[] file = File.ReadAllBytes(TestSamples.PathOf("testsrc-420.avi"));
        int avih = file.AsSpan().IndexOf("avih"u8) + 8;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(avih + 32), 16);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(avih + 36), 16);
        var small = new ThumbnailOptions { MaxPixels = (320 * 240) - 1 };
        var refused = Assert.Throws<NotSupportedException>(() => VideoThumbnail.Create(new MemoryStream(file), small));
        Assert.StartsWith("A JPEG image in the video declares 320x240", refused.Message);
        var thumbnail = VideoThumbnail.Create(new MemoryStream(file), new ThumbnailOptions { MaxWidth = 240 });
        Assert.Equal((240, 180), (thumbnail.Width, thumbnail.Height)); // the frame's size, not the header's

        using var demuxer = Demuxer.Open(TestSamples.PathOf("testsrc-420.avi"));
        var packet = demuxer.ReadPackets().First();
        Assert.Equal((320, 240), MjpegDecoder.ImageSize(packet.Data.Span));
        Assert.Null(MjpegDecoder.ImageSize([0xFF, 0xD8, 0xFF, 0xD9]));
        Assert.Throws<InvalidDataException>(() => MjpegDecoder.ImageSize([1, 2, 3]));
    }

    [Fact]
    public async Task AsyncGivesTheSame()
    {
        if (TestSamples.Directory is null)
        {
            return;
        }

        using var stream = File.OpenRead(TestSamples.PathOf("h264-bframes.mp4"));
        var options = new ThumbnailOptions { At = TimeSpan.FromSeconds(1.2), MaxWidth = 100 };
        var synchronous = VideoThumbnail.Create(stream, options);
        stream.Position = 0;
        var asynchronous = await VideoThumbnail.CreateAsync(stream, options);
        Assert.Equal((synchronous.Width, synchronous.Height, synchronous.Time), (asynchronous.Width, asynchronous.Height, asynchronous.Time));
        Assert.Equal(synchronous.Pixels, asynchronous.Pixels);
        Assert.Equal(synchronous.ToPng(), asynchronous.ToPng());
    }

    [Fact]
    public async Task StopsWhenCancelled()
    {
        if (TestSamples.Directory is null)
        {
            return;
        }

        using var stream = File.OpenRead(TestSamples.PathOf("h264-bframes.mp4"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => VideoThumbnail.Create(stream, null, cancelled.Token));
        stream.Position = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VideoThumbnail.CreateAsync(stream, null, cancelled.Token));
    }

    [Fact]
    public void RefusesWhatItCannotThumbnail()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoThumbnail.Create(new MemoryStream(), new ThumbnailOptions { MaxWidth = 0 }));
        if (TestSamples.Directory is null)
        {
            return;
        }

        Assert.Throws<InvalidDataException>(() => VideoThumbnail.Create(TestSamples.PathOf("aac-stereo-48k.mp4")));
        Assert.Throws<NotSupportedException>(() => VideoThumbnail.Create(TestSamples.PathOf("mpeg4-bframes.mp4")));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VideoThumbnail.Create(TestSamples.PathOf("h264-bframes.mp4"), new ThumbnailOptions { At = TimeSpan.FromSeconds(-1) }));
    }

    [Theory]
    [InlineData(320, 240, 1, 1, 0, 320, 320, 320, 240)] // already fits
    [InlineData(1920, 1080, 1, 1, 0, 320, 320, 320, 180)] // width bound
    [InlineData(1080, 1920, 1, 1, 0, 320, 320, 180, 320)] // height bound
    [InlineData(1920, 1080, 1, 1, 90, 320, 320, 180, 320)] // turned, then fit
    [InlineData(320, 240, 2, 1, 0, 1000, 1000, 320, 120)] // shown 640 x 240: no wider than the frame, aspect kept
    [InlineData(320, 240, 1, 2, 0, 1000, 1000, 160, 240)] // shown 320 x 480: no taller than the frame
    [InlineData(4000, 2, 1, 1, 0, 100, 100, 100, 1)] // at least a pixel
    public void FitsTheFrameAsShown(int width, int height, int sarNumerator, int sarDenominator, int rotation, int maxWidth, int maxHeight, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), VideoThumbnail.Fit(width, height, new Rational(sarNumerator, sarDenominator), rotation, maxWidth, maxHeight));
    }

    private static Thumbnail Compare(string file, string referencePath, TimeSpan at, VideoInfo info)
    {
        byte[] expected = File.ReadAllBytes(referencePath);
        bool turned = info.Rotation is 90 or 270;
        var (shownWidth, shownHeight) = turned ? (info.Height, info.Width) : (info.Width, info.Height);
        var options = new ThumbnailOptions { At = at, MaxWidth = Math.Max(1, shownWidth * 3 / 8), MaxHeight = Math.Max(1, shownHeight * 3 / 8) };
        var thumbnail = VideoThumbnail.Create(TestSamples.PathOf(file), options);
        Assert.Equal((options.MaxWidth, options.MaxHeight), (thumbnail.Width, thumbnail.Height));
        Assert.Equal(expected.Length, thumbnail.Pixels.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            int below = thumbnail.Pixels[i] - expected[i];
            if (below > RgbaImageTests.FfmpegBelow || below < -RgbaImageTests.FfmpegAbove)
            {
                Assert.Fail($"FFmpeg's {expected[i]} against our {thumbnail.Pixels[i]} at pixel ({(i / 4) % thumbnail.Width}, {i / 4 / thumbnail.Width}), channel {i % 4}, frame at {thumbnail.Time}.");
            }
        }

        return thumbnail;
    }
}
