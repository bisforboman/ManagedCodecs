namespace ManagedCodecs;

/// <summary>A rational number, used for time bases (e.g. 1/25 or 1/12800 seconds per tick).</summary>
public readonly record struct Rational(int Numerator, int Denominator)
{
    public double ToDouble() => Denominator == 0 ? 0 : (double)Numerator / Denominator;

    /// <summary>Divides out the greatest common divisor, e.g. 40000/1000000 becomes 1/25.</summary>
    public static Rational Reduce(long numerator, long denominator)
    {
        long a = Math.Abs(numerator);
        long b = Math.Abs(denominator);
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a == 0 ? new Rational(0, 1) : new Rational(checked((int)(numerator / a)), checked((int)(denominator / a)));
    }

    public override string ToString() => $"{Numerator}/{Denominator}";
}

/// <summary>Normalized codec identifiers used in <see cref="VideoStreamInfo.Codec"/>.</summary>
public static class CodecIds
{
    public const string Mjpeg = "MJPG";
    public const string H264 = "H264";

    /// <summary>MPEG-4 Part 2 Visual.</summary>
    public const string Mpeg4Visual = "MP4V";

    /// <summary>MPEG-4 / MPEG-2 AAC.</summary>
    public const string Aac = "AAC";
}

/// <summary>One compressed unit of data pulled out of a container, e.g. one MJPEG frame.</summary>
/// <param name="Sequence">Position of the packet in decode order, starting at 0.</param>
/// <param name="Dts">Decode timestamp in the stream's <see cref="VideoStreamInfo.TimeBase"/>.</param>
/// <param name="Pts">Presentation timestamp in the stream's time base; equals <paramref name="Dts"/> without frame reordering.</param>
/// <param name="Duration">Duration in the stream's time base.</param>
public readonly record struct Packet(
    int StreamIndex,
    long Sequence,
    ReadOnlyMemory<byte> Data,
    bool IsKeyframe,
    long Dts,
    long Pts,
    long Duration);

/// <summary>Basic facts about a container's video stream.</summary>
/// <param name="Codec">
/// One of <see cref="CodecIds"/> when the codec is recognized, otherwise the container's own
/// tag (an AVI FourCC or an MP4 sample entry type).
/// </param>
/// <param name="TimeBase">Seconds per tick of the packet timestamps.</param>
/// <param name="CodecPrivate">
/// Codec configuration from the container, e.g. the avcC payload for H.264; empty if none.
/// </param>
/// <param name="StreamIndex">The <see cref="Packet.StreamIndex"/> of this stream's packets.</param>
public sealed record VideoStreamInfo(
    string Codec,
    int Width,
    int Height,
    double FramesPerSecond,
    long FrameCount,
    Rational TimeBase,
    ReadOnlyMemory<byte> CodecPrivate,
    int StreamIndex);

/// <summary>Basic facts about a container's audio stream.</summary>
/// <param name="Codec">One of <see cref="CodecIds"/> when recognized, otherwise the container's own tag.</param>
/// <param name="SampleRate">Samples per second, from the codec configuration where there is one.</param>
/// <param name="FrameCount">Number of packets, each one coded audio frame (1024 samples per channel for AAC LC).</param>
/// <param name="CodecPrivate">Codec configuration, e.g. the AudioSpecificConfig for AAC; empty if none.</param>
/// <param name="StreamIndex">The <see cref="Packet.StreamIndex"/> of this stream's packets.</param>
public sealed record AudioStreamInfo(
    string Codec,
    int SampleRate,
    int Channels,
    long FrameCount,
    Rational TimeBase,
    ReadOnlyMemory<byte> CodecPrivate,
    int StreamIndex);

/// <summary>Reads packets out of a container format (AVI, MP4, ...).</summary>
public interface IDemuxer : IDisposable
{
    /// <summary>The first video stream, or null if there is none.</summary>
    VideoStreamInfo? Video { get; }

    /// <summary>The first audio stream, or null if there is none (or the container does not expose audio).</summary>
    AudioStreamInfo? Audio { get; }

    /// <summary>
    /// Packets of the video and audio streams, interleaved in file order; each stream's packets
    /// are in decode order. <see cref="Packet.StreamIndex"/> tells them apart.
    /// </summary>
    IEnumerable<Packet> ReadPackets();
}

/// <summary>Turns one compressed packet into one decoded frame.</summary>
public interface IVideoDecoder
{
    VideoFrame Decode(ReadOnlySpan<byte> packet);
}

/// <summary>Turns one compressed packet into one block of audio samples.</summary>
public interface IAudioDecoder
{
    AudioFrame Decode(ReadOnlySpan<byte> packet);
}
