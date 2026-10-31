using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using ManagedCodecs.Thumbnails;

namespace ManagedCodecs.Tests.Thumbnails;

/// <summary>
/// The PNG writer (<see cref="Png"/>): its CRC against the spec's known value, its files read back by a reader written
/// here from the spec (chunks, CRCs, inflate, the five filters undone), and, where ffmpeg is installed (CI), by
/// FFmpeg's PNG decoder.
/// </summary>
public class PngTests
{
    [Fact]
    public void CrcIsTheSpecs()
    {
        // Every PNG file ends in the same IEND chunk, whose CRC is AE 42 60 82; and the check value of CRC-32 ("123456789").
        Assert.Equal(0xAE426082u, Png.Crc("IEND"u8) ^ 0xFFFFFFFF);
        Assert.Equal(0xCBF43926u, Png.Crc("123456789"u8) ^ 0xFFFFFFFF);
    }

    [Fact]
    public void Adler32IsZlibs()
    {
        // RFC 1950's checksum, which the Unity build (netstandard2.1, no ZLibStream) writes after its deflate data:
        // Wikipedia's worked example, the empty input, and a long run that wraps both sums past the modulus (zlib's
        // adler32 of 100000 bytes of 0xFF).
        Assert.Equal(0x11E60398u, Png.Adler32("Wikipedia"u8));
        Assert.Equal(1u, Png.Adler32([]));
        var data = new byte[100_000];
        Array.Fill(data, (byte)0xFF);
        Assert.Equal(0x149A302Cu, Png.Adler32(data));
    }

    [Fact]
    public void PaethTiesGoToLeftThenUp()
    {
        Assert.Equal(10, Png.Paeth(10, 10, 10));
        Assert.Equal(20, Png.Paeth(10, 20, 0)); // estimate 30: up nearest
        Assert.Equal(10, Png.Paeth(10, 20, 20)); // estimate 10: left
        Assert.Equal(20, Png.Paeth(10, 20, 5)); // estimate 25: up 5 away, left 15, upLeft 20
        Assert.Equal(15, Png.Paeth(20, 10, 15)); // estimate 15: upLeft itself
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(17, 9)]
    [InlineData(64, 48)]
    public void ReadsBackAsWritten(int width, int height)
    {
        var random = new Random((width * 31) + height);
        foreach (bool smooth in new[] { false, true })
        {
            // Noise (filter 0 wins) and gradients (the predicting filters win), so every filter is taken somewhere.
            var rgba = new byte[width * height * 4];
            for (int i = 0; i < rgba.Length; i++)
            {
                int x = (i / 4) % width;
                int y = i / 4 / width;
                rgba[i] = smooth ? (byte)((x * 7) + (y * 3) + ((i % 4) * 50)) : (byte)random.Next(256);
            }

            var png = new MemoryStream();
            Png.Write(png, rgba, width, height);
            var (readWidth, readHeight, pixels) = Read(png.ToArray());
            Assert.Equal((width, height), (readWidth, readHeight));
            Assert.Equal(rgba, pixels);
        }
    }

    [Fact]
    public void TakesEveryFilter()
    {
        // A row of noise under a gradient, a vertical ramp, ...: the heuristic picks more than one type over an image.
        const int width = 32;
        const int height = 32;
        var rgba = new byte[width * height * 4];
        var random = new Random(7);
        for (int i = 0; i < rgba.Length; i++)
        {
            int x = (i / 4) % width;
            int y = i / 4 / width;
            rgba[i] = (y / 8) switch
            {
                0 => (byte)random.Next(256),
                1 => (byte)(x * 8),
                2 => (byte)(y * 8),
                _ => (byte)((x + y) * 4),
            };
        }

        var types = Png.Filter(rgba, width, height).Where((_, i) => i % ((width * 4) + 1) == 0).Distinct().ToArray();
        Assert.True(types.Length >= 3, $"Filter types used: {string.Join(", ", types)}.");
    }

    [Fact]
    public void FfmpegReadsThePixels()
    {
        if (FindOnPath("ffmpeg") is not { } ffmpeg)
        {
            return; // CI installs it; this check runs there
        }

        const int width = 37;
        const int height = 23;
        var rgba = new byte[width * height * 4];
        new Random(253).NextBytes(rgba);
        for (int i = 3; i < rgba.Length; i += 4)
        {
            rgba[i] = 255; // FFmpeg keeps alpha, but as a thumbnail has
        }

        string directory = Directory.CreateTempSubdirectory("png").FullName;
        try
        {
            string path = Path.Combine(directory, "test.png");
            using (var file = File.Create(path))
            {
                Png.Write(file, rgba, width, height);
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
            Assert.Equal(rgba, output.ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // A PNG reader for this test only, from the spec: 8-bit RGBA, not interlaced, the IDAT chunks inflated together.
    private static (int Width, int Height, byte[] Pixels) Read(byte[] png)
    {
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        int width = 0;
        int height = 0;
        var idat = new MemoryStream();
        int at = 8;
        string last = "";
        while (at < png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            string type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            var data = png.AsSpan(at + 8, length);
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + length));
            Assert.Equal(BitwiseCrc(png.AsSpan(at + 4, length + 4)), crc);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                Assert.Equal(new byte[] { 8, 6, 0, 0, 0 }, data[8..13].ToArray());
            }
            else if (type == "IDAT")
            {
                idat.Write(data);
            }

            last = type;
            at += 12 + length;
        }

        Assert.Equal("IEND", last);
        idat.Position = 0;
        var raw = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress))
        {
            zlib.CopyTo(raw);
        }

        int stride = width * 4;
        var filtered = raw.ToArray();
        Assert.Equal((stride + 1) * height, filtered.Length);
        var pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            byte type = filtered[y * (stride + 1)];
            for (int i = 0; i < stride; i++)
            {
                int a = i >= 4 ? pixels[(y * stride) + i - 4] : 0;
                int b = y > 0 ? pixels[((y - 1) * stride) + i] : 0;
                int c = i >= 4 && y > 0 ? pixels[((y - 1) * stride) + i - 4] : 0;
                int predicted = type switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => PaethByTheSpec(a, b, c),
                    _ => throw new InvalidDataException($"Filter type {type}."),
                };
                pixels[(y * stride) + i] = (byte)(filtered[(y * (stride + 1)) + 1 + i] + predicted);
            }
        }

        return (width, height, pixels);
    }

    // CRC-32 a bit at a time (ISO 3309's reflected polynomial), not by the writer's table.
    private static uint BitwiseCrc(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc >> 1) ^ ((crc & 1) * 0xEDB88320);
            }
        }

        return crc ^ 0xFFFFFFFF;
    }

    // The spec's PaethPredictor, as its section 9.4 writes it.
    private static int PaethByTheSpec(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
        {
            return a;
        }

        return pb <= pc ? b : c;
    }

    private static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
}
