namespace ManagedCodecs;

/// <summary>A rational number, used for time bases (e.g. 1/25 or 1/12800 seconds per tick).</summary>
public readonly record struct Rational(int Numerator, int Denominator)
{
    /// <summary>The value as a double; 0 when <see cref="Denominator"/> is 0.</summary>
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

    /// <summary>The fraction as written, e.g. "1/25".</summary>
    public override string ToString() => $"{Numerator}/{Denominator}";
}

/// <summary>Normalized codec identifiers used in <see cref="VideoStreamInfo.Codec"/>.</summary>
public static class CodecIds
{
    /// <summary>Motion JPEG: each packet one JPEG image (or, interlaced, two fields).</summary>
    public const string Mjpeg = "MJPG";

    /// <summary>H.264 / AVC, packets of length-prefixed NAL units as MP4 stores them.</summary>
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
/// presentation starts, such as encoder priming that an edit list cuts off; the decoder drops them. More than the packet
/// decodes to (a delay given for the whole stream) runs on into the next packets.
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

/// <summary>
/// A <see cref="Packet"/> without its data: what a demuxer knows of it from the container's index alone, for a caller that
/// needs only the timing and sizes (a duration, a packet list, a bitrate graph).
/// </summary>
/// <param name="Size">The packet's <see cref="Packet.Data"/> length in bytes.</param>
public readonly record struct PacketInfo(
    int StreamIndex,
    long Sequence,
    int Size,
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
    int StreamIndex)
{
    /// <summary>
    /// The samples' range as the container says it (MP4's 'colr' of type 'nclx'), <see cref="ColorRange.Unspecified"/>
    /// where it says nothing. A decoder given this stream takes it for frames whose bitstream does not say (as FFmpeg's).
    /// </summary>
    public ColorRange ColorRange { get; init; }

    /// <summary>The colour primaries as the container says them (MP4's 'colr', 'nclx' or QuickTime's 'nclc').</summary>
    public ColorPrimaries ColorPrimaries { get; init; } = ColorPrimaries.Unspecified;

    /// <summary>The transfer characteristics as the container says them (MP4's 'colr').</summary>
    public ColorTransfer ColorTransfer { get; init; } = ColorTransfer.Unspecified;

    /// <summary>The YCbCr to RGB matrix as the container says it (MP4's 'colr').</summary>
    public ColorMatrix ColorMatrix { get; init; } = ColorMatrix.Unspecified;

    /// <summary>
    /// The sample aspect ratio as the container says it (MP4's 'pasp', AVI's 'vprp'), in lowest terms; 0:1 where it says nothing. A
    /// decoder given this stream gives its frames this ratio where there is one, as ffprobe shows frames (FFmpeg's
    /// <c>av_guess_sample_aspect_ratio</c> prefers the container's).
    /// </summary>
    public Rational SampleAspectRatio { get; init; } = new(0, 1);

    /// <summary>
    /// How far to turn the frames counter-clockwise to show them, in degrees: 0, 90, 180 or 270, from MP4's track and
    /// movie matrices ('tkhd', 'mvhd'), as ffprobe's display matrix <c>rotation</c> (-90 there is 270 here). 0 for a
    /// matrix that turns by another angle or mirrors. The decoders do not turn frames.
    /// </summary>
    public int Rotation { get; init; }

    /// <summary>
    /// The stream's length in <c>TimeBase</c> ticks as the container gives it, as FFmpeg's stream duration: MP4's edits'
    /// summed length (a leading empty edit only moves the start), else the track's 'mdhd' duration, else its samples'
    /// summed durations; AVI's 'strh' length in frames. 0 where the container says nothing.
    /// </summary>
    public long Duration { get; init; }
}

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
    int StreamIndex)
{
    /// <summary>
    /// The stream's length in <c>TimeBase</c> ticks as the container gives it, as FFmpeg's stream duration: MP4's edits'
    /// summed length (a leading empty edit only moves the start), else the track's 'mdhd' duration, else its samples'
    /// summed durations; AVI's 'strh' length in frames. 0 where the container says nothing.
    /// </summary>
    public long Duration { get; init; }
}

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
    /// <see cref="ReadPackets"/> as an asynchronous sequence, for <c>await foreach</c>. This library's demuxers await every
    /// read of the file; this default, for a demuxer that has only <see cref="ReadPackets"/>, reads each packet
    /// synchronously and yields the thread and checks cancellation between them.
    /// </summary>
    IAsyncEnumerable<Packet> ReadPacketsAsync(CancellationToken cancellationToken = default) => AsyncPackets.Of(ReadPackets(), cancellationToken);

    /// <summary>
    /// Moves where reading starts: the next <see cref="ReadPackets"/> (or another read started after this call) begins
    /// at the keyframe at or before <paramref name="pts"/> of the stream <paramref name="streamIndex"/>, and the file's
    /// other stream at the same instant. A time before the first keyframe starts at the beginning, one past the end at
    /// the last keyframe. The packets are those a read from the start gives from there, each stream's in its order and
    /// with the same values (<see cref="Packet.Sequence"/> too), so a decoder for them starts afresh (a new one, or one
    /// flushed) and the frames shown before <paramref name="pts"/> are dropped by whoever wants that time, as FFmpeg's
    /// <c>-ss</c> drops them. This default, for a demuxer that cannot seek, throws.
    /// </summary>
    /// <param name="pts">A presentation time in the stream's <c>TimeBase</c>.</param>
    /// <param name="streamIndex">The stream the time belongs to: <see cref="VideoStreamInfo.StreamIndex"/> or <see cref="AudioStreamInfo.StreamIndex"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">No stream of the demuxer has <paramref name="streamIndex"/>.</exception>
    /// <exception cref="NotSupportedException">The demuxer, or this stream's codec in it, cannot seek.</exception>
    void Seek(long pts, int streamIndex) => throw new NotSupportedException("This demuxer cannot seek.");
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
    /// packet it was coded in; a <see cref="Packet.Discard"/> packet's frame is decoded but never returned.
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
    /// Decodes one packet and drops its first <see cref="Packet.SkipSamples"/> samples (those past its own from the next
    /// packets'), so the output starts where the container says presentation starts, or all of them for a
    /// <see cref="Packet.Discard"/> packet. A packet can come out empty.
    /// </summary>
    AudioFrame Decode(Packet packet);
}
