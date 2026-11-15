using System.Diagnostics;
using ManagedCodecs.Playback;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;

namespace ManagedCodecs.MonoGameSample;

/// <summary>
/// A live camera (RTSP or RTSPS, or MJPEG over HTTP) through <see cref="LivePlayer"/>: the newest picture into a
/// texture each frame of the game, and, over RTSP, the camera's sound (<see cref="RtspSource.Audio"/>,
/// <see cref="LivePlayer.TakeAudio"/>) through a dynamic sound (issue #526). The sound is played as it comes, and it is
/// the later of the two by what is queued ahead of it, so the picture waits for it (lip sync): a picture is shown once
/// the sound being heard was captured when it was, by the camera's clock (<see cref="RgbaFrame.CaptureTime"/>,
/// <see cref="AudioChunk.CaptureTime"/>; docs/decisions/2026-11-15-live-rtcp.md, point 3). Without those times (before
/// the camera's first RTCP sender reports, MJPEG over HTTP, no sound) the picture is the newest, at once.
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

    // The most pictures waiting for the sound: past it the oldest is shown early (a sound that never catches up, or
    // times that make no sense, must not hold the picture or its memory for good).
    private const int MaxHeld = 12;

    // A picture this far ahead of the sound isn't waited for: the tracks' times don't belong to one clock.
    private static readonly TimeSpan MaxAhead = TimeSpan.FromSeconds(1);

    // Each buffer in the sound, oldest first: when the camera captured its start and how long it plays; and their sum.
    private readonly Queue<(DateTime? Capture, TimeSpan Duration)> _queued = new();

    // The pictures taken and not yet shown, oldest first, each with when it was taken (Stopwatch ticks).
    private readonly Queue<(RgbaFrame Frame, long Taken)> _held = new();
    private readonly GraphicsDevice _device;
    private TimeSpan _queuedTime;
    private bool _running;

    // When the oldest buffer started to play (Stopwatch ticks): the sound heard now is that long into it.
    private long _oldestStarted;
    private long _shown;
    private long _waitedTicks;
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

    /// <summary>Pictures shown that had waited for the sound (not those shown at once for want of times).</summary>
    public long SyncedFrames { get; private set; }

    /// <summary>How long a picture waited for the sound before it was shown, the mean in milliseconds.</summary>
    public double MeanWaitMilliseconds => _shown == 0 ? 0 : _waitedTicks * 1000.0 / Stopwatch.Frequency / _shown;

    /// <summary>
    /// How far the sound heard was past the last synced picture's capture when it was shown, in milliseconds: at most
    /// about a tick of the game and a sound buffer when the two are in step.
    /// </summary>
    public double LastOffsetMilliseconds { get; private set; }

    /// <summary>Takes the newest picture, queues the sound that came since, and shows the picture the sound has reached.</summary>
    public void Update()
    {
        long now = Stopwatch.GetTimestamp();
        if (Player.TakeLatest() is { } taken)
        {
            _held.Enqueue((taken, now));
        }

        // The buffers the sound has finished: all but its PendingBufferCount, which counts the one playing.
        if (_sound is not null)
        {
            while (_queued.Count > _sound.PendingBufferCount)
            {
                _queuedTime -= _queued.Dequeue().Duration;
                _oldestStarted = now;
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
            _oldestStarted = now;
        }

        Show(now);
    }

    // Shows the newest picture the sound has reached: every waiting picture captured no later than the sound heard now
    // is due, and the last of them is shown. With no time to go by (the sound's, or the picture's), or a picture too
    // far ahead to be on the sound's clock, a picture is due at once; so is the oldest when too many wait.
    private void Show(long now)
    {
        var heard = Heard(now);
        RgbaFrame? due = null;
        bool synced = false;
        long dueTaken = 0;
        while (_held.Count > 0)
        {
            var (frame, taken) = _held.Peek();
            bool timed = heard is not null && frame.CaptureTime is { } captured && captured - heard.Value <= MaxAhead;
            if (timed && frame.CaptureTime > heard && _held.Count <= MaxHeld)
            {
                break; // the sound hasn't reached it yet
            }

            if (due is not null)
            {
                Player.Return(due);
            }

            _held.Dequeue();
            due = frame;
            dueTaken = taken;
            synced = timed && frame.CaptureTime <= heard;
        }

        if (due is null)
        {
            return;
        }

        if (_texture is null || _texture.Width != due.Width || _texture.Height != due.Height)
        {
            _texture?.Dispose();
            _texture = new Texture2D(_device, due.Width, due.Height, false, SurfaceFormat.Color);
        }

        _texture.SetData(due.Pixels);
        _shown++;
        _waitedTicks += now - dueTaken;
        if (synced)
        {
            SyncedFrames++;
            LastOffsetMilliseconds = (heard!.Value - due.CaptureTime!.Value).TotalMilliseconds;
        }

        Player.Return(due);
    }

    // When the camera captured the sound heard now: the oldest buffer's start and how long it has played. Null while
    // the sound isn't playing, or its buffer has no capture time. (What the sound device itself buffers after the mix
    // is not known here, so the sound is heard that much later than this says.)
    private DateTime? Heard(long now)
    {
        if (!_running || _queued.Count == 0 || _queued.Peek().Capture is not { } start)
        {
            return null;
        }

        var played = TimeSpan.FromSeconds((double)(now - _oldestStarted) / Stopwatch.Frequency);
        return start + (played < _queued.Peek().Duration ? played : _queued.Peek().Duration);
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
        _held.Clear();
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
        _queued.Enqueue((chunk.CaptureTime, duration));
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
