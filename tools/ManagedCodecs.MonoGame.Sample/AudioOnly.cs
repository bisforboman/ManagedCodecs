using ManagedCodecs.Playback;
using Microsoft.Xna.Framework.Audio;

namespace ManagedCodecs.MonoGameSample;

/// <summary>
/// A file without video (.wav, raw MP3 or AAC), played by <see cref="AudioPlayer"/> through a dynamic sound, as
/// <c>VideoTexture</c> plays a video's sound (docs/decisions/2026-11-11-audio-player.md). Without an audio device the
/// audio is taken and dropped, so the file still plays through to its end.
/// </summary>
internal sealed class AudioOnly : IDisposable
{
    // Buffers queued ahead of the sound: each a decoded chunk (a frame of AAC or MP3, 4096 bytes of PCM).
    private const int QueuedBuffers = 8;

    private readonly AudioPlayer _player;
    private DynamicSoundEffectInstance? _sound;
    private bool _noAudio;
    private bool _paused;

    public AudioOnly(string path)
    {
        _player = new AudioPlayer(path);
        _player.Start();
    }

    /// <summary>The stream played.</summary>
    public AudioStreamInfo Audio => _player.Audio;

    /// <summary>What ended the decoding early, or null.</summary>
    public Exception? Error => _player.Error;

    /// <summary>Whether everything was decoded and the sound has played it out.</summary>
    public bool IsFinished => _player.IsFinished && (_sound is null || _sound.PendingBufferCount == 0);

    public bool IsPlaying => !_paused;

    public void Pause()
    {
        _paused = true;
        _sound?.Pause();
    }

    public void Play()
    {
        _paused = false;
        _sound?.Resume();
    }

    /// <summary>Keeps a few buffers queued; the first audio sets the rate and starts the sound.</summary>
    public void Update()
    {
        while ((_sound is null || _sound.PendingBufferCount < QueuedBuffers) && _player.TakeAudio() is { } chunk)
        {
            if (_noAudio)
            {
                continue;
            }

            if (_sound is null)
            {
                try
                {
                    _sound = new DynamicSoundEffectInstance(chunk.SampleRate, AudioChannels.Stereo);
                }
                catch (NoAudioHardwareException)
                {
                    _noAudio = true;
                    continue;
                }
            }

            _sound.SubmitBuffer(ToPcm16(chunk.Samples));
            if (!_paused && _sound.State != SoundState.Playing)
            {
                _sound.Play();
            }
        }
    }

    public void Dispose()
    {
        _sound?.Dispose();
        _player.Dispose();
    }

    // Float samples to 16-bit little-endian, rounded and clamped, as VideoTexture gives them to its sound.
    private static byte[] ToPcm16(float[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short value = (short)Math.Clamp(MathF.Round(samples[i] * 32767), -32768, 32767);
            bytes[2 * i] = (byte)value;
            bytes[(2 * i) + 1] = (byte)(value >> 8);
        }

        return bytes;
    }
}
