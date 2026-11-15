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
/// game's frame budget cares about. A file without video (.wav, raw MP3 or AAC) plays its sound alone
/// (<see cref="AudioTrack"/>), the window black and its title showing the time played, until its end. A camera's URL
/// (rtsp, rtsps, or http for MJPEG) plays live (<see cref="LiveView"/>): the newest picture, and over RTSP the camera's
/// sound, until escape.
/// </summary>
internal sealed class PlayerGame : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private readonly string _path;
    private readonly string? _certificateHash;
    private SpriteBatch? _sprites;
    private VideoTexture? _video;
    private AudioTrack? _audio;
    private LiveView? _live;
    private bool _spaceDown;
    private int _quit;

    public PlayerGame(string path, string? certificateHash = null)
    {
        _path = path;
        _certificateHash = certificateHash;
        _graphics = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = 1280, PreferredBackBufferHeight = 720 };
        Window.AllowUserResizing = true;
        Window.Title = Path.GetFileName(path);
        IsMouseVisible = true;
    }

    /// <summary>Ends a live view at its next update, as Escape does (Ctrl+C, from another thread).</summary>
    public void Quit() => Volatile.Write(ref _quit, 1);

    protected override void LoadContent()
    {
        _sprites = new SpriteBatch(GraphicsDevice);
        if (Uri.TryCreate(_path, UriKind.Absolute, out var uri) && uri.Scheme is "rtsp" or "rtsps" or "http" or "https")
        {
            _live = new LiveView(GraphicsDevice, uri, _certificateHash);
            return;
        }

        bool hasVideo;
        using (var demuxer = Demuxer.Open(_path))
        {
            hasVideo = demuxer.Video is not null;
        }

        if (!hasVideo)
        {
            _audio = new AudioTrack(_path);
            _audio.Play();
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
        if (_live is null && Volatile.Read(ref _quit) != 0)
        {
            Exit();
            return;
        }

        if (_live is { } live)
        {
            UpdateLive(live, keyboard);
            base.Update(gameTime);
            return;
        }

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

    // A live camera: escape quits; a final error (a wrong password, a refused certificate) ends it too.
    private void UpdateLive(LiveView live, KeyboardState keyboard)
    {
        if (keyboard.IsKeyDown(Keys.Escape) || Volatile.Read(ref _quit) != 0 || live.Player.State == ManagedCodecs.Playback.LiveState.Failed)
        {
            if (live.Player.Error is { } error)
            {
                Console.Error.WriteLine($"mcplay: {error.Message}");
            }

            // The title's last line, for a run nobody watched (under Xvfb, ended with Ctrl+C).
            Console.Error.WriteLine(Window.Title);
            Exit();
            return;
        }

        live.Update();
        var s = live.Player.Statistics;
        string sound = live.Player.Audio is { } a ? $", sound {a.Codec} {a.SampleRate} Hz ({s.DecodedAudioFrames} frames, {s.DroppedAudioFrames} dropped, ran dry {live.RanDry})"
            : live.Player.SkippedAudioCodec is { } skipped ? $", sound {skipped} not played" : ", no sound";
        string sync = live.SyncedFrames > 0 ? $", lip sync: {live.SyncedFrames} pictures waited for the sound ({live.MeanWaitMilliseconds:F0} ms a picture, the sound {live.LastOffsetMilliseconds:F0} ms past the last)" : "";
        Window.Title = $"mcplay: {live.Player.State}, {s.DecodedFrames} frames, {s.DroppedFrames} dropped, {s.LostPackets} packets lost{sound}{sync}";
    }

    // A sound-only file: escape quits, space pauses, and it ends with its sound.
    private void UpdateAudioOnly(AudioTrack audio, KeyboardState keyboard)
    {
        if (keyboard.IsKeyDown(Keys.Escape) || audio.IsFinished)
        {
            if (audio.Player.Error is { } error)
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
        Window.Title = $"mcplay: {Path.GetFileName(_path)}, sound only ({audio.Player.Audio.Codec}, {audio.Player.Audio.SampleRate} Hz), {audio.Position:m\\:ss\\.f}";
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Color.Black);
        if (_live is { } live)
        {
            _sprites!.Begin(samplerState: SamplerState.LinearClamp);
            live.Draw(_sprites, GraphicsDevice.Viewport.Bounds);
            _sprites.End();
            base.Draw(gameTime);
            return;
        }

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
            _live?.Dispose();
            _sprites?.Dispose();
        }

        base.Dispose(disposing);
    }
}
