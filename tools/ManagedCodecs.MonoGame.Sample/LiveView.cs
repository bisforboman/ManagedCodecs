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
    // The most sound queued ahead: the delay it adds at worst. What is decoded beyond it stays in the player, whose own
    // queue (half a second) drops the oldest and counts it (LiveStatistics.DroppedAudioFrames), so a camera running
    // faster than the sound card loses a chunk now and then and the delay doesn't grow.
    private static readonly TimeSpan MaxQueued = TimeSpan.FromMilliseconds(250);

    // The sound queued before it starts, and before it starts again after running dry. A packet carries several frames
    // (FFmpeg's 3 or 4 of AAC, 64 to 85 ms) and they arrive together: started on the first chunk, the sound has nothing
    // left when the next packet is a few milliseconds late, and gaps a mixing period long (about 19 ms) follow, a few
    // every minute (measured: the README).
    private static readonly TimeSpan StartQueued = TimeSpan.FromMilliseconds(100);

    // How long each buffer in the sound plays, oldest first, and their sum.
    private readonly Queue<TimeSpan> _queued = new();
    private readonly GraphicsDevice _device;
    private TimeSpan _queuedTime;
    private bool _running;
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

    /// <summary>How often the sound ran out of buffers while playing.</summary>
    public long RanDry { get; private set; }

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

        // The buffers the sound has finished: all but its PendingBufferCount, which counts the one playing.
        if (_sound is not null)
        {
            while (_queued.Count > _sound.PendingBufferCount)
            {
                _queuedTime -= _queued.Dequeue();
            }

            if (_running && _queued.Count == 0)
            {
                _running = false;
                RanDry++;
                _sound.Stop();
            }
        }

        // Without a sound device everything is taken and dropped.
        while ((_noAudio || _queuedTime < MaxQueued) && Player.TakeAudio() is { } chunk)
        {
            Play(chunk);
        }

        if (_sound is not null && !_running && _queuedTime >= StartQueued)
        {
            _sound.Play();
            _running = true;
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
            _queued.Clear();
            _queuedTime = TimeSpan.Zero;
            _running = false;
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

        _sound.SubmitBuffer(ToPcm16(chunk.Samples));
        var duration = TimeSpan.FromTicks((long)(chunk.Samples.Length / 2) * TimeSpan.TicksPerSecond / chunk.SampleRate);
        _queued.Enqueue(duration);
        _queuedTime += duration;
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
