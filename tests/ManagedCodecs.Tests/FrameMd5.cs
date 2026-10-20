using System.Globalization;
using System.Security.Cryptography;

namespace ManagedCodecs.Tests;

/// <summary>One line of FFmpeg's framemd5 output.</summary>
public sealed record FrameMd5Entry(int StreamIndex, long Dts, long Pts, long Duration, int Size, string Md5);

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
                long.Parse(fields[1], CultureInfo.InvariantCulture),
                long.Parse(fields[2], CultureInfo.InvariantCulture),
                long.Parse(fields[3], CultureInfo.InvariantCulture),
                int.Parse(fields[4], CultureInfo.InvariantCulture),
                fields[^1].ToLowerInvariant()));
        }

        return entries;
    }

    /// <summary>
    /// The size and MD5 of a stream's codec private data, from a <c>#extradata 0, size, md5</c>
    /// header line, or null when the stream has none.
    /// </summary>
    public static (int Size, string Md5)? ParseExtradata(IEnumerable<string> lines, int streamIndex = 0)
    {
        string prefix = $"#extradata {streamIndex.ToString(CultureInfo.InvariantCulture)},";
        foreach (var line in lines)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                var fields = line[prefix.Length..].Split(',', StringSplitOptions.TrimEntries);
                return (int.Parse(fields[0], CultureInfo.InvariantCulture), fields[1].ToLowerInvariant());
            }
        }

        return null;
    }

    /// <summary>The time base of a stream's timestamps, from a <c>#tb 0: 1/25</c> header line.</summary>
    public static Rational ParseTimeBase(IEnumerable<string> lines, int streamIndex = 0)
    {
        string prefix = $"#tb {streamIndex.ToString(CultureInfo.InvariantCulture)}:";
        var line = lines.First(l => l.StartsWith(prefix, StringComparison.Ordinal));
        var parts = line[prefix.Length..].Trim().Split('/');
        return new Rational(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    /// <summary>Whether two timestamps in different time bases are the same instant, exactly.</summary>
    public static bool SameTime(long a, Rational timeBaseA, long b, Rational timeBaseB) =>
        (Int128)a * timeBaseA.Numerator * timeBaseB.Denominator == (Int128)b * timeBaseB.Numerator * timeBaseA.Denominator;

    public static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(MD5.HashData(data));

    public static string Hash(VideoFrame frame)
    {
        using var buffer = new MemoryStream();
        frame.WritePacked(buffer);
        return Convert.ToHexStringLower(MD5.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));
    }
}
