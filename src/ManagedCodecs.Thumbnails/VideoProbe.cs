using ManagedCodecs.Containers;

namespace ManagedCodecs.Thumbnails;

/// <summary>
/// The basic facts of a video file, as ffprobe reports them, without decoding a frame: <see cref="Read(Stream)"/> reads
/// the container's headers and sample tables (MP4) or chunk headers (AVI) and nothing else.
/// </summary>
public static class VideoProbe
{
    /// <summary>Reads the facts of the file at <paramref name="path"/>.</summary>
    /// <inheritdoc cref="Read(Stream)"/>
    public static VideoInfo Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    /// <summary>
    /// Reads the facts of the video file in <paramref name="stream"/>, which must be seekable: an MP4 file's index can
    /// come after its data (most camera and phone files), so a request body or an upload is first buffered (a
    /// <see cref="MemoryStream"/>, or a temporary file for large ones). The stream is left open, at an unspecified
    /// position.
    /// </summary>
    /// <exception cref="ArgumentException">The stream is not seekable.</exception>
    /// <exception cref="InvalidDataException">The file is not AVI or MP4, is malformed, or has no video stream.</exception>
    /// <exception cref="NotSupportedException">The video is in a codec this library does not decode (HEVC, VP9, AV1, ...).</exception>
    public static VideoInfo Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var demuxer = Demuxer.Open(stream, leaveOpen: true);
        if (demuxer.Video is not { } video)
        {
            throw new InvalidDataException("The file has no video stream.");
        }

        if (video.Codec is not (CodecIds.H264 or CodecIds.Mjpeg))
        {
            throw new NotSupportedException($"The video is in {video.Codec}, which this library does not decode (H.264 and MJPEG only).");
        }

        // H.264's frames are the SPS's visible area, or the container's size where it crops that as FFmpeg's decoder takes it.
        var (codedWidth, codedHeight) = video.Codec == CodecIds.H264 && H264.H264Decoder.FrameSize(video) is { } size ? size : (video.Width, video.Height);
        var (width, height) = DisplaySize(codedWidth, codedHeight, video.SampleAspectRatio, video.Rotation);
        var audio = demuxer.Audio is { } a ? new AudioInfo(a.Codec, a.SampleRate, a.Channels) : null;
        return new VideoInfo(Timing(demuxer).Duration, video.Codec, codedWidth, codedHeight, width, height, video.FramesPerSecond, video.Rotation, Ratio(video.SampleAspectRatio), audio);
    }

    /// <summary>
    /// The size a frame is shown at: the coded size stretched by the sample aspect ratio (wider for a ratio above 1,
    /// taller below it, so never smaller than coded), rounded to whole pixels, then turned by the rotation.
    /// </summary>
    internal static (int Width, int Height) DisplaySize(int width, int height, Rational sampleAspectRatio, int rotation)
    {
        var ratio = Ratio(sampleAspectRatio);
        long w = width;
        long h = height;
        if (ratio.Numerator > ratio.Denominator)
        {
            w = ((2L * width * ratio.Numerator) + ratio.Denominator) / (2L * ratio.Denominator);
        }
        else if (ratio.Numerator < ratio.Denominator)
        {
            h = ((2L * height * ratio.Denominator) + ratio.Numerator) / (2L * ratio.Numerator);
        }

        return rotation is 90 or 270 ? ((int)h, (int)w) : ((int)w, (int)h);
    }

    // A container's "unknown" 0:1 is square pixels, as ffprobe's display aspect ratio takes it.
    private static Rational Ratio(Rational sampleAspectRatio) =>
        sampleAspectRatio.Numerator > 0 && sampleAspectRatio.Denominator > 0 ? sampleAspectRatio : new Rational(1, 1);

    /// <summary>
    /// As ffprobe's <c>format=duration</c> (FFmpeg's update_stream_timings): from the earliest stream start to the
    /// latest stream end, a stream starting at its first packet shown (packets an edit only decodes left out) and
    /// lasting its container duration (<see cref="VideoStreamInfo.Duration"/>), or to the end of its last packet where
    /// the container gives none.
    /// </summary>
    /// <returns>The duration, and the earliest stream's start in seconds (ffprobe's <c>format=start_time</c>).</returns>
    internal static (TimeSpan Duration, double Start) Timing(IDemuxer demuxer)
    {
        var streams = new Dictionary<int, (Rational TimeBase, long Duration, long Start, long End)>();
        if (demuxer.Video is { } video)
        {
            streams[video.StreamIndex] = (video.TimeBase, video.Duration, long.MaxValue, long.MinValue);
        }

        if (demuxer.Audio is { } audio)
        {
            streams[audio.StreamIndex] = (audio.TimeBase, audio.Duration, long.MaxValue, long.MinValue);
        }

        var packets = demuxer switch
        {
            Mp4Demuxer mp4 => mp4.ReadPacketInfo(),
            AviDemuxer avi => avi.ReadPacketInfo(),
            _ => [],
        };

        foreach (var packet in packets)
        {
            if (!packet.Discard && streams.TryGetValue(packet.StreamIndex, out var s))
            {
                streams[packet.StreamIndex] = s with { Start = Math.Min(s.Start, packet.Pts), End = Math.Max(s.End, packet.Pts + packet.Duration) };
            }
        }

        double start = double.PositiveInfinity;
        double end = double.NegativeInfinity;
        foreach (var (timeBase, duration, first, last) in streams.Values)
        {
            if (first == long.MaxValue)
            {
                continue; // no packet shown
            }

            start = Math.Min(start, first * timeBase.ToDouble());
            end = Math.Max(end, (duration > 0 ? first + duration : last) * timeBase.ToDouble());
        }

        return end > start ? (Time(end - start), start) : (TimeSpan.Zero, double.IsFinite(start) ? start : 0);
    }

    /// <summary>
    /// <paramref name="seconds"/> as a <see cref="TimeSpan"/>, the ticks truncated as .NET's
    /// <see cref="TimeSpan.FromSeconds(double)"/> does them. Not that method itself: .NET Standard's (Mono's, in the Unity
    /// build) rounds to the millisecond, so a time 0.2002 s in would read 0.2 s there and 8 s, as a double just under it,
    /// 7.9999999 s here (tools/unity-check.sh compares the two).
    /// </summary>
    /// <exception cref="OverflowException">The time is longer than a <see cref="TimeSpan"/> holds.</exception>
    internal static TimeSpan Time(double seconds)
    {
        double ticks = seconds * TimeSpan.TicksPerSecond;
        if (!(ticks >= long.MinValue && ticks < long.MaxValue))
        {
            throw new OverflowException("The time is longer than a TimeSpan holds.");
        }

        return TimeSpan.FromTicks((long)ticks);
    }
}

/// <summary>The basic facts of a video file (<see cref="VideoProbe.Read(Stream)"/>).</summary>
/// <param name="Duration">From the first frame or sample shown to the end of the last, as ffprobe's <c>format=duration</c>.</param>
/// <param name="Codec">The video codec, one of <see cref="CodecIds"/>.</param>
/// <param name="Width">The coded width in pixels, as the container gives it.</param>
/// <param name="Height">The coded height in pixels.</param>
/// <param name="DisplayWidth">The width a frame is shown at: stretched by <paramref name="SampleAspectRatio"/>, then turned by <paramref name="Rotation"/>.</param>
/// <param name="DisplayHeight">The height a frame is shown at.</param>
/// <param name="FramesPerSecond">The container's frame rate.</param>
/// <param name="Rotation">How far to turn frames counter-clockwise to show them: 0, 90, 180 or 270 degrees.</param>
/// <param name="SampleAspectRatio">The pixels' aspect ratio, 1:1 where the container says nothing.</param>
/// <param name="Audio">The first audio stream, or null without one (AVI's audio is not read).</param>
public sealed record VideoInfo(
    TimeSpan Duration,
    string Codec,
    int Width,
    int Height,
    int DisplayWidth,
    int DisplayHeight,
    double FramesPerSecond,
    int Rotation,
    Rational SampleAspectRatio,
    AudioInfo? Audio);

/// <summary>The basic facts of a video file's audio stream.</summary>
/// <param name="Codec">One of <see cref="CodecIds"/> when recognized, otherwise the container's own tag.</param>
/// <param name="SampleRate">Samples per second.</param>
/// <param name="Channels">The number of channels.</param>
public sealed record AudioInfo(string Codec, int SampleRate, int Channels);
