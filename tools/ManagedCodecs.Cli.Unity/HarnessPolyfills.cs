// What mcdec itself uses of .NET's newer libraries and netstandard2.1 lacks (the decoders' own are in
// src/ManagedCodecs.Unity/Polyfills.cs, linked in too).
using System.Security.Cryptography;

namespace ManagedCodecs.Cli;

internal static class HarnessPolyfills
{
    extension(Convert)
    {
        public static string ToHexStringLower(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

        public static string ToHexStringLower(ReadOnlySpan<byte> bytes) => ToHexStringLower(bytes.ToArray());
    }

    extension(TimeSpan time)
    {
        public double TotalMicroseconds => time.Ticks / 10.0;
    }
}

internal static class Md5Polyfill
{
    extension(MD5)
    {
        public static byte[] HashData(ReadOnlySpan<byte> data)
        {
            using var md5 = MD5.Create();
            return md5.ComputeHash(data.ToArray());
        }
    }
}

// Static extension members of one signature need classes of their own.
internal static class Sha256Polyfill
{
    extension(SHA256)
    {
        public static byte[] HashData(ReadOnlySpan<byte> data)
        {
            using var sha = SHA256.Create();
            return sha.ComputeHash(data.ToArray());
        }
    }
}
