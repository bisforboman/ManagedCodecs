using System.Globalization;
using System.Security.Cryptography;

namespace ManagedCodecs.Tests;

/// <summary>One line of FFmpeg's framemd5 output.</summary>
public sealed record FrameMd5Entry(int StreamIndex, long Pts, int Size, string Md5);

/// <summary>
/// Reads reference hashes produced by <c>ffmpeg -i input -f framemd5 out.framemd5</c> and hashes
/// our own frames the same way, so decoder output can be checked to be bit-exact.
/// </summary>
public static class FrameMd5
{
    public static List<FrameMd5Entry> Parse(IEnumerable<string> lines)
    {
        var entries = new List<FrameMd5Entry>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            // stream_index, dts, pts, duration, size, hash
            var fields = line.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length < 6)
            {
                throw new FormatException($"Unexpected framemd5 line: {line}");
            }

            entries.Add(new FrameMd5Entry(
                int.Parse(fields[0], CultureInfo.InvariantCulture),
                long.Parse(fields[2], CultureInfo.InvariantCulture),
                int.Parse(fields[4], CultureInfo.InvariantCulture),
                fields[^1].ToLowerInvariant()));
        }

        return entries;
    }

    public static string Hash(VideoFrame frame)
    {
        using var buffer = new MemoryStream();
        frame.WritePacked(buffer);
        return Convert.ToHexStringLower(MD5.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));
    }
}
