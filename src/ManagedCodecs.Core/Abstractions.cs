namespace ManagedCodecs;

/// <summary>One compressed unit of data pulled out of a container, e.g. one MJPEG frame.</summary>
public readonly record struct Packet(int StreamIndex, long Sequence, ReadOnlyMemory<byte> Data, bool IsKeyframe);

/// <summary>Basic facts about a container's video stream.</summary>
/// <param name="Codec">FourCC of the codec, e.g. "MJPG".</param>
public sealed record VideoStreamInfo(string Codec, int Width, int Height, double FramesPerSecond, long FrameCount);

/// <summary>Reads packets out of a container format (AVI, MP4, ...).</summary>
public interface IDemuxer : IDisposable
{
    VideoStreamInfo Video { get; }

    /// <summary>Packets of the video stream, in file order.</summary>
    IEnumerable<Packet> ReadPackets();
}

/// <summary>Turns one compressed packet into one decoded frame.</summary>
public interface IVideoDecoder
{
    VideoFrame Decode(ReadOnlySpan<byte> packet);
}
