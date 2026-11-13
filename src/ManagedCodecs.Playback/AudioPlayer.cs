using System.Collections.Concurrent;
using ManagedCodecs.Containers;

namespace ManagedCodecs.Playback;

/// <summary>
/// Plays a file's audio for a game or an app: after <see cref="Start"/>, a thread of its own demuxes and decodes ahead
/// of playback and converts the audio to float stereo, which the sound takes as it has room for it
/// (<see cref="TakeAudio"/>). For audio-only files (.wav, raw MP3 and AAC, M4A, Matroska audio), which
/// <see cref="MediaPlayer"/> refuses, and the audio of any file it opens: AAC, MP3, PCM, IMA and Microsoft ADPCM, as
/// <see cref="MediaPlayer"/> plays them (docs/decisions/2026-11-11-audio-player.md).
/// </summary>
/// <remarks>
/// The queue is bounded, so the decoding waits when the sound falls behind. No seeking and no clock of its own: the
/// sound's playback is the clock. <see cref="TakeAudio"/> is for one thread at a time.
/// </remarks>
public sealed class AudioPlayer : IDisposable
{
    private const int AudioQueue = 32;

    private readonly IDemuxer _demuxer;
    private readonly PlaybackOptions _options;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stop = new();
    private readonly BlockingCollection<AudioChunk> _audio = new(AudioQueue);
    private int _started;
    private volatile Exception? _error;

    /// <summary>Opens the file at <paramref name="path"/>; <see cref="Start"/> starts the decoding.</summary>
    /// <exception cref="NotSupportedException">No audio stream, or one this library doesn't decode.</exception>
    /// <exception cref="InvalidDataException">The file is malformed.</exception>
    public AudioPlayer(string path)
        : this(path, null)
    {
    }

    /// <summary>Opens the file at <paramref name="path"/> with <paramref name="options"/> (only <see cref="PlaybackOptions.Loop"/> counts).</summary>
    /// <exception cref="NotSupportedException">No audio stream, or one this library doesn't decode.</exception>
    /// <exception cref="InvalidDataException">The file is malformed.</exception>
    public AudioPlayer(string path, PlaybackOptions? options)
        : this(Demuxer.Open(path), options)
    {
    }

    /// <summary>Opens <paramref name="stream"/>, which the player disposes with itself.</summary>
    /// <exception cref="NotSupportedException">No audio stream, or one this library doesn't decode.</exception>
    /// <exception cref="InvalidDataException">The file is malformed.</exception>
    public AudioPlayer(Stream stream)
        : this(stream, null)
    {
    }

    /// <summary>Opens <paramref name="stream"/> with <paramref name="options"/> (only <see cref="PlaybackOptions.Loop"/> counts); the player disposes it with itself.</summary>
    /// <exception cref="NotSupportedException">No audio stream, or one this library doesn't decode.</exception>
    /// <exception cref="InvalidDataException">The file is malformed.</exception>
    public AudioPlayer(Stream stream, PlaybackOptions? options)
        : this(Demuxer.Open(stream), options)
    {
    }

    private AudioPlayer(IDemuxer demuxer, PlaybackOptions? options)
    {
        _demuxer = demuxer;
        _options = options ?? new PlaybackOptions();
        try
        {
            var audio = demuxer.Audio ?? throw new NotSupportedException("The file has no audio stream.");
            if (!AudioPipeline.CanPlay(audio.Codec))
            {
                throw new NotSupportedException($"No decoder for {audio.Codec}.");
            }

            Audio = audio;
        }
        catch
        {
            demuxer.Dispose();
            throw;
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "ManagedCodecs audio playback" };
    }

    /// <summary>The audio stream played.</summary>
    public AudioStreamInfo Audio { get; }

    /// <summary>Whether every chunk was taken and no more will come (the end of the file, without <see cref="PlaybackOptions.Loop"/>, or an <see cref="Error"/>).</summary>
    public bool IsFinished => _audio.IsCompleted;

    /// <summary>What ended the decoding early (a damaged file, a demuxer that can't loop), or null.</summary>
    public Exception? Error => _error;

    /// <summary>Starts the decoding thread. Again does nothing.</summary>
    /// <exception cref="ObjectDisposedException">The player was disposed.</exception>
    public void Start()
    {
        if (_stop.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(AudioPlayer));
        }

        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _thread.Start();
        }
    }

    /// <summary>
    /// The next decoded audio, in file order, or null when none is decoded yet (or ever again: <see cref="IsFinished"/>).
    /// Take it as the sound needs it; the decoding waits once about 32 chunks are waiting.
    /// </summary>
    public AudioChunk? TakeAudio() => _audio.TryTake(out var chunk) ? chunk : null;

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
            // Never started: no thread to end the queue, so that IsFinished holds.
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
            // Again from the start for a loop, as long as a pass gave audio: the seek before the first packet starts at
            // the beginning (the raw demuxers seek by the frames found when opening).
            while (Play(token) && _options.Loop && !token.IsCancellationRequested)
            {
                _demuxer.Seek(long.MinValue / 4, Audio.StreamIndex);
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose: only its token cancels here.
        }
        catch (Exception e)
        {
            // A damaged or unreadable file, a demuxer that can't loop, or the file closed by Dispose after its wait:
            // thrown on this thread, any of them would end the process. Kept for the caller to log instead.
            _error = e;
        }
        finally
        {
            _audio.CompleteAdding();
        }
    }

    // Plays the file's audio through once, from where the demuxer reads; true when it gave any (so that playing it again,
    // for a loop, is worth it).
    private bool Play(CancellationToken token)
    {
        var decoder = AudioPipeline.CreateDecoder(Audio);
        bool any = false;
        foreach (var packet in _demuxer.ReadPackets())
        {
            token.ThrowIfCancellationRequested();
            if (packet.StreamIndex != Audio.StreamIndex || packet.Discard)
            {
                continue;
            }

            var frame = decoder.Decode(packet);
            if (frame.SampleCount > 0)
            {
                _audio.Add(AudioPipeline.ToStereo(frame), token);
                any = true;
            }
        }

        return any;
    }
}
