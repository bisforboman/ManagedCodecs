using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using ManagedCodecs.Containers;

namespace ManagedCodecs.Cli;

/// <summary>
/// Lists a file's streams and packets as its demuxer gives them, nothing decoded, for files whose codecs this library
/// only names (tools/realworld.py compares the list with <c>ffprobe -show_packets -show_data_hash MD5</c>): a
/// "stream" line for the video and for the audio (index, kind, codec, then the size or the rate and channels, then
/// the time base), and a line a packet in file order: stream index, pts, dts, duration, size, K for a keyframe or _,
/// and the data's MD5.
/// </summary>
[SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "MD5 as ffprobe's -show_data_hash MD5 prints it, to compare packets with its: a checksum, not security.")]
public static class PacketDump
{
    public static void Write(string path, TextWriter output)
    {
        using var demuxer = Demuxer.Open(path);
        if (demuxer.Video is { } video)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"stream {video.StreamIndex} video {video.Codec} {video.Width}x{video.Height} {video.TimeBase.Numerator}/{video.TimeBase.Denominator}"));
        }

        if (demuxer.Audio is { } audio)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"stream {audio.StreamIndex} audio {audio.Codec} {audio.SampleRate} {audio.Channels} {audio.TimeBase.Numerator}/{audio.TimeBase.Denominator}"));
        }

        foreach (var packet in demuxer.ReadPackets())
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{packet.StreamIndex},{packet.Pts},{packet.Dts},{packet.Duration},{packet.Data.Length},{(packet.IsKeyframe ? 'K' : '_')},{Convert.ToHexStringLower(MD5.HashData(packet.Data.Span))}"));
        }
    }
}
