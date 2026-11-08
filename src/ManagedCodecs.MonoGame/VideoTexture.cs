using ManagedCodecs.Playback;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;

namespace ManagedCodecs.MonoGame;

/// <summary>
/// A video file played into a <see cref="Texture2D"/> (<see cref="SurfaceFormat.Color"/>) and its audio into a
/// <see cref="DynamicSoundEffectInstance"/>, decoded on a thread of its own by <see cref="MediaPlayer"/>. Call
/// <see cref="Update"/> from the game's <c>Update</c> and <see cref="Draw(SpriteBatch, Rectangle)"/> from its
/// <c>Draw</c>; <see cref="Play"/> starts it. Its clock is the game's: it runs by <see cref="GameTime.ElapsedGameTime"/>
/// while playing, from the first frame on, so frames due while the game was busy are dropped rather than played late.
/// </summary>
/// <remarks>
/// Not named VideoPlayer: <c>Microsoft.Xna.Framework.Media</c> has one. For the game's thread only (the decoding runs
/// on another). Without an audio device (OpenAL finds none) the video plays alone.
/// </remarks>
public sealed class VideoTexture : IDisposable
{
    // Audio buffers kept queued ahead of the sound: about 85 ms of 48 kHz AAC, room for a slow frame of the game.
    private const int QueuedBuffers = 4;

    private readonly GraphicsDevice _device;
    private DynamicSoundEffectInstance? _sound;
    private bool _noAudio;
    private bool _playing;
    private bool _clockStarted;
    private bool _soundStarted;
    private readonly Queue<AudioChunk> _early = new();
    private TimeSpan _clock;
    private float _volume = 1;
    private Rational _sampleAspectRatio;

    /// <summary>Opens a file, to play with the default <see cref="PlaybackOptions"/>.</summary>
    /// <exception cref="NotSupportedException">No video stream, or one this library doesn't decode.</exception>
    /// <exception cref="InvalidDataException">Not a file of a container this library reads, or a damaged one.</exception>
    public VideoTexture(GraphicsDevice device, string path)
        : this(device, new MediaPlayer(path))
    {
    }

    /// <summary>Opens a file.</summary>
    /// <param name="device">The device the texture is made on.</param>
    /// <param name="path">The file.</param>
    /// <param name="options">How to play it; null for the defaults.</param>
    /// <exception cref="NotSupportedException">No video stream, or one this library doesn't decode.</exception>
    /// <exception cref="InvalidDataException">Not a file of a container this library reads, or a damaged one.</exception>
    public VideoTexture(GraphicsDevice device, string path, PlaybackOptions? options)
        : this(device, new MediaPlayer(path, options))
    {
    }

    /// <summary>
    /// Opens a file held in a seekable stream (<c>TitleContainer.OpenStream</c>'s, read into a <c>MemoryStream</c> where
    /// it can't seek), which it disposes, to play with the default <see cref="PlaybackOptions"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">No video stream, or one this library doesn't decode.</exception>
    /// <exception cref="InvalidDataException">Not a file of a container this library reads, or a damaged one.</exception>
    public VideoTexture(GraphicsDevice device, Stream stream)
        : this(device, new MediaPlayer(stream))
    {
    }

    /// <summary>Opens a file held in a seekable stream, which it disposes.</summary>
    /// <param name="device">The device the texture is made on.</param>
    /// <param name="stream">The file.</param>
    /// <param name="options">How to play it; null for the defaults.</param>
    /// <exception cref="NotSupportedException">No video stream, or one this library doesn't decode.</exception>
    /// <exception cref="InvalidDataException">Not a file of a container this library reads, or a damaged one.</exception>
    public VideoTexture(GraphicsDevice device, Stream stream, PlaybackOptions? options)
        : this(device, new MediaPlayer(stream, options))
    {
    }

    private VideoTexture(GraphicsDevice device, MediaPlayer player)
    {
        if (device is null)
        {
            player.Dispose();
            throw new ArgumentNullException(nameof(device));
        }

        _device = device;
        Player = player;
    }

    /// <summary>The player underneath: its <see cref="MediaPlayer.Video"/>, <see cref="MediaPlayer.Statistics"/> and <see cref="MediaPlayer.Error"/>.</summary>
    public MediaPlayer Player { get; }

    /// <summary>The frame shown, or null before the first; a new texture when the decoded size changes.</summary>
    public Texture2D? Texture { get; private set; }

    /// <summary>Between <see cref="Play"/> and <see cref="Pause"/>, until the video has ended.</summary>
    public bool IsPlaying => _playing && !Player.IsFinished;

    /// <summary>Every frame has been shown or dropped (<see cref="MediaPlayer.IsFinished"/>); the last stays in <see cref="Texture"/>.</summary>
    public bool IsFinished => Player.IsFinished;

    /// <summary>The time played, from the first frame shown; it stands still while paused.</summary>
    public TimeSpan Position => _clock;

    /// <summary>The audio's volume, 0 (silent) to 1 (as decoded, the default).</summary>
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

    /// <summary>Stops the clock and the sound where they are; the decoding fills its queues and waits.</summary>
    public void Pause()
    {
        _playing = false;
        if (_sound is { State: SoundState.Playing })
        {
            _sound.Pause();
        }
    }

    /// <summary>
    /// Moves the clock on by the game's elapsed time while playing, puts the frame due into <see cref="Texture"/>
    /// (dropping late ones) and queues the audio. Once a frame of the game, from its <c>Update</c>.
    /// </summary>
    public void Update(GameTime gameTime)
    {
        ArgumentNullException.ThrowIfNull(gameTime);
        if (!_playing)
        {
            return;
        }

        if (_clockStarted)
        {
            _clock += gameTime.ElapsedGameTime;
        }

        if (Player.TakeFrame(_clock) is { } frame)
        {
            _clockStarted = true;
            if (Texture is null || Texture.Width != frame.Width || Texture.Height != frame.Height)
            {
                // The decoded size, after the stream's cropping, which can differ from the container's.
                Texture?.Dispose();
                Texture = new Texture2D(_device, frame.Width, frame.Height, false, SurfaceFormat.Color);
            }

            Texture.SetData(frame.Pixels);
            _sampleAspectRatio = frame.SampleAspectRatio;
            Player.Return(frame);
        }

        SubmitAudio();
    }

    /// <summary>
    /// Draws the frame shown, as large as fits in <paramref name="bounds"/> and centred there: at its sample aspect ratio
    /// (anamorphic video wider than its pixels) and turned upright by the stream's rotation (phone video). Between the
    /// batch's <c>Begin</c> and <c>End</c>; nothing before the first frame.
    /// </summary>
    public void Draw(SpriteBatch spriteBatch, Rectangle bounds) => Draw(spriteBatch, bounds, Color.White);

    /// <summary><see cref="Draw(SpriteBatch, Rectangle)"/>, tinted by <paramref name="color"/> (white for none; a lower alpha fades it).</summary>
    public void Draw(SpriteBatch spriteBatch, Rectangle bounds, Color color)
    {
        ArgumentNullException.ThrowIfNull(spriteBatch);
        if (Texture is null)
        {
            return;
        }

        var (position, rotation, origin, scale) = Placement(Texture.Width, Texture.Height, AspectOf(_sampleAspectRatio), Player.Video.Rotation, bounds);
        spriteBatch.Draw(Texture, position, null, color, rotation, origin, scale, SpriteEffects.None, 0);
    }

    /// <summary>Stops the decoding and the sound, and frees the texture.</summary>
    public void Dispose()
    {
        Player.Dispose();
        _sound?.Dispose();
        _sound = null;
        Texture?.Dispose();
        Texture = null;
    }

    /// <summary>
    /// Where SpriteBatch draws a <paramref name="width"/> x <paramref name="height"/> texture to show it in
    /// <paramref name="bounds"/>: its width stretched by <paramref name="aspect"/>, turned by <paramref name="degrees"/>
    /// counter-clockwise (as FFmpeg's command line turns a stream's rotation; a quarter turn swaps the sides it needs),
    /// scaled to fit and centred.
    /// </summary>
    internal static (Vector2 Position, float Rotation, Vector2 Origin, Vector2 Scale) Placement(int width, int height, double aspect, int degrees, Rectangle bounds)
    {
        bool quarter = degrees is 90 or 270 or -90 or -270;
        float shownWidth = (float)(width * aspect);
        float shownHeight = height;
        var (fitWidth, fitHeight) = quarter ? (shownHeight, shownWidth) : (shownWidth, shownHeight);
        float scale = Math.Min(bounds.Width / fitWidth, bounds.Height / fitHeight);
        var centre = new Vector2(bounds.X + (bounds.Width / 2f), bounds.Y + (bounds.Height / 2f));
        return (centre, -MathHelper.ToRadians(degrees), new Vector2(width / 2f, height / 2f), new Vector2(scale * (float)aspect, scale));
    }

    /// <summary>A sample aspect ratio as a number: 1 where the stream says none (0/0, 0/1).</summary>
    internal static double AspectOf(Rational sampleAspectRatio) =>
        sampleAspectRatio.Numerator > 0 && sampleAspectRatio.Denominator > 0 ? sampleAspectRatio.ToDouble() : 1;

    /// <summary>Float samples (-1 to 1) as 16-bit little-endian PCM, as DynamicSoundEffectInstance takes them.</summary>
    internal static byte[] ToPcm16(ReadOnlySpan<float> samples)
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

    // Keeps a few buffers queued; the first audio sets the rate. The sound is made, and OpenAL's device opened, as soon
    // as the first audio is decoded (opening it took 234 ms once, cold), with its first buffers queued while it is
    // stopped; it starts with the clock, at the first frame. The first audio comes out of the decoder before the first
    // frame (frame threading's delay and more), so the rest of it is held here until then, not left in the player's
    // bounded queue, which would stop the decoding before the first frame. Without an audio device the audio is taken
    // and dropped, so that its bounded queue doesn't stop the decoding thread.
    private void SubmitAudio()
    {
        while ((_sound is null || _sound.PendingBufferCount < QueuedBuffers) && (_early.TryDequeue(out var chunk) || (chunk = Player.TakeAudio()) is not null))
        {
            if (_noAudio)
            {
                continue;
            }

            if (_sound is null)
            {
                try
                {
                    _sound = new DynamicSoundEffectInstance(chunk.SampleRate, AudioChannels.Stereo) { Volume = _volume };
                }
                catch (NoAudioHardwareException)
                {
                    _noAudio = true;
                    continue;
                }
            }

            _sound.SubmitBuffer(ToPcm16(chunk.Samples));
        }

        if (!_clockStarted)
        {
            while (Player.TakeAudio() is { } early)
            {
                _early.Enqueue(early);
            }

            return;
        }

        if (_sound is not null && !_soundStarted)
        {
            _sound.Play();
            _soundStarted = true;
        }
    }
}
