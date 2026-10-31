using System.Buffers.Binary;
using System.IO.Compression;

namespace ManagedCodecs.Thumbnails;

/// <summary>
/// Writes 8-bit RGBA as a PNG file (ISO/IEC 15948, W3C PNG): the signature, IHDR (colour type 6, not interlaced), one
/// IDAT of the filtered rows deflated by the BCL's <see cref="ZLibStream"/>, and IEND. Each row takes the filter whose
/// output has the least sum of absolute values (the spec's recommended heuristic, section 12.8), Paeth among them.
/// </summary>
internal static class Png
{
    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly uint[] CrcTable = MakeCrcTable();

    /// <summary>Writes <paramref name="rgba"/>, <paramref name="width"/> x <paramref name="height"/> pixels, to <paramref name="destination"/>.</summary>
    public static void Write(Stream destination, ReadOnlySpan<byte> rgba, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfNotEqual(rgba.Length, width * height * 4, nameof(rgba));

        destination.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8; // bit depth
        header[9] = 6; // colour type: RGB with alpha
        header[10] = 0; // deflate
        header[11] = 0; // adaptive filtering, five filter types
        header[12] = 0; // not interlaced
        WriteChunk(destination, "IHDR"u8, header);

        WriteChunk(destination, "IDAT"u8, Compress(Filter(rgba, width, height)));
        WriteChunk(destination, "IEND"u8, []);
    }

    /// <summary>The CRC-32 of the bytes (ISO 3309, as PNG's chunks take it: the chunk type and data), from <paramref name="crc"/> on.</summary>
    internal static uint Crc(ReadOnlySpan<byte> data, uint crc = 0xFFFFFFFF)
    {
        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    /// <summary>The rows, each behind its filter type byte, filtered by the type that leaves the least sum of magnitudes.</summary>
    internal static byte[] Filter(ReadOnlySpan<byte> rgba, int width, int height)
    {
        int stride = width * 4;
        var filtered = new byte[(stride + 1) * height];
        var candidate = new byte[stride];
        var best = new byte[stride];
        var zeros = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            var row = rgba.Slice(y * stride, stride);
            var above = y > 0 ? rgba.Slice((y - 1) * stride, stride) : zeros;
            long bestSum = long.MaxValue;
            byte bestType = 0;
            for (byte type = 0; type <= 4; type++)
            {
                long sum = 0;
                for (int i = 0; i < stride; i++)
                {
                    int left = i >= 4 ? row[i - 4] : 0;
                    int up = above[i];
                    int upLeft = i >= 4 ? above[i - 4] : 0;
                    int predicted = type switch
                    {
                        0 => 0,
                        1 => left,
                        2 => up,
                        3 => (left + up) >> 1,
                        _ => Paeth(left, up, upLeft),
                    };
                    byte value = (byte)(row[i] - predicted);
                    candidate[i] = value;
                    sum += Math.Abs((int)(sbyte)value); // the filtered bytes as signed, as the heuristic counts them
                }

                if (sum < bestSum)
                {
                    (bestSum, bestType) = (sum, type);
                    candidate.CopyTo(best, 0);
                }
            }

            filtered[y * (stride + 1)] = bestType;
            best.CopyTo(filtered, (y * (stride + 1)) + 1);
        }

        return filtered;
    }

    /// <summary>The Paeth predictor (PNG section 9.4): the neighbour nearest to left + up - upLeft, ties to left, then up.</summary>
    internal static int Paeth(int left, int up, int upLeft)
    {
        int estimate = left + up - upLeft;
        int toLeft = Math.Abs(estimate - left);
        int toUp = Math.Abs(estimate - up);
        int toUpLeft = Math.Abs(estimate - upLeft);
        return toLeft <= toUp && toLeft <= toUpLeft ? left : toUp <= toUpLeft ? up : upLeft;
    }

    private static byte[] Compress(byte[] data)
    {
        using var compressed = new MemoryStream();
#if NET
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }
#else
        // The Unity build (netstandard2.1, no ZLibStream): the zlib format around raw deflate itself (RFC 1950), a
        // header (deflate, 32 KB window, default level, its check bits) and the data's Adler-32 after.
        compressed.WriteByte(0x78);
        compressed.WriteByte(0x9C);
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data, 0, data.Length);
        }

        Span<byte> adler = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(adler, Adler32(data));
        compressed.Write(adler);
#endif

        return compressed.ToArray();
    }

    /// <summary>The Adler-32 checksum of the bytes (RFC 1950, section 8), as the zlib format ends with it.</summary>
    internal static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint Modulus = 65521;
        uint a = 1;
        uint b = 0;
        foreach (byte value in data)
        {
            a = (a + value) % Modulus;
            b = (b + a) % Modulus;
        }

        return (b << 16) | a;
    }

    private static void WriteChunk(Stream destination, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> field = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(field, data.Length);
        destination.Write(field);
        destination.Write(type);
        destination.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(field, Crc(data, Crc(type)) ^ 0xFFFFFFFF);
        destination.Write(field);
    }

    // The PNG spec's table (its annex D): the reflected polynomial 0xEDB88320, one entry for each byte value.
    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
