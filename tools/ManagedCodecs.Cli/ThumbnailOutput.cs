#if THUMBNAILS
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using ManagedCodecs.Thumbnails;

namespace ManagedCodecs.Cli;

/// <summary>
/// The Thumbnails package's results for a file as one line: <see cref="VideoProbe"/>'s facts and a small
/// <see cref="VideoThumbnail"/>'s size, time and pixels (SHA-256 of the RGBA, not of the PNG, whose deflated bytes
/// are the runtime's zlib's). `mcdec --thumbnail-hash` and the WebAssembly check (tools/ManagedCodecs.Wasm) both print
/// it, so browser-wasm's thumbnails are compared with the native ones, which the tests compare with FFmpeg, and so does
/// tools/unity-check.sh for the Unity build on Mono (the THUMBNAILS symbol, set by the projects that have the package).
/// </summary>
public static class ThumbnailOutput
{
    /// <summary>The options the hash is made with: small, past dark frames, the default time.</summary>
    public static ThumbnailOptions Options { get; } = new() { MaxWidth = 160, MaxHeight = 160, SkipDark = true };

    /// <summary>
    /// "name: probe D WxH codec fps rotation, thumbnail WxH at T hash", or "name: Exception" for a file the package
    /// refuses (no video, another codec), the same on every runtime.
    /// </summary>
    public static string Line(string name, byte[] file)
    {
        // No exception filter (catch ... when): compiled ahead of time to WebAssembly, `name` came out empty after one
        // had caught the exception, inside the catch and after it (tools/wasm-check.sh aot, " : InvalidDataException"
        // for every refused file); a plain catch that rethrows the others keeps it (tools/ManagedCodecs.Wasm/CLAUDE.md).
        string result;
        try
        {
            var info = VideoProbe.Read(new MemoryStream(file));
            var thumbnail = VideoThumbnail.Create(new MemoryStream(file), Options);
            result = FormattableString.Invariant(
                $"probe {info.Duration.Ticks} {info.DisplayWidth}x{info.DisplayHeight} {info.Codec} {info.FramesPerSecond:R} {info.Rotation}, thumbnail {thumbnail.Width}x{thumbnail.Height} at {thumbnail.Time.Ticks} {Hash(thumbnail)}");
        }
        catch (Exception e)
        {
            if (e is not (InvalidDataException or NotSupportedException))
            {
                throw;
            }

            result = e.GetType().Name;
        }

        return name + ": " + result;
    }

    /// <summary>
    /// Writes the thumbnail's raw RGBA to <paramref name="output"/> (default options, 480 x 480 at most) and returns
    /// "thumbnail WxH at T, probe D WxH rotation R fps F audio RATE CHANNELS" (ticks for the times): what
    /// tools/realworld.py compares with ffprobe and FFmpeg's scaler at that size.
    /// </summary>
    public static string Write(string path, string output)
    {
        var info = VideoProbe.Read(path);
        var thumbnail = VideoThumbnail.Create(path, new ThumbnailOptions { MaxWidth = 480, MaxHeight = 480 });
        File.WriteAllBytes(output, thumbnail.Pixels);
        return FormattableString.Invariant(
            $"thumbnail {thumbnail.Width}x{thumbnail.Height} at {thumbnail.Time.Ticks}, probe {info.Duration.Ticks} {info.Width}x{info.Height} rotation {info.Rotation} fps {info.FramesPerSecond:R} audio {info.Audio?.SampleRate ?? 0} {info.Audio?.Channels ?? 0}");
    }

    /// <summary>SHA-256 of the width, height and pixels.</summary>
    public static string Hash(Thumbnail thumbnail)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> size = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(size, thumbnail.Width);
        BinaryPrimitives.WriteInt32LittleEndian(size[4..], thumbnail.Height);
        sha.AppendData(size);
        sha.AppendData(thumbnail.Pixels);
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}
#endif
