using System.Buffers.Binary;
using ManagedCodecs.Containers;
using ManagedCodecs.H264;
using ManagedCodecs.Mjpeg;
using ManagedCodecs.Thumbnails;

namespace ManagedCodecs.Tests.Thumbnails;

/// <summary>
/// The thumbnail's pixels (<see cref="RgbaImage"/>): the conversion against its definition in double precision, the area
/// scaling against the overlap sum computed pixel by pixel, the rotation against the turned coordinates, and all three
/// together against FFmpeg's scaler (make-samples.sh's name.thumb.rgba: <c>-vf format=rgba,scale=W:H:flags=area...</c>
/// with autorotate, W and H 3/8 of the turned size).
/// </summary>
public class RgbaImageTests
{
    /// <summary>
    /// How far below ours FFmpeg's values may be, per channel: its fast YUV to RGB path rounds down, 0 to 3 levels under
    /// the definition at full size (green most), never over it; its area scaler then adds at most one either way.
    /// </summary>
    internal const int FfmpegBelow = 3;

    /// <summary>
    /// How far above ours FFmpeg's values may be: its area scaler's one level, and on ARM64 two, where its NEON code
    /// reduces 9 to 14 bits to 8 (the deep samples' references go through 8 bits) rounding up more often than x64's
    /// (CI's build-arm64: 2 above in blue on the 10, 12 and 14-bit samples). Widened there rather than matched.
    /// </summary>
    internal static readonly int FfmpegAbove = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? 2 : 1;

    private static readonly PixelFormat[] Formats = Enum.GetValues<PixelFormat>();

    public static TheoryData<string> Thumbnails()
    {
        var files = new TheoryData<string>();

        // Not where FFmpeg's frames have no reference to find the first by (make-samples.sh removes one).
        foreach (var file in TestSamples.WithReference(".thumb.rgba").Where(f => File.Exists(TestSamples.ReferenceOf(f, ".framemd5"))))
        {
            files.Add(file);
        }

        return files;
    }

    [Theory]
    [MemberData(nameof(Thumbnails))]
    public void MatchesFfmpegsScaler(string file)
    {
        byte[] expected = File.ReadAllBytes(TestSamples.ReferenceOf(file, ".thumb.rgba"));
        using var demuxer = Demuxer.Open(TestSamples.PathOf(file));
        var video = demuxer.Video!;
        using var frame = FfmpegsFirstFrame(file, demuxer, video);

        bool turned = video.Rotation is 90 or 270;
        var (shownWidth, shownHeight) = turned ? (frame.Height, frame.Width) : (frame.Width, frame.Height);
        int width = Math.Max(1, shownWidth * 3 / 8);
        int height = Math.Max(1, shownHeight * 3 / 8);
        byte[] actual = RgbaImage.Render(frame, width, height, video.Rotation, RgbaConverter.For(frame, video.Codec));

        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < actual.Length; i++)
        {
            int below = actual[i] - expected[i];
            if (below > FfmpegBelow || below < -FfmpegAbove)
            {
                Assert.Fail($"FFmpeg's {expected[i]} against our {actual[i]} at pixel ({(i / 4) % width}, {i / 4 / width}) of {width}x{height}, channel {i % 4}.");
            }
        }
    }

    [Fact]
    public void ConverterIsWithinOneLevelOfTheDefinition()
    {
        var random = new Random(253);
        foreach (var format in Formats)
        {
            for (int round = 0; round < 12; round++)
            {
                int width = random.Next(1, 40);
                int height = random.Next(1, 12);
                using var frame = RandomFrame(random, format, width, height);
                double kr = round < 5 ? RgbaConverter.Coefficients((ColorMatrix)(round + 1)).Kr : 0.05 + (random.NextDouble() * 0.4);
                double kb = round < 5 ? RgbaConverter.Coefficients((ColorMatrix)(round + 1)).Kb : 0.05 + (random.NextDouble() * 0.4);
                bool full = round % 2 == 0;
                var converter = RgbaConverter.Of(kr, kb, full, format.BitDepth());
                var row = new byte[width * 4];
                for (int y = 0; y < height; y++)
                {
                    converter.ConvertRow(frame, y, row);
                    for (int x = 0; x < width; x++)
                    {
                        var (r, g, b) = Definition(frame, x, y, kr, kb, full);
                        Assert.InRange(row[x * 4] - r, -1, 1);
                        Assert.InRange(row[(x * 4) + 1] - g, -1, 1);
                        Assert.InRange(row[(x * 4) + 2] - b, -1, 1);
                        Assert.Equal(255, row[(x * 4) + 3]);
                    }
                }
            }
        }
    }

    [Fact]
    public void ConverterKeepsTheExtremes()
    {
        // Limited range black and white are 0 and 255 exactly; full range's too; neutral grey has R = G = B.
        foreach (var format in new[] { PixelFormat.Yuv420P, PixelFormat.Yuv444P10, PixelFormat.Yuv422P14, PixelFormat.Gray12 })
        {
            int scale = 1 << (format.BitDepth() - 8);
            foreach (bool full in new[] { false, true })
            {
                foreach (var (luma, expected) in new[] { ((full ? 0 : 16) * scale, 0), ((full ? 255 : 235) * scale, 255) })
                {
                    using var frame = new VideoFrame(2, 2, format);
                    Fill(frame, 0, luma);
                    if (format.PlaneCount() == 3)
                    {
                        Fill(frame, 1, 128 * scale);
                        Fill(frame, 2, 128 * scale);
                    }

                    var row = new byte[8];
                    RgbaConverter.Of(0.2126, 0.0722, full, format.BitDepth()).ConvertRow(frame, 1, row);
                    Assert.Equal(new byte[] { (byte)expected, (byte)expected, (byte)expected, 255 }, row[..4]);
                }
            }
        }
    }

    [Fact]
    public void ConverterTakesFfmpegsDefaults()
    {
        // No matrix said, or one FFmpeg's scaler doesn't know, converts as BT.601; no range said as limited.
        // MJPEG's unspecified range as full (JPEG's).
        using var frame = new VideoFrame(2, 2, PixelFormat.Yuv420P);
        var unspecified = RgbaConverter.For(frame, CodecIds.H264);
        Assert.Equal((0.299, 0.114, false), (unspecified.Kr, unspecified.Kb, unspecified.FullRange));
        Assert.True(RgbaConverter.For(frame, CodecIds.Mjpeg).FullRange);
        Assert.False(RgbaConverter.For(frame, CodecIds.Mjpeg, range: ColorRange.Limited).FullRange);
        Assert.Equal((0.299, 0.114), RgbaConverter.Coefficients(ColorMatrix.YCgCo));
        Assert.Equal((0.2126, 0.0722), RgbaConverter.Coefficients(ColorMatrix.Bt709));
        Assert.True(RgbaConverter.For(frame, CodecIds.H264, range: ColorRange.Full).FullRange);
        Assert.Equal(0.2627, RgbaConverter.For(frame, CodecIds.H264, ColorMatrix.Bt2020Ncl).Kr);
    }

    [Fact]
    public void AreaScalerIsTheExactOverlapMean()
    {
        var random = new Random(254);
        for (int round = 0; round < 300; round++)
        {
            int width = random.Next(1, 50);
            int height = random.Next(1, 30);
            int targetWidth = round % 10 == 0 ? width : random.Next(1, width + 1);
            int targetHeight = round % 7 == 0 ? height : random.Next(1, height + 1);
            var source = new byte[width * height * 4];
            random.NextBytes(source);

            var actual = new byte[targetWidth * targetHeight * 4];
            var scaler = new AreaScaler(width, height, targetWidth, targetHeight, actual);
            for (int y = 0; y < height; y++)
            {
                scaler.AddRow(source.AsSpan(y * width * 4, width * 4));
            }

            Assert.Equal(AreaMean(source, width, height, targetWidth, targetHeight), actual);
        }
    }

    [Fact]
    public void RotatesCounterClockwise()
    {
        // A 3x2 image of pixels 0..5 (row by row), each pixel's four bytes its number.
        byte[] image = [.. Enumerable.Range(0, 6).SelectMany(p => Enumerable.Repeat((byte)p, 4))];
        Assert.Equal(Pixels(2, 5, 1, 4, 0, 3), RgbaImage.Rotate(image, 3, 2, 90));
        Assert.Equal(Pixels(5, 4, 3, 2, 1, 0), RgbaImage.Rotate(image, 3, 2, 180));
        Assert.Equal(Pixels(3, 0, 4, 1, 5, 2), RgbaImage.Rotate(image, 3, 2, 270));
        Assert.Same(image, RgbaImage.Rotate(image, 3, 2, 0));

        var random = new Random(255);
        var big = new byte[7 * 5 * 4];
        random.NextBytes(big);
        foreach (int rotation in new[] { 90, 180, 270 })
        {
            int width = 7;
            int height = 5;
            var turned = RgbaImage.Rotate(big, width, height, rotation);
            var (turnedWidth, turnedHeight) = rotation == 180 ? (width, height) : (height, width);
            for (int v = 0; v < turnedHeight; v++)
            {
                for (int u = 0; u < turnedWidth; u++)
                {
                    // The turned image's (u, v) seen from the source: counter-clockwise by 90, the source's right column
                    // is the top row.
                    var (x, y) = rotation switch
                    {
                        90 => (width - 1 - v, u),
                        180 => (width - 1 - u, height - 1 - v),
                        _ => (v, height - 1 - u),
                    };
                    Assert.Equal(big.AsSpan(((y * width) + x) * 4, 4).ToArray(), turned.AsSpan(((v * turnedWidth) + u) * 4, 4).ToArray());
                }
            }
        }
    }

    [Fact]
    public void RenderRefusesToScaleUp()
    {
        using var frame = new VideoFrame(8, 4, PixelFormat.Yuv420P);
        var converter = RgbaConverter.For(frame, CodecIds.H264);
        Assert.Throws<ArgumentOutOfRangeException>(() => RgbaImage.Render(frame, 9, 4, 0, converter));
        Assert.Throws<ArgumentOutOfRangeException>(() => RgbaImage.Render(frame, 8, 4, 90, converter));
        Assert.Throws<ArgumentOutOfRangeException>(() => RgbaImage.Render(frame, 4, 8, 45, converter));
        Assert.Equal(4 * 8 * 4, RgbaImage.Render(frame, 4, 8, 270, converter).Length);
    }

    // The frame FFmpeg turned into the reference: its first output frame, found by its MD5 among ours (the decoders are
    // bit-exact, name.framemd5).
    private static VideoFrame FfmpegsFirstFrame(string file, IDemuxer demuxer, VideoStreamInfo video)
    {
        string first = FrameMd5.Parse(File.ReadLines(TestSamples.ReferenceOf(file, ".framemd5"))).First(e => e.StreamIndex == 0).Md5;
        IVideoDecoder decoder = video.Codec == CodecIds.H264 ? new H264Decoder(video) : new MjpegDecoder(video);
        foreach (var packet in demuxer.ReadPackets().Where(p => p.StreamIndex == video.StreamIndex))
        {
            foreach (var frame in decoder.Decode(packet))
            {
                if (FrameMd5.Hash(frame) == first)
                {
                    return frame;
                }

                frame.Dispose();
            }
        }

        foreach (var frame in decoder.Flush())
        {
            if (FrameMd5.Hash(frame) == first)
            {
                return frame;
            }
        }

        throw new InvalidOperationException($"No decoded frame of {file} is FFmpeg's first.");
    }

    // ITU-T H.273's equations solved for R, G and B, in double precision, rounded half up and clamped.
    private static (int R, int G, int B) Definition(VideoFrame frame, int x, int y, double kr, double kb, bool full)
    {
        var format = frame.Format;
        double scale = 1 << (format.BitDepth() - 8);
        double luma = Sample(frame, 0, x, y) / scale;
        double l = full ? luma : (luma - 16) * 255 / 219;
        if (format.PlaneCount() == 1)
        {
            int grey = Round(l);
            return (grey, grey, grey);
        }

        var (chromaWidth, chromaHeight) = format.PlaneSize(1, frame.Width, frame.Height);
        int cx = chromaWidth < frame.Width ? x / 2 : x;
        int cy = chromaHeight < frame.Height ? y / 2 : y;
        double cb = (Sample(frame, 1, cx, cy) / scale) - 128;
        double cr = (Sample(frame, 2, cx, cy) / scale) - 128;
        if (!full)
        {
            cb *= 255.0 / 224;
            cr *= 255.0 / 224;
        }

        double r = l + (2 * (1 - kr) * cr);
        double b = l + (2 * (1 - kb) * cb);
        double g = (l - (kr * r) - (kb * b)) / (1 - kr - kb);
        return (Round(r), Round(g), Round(b));
    }

    private static int Round(double value) => (int)Math.Clamp(Math.Floor(value + 0.5), 0, 255);

    // Each output pixel the overlap-weighted sum over every source pixel, rounded half up: a source pixel spans
    // [x·tw, (x+1)·tw), an output pixel [u·w, (u+1)·w).
    private static byte[] AreaMean(byte[] source, int width, int height, int targetWidth, int targetHeight)
    {
        var target = new byte[targetWidth * targetHeight * 4];
        long area = (long)width * height;
        for (int v = 0; v < targetHeight; v++)
        {
            for (int u = 0; u < targetWidth; u++)
            {
                for (int c = 0; c < 4; c++)
                {
                    long sum = 0;
                    for (int y = 0; y < height; y++)
                    {
                        long overlapY = Overlap((long)y * targetHeight, (long)(y + 1) * targetHeight, (long)v * height, (long)(v + 1) * height);
                        for (int x = 0; x < width && overlapY > 0; x++)
                        {
                            long overlapX = Overlap((long)x * targetWidth, (long)(x + 1) * targetWidth, (long)u * width, (long)(u + 1) * width);
                            sum += overlapX * overlapY * (c == 3 ? 255 : source[(((y * width) + x) * 4) + c]);
                        }
                    }

                    target[(((v * targetWidth) + u) * 4) + c] = (byte)(((2 * sum) + area) / (2 * area));
                }
            }
        }

        return target;
    }

    private static long Overlap(long start, long end, long otherStart, long otherEnd) => Math.Max(0, Math.Min(end, otherEnd) - Math.Max(start, otherStart));

    private static byte[] Pixels(params int[] numbers) => [.. numbers.SelectMany(p => Enumerable.Repeat((byte)p, 4))];

    private static VideoFrame RandomFrame(Random random, PixelFormat format, int width, int height)
    {
        var frame = new VideoFrame(width, height, format);
        int max = (1 << format.BitDepth()) - 1;
        for (int plane = 0; plane < format.PlaneCount(); plane++)
        {
            var (_, planeHeight) = format.PlaneSize(plane, width, height);
            for (int y = 0; y < planeHeight; y++)
            {
                var row = frame.GetRow(plane, y);
                for (int x = 0; x < row.Length / format.BytesPerSample(); x++)
                {
                    Write(row, x, format, random.Next(max + 1));
                }
            }
        }

        return frame;
    }

    private static void Fill(VideoFrame frame, int plane, int value)
    {
        var (_, planeHeight) = frame.Format.PlaneSize(plane, frame.Width, frame.Height);
        for (int y = 0; y < planeHeight; y++)
        {
            var row = frame.GetRow(plane, y);
            for (int x = 0; x < row.Length / frame.Format.BytesPerSample(); x++)
            {
                Write(row, x, frame.Format, value);
            }
        }
    }

    private static void Write(Span<byte> row, int x, PixelFormat format, int value)
    {
        if (format.BytesPerSample() == 2)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(row[(x * 2)..], (ushort)value);
        }
        else
        {
            row[x] = (byte)value;
        }
    }

    private static int Sample(VideoFrame frame, int plane, int x, int y)
    {
        var row = frame.GetRow(plane, y);
        return frame.Format.BytesPerSample() == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(row[(x * 2)..]) : row[x];
    }
}
