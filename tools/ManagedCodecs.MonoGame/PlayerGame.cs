using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace ManagedCodecs.MonoGame;

/// <summary>
/// Plays a file into a texture: the newest decoded frame that is due by a clock started at the first frame, drawn
/// letterboxed to the window at its sample aspect ratio and turned by the stream's rotation (phone video); audio
/// submitted to a dynamic sound as it comes. Escape quits. The window title shows the decoding's and the RGBA
/// conversion's time per frame (means over the run, on the decoding thread), what a game's frame budget cares about.
/// </summary>
internal sealed class PlayerGame : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private readonly MediaSource _source;
    private readonly Stopwatch _clock = new();
    private SpriteBatch? _sprites;
    private Texture2D? _texture;
    private DynamicSoundEffectInstance? _sound;
    private RgbaFrame? _next;
    private double _aspect = 1;
    private bool _ended;
    private double _start;

    public PlayerGame(string path)
    {
        _source = new MediaSource(path);
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = Math.Min(_source.VideoStream.Width, 1280),
            PreferredBackBufferHeight = Math.Min(_source.VideoStream.Height, 720),
        };
        Window.AllowUserResizing = true;
        Window.Title = Path.GetFileName(path);
        IsMouseVisible = true;
    }

    protected override void LoadContent()
    {
        _sprites = new SpriteBatch(GraphicsDevice);
        _source.Start();
    }

    protected override void Update(GameTime gameTime)
    {
        if (Keyboard.GetState().IsKeyDown(Keys.Escape) || (_ended && _next is null))
        {
            Exit();
            return;
        }

        SubmitAudio();

        // The newest frame due; frames that are late are dropped, as a player does when decoding can't keep up.
        RgbaFrame? due = null;
        while (true)
        {
            if (_next is null && !_source.Frames.TryTake(out _next))
            {
                _ended = _source.Frames.IsCompleted;
                break;
            }

            if (!_clock.IsRunning)
            {
                _clock.Start();
                _start = _next.Time;
            }

            if (_next.Time - _start > _clock.Elapsed.TotalSeconds)
            {
                break;
            }

            due = _next;
            _next = null;
        }

        if (due is not null)
        {
            // The decoded size, after the stream's cropping, which can differ from the container's.
            if (_texture is null || _texture.Width != due.Width || _texture.Height != due.Height)
            {
                _texture?.Dispose();
                _texture = new Texture2D(GraphicsDevice, due.Width, due.Height, false, SurfaceFormat.Color);
            }

            _texture.SetData(due.Pixels);
            _aspect = due.AspectRatio;
        }

        var (count, decode) = _source.DecodeStatistics;
        var (_, convert) = _source.ConvertStatistics;
        Window.Title = $"mcplay: {_source.VideoStream.Width}x{_source.VideoStream.Height}, {count} frames, ms a frame: decode {decode:F1}, RGBA conversion {convert:F1}";
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
        int rotation = _source.VideoStream.Rotation;
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
            _source.Dispose();
            _sound?.Dispose();
            _texture?.Dispose();
            _sprites?.Dispose();
        }

        base.Dispose(disposing);
    }

    // Keeps a few buffers queued; the first audio frame sets the rate.
    private void SubmitAudio()
    {
        while ((_sound is null || _sound.PendingBufferCount < 4) && _source.Audio.TryTake(out var chunk))
        {
            if (_sound is null)
            {
                _sound = new DynamicSoundEffectInstance(chunk.SampleRate, AudioChannels.Stereo);
                _sound.Play();
            }

            var bytes = new byte[chunk.Samples.Length * 2];
            Buffer.BlockCopy(chunk.Samples, 0, bytes, 0, bytes.Length);
            _sound.SubmitBuffer(bytes);
        }
    }
}
