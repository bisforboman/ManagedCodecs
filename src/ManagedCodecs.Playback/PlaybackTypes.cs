namespace ManagedCodecs.Playback;

/// <summary>How a <see cref="MediaPlayer"/> (or, for the audio, an <see cref="AudioPlayer"/>) decodes and converts.</summary>
public sealed record PlaybackOptions
{
    /// <summary>
    /// The H.264 decoder's threads (frame threading); 0, the default, for one fewer than the processors, from 1 to 4,
    /// which leaves a core to the game. MJPEG decodes on one thread.
    /// </summary>
    public int DecoderThreads { get; init; }

    /// <summary>The matrix to convert by in place of the one each frame says; null (the default) for the frame's.</summary>
    public ColorMatrix? Matrix { get; init; }

    /// <summary>The range to convert by in place of the one each frame says; null (the default) for the frame's.</summary>
    public ColorRange? Range { get; init; }

    /// <summary>
    /// Plays the file again from its start once it ends, for ever: a menu's backdrop rather than a cutscene. The frames'
    /// <see cref="RgbaFrame.Time"/> runs on across each start, so the caller's clock does not go back.
    /// </summary>
    public bool Loop { get; init; }

    /// <summary>
    /// Which of the file's audio tracks to play, counted from 0 in the file's order (<see cref="IDemuxer.AudioTracks"/>);
    /// null, the default, for the one the demuxer reads unasked (<see cref="IDemuxer.Audio"/>: the file's first today).
    /// A file with audio tracks but not this many is refused (<see cref="ArgumentOutOfRangeException"/>); one with no
    /// audio at all plays silent whatever this says.
    /// In an MXF file, where a mono track is usually one channel of the programme, a mono track plays as the left
    /// channel and the next track, if it is mono of the same codec and rate, as the right; any other mono track
    /// plays on both.
    /// </summary>
    public int? AudioTrack { get; init; }
}

/// <summary>A decoded video frame as RGBA, and when to show it.</summary>
public sealed class RgbaFrame
{
    internal RgbaFrame(TimeSpan time, int width, int height, byte[] pixels, Rational sampleAspectRatio)
    {
        Time = time;
        Width = width;
        Height = height;
        Pixels = pixels;
        SampleAspectRatio = sampleAspectRatio;
    }

    /// <summary>When to show the frame, from the first frame (shown at zero) on.</summary>
    public TimeSpan Time { get; }

    /// <summary>The decoded width in pixels (after the stream's cropping, so it can differ from the container's).</summary>
    public int Width { get; }

    /// <summary>The decoded height in pixels.</summary>
    public int Height { get; }

    /// <summary>
    /// <see cref="Width"/> x <see cref="Height"/> pixels from the top row down, 4 bytes each: R, G, B and A (255).
    /// Given back to the player with <see cref="MediaPlayer.Return"/> once copied, for a later frame.
    /// </summary>
    public byte[] Pixels { get; }

    /// <summary>The width of a pixel over its height: shown this much wider (0/0 or 1/1 for square pixels).</summary>
    public Rational SampleAspectRatio { get; }

    /// <summary>
    /// From a <see cref="LivePlayer"/> over RTSP: when the camera captured the frame, by the camera's own clock (UTC),
    /// from the RTP timestamp and the video track's last RTCP sender report. The sound's
    /// <see cref="AudioChunk.CaptureTime"/> is on the same clock, so the two say which sound belongs to which picture
    /// (lip sync), whatever the clock is set to; against this machine's clock it is only as right as the camera's.
    /// Null until the camera's first sender report for the track (usually within seconds of connecting), and always
    /// from a file or MJPEG over HTTP.
    /// </summary>
    public DateTime? CaptureTime { get; internal init; }

    // How long before Time the frame counts as due (MediaPlayer.TakeFrame): half the interval before it, so that a clock
    // a rounding error short of Time, or a tick early by vsync's jitter, shows it rather than nothing.
    internal TimeSpan Lead { get; init; }
}

/// <summary>Decoded audio: interleaved stereo, 32-bit float (-1 to 1), at <see cref="SampleRate"/>.</summary>
public sealed class AudioChunk
{
    internal AudioChunk(int sampleRate, float[] samples)
    {
        SampleRate = sampleRate;
        Samples = samples;
    }

    /// <summary>
    /// Samples a second per channel: the decoder's output rate, which for HE-AAC is twice the one the stream's header
    /// gives (<see cref="AudioStreamInfo.SampleRate"/>).
    /// </summary>
    public int SampleRate { get; }

    /// <summary>
    /// Left, right, left, right... A mono stream is copied to both (but an MXF file's pair of mono tracks is left and
    /// right: <see cref="PlaybackOptions.AudioTrack"/>); more channels give their first two.
    /// </summary>
    public float[] Samples { get; }

    /// <summary>
    /// From a <see cref="LivePlayer"/> over RTSP: when the camera captured the chunk's first sample, by the camera's own
    /// clock (UTC), from the RTP timestamp and the audio track's last RTCP sender report: the clock of
    /// <see cref="RgbaFrame.CaptureTime"/>. Within a packet of several AAC frames, the frames after the first are
    /// placed by the length of those decoded before them. Null until the camera's first sender report for the track,
    /// and always from a file.
    /// </summary>
    public DateTime? CaptureTime { get; internal init; }
}

/// <summary>How the decoding thread has kept up, for a log line.</summary>
/// <param name="DecodedFrames">Video frames the decoder has returned.</param>
/// <param name="DecodeMilliseconds">
/// Its time a frame, the mean over the run: every decoder call's wall time over the frames they returned. With H.264
/// frame threading a single call's time doesn't belong to its frames (it queues a packet and returns frames other
/// threads finished), but over many the mean is the throughput the decoding thread sees, the figure that says whether
/// playback keeps up.
/// </param>
/// <param name="ConvertedFrames">
/// Video frames converted to RGBA and queued: <see cref="MediaPlayer.TakeFrame"/> can take each one counted.
/// </param>
/// <param name="ConvertMilliseconds">The conversion's time a frame, the mean over the run.</param>
/// <param name="DroppedFrames">Frames <see cref="MediaPlayer.TakeFrame"/> skipped because a later one was already due.</param>
public readonly record struct PlaybackStatistics(long DecodedFrames, double DecodeMilliseconds, long ConvertedFrames, double ConvertMilliseconds, long DroppedFrames);
