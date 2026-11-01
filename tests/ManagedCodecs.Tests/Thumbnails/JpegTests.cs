using System.Diagnostics;
using ManagedCodecs.Mjpeg;
using ManagedCodecs.Thumbnails;

namespace ManagedCodecs.Tests.Thumbnails;

/// <summary>
/// The JPEG writer (<see cref="Jpeg"/>): its pieces against T.81's definitions (the forward DCT of A.3.3 summed by the
/// definition, the categories of Table F.1, Annex K.1's tables at quality 50), its files read back by this
/// repository's MJPEG decoder (structure, size, and the picture within a PSNR of the source) and, where ffmpeg is
/// installed (CI), by FFmpeg's JPEG decoder.
/// </summary>
public class JpegTests
{
    [Fact]
    public void ForwardDctIsTheDefinition()
    {
        var random = new Random(1);
        Span<int> block = stackalloc int[64];
        Span<long> coefficients = stackalloc long[64];
        double worst = 0;
        for (int n = 0; n < 1000; n++)
        {
            for (int i = 0; i < 64; i++)
            {
                block[i] = n == 0 ? 127 : n == 1 ? -128 : random.Next(-128, 128); // the extremes, then noise
            }

            Jpeg.ForwardDct(block, coefficients);
            for (int v = 0; v < 8; v++)
            {
                for (int u = 0; u < 8; u++)
                {
                    // A.3.3: F(v,u) = 1/4 C(u) C(v) Σ Σ s(y,x) cos((2x+1)uπ/16) cos((2y+1)vπ/16).
                    double sum = 0;
                    for (int y = 0; y < 8; y++)
                    {
                        for (int x = 0; x < 8; x++)
                        {
                            sum += block[(y * 8) + x] * Math.Cos(((2 * x) + 1) * u * Math.PI / 16) * Math.Cos(((2 * y) + 1) * v * Math.PI / 16);
                        }
                    }

                    double expected = (u == 0 ? 1 / Math.Sqrt(2) : 1) * (v == 0 ? 1 / Math.Sqrt(2) : 1) / 4 * sum;
                    worst = Math.Max(worst, Math.Abs((coefficients[(v * 8) + u] / (double)(1L << 30)) - expected));
                }
            }
        }

        Assert.True(worst < 0.05, $"off the definition by {worst}");
    }

    [Fact]
    public void CosinesRoundFarFromATie()
    {
        // The fixed-point basis is Math.Cos rounded: a value near x.5 could round either way on another runtime's
        // cosine (Unity's Mono), and the files would differ. None is near.
        for (int u = 0; u < 8; u++)
        {
            for (int x = 0; x < 8; x++)
            {
                double scaled = (u == 0 ? 1 / Math.Sqrt(2) : 1) / 2 * Math.Cos(((2 * x) + 1) * u * Math.PI / 16) * (1 << 15);
                double fraction = Math.Abs(scaled - Math.Floor(scaled) - 0.5);
                Assert.True(fraction > 1e-3, $"C({u},{x}) = {scaled}");
            }
        }
    }

    [Fact]
    public void QualityScalesTheExampleTables()
    {
        Span<byte> table = stackalloc byte[64];
        ReadOnlySpan<byte> k1 = [16, 11, 10, 16, 24, 40, 51, 61];
        Jpeg.ScaleQuantization([.. k1, .. new byte[56]], 50, table);
        Assert.Equal(k1.ToArray(), table[..8].ToArray()); // 50: Annex K.1 as it is
        Jpeg.ScaleQuantization([.. k1, .. new byte[56]], 100, table);
        Assert.All(table[..8].ToArray(), q => Assert.Equal(1, q)); // 100: all ones, and never 0
        Jpeg.ScaleQuantization([.. k1, .. new byte[56]], 1, table);
        Assert.All(table[..8].ToArray(), q => Assert.Equal(255, q)); // 1: 50 times, kept within 8 bits
        Jpeg.ScaleQuantization([.. k1, .. new byte[56]], 75, table);
        Assert.Equal(new byte[] { 8, 6, 5, 8, 12, 20, 26, 31 }, table[..8].ToArray()); // libjpeg's -quality 75
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 1, 1)]
    [InlineData(-1, 1, 0)]
    [InlineData(2, 2, 2)]
    [InlineData(-2, 2, 1)]
    [InlineData(-3, 2, 0)]
    [InlineData(3, 2, 3)]
    [InlineData(1023, 10, 1023)]
    [InlineData(-1023, 10, 0)]
    [InlineData(2047, 11, 2047)]
    public void CategoryAndAmplitudeAreTableF1s(int value, int category, int amplitude)
    {
        Assert.Equal(category, Jpeg.Category(value));
        Assert.Equal(amplitude, Jpeg.Amplitude(value, category));
    }

    [Theory]
    [InlineData(1, 1, 85)]
    [InlineData(8, 8, 85)]
    [InlineData(17, 9, 85)]
    [InlineData(33, 31, 100)]
    [InlineData(64, 48, 50)]
    [InlineData(321, 180, 85)]
    public void ReadsBackThroughTheMjpegDecoder(int width, int height, int quality)
    {
        var rgba = Picture(width, height);
        var jpeg = new MemoryStream();
        Jpeg.Write(jpeg, rgba, width, height, quality);
        byte[] bytes = jpeg.ToArray();
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, bytes[..4]); // SOI, then JFIF's APP0
        Assert.Equal(new byte[] { 0xFF, 0xD9 }, bytes[^2..]);

        using var frame = new MjpegDecoder().Decode(bytes);
        Assert.Equal((width, height, PixelFormat.Yuv420P), (frame.Width, frame.Height, frame.Format));
        Assert.False(frame.IsDamaged);

        // The decoded luma against the source's, converted by JFIF's equations.
        double error = 0;
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetRow(0, y);
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 4;
                double luma = (0.299 * rgba[i]) + (0.587 * rgba[i + 1]) + (0.114 * rgba[i + 2]);
                error += (row[x] - luma) * (row[x] - luma);
            }
        }

        double psnr = 10 * Math.Log10(255.0 * 255 / Math.Max(error / (width * height), 1e-9));

        // libjpeg (ImageMagick, -quality 85, 4:2:0) gets 32.2 dB on the 8x8 picture, its hardest: the square's edges.
        Assert.True(psnr > (quality == 100 ? 45 : 31), $"luma PSNR {psnr:F1} dB at quality {quality}");
    }

    [Fact]
    public void EveryRuntimeWritesTheseBytes()
    {
        // Integer arithmetic only: the same file on every runtime and architecture (the Unity build compiles this
        // source too). A change to the output is a change to this hash, on purpose or not.
        var rgba = Picture(40, 30);
        var jpeg = new MemoryStream();
        Jpeg.Write(jpeg, rgba, 40, 30, 85);
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(jpeg.ToArray()));
        Assert.True(hash == "0BDC7784460B8AED6403D97B70E3B887802093ACCF3513808298B83E15223AAC", hash);
    }

    [Fact]
    public void StuffsAZeroAfterEach0xFFInTheScan()
    {
        // Noise at quality 100 codes long AC runs, so 0xFF bytes come up in the scan: each must be followed by 0
        // (F.1.2.3), and the only markers after SOS are EOI.
        const int width = 64;
        const int height = 64;
        var rgba = new byte[width * height * 4];
        new Random(5).NextBytes(rgba);
        var jpeg = new MemoryStream();
        Jpeg.Write(jpeg, rgba, width, height, 100);
        byte[] bytes = jpeg.ToArray();
        int scan = bytes.AsSpan().IndexOf([(byte)0xFF, (byte)0xDA]);
        int start = scan + 2 + ((bytes[scan + 2] << 8) | bytes[scan + 3]);
        int stuffed = 0;
        for (int i = start; i < bytes.Length - 2; i++)
        {
            if (bytes[i] == 0xFF)
            {
                Assert.Equal(0, bytes[i + 1]);
                stuffed++;
                i++;
            }
        }

        Assert.True(stuffed > 0, "no 0xFF in the scan: the test needs another picture");
        using var frame = new MjpegDecoder().Decode(bytes);
        Assert.False(frame.IsDamaged);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void RefusesAQualityOutsideOneToAHundred(int quality) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Jpeg.Write(new MemoryStream(), new byte[4], 1, 1, quality));

    [Fact]
    public void ThumbnailWritesJpeg()
    {
        var thumbnail = new Thumbnail(160, 120, Picture(160, 120), TimeSpan.Zero);
        byte[] jpeg = thumbnail.ToJpeg(90);
        var saved = new MemoryStream();
        thumbnail.SaveJpeg(saved, 90);
        Assert.Equal(jpeg, saved.ToArray());
        using var frame = new MjpegDecoder().Decode(jpeg);
        Assert.Equal((160, 120), (frame.Width, frame.Height));
    }

    [Fact]
    public void FfmpegReadsWhatItWrites()
    {
        if (PngTests.FindOnPath("ffmpeg") is not { } ffmpeg)
        {
            return; // CI installs it; this check runs there
        }

        const int width = 37;
        const int height = 23;
        var rgba = Picture(width, height);
        string directory = Directory.CreateTempSubdirectory("jpeg").FullName;
        try
        {
            string path = Path.Combine(directory, "test.jpg");
            using (var file = File.Create(path))
            {
                Jpeg.Write(file, rgba, width, height, 95);
            }

            var start = new ProcessStartInfo(ffmpeg, ["-v", "error", "-i", path, "-f", "rawvideo", "-pix_fmt", "rgba", "-"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(start)!;
            var output = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(output);
            string errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, errors);
            byte[] decoded = output.ToArray();
            Assert.Equal(rgba.Length, decoded.Length);
            double error = 0;
            int samples = 0;
            for (int i = 0; i < rgba.Length; i++)
            {
                if (i % 4 != 3)
                {
                    error += (decoded[i] - rgba[i]) * (decoded[i] - rgba[i]);
                    samples++;
                }
            }

            double psnr = 10 * Math.Log10(255.0 * 255 / Math.Max(error / samples, 1e-9));

            // Chroma at half resolution limits it on this picture's coloured edges: libjpeg (ImageMagick, -quality 95,
            // 4:2:0) gets 26.2 dB here too, FFmpeg's own MJPEG encoder at -q:v 2 27.0 dB.
            Assert.True(psnr > 25.5, $"RGB PSNR {psnr:F1} dB through FFmpeg");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A smooth picture with some edges: gradients in each channel and a bright square, alpha 255.</summary>
    private static byte[] Picture(int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 4;
                bool square = x > width / 4 && x < width / 2 && y > height / 4 && y < height / 2;
                rgba[i] = (byte)(square ? 240 : x * 255 / Math.Max(width - 1, 1));
                rgba[i + 1] = (byte)(square ? 220 : y * 255 / Math.Max(height - 1, 1));
                rgba[i + 2] = (byte)(square ? 40 : 128 + (((x + y) % 32) * 2));
                rgba[i + 3] = 255;
            }
        }

        return rgba;
    }
}
