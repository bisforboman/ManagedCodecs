using Godot;
using ManagedCodecs.Playback;

namespace ManagedCodecs.GodotAddon;

/// <summary>
/// Plays a video file (H.264 or MJPEG, AAC audio; MP4, Matroska or AVI) into this rectangle's texture, decoded by
/// ManagedCodecs.Playback: a thread of its own decodes ahead (<see cref="MediaPlayer"/>), <see cref="_Process"/> shows
/// the newest frame due by a clock started at the first frame (late ones are dropped), and the audio streams through
/// an <see cref="AudioStreamGenerator"/>. Not a media player: no seeking, no pause. A global class, so the editor's Add
/// Node dialog lists it once the project has built.
/// </summary>
[GlobalClass]
public partial class ManagedCodecsVideo : TextureRect
{
    private MediaPlayer? _player;
    private ImageTexture? _texture;
    private Image? _image;
    private double _clock = -1;
    private AudioStreamGeneratorPlayback? _playback;
    private (float[] Samples, int Used)? _pending;
    private readonly Queue<AudioChunk> _early = new();
    private int _shown;
    private double _lastReport;

    /// <summary>The file, a res:// path (read through Godot's FileAccess, so it plays from an exported .pck too) or a file system path.</summary>
    [Export]
    public string FilePath { get; set; } = "res://video.mp4";

    /// <summary>Plays the file again from its start once it ends (a menu's backdrop), rather than once (a cutscene).</summary>
    [Export]
    public bool Loop { get; set; }

    /// <inheritdoc/>
    public override void _Ready()
    {
        // FileAccess reads res:// from the project folder in the editor and from the .pck in an exported game; the
        // demuxer needs a seekable stream, so the whole file is read into memory (fine for cutscenes, not for films).
        byte[] bytes = Godot.FileAccess.GetFileAsBytes(FilePath);
        if (bytes.Length == 0)
        {
            GD.PrintErr($"ManagedCodecsVideo: cannot read {FilePath}: {Godot.FileAccess.GetOpenError()}");
            SetProcess(false);
            return;
        }

        try
        {
            _player = new MediaPlayer(new MemoryStream(bytes, writable: false), new PlaybackOptions { Loop = Loop });
        }
        catch (Exception e) when (e is NotSupportedException or InvalidDataException)
        {
            GD.PrintErr($"ManagedCodecsVideo: cannot play {FilePath}: {e.Message}");
            SetProcess(false);
            return;
        }

        StretchMode = StretchModeEnum.KeepAspectCentered;
        ExpandMode = ExpandModeEnum.IgnoreSize;
        _player.Start();
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        if (_player is null)
        {
            return;
        }

        // The clock starts with the first frame (due at zero) and runs by the frames' delta from then on.
        _clock = _clock < 0 ? 0 : _clock + delta;
        var due = _player.TakeFrame(TimeSpan.FromSeconds(_clock));
        if (due is null)
        {
            if (_shown == 0)
            {
                _clock = -1;
            }
        }
        else
        {
            Show(due);
            _player.Return(due);
            _shown++;
        }

        PushAudio();

        if (_clock - _lastReport >= 5)
        {
            _lastReport = _clock;
            Report();
        }

        if (_player.IsFinished)
        {
            if (_player.Error is { } error)
            {
                GD.PrintErr($"ManagedCodecsVideo: {FilePath} stopped early: {error.Message}");
            }

            Report();
            SetProcess(false);
        }
    }

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        if (_player is not null && _shown > 0 && IsProcessing())
        {
            Report();
        }

        _player?.Dispose();
        _player = null;
    }

    // Whether playback keeps up: frames dropped steadily mean decoding or the RGBA conversion (each timed per frame)
    // is slower than real time.
    private void Report()
    {
        var statistics = _player!.Statistics;
        GD.Print($"ManagedCodecsVideo: {_player.Video.Width}x{_player.Video.Height}, {_shown} frames shown, {statistics.DroppedFrames} dropped, ms a frame: decode {statistics.DecodeMilliseconds:F1}, RGBA conversion {statistics.ConvertMilliseconds:F1}");
    }

    // The frame's RGBA rows into one ImageTexture (Godot's images start at the top row, as the frames do). A sample
    // aspect ratio other than 1 is shown by the rectangle's size (keep aspect, centred), set from the shown width.
    private void Show(RgbaFrame frame)
    {
        if (_image is null || _image.GetWidth() != frame.Width || _image.GetHeight() != frame.Height)
        {
            var sar = frame.SampleAspectRatio;
            double aspect = sar.Numerator > 0 && sar.Denominator > 0 ? sar.ToDouble() : 1;
            _image = Image.CreateFromData(frame.Width, frame.Height, false, Image.Format.Rgba8, frame.Pixels);
            _texture = ImageTexture.CreateFromImage(_image);
            Texture = _texture;
            CustomMinimumSize = new Vector2((float)(frame.Width * aspect), frame.Height);
            return;
        }

        _image.SetData(frame.Width, frame.Height, false, Image.Format.Rgba8, frame.Pixels);
        _texture!.Update(_image);
    }

    // Decoded float stereo into the generator, as much as its buffer takes; the generator and its player are made once
    // the first frame is shown, so that the sound starts with the picture's clock (the first audio comes out of the
    // decoder before the first frame, by frame threading's delay and more), at the rate the first audio gives (HE-AAC's
    // output is twice its core's). Until then the audio is held here rather than in the player's bounded queue, which
    // would stop the decoding before the first frame.
    private void PushAudio()
    {
        if (_playback is null)
        {
            while (_player!.TakeAudio() is { } early)
            {
                _early.Enqueue(early);
            }

            if (_shown == 0)
            {
                return;
            }

            if (_pending is null && (_early.TryDequeue(out var first) || (first = _player.TakeAudio()) is not null))
            {
                var player = new AudioStreamPlayer { Stream = new AudioStreamGenerator { MixRate = first.SampleRate, BufferLength = 0.5f } };
                AddChild(player);
                player.Play();
                _playback = (AudioStreamGeneratorPlayback)player.GetStreamPlayback();
                _pending = (first.Samples, 0);
            }

            if (_playback is null)
            {
                return;
            }
        }

        int room = _playback.GetFramesAvailable();
        while (room > 0)
        {
            if (_pending is null)
            {
                if (!_early.TryDequeue(out var chunk) && (chunk = _player!.TakeAudio()) is null)
                {
                    return;
                }

                _pending = (chunk.Samples, 0);
            }

            var (samples, used) = _pending.Value;
            int frames = Math.Min(room, (samples.Length - used) / 2);
            var buffer = new Vector2[frames];
            for (int i = 0; i < frames; i++)
            {
                buffer[i] = new Vector2(samples[used + (2 * i)], samples[used + (2 * i) + 1]);
            }

            _playback.PushBuffer(buffer);
            room -= frames;
            used += 2 * frames;
            _pending = used < samples.Length ? (samples, used) : null;
        }
    }
}
