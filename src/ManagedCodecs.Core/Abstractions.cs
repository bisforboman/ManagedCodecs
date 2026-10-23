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
/// <param name="SkipSamples">
/// Audio: how many decoded samples (per channel) at the start of this packet come before the
/// presentation starts, such as encoder priming that an edit list cuts off; the decoder drops them.
/// </param>
/// <param name="Discard">
/// To be decoded but not shown: a packet an edit list leaves out that later ones need, such as the frames from the
/// keyframe before an edit's start, or audio before it (whose decoding primes the next frames). The decoders decode it
/// and return nothing for it (no frame, or an empty <see cref="AudioFrame"/>). Its timestamps follow the edit's.
/// </param>
public readonly record struct Packet(
    int StreamIndex,
    long Sequence,
    ReadOnlyMemory<byte> Data,
    bool IsKeyframe,
    long Dts,
    long Pts,
    long Duration,
    int SkipSamples = 0,
    bool Discard = false);

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

    /// <summary>
    /// <see cref="ReadPackets"/> as an asynchronous sequence, for <c>await foreach</c>. Reading a packet is synchronous (the
    /// demuxers read from a seekable stream); between packets the thread is yielded and cancellation checked.
    /// </summary>
    IAsyncEnumerable<Packet> ReadPacketsAsync(CancellationToken cancellationToken = default) => AsyncPackets.Of(ReadPackets(), cancellationToken);
}

/// <summary>
/// Turns compressed packets, given in decode order, into frames in display order. A codec with
/// frame reordering (H.264 with B frames) holds frames back until every frame that is shown
/// before them has been decoded, so one packet can give no frame or several.
/// </summary>
public interface IVideoDecoder
{
    /// <summary>
    /// Decodes one packet and returns the frames that are now ready to be shown, in display
    /// order. Each frame's <see cref="VideoFrame.Pts"/> is the <see cref="Packet.Pts"/> of the
    /// packet it was coded in.
    /// </summary>
    IReadOnlyList<VideoFrame> Decode(Packet packet);

    /// <summary>At the end of the stream: returns the frames still held back, in display order.</summary>
    IReadOnlyList<VideoFrame> Flush();

    /// <summary>
    /// <see cref="Decode"/> for asynchronous callers: a decoder that would block the calling thread (H.264 with
    /// several threads, waiting for a picture to finish) awaits instead. Cancelling stops the waiting, not the decoding of
    /// a picture already started. Exceptions are the ones <see cref="Decode"/> throws.
    /// </summary>
    ValueTask<IReadOnlyList<VideoFrame>> DecodeAsync(Packet packet, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(Decode(packet));
    }

    /// <summary><see cref="Flush"/> for asynchronous callers, awaiting where it would block.</summary>
    ValueTask<IReadOnlyList<VideoFrame>> FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(Flush());
    }
}

/// <summary>Turns one compressed packet into one block of audio samples.</summary>
public interface IAudioDecoder
{
    /// <summary>Decodes one packet's data into every sample it codes.</summary>
    AudioFrame Decode(ReadOnlySpan<byte> packet);

    /// <summary>
    /// Decodes one packet and drops its first <see cref="Packet.SkipSamples"/> samples, so the
    /// output starts where the container says presentation starts, or all of them for a
    /// <see cref="Packet.Discard"/> packet. A packet can come out empty.
    /// </summary>
    AudioFrame Decode(Packet packet);
}
