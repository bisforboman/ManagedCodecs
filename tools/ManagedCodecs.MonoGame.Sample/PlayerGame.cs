using ManagedCodecs.Containers;
using ManagedCodecs.MonoGame;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace ManagedCodecs.MonoGameSample;

/// <summary>
/// Plays a file in the window with <see cref="VideoTexture"/>: letterboxed, at its sample aspect ratio and turned by
/// the stream's rotation (phone video), its audio through a dynamic sound. Escape quits, space pauses. The window title
/// shows the decoding's and the RGBA conversion's time per frame (means over the run, on the decoding thread), what a
/// game's frame budget cares about. A file without video (.wav, raw MP3 or AAC) plays its sound alone (<see cref="AudioOnly"/>,
/// over <c>AudioPlayer</c>), the window black, until its end.
/// </summary>
internal sealed class PlayerGame : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private readonly string _path;
    private SpriteBatch? _sprites;
    private VideoTexture? _video;
    private AudioOnly? _audio;
    private bool _spaceDown;

    public PlayerGame(string path)
    {
        _path = path;
        _graphics = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = 1280, PreferredBackBufferHeight = 720 };
        Window.AllowUserResizing = true;
        Window.Title = Path.GetFileName(path);
        IsMouseVisible = true;
    }

    protected override void LoadContent()
    {
        _sprites = new SpriteBatch(GraphicsDevice);
        bool hasVideo;
        using (var demuxer = Demuxer.Open(_path))
        {
            hasVideo = demuxer.Video is not null;
        }

        if (!hasVideo)
        {
            _audio = new AudioOnly(_path);
            Window.Title = $"mcplay: {Path.GetFileName(_path)}, sound only ({_audio.Audio.Codec}, {_audio.Audio.SampleRate} Hz)";
            return;
        }

        _video = new VideoTexture(GraphicsDevice, _path);
        var video = _video.Player.Video;
        if (_video.Player.SkippedAudioCodec is { } codec)
        {
            Console.Error.WriteLine($"mcplay: the audio is {codec}, which isn't decoded (only AAC, MP3 and PCM are); playing without sound.");
        }

        _graphics.PreferredBackBufferWidth = Math.Min(video.Width, 1280);
        _graphics.PreferredBackBufferHeight = Math.Min(video.Height, 720);
        _graphics.ApplyChanges();
        _video.Play();
    }

    protected override void Update(GameTime gameTime)
    {
        var keyboard = Keyboard.GetState();
        if (_audio is { } audio)
        {
            UpdateAudioOnly(audio, keyboard);
            base.Update(gameTime);
            return;
        }

        var video = _video!;
        if (keyboard.IsKeyDown(Keys.Escape) || video.IsFinished)
        {
            if (video.Player.Error is { } error)
            {
                Console.Error.WriteLine($"mcplay: stopped early: {error.Message}");
            }

            Exit();
            return;
        }

        bool space = keyboard.IsKeyDown(Keys.Space);
        if (space && !_spaceDown)
        {
            if (video.IsPlaying)
            {
                video.Pause();
            }
            else
            {
                video.Play();
            }
        }

        _spaceDown = space;
        video.Update(gameTime);
        var statistics = video.Player.Statistics;
        Window.Title = $"mcplay: {video.Player.Video.Width}x{video.Player.Video.Height}, {statistics.DecodedFrames} frames, {statistics.DroppedFrames} dropped, ms a frame: decode {statistics.DecodeMilliseconds:F1}, RGBA conversion {statistics.ConvertMilliseconds:F1}";
        base.Update(gameTime);
    }

    // A sound-only file: escape quits, space pauses, and it ends with its sound.
    private void UpdateAudioOnly(AudioOnly audio, KeyboardState keyboard)
    {
        if (keyboard.IsKeyDown(Keys.Escape) || audio.IsFinished)
        {
            if (audio.Error is { } error)
            {
                Console.Error.WriteLine($"mcplay: stopped early: {error.Message}");
            }

            Exit();
            return;
        }

        bool space = keyboard.IsKeyDown(Keys.Space);
        if (space && !_spaceDown)
        {
            if (audio.IsPlaying)
            {
                audio.Pause();
            }
            else
            {
                audio.Play();
            }
        }

        _spaceDown = space;
        audio.Update();
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Color.Black);
        if (_video is null)
        {
            base.Draw(gameTime);
            return;
        }

        _sprites!.Begin(samplerState: SamplerState.LinearClamp);
        _video!.Draw(_sprites, GraphicsDevice.Viewport.Bounds);
        _sprites.End();
        base.Draw(gameTime);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _video?.Dispose();
            _audio?.Dispose();
            _sprites?.Dispose();
        }

        base.Dispose(disposing);
    }
}
