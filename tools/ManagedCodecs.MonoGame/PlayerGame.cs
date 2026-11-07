using System.Diagnostics;
using ManagedCodecs.Playback;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace ManagedCodecs.MonoGame;

/// <summary>
/// Plays a file into a texture: the newest decoded frame that is due by a clock started at the first frame
/// (<see cref="MediaPlayer.TakeFrame"/>), drawn letterboxed to the window at its sample aspect ratio and turned by the
/// stream's rotation (phone video); audio submitted to a dynamic sound as it comes. Escape quits. The window title
/// shows the decoding's and the RGBA conversion's time per frame (means over the run, on the decoding thread), what a
/// game's frame budget cares about.
/// </summary>
internal sealed class PlayerGame : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private readonly MediaPlayer _player;
    private readonly Stopwatch _clock = new();
    private SpriteBatch? _sprites;
    private Texture2D? _texture;
    private DynamicSoundEffectInstance? _sound;
    private double _aspect = 1;
    private bool _noAudio;

    public PlayerGame(string path)
    {
        _player = new MediaPlayer(path);
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = Math.Min(_player.Video.Width, 1280),
            PreferredBackBufferHeight = Math.Min(_player.Video.Height, 720),
        };
        Window.AllowUserResizing = true;
        Window.Title = Path.GetFileName(path);
        IsMouseVisible = true;
    }

    protected override void LoadContent()
    {
        _sprites = new SpriteBatch(GraphicsDevice);
        _player.Start();
    }

    protected override void Update(GameTime gameTime)
    {
        if (Keyboard.GetState().IsKeyDown(Keys.Escape) || _player.IsFinished)
        {
            if (_player.Error is { } error)
            {
                Console.Error.WriteLine($"mcplay: stopped early: {error.Message}");
            }

            Exit();
            return;
        }

        SubmitAudio();

        // The clock starts with the first frame shown; late frames are dropped, as a player does when decoding can't
        // keep up.
        var due = _player.TakeFrame(_clock.Elapsed);
        if (due is not null)
        {
            _clock.Start();

            // The decoded size, after the stream's cropping, which can differ from the container's.
            if (_texture is null || _texture.Width != due.Width || _texture.Height != due.Height)
            {
                _texture?.Dispose();
                _texture = new Texture2D(GraphicsDevice, due.Width, due.Height, false, SurfaceFormat.Color);
            }

            _texture.SetData(due.Pixels);
            var sar = due.SampleAspectRatio;
            _aspect = sar.Numerator > 0 && sar.Denominator > 0 ? sar.ToDouble() : 1;
            _player.Return(due);
        }

        var statistics = _player.Statistics;
        Window.Title = $"mcplay: {_player.Video.Width}x{_player.Video.Height}, {statistics.DecodedFrames} frames, {statistics.DroppedFrames} dropped, ms a frame: decode {statistics.DecodeMilliseconds:F1}, RGBA conversion {statistics.ConvertMilliseconds:F1}";
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Color.Black);
        if (_texture is null)
        {
            base.Draw(gameTime);
            return;
        }

        // The picture as shown: the texture's width stretched by the aspect ratio, then turned (counter-clockwise; a
        // quarter turn swaps the sides it needs), centred and scaled to fit.
        var viewport = GraphicsDevice.Viewport;
        int rotation = _player.Video.Rotation;
        bool quarter = rotation is 90 or 270;
        float shownWidth = (float)(_texture.Width * _aspect);
        float shownHeight = _texture.Height;
        var (fitWidth, fitHeight) = quarter ? (shownHeight, shownWidth) : (shownWidth, shownHeight);
        float scale = Math.Min(viewport.Width / fitWidth, viewport.Height / fitHeight);
        var centre = new Vector2(viewport.Width, viewport.Height) / 2;
        var origin = new Vector2(_texture.Width, _texture.Height) / 2;
        var stretch = new Vector2(scale * (float)_aspect, scale);
        _sprites!.Begin(samplerState: SamplerState.LinearClamp);
        _sprites.Draw(_texture, centre, null, Color.White, -MathHelper.ToRadians(rotation), origin, stretch, SpriteEffects.None, 0);
        _sprites.End();
        base.Draw(gameTime);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _player.Dispose();
            _sound?.Dispose();
            _texture?.Dispose();
            _sprites?.Dispose();
        }

        base.Dispose(disposing);
    }

    // Keeps a few buffers queued; the first audio sets the rate. Without an audio device (OpenAL finds none: a server, a
    // container, a machine with sound off) the video plays alone, the audio taken and dropped so that its bounded queue
    // doesn't stop the decoding thread.
    private void SubmitAudio()
    {
        while ((_sound is null || _sound.PendingBufferCount < 4) && _player.TakeAudio() is { } chunk)
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
                catch (NoAudioHardwareException e)
                {
                    Console.Error.WriteLine($"mcplay: no audio device, playing the video alone ({e.Message})");
                    _noAudio = true;
                    continue;
                }

                _sound.Play();
            }

            // 16-bit little-endian PCM, as DynamicSoundEffectInstance takes it.
            var samples = chunk.Samples;
            var bytes = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                short value = (short)Math.Clamp(MathF.Round(samples[i] * 32767), -32768, 32767);
                bytes[2 * i] = (byte)value;
                bytes[(2 * i) + 1] = (byte)(value >> 8);
            }

            _sound.SubmitBuffer(bytes);
        }
    }
}
