using ManagedCodecs.Playback;
using Microsoft.Xna.Framework.Audio;

namespace ManagedCodecs.MonoGame;

/// <summary>
/// A sound-only file (.wav, raw MP3 or AAC, M4A, Matroska audio) played into a <see cref="DynamicSoundEffectInstance"/>,
/// decoded on a thread of its own by <see cref="AudioPlayer"/>: what <see cref="VideoTexture"/> is for a file with
/// video (docs/decisions/2026-11-13-audio-track.md). Call <see cref="Update"/> from the game's <c>Update</c>;
/// <see cref="Play"/> starts it. It plays any file's audio, video or not, but a file with video plays its sound with
/// its picture in <see cref="VideoTexture"/>.
/// </summary>
/// <remarks>
/// Not named Song or SoundEffect: <c>Microsoft.Xna.Framework.Media</c> and <c>.Audio</c> have them. For the game's
/// thread only (the decoding runs on another). Without an audio device (OpenAL finds none) the audio is taken and
/// dropped as it is decoded, so the file still runs through to its end, quickly, and <see cref="Position"/> counts it
/// all.
/// </remarks>
public sealed class AudioTrack : IDisposable
{
    // Buffers queued ahead of the sound: each a decoded chunk (a frame of AAC or MP3, 4096 bytes of PCM); 8 frames of
    // 48 kHz AAC are 170 ms, room for a slow frame of the game.
    private const int QueuedBuffers = 8;

    // How long each buffer queued in the sound plays, oldest first: what Position adds as the sound finishes them.
    private readonly Queue<TimeSpan> _queued = new();
    private DynamicSoundEffectInstance? _sound;
    private bool _noAudio;
    private bool _playing;
    private float _volume = 1;
    private TimeSpan _played;

    /// <summary>Opens a file, to play once.</summary>
    /// <exception cref="NotSupportedException">No audio stream, or one this library doesn't decode.</exception>
    /// <exception cref="InvalidDataException">Not a file of a container this library reads, or a damaged one.</exception>
    public AudioTrack(string path)
        : this(new AudioPlayer(path))
    {
    }

    /// <summary>Opens a file.</summary>
    /// <param name="path">The file.</param>
    /// <param name="options">How to play it (only <see cref="PlaybackOptions.Loop"/> counts); null for the defaults.</param>
    /// <exception cref="NotSupportedException">No audio stream, or one this library doesn't decode.</exception>
    /// <exception cref="InvalidDataException">Not a file of a container this library reads, or a damaged one.</exception>
    public AudioTrack(string path, PlaybackOptions? options)
        : this(new AudioPlayer(path, options))
    {
    }

    /// <summary>
    /// Opens a file held in a seekable stream (<c>TitleContainer.OpenStream</c>'s, read into a <c>MemoryStream</c> where
    /// it can't seek), which it disposes, to play once.
    /// </summary>
    /// <exception cref="NotSupportedException">No audio stream, or one this library doesn't decode.</exception>
    /// <exception cref="InvalidDataException">Not a file of a container this library reads, or a damaged one.</exception>
    public AudioTrack(Stream stream)
        : this(new AudioPlayer(stream))
    {
    }

    /// <summary>Opens a file held in a seekable stream, which it disposes.</summary>
    /// <param name="stream">The file.</param>
    /// <param name="options">How to play it (only <see cref="PlaybackOptions.Loop"/> counts); null for the defaults.</param>
    /// <exception cref="NotSupportedException">No audio stream, or one this library doesn't decode.</exception>
    /// <exception cref="InvalidDataException">Not a file of a container this library reads, or a damaged one.</exception>
    public AudioTrack(Stream stream, PlaybackOptions? options)
        : this(new AudioPlayer(stream, options))
    {
    }

    private AudioTrack(AudioPlayer player) => Player = player;

    /// <summary>The player underneath: its <see cref="AudioPlayer.Audio"/> and <see cref="AudioPlayer.Error"/>.</summary>
    public AudioPlayer Player { get; }

    /// <summary>Between <see cref="Play"/> and <see cref="Pause"/>, until the sound has ended.</summary>
    public bool IsPlaying => _playing && !IsFinished;

    /// <summary>Everything was decoded (<see cref="AudioPlayer.IsFinished"/>) and the sound has played it out.</summary>
    public bool IsFinished => Player.IsFinished && _queued.Count == 0;

    /// <summary>
    /// The time played: the sound's buffers it has finished, so it follows the sound, standing still while paused or while
    /// the decoding falls behind, in steps of a buffer (a frame of AAC or MP3, 4096 bytes of PCM). A looped file goes on
    /// counting.
    /// </summary>
    public TimeSpan Position => _played;

    /// <summary>The volume, 0 (silent) to 1 (as decoded, the default).</summary>
    public float Volume
    {
        get => _volume;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 0f);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 1f);
            _volume = value;
            if (_sound is not null)
            {
                _sound.Volume = value;
            }
        }
    }

    /// <summary>Starts playing, or goes on after <see cref="Pause"/>. The first call starts the decoding.</summary>
    public void Play()
    {
        Player.Start();
        _playing = true;
        if (_sound is { State: SoundState.Paused })
        {
            _sound.Resume();
        }
    }

    /// <summary>Stops the sound where it is; the decoding fills its queue and waits.</summary>
    public void Pause()
    {
        _playing = false;
        if (_sound is { State: SoundState.Playing })
        {
            _sound.Pause();
        }
    }

    /// <summary>
    /// Counts the buffers the sound has finished into <see cref="Position"/> and keeps a few queued. Once a frame of
    /// the game, from its <c>Update</c>; the first audio sets the rate and opens the sound.
    /// </summary>
    public void Update()
    {
        if (_sound is not null)
        {
            _played += Finished(_queued, _sound.PendingBufferCount);
        }

        if (!_playing)
        {
            return;
        }

        while ((_sound is null || _sound.PendingBufferCount < QueuedBuffers) && Player.TakeAudio() is { } chunk)
        {
            if (!_noAudio && _sound is null)
            {
                try
                {
                    _sound = new DynamicSoundEffectInstance(chunk.SampleRate, AudioChannels.Stereo) { Volume = _volume };
                }
                catch (NoAudioHardwareException)
                {
                    _noAudio = true;
                }
            }

            if (_sound is null)
            {
                _played += Duration(chunk.Samples.Length, chunk.SampleRate);
                continue;
            }

            _sound.SubmitBuffer(VideoTexture.ToPcm16(chunk.Samples));
            _queued.Enqueue(Duration(chunk.Samples.Length, chunk.SampleRate));
        }

        if (_sound is { State: SoundState.Stopped } && _sound.PendingBufferCount > 0)
        {
            _sound.Play();
        }
    }

    /// <summary>Stops the decoding and the sound.</summary>
    public void Dispose()
    {
        Player.Dispose();
        _sound?.Dispose();
        _sound = null;
        _queued.Clear();
    }

    /// <summary>How long <paramref name="samples"/> interleaved stereo samples play at <paramref name="sampleRate"/>.</summary>
    internal static TimeSpan Duration(int samples, int sampleRate) =>
        TimeSpan.FromTicks((long)(samples / 2) * TimeSpan.TicksPerSecond / sampleRate);

    /// <summary>
    /// The time of the buffers the sound has finished, taken off the front of <paramref name="queued"/>: all but the
    /// last <paramref name="pending"/> (the sound's <c>PendingBufferCount</c>, which counts the one playing).
    /// </summary>
    internal static TimeSpan Finished(Queue<TimeSpan> queued, int pending)
    {
        var finished = TimeSpan.Zero;
        while (queued.Count > pending)
        {
            finished += queued.Dequeue();
        }

        return finished;
    }
}
