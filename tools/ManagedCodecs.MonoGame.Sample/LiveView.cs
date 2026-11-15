using ManagedCodecs.Playback;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;

namespace ManagedCodecs.MonoGameSample;

/// <summary>
/// A live camera (RTSP or RTSPS, or MJPEG over HTTP) through <see cref="LivePlayer"/>: the newest picture into a
/// texture each frame of the game, and, over RTSP, the camera's sound (<see cref="RtspSource.Audio"/>,
/// <see cref="LivePlayer.TakeAudio"/>) through a dynamic sound (issue #526). No clock: the picture is the newest, the
/// sound is played as it comes, with no lip sync (docs/decisions/2026-11-15-live-rtsp-audio.md).
/// </summary>
internal sealed class LiveView : IDisposable
{
    // Buffers queued ahead of the sound, each a decoded chunk (a frame of AAC, 20 to 40 ms of G.711): past this, chunks
    // are dropped rather than queued, so that a sound that fell behind doesn't keep a growing delay.
    private const int MaxQueuedBuffers = 6;

    private readonly GraphicsDevice _device;
    private DynamicSoundEffectInstance? _sound;
    private int _soundRate;
    private bool _noAudio;
    private Texture2D? _texture;

    public LiveView(GraphicsDevice device, Uri uri, string? certificateHash)
    {
        _device = device;
        Player = uri.Scheme is "rtsp" or "rtsps"
            ? new LivePlayer(new RtspSource(uri) { Audio = true, CertificateHash = certificateHash })
            : new LivePlayer(new MjpegHttpSource(uri));
        Player.Start();
    }

    public LivePlayer Player { get; }

    /// <summary>Chunks dropped here because the sound had too many queued.</summary>
    public long DroppedChunks { get; private set; }

    /// <summary>Takes the newest picture into the texture and queues the sound that came since.</summary>
    public void Update()
    {
        if (Player.TakeLatest() is { } frame)
        {
            if (_texture is null || _texture.Width != frame.Width || _texture.Height != frame.Height)
            {
                _texture?.Dispose();
                _texture = new Texture2D(_device, frame.Width, frame.Height, false, SurfaceFormat.Color);
            }

            _texture.SetData(frame.Pixels);
            Player.Return(frame);
        }

        while (Player.TakeAudio() is { } chunk)
        {
            Play(chunk);
        }
    }

    /// <summary>The picture, as large as fits in <paramref name="bounds"/> and centred there.</summary>
    public void Draw(SpriteBatch sprites, Rectangle bounds)
    {
        if (_texture is null)
        {
            return;
        }

        float scale = Math.Min((float)bounds.Width / _texture.Width, (float)bounds.Height / _texture.Height);
        int width = (int)(_texture.Width * scale);
        int height = (int)(_texture.Height * scale);
        sprites.Draw(_texture, new Rectangle(bounds.X + ((bounds.Width - width) / 2), bounds.Y + ((bounds.Height - height) / 2), width, height), Color.White);
    }

    public void Dispose()
    {
        Player.Dispose();
        _sound?.Dispose();
        _texture?.Dispose();
    }

    private void Play(AudioChunk chunk)
    {
        if (_noAudio)
        {
            return;
        }

        if (_sound is null || _soundRate != chunk.SampleRate)
        {
            _sound?.Dispose();
            try
            {
                _sound = new DynamicSoundEffectInstance(chunk.SampleRate, AudioChannels.Stereo);
                _soundRate = chunk.SampleRate;
            }
            catch (NoAudioHardwareException)
            {
                _noAudio = true;
                return;
            }
        }

        if (_sound.PendingBufferCount >= MaxQueuedBuffers)
        {
            DroppedChunks++;
            return;
        }

        _sound.SubmitBuffer(ToPcm16(chunk.Samples));
        if (_sound.State != SoundState.Playing)
        {
            _sound.Play();
        }
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
