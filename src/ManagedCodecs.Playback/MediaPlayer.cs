using System.Collections.Concurrent;
using System.Diagnostics;
using ManagedCodecs.Aac;
using ManagedCodecs.Containers;
using ManagedCodecs.H264;
using ManagedCodecs.Mjpeg;
using ManagedCodecs.Mp3;

namespace ManagedCodecs.Playback;

/// <summary>
/// Plays a video file for a game engine: after <see cref="Start"/>, a thread of its own demuxes and decodes ahead of
/// playback, converts each frame to RGBA and the audio to float stereo, and the game loop takes from it the frame that
/// is due by its own clock (<see cref="TakeFrame"/>) and the audio its sound has room for (<see cref="TakeAudio"/>).
/// H.264 (with frame threading) or MJPEG video, AAC or MP3 audio, from MP4, Matroska, WebM or AVI. Both queues are bounded, so
/// the decoding waits when playback falls behind: take the audio, or drop it, even without a sound to play it.
/// </summary>
/// <remarks>
/// Not a media player: no seeking and no clock of its own. <see cref="TakeFrame"/>, <see cref="TakeAudio"/> and
/// <see cref="Return"/> are for one thread, the game loop's (audio may be taken on another one, the audio thread).
/// </remarks>
public sealed class MediaPlayer : IDisposable
{
    // Frames ahead: a player keeps one or two, so a few spare arrays cover the steady state; more are left to the
    // collector. A 720p frame's 3.6 MB would otherwise be a new array on the large object heap each frame.
    private const int FrameQueue = 8;
    private const int AudioQueue = 32;
    private const int SpareLimit = 4;

    private readonly IDemuxer _demuxer;
    private readonly PlaybackOptions _options;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stop = new();
    private readonly BlockingCollection<RgbaFrame> _frames = new(FrameQueue);
    private readonly BlockingCollection<AudioChunk> _audio = new(AudioQueue);
    private readonly ConcurrentBag<byte[]> _spare = [];
    private RgbaFrame? _next;
    private long _decoded;
    private long _decodeTicks;
    private long _converted;
    private long _convertTicks;
    private long _dropped;
    private int _started;
    private volatile Exception? _error;

    // The decoding thread's own: the first frame's time (the origin), and the loop's offset and the last frames' times.
    private long _firstPts = long.MinValue;
    private TimeSpan _offset;
    private TimeSpan _last = TimeSpan.MinValue;
    private TimeSpan _frameDuration;

    /// <summary>Opens a file, to play with the default <see cref="PlaybackOptions"/>.</summary>
    /// <exception cref="NotSupportedException">No video stream, or one this library doesn't decode.</exception>
    /// <exception cref="InvalidDataException">Not a file of a container this library reads, or a damaged one.</exception>
    public MediaPlayer(string path)
        : this(path, null)
    {
    }

    /// <summary>Opens a file.</summary>
    /// <param name="path">The file.</param>
    /// <param name="options">How to play it; null for the defaults.</param>
    /// <exception cref="NotSupportedException">No video stream, or one this library doesn't decode.</exception>
    /// <exception cref="InvalidDataException">Not a file of a container this library reads, or a damaged one.</exception>
    public MediaPlayer(string path, PlaybackOptions? options)
        : this(Demuxer.Open(path), options)
    {
    }

    /// <summary>
    /// Opens a file held in a seekable stream (an engine's packed resources, read into memory), which the player
    /// disposes, to play with the default <see cref="PlaybackOptions"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">No video stream, or one this library doesn't decode.</exception>
    /// <exception cref="InvalidDataException">Not a file of a container this library reads, or a damaged one.</exception>
    public MediaPlayer(Stream stream)
        : this(stream, null)
    {
    }

    /// <summary>
    /// Opens a file held in a seekable stream (an engine's packed resources, read into memory), which the player
    /// disposes.
    /// </summary>
    /// <param name="stream">The file.</param>
    /// <param name="options">How to play it; null for the defaults.</param>
    /// <exception cref="NotSupportedException">No video stream, or one this library doesn't decode.</exception>
    /// <exception cref="InvalidDataException">Not a file of a container this library reads, or a damaged one.</exception>
    public MediaPlayer(Stream stream, PlaybackOptions? options)
        : this(Demuxer.Open(stream), options)
    {
    }

    private MediaPlayer(IDemuxer demuxer, PlaybackOptions? options)
    {
        _demuxer = demuxer;
        _options = options ?? new PlaybackOptions();
        try
        {
            var video = demuxer.Video ?? throw new NotSupportedException("The file has no video stream.");
            if (video.Codec is not (CodecIds.H264 or CodecIds.Mjpeg))
            {
                throw new NotSupportedException($"No decoder for {video.Codec}.");
            }

            Video = video;
            Audio = demuxer.Audio is { } audio && AudioPipeline.CanPlay(audio.Codec) ? audio : null;
            SkippedAudioCodec = Audio is null ? demuxer.Audio?.Codec : null;
        }
        catch
        {
            demuxer.Dispose();
            throw;
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "ManagedCodecs playback" };
    }

    /// <summary>The video stream played.</summary>
    public VideoStreamInfo Video { get; }

    /// <summary>
    /// The audio stream played, or null for none: the file has no audio, or audio this library doesn't decode (only
    /// AAC, MP3, PCM in AVI, QuickTime and Matroska, and IMA and Microsoft ADPCM), and plays silent.
    /// </summary>
    public AudioStreamInfo? Audio { get; }

    /// <summary>
    /// The codec of the file's audio when it isn't played (<see cref="Audio"/> is null): a <see cref="CodecIds"/> value
    /// (none of the decoded ones), or the container's own name for it (<c>A_OPUS</c>). Null when the audio plays
    /// or there is none. For a warning, so that a video playing silent says why.
    /// </summary>
    public string? SkippedAudioCodec { get; }

    /// <summary>
    /// The decoding has ended (the file played through, <see cref="Error"/>, or <see cref="Dispose"/>) and every frame
    /// has been taken. Never with <see cref="PlaybackOptions.Loop"/>, unless the file can't be played again.
    /// </summary>
    public bool IsFinished => _frames.IsCompleted && _next is null;

    /// <summary>
    /// What stopped the decoding thread early, or null: a damaged file it couldn't go on with (usually an
    /// <see cref="InvalidDataException"/>), a read that failed, or a bug. The frames before it still play; the file is
    /// then <see cref="IsFinished"/>. Not thrown: on the decoding thread an exception would end the game's process.
    /// </summary>
    public Exception? Error => _error;

    /// <summary>The decoding's and the conversion's time a frame so far, and the frames dropped.</summary>
    public PlaybackStatistics Statistics
    {
        get
        {
            long decoded = Interlocked.Read(ref _decoded);
            long converted = Interlocked.Read(ref _converted);
            return new PlaybackStatistics(
                decoded,
                decoded == 0 ? 0 : Interlocked.Read(ref _decodeTicks) * 1000.0 / Stopwatch.Frequency / decoded,
                converted,
                converted == 0 ? 0 : Interlocked.Read(ref _convertTicks) * 1000.0 / Stopwatch.Frequency / converted,
                Interlocked.Read(ref _dropped));
        }
    }

    /// <summary>Starts the decoding thread. Again does nothing.</summary>
    /// <exception cref="ObjectDisposedException">The player was disposed.</exception>
    public void Start()
    {
        if (_stop.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(MediaPlayer));
        }

        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _thread.Start();
        }
    }

    /// <summary>
    /// The newest frame due at <paramref name="position"/> (from the first frame, which is due at zero), or null when
    /// no frame is due that wasn't taken already, or none is decoded yet. Frames due before it are skipped, as a player
    /// does when it can't keep up, and counted in <see cref="PlaybackStatistics.DroppedFrames"/>. A frame is due from
    /// half the interval before its <see cref="RgbaFrame.Time"/> on, so each call shows the frame nearest the clock: a
    /// game ticking at the video's own rate shows every frame, though its clock, a sum of frame times, falls a rounding
    /// error short of some of them, or vsync's jitter makes a tick a little early.
    /// </summary>
    /// <param name="position">The playback clock: the time played since the first frame was shown.</param>
    public RgbaFrame? TakeFrame(TimeSpan position)
    {
        RgbaFrame? due = null;
        while (true)
        {
            if (_next is null && !_frames.TryTake(out _next))
            {
                break;
            }

            if (_next.Time - _next.Lead > position)
            {
                break;
            }

            if (due is not null)
            {
                Interlocked.Increment(ref _dropped);
                Return(due);
            }

            due = _next;
            _next = null;
        }

        return due;
    }

    /// <summary>
    /// The next decoded audio, in file order, or null when none is decoded yet (or ever: <see cref="Audio"/> is null).
    /// Take it as the sound needs it; the decoding waits once about 32 chunks (0.7 s of 48 kHz AAC) are waiting.
    /// </summary>
    public AudioChunk? TakeAudio() => _audio.TryTake(out var chunk) ? chunk : null;

    /// <summary>
    /// Gives a frame's pixels back for a later frame to be converted into, once they are no longer read (copied into a
    /// texture). Optional: a frame not returned is collected as usual. Return each frame once, and don't read its
    /// pixels after.
    /// </summary>
    public void Return(RgbaFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_spare.Count < SpareLimit)
        {
            _spare.Add(frame.Pixels);
        }
    }

    /// <summary>Stops the decoding thread (waiting up to 2 seconds for it) and closes the file.</summary>
    public void Dispose()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        _stop.Cancel();
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            // Never started: no thread to end the queues, so that IsFinished holds.
            _frames.CompleteAdding();
            _audio.CompleteAdding();
        }
        else if (_thread.IsAlive)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        _demuxer.Dispose();
    }

    private void Run()
    {
        var token = _stop.Token;
        try
        {
            while (Play(token) && _options.Loop && !token.IsCancellationRequested)
            {
                // Again from the start, its frames after the last one shown: the Seek before the first keyframe starts
                // at the beginning, and new decoders start afresh (IDemuxer.Seek).
                _demuxer.Seek(long.MinValue / 4, Video.StreamIndex);
                _offset = _last + _frameDuration;
                _firstPts = long.MinValue;
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose: only its token cancels here.
        }
        catch (Exception e)
        {
            // A damaged or unreadable file, a demuxer that can't loop, the file closed by Dispose after its wait, or a
            // bug: any of them, thrown on this thread, would end the game's process. Kept for the game to log instead.
            _error = e;
        }
        finally
        {
            _frames.CompleteAdding();
            _audio.CompleteAdding();
        }
    }

    // Plays the file through once, from where the demuxer reads; true when a frame was shown (so that playing it
    // again, for a loop, is worth it).
    private bool Play(CancellationToken token)
    {
        var video = Video;
        IVideoDecoder decoder = video.Codec == CodecIds.H264
            ? new H264Decoder(video) { Threads = _options.DecoderThreads > 0 ? _options.DecoderThreads : Math.Clamp(Environment.ProcessorCount - 1, 1, 4) }
            : new MjpegDecoder(video);
        IAudioDecoder? audio = Audio is null ? null : AudioPipeline.CreateDecoder(Audio);
        long shown = Interlocked.Read(ref _converted);
        foreach (var packet in _demuxer.ReadPackets())
        {
            token.ThrowIfCancellationRequested();
            if (packet.StreamIndex == video.StreamIndex)
            {
                long start = Stopwatch.GetTimestamp();
                var frames = decoder.Decode(packet);
                Count(start, frames.Count);
                Show(frames, token);
            }
            else if (audio is not null && packet.StreamIndex == Audio!.StreamIndex && !packet.Discard)
            {
                var frame = audio.Decode(packet);
                if (frame.SampleCount > 0)
                {
                    _audio.Add(AudioPipeline.ToStereo(frame), token);
                }
            }
        }

        long flushStart = Stopwatch.GetTimestamp();
        var flushed = decoder.Flush();
        Count(flushStart, flushed.Count);
        Show(flushed, token);
        return Interlocked.Read(ref _converted) > shown;
    }

    // Adds a Decode or Flush call, timed from start, and the frames it returned to the statistics.
    private void Count(long start, int frames)
    {
        Interlocked.Add(ref _decodeTicks, Stopwatch.GetTimestamp() - start);
        Interlocked.Add(ref _decoded, frames);
    }

    private void Show(IReadOnlyList<VideoFrame> frames, CancellationToken token)
    {
        var timeBase = Video.TimeBase;
        foreach (var frame in frames)
        {
            long start = Stopwatch.GetTimestamp();
            if (_firstPts == long.MinValue)
            {
                _firstPts = frame.Pts;
            }

            var time = _offset + TimeSpan.FromSeconds((frame.Pts - _firstPts) * (double)timeBase.Numerator / timeBase.Denominator);
            if (_last != TimeSpan.MinValue && time > _last)
            {
                _frameDuration = time - _last;
            }

            _last = time;
            var pixels = Rent(frame.Width * frame.Height * 4);
            YuvToRgba.For(frame, _options.Matrix, _options.Range).Convert(frame, pixels);
            var rgba = new RgbaFrame(time, frame.Width, frame.Height, pixels, frame.SampleAspectRatio) { Lead = _frameDuration / 2 };
            frame.Dispose();
            Interlocked.Add(ref _convertTicks, Stopwatch.GetTimestamp() - start);
            _frames.Add(rgba, token);
            Interlocked.Increment(ref _converted); // once TakeFrame can take it
        }
    }

    // A returned array of the length if there is one (a spare of another size, from before the stream changed size, is
    // dropped), else a new one.
    private byte[] Rent(int length)
    {
        while (_spare.TryTake(out var pixels))
        {
            if (pixels.Length == length)
            {
                return pixels;
            }
        }

        return new byte[length];
    }
}
