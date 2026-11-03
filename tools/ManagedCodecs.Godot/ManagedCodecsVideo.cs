using Godot;
using ManagedCodecs.MonoGame;

namespace ManagedCodecs.GodotSample;

/// <summary>
/// Plays a video file (H.264 or MJPEG, AAC audio; MP4, Matroska or AVI) into this rectangle's texture, decoded by
/// ManagedCodecs: a thread of its own decodes ahead (<see cref="MediaSource"/>, mcplay's), <see cref="_Process"/> shows
/// the newest frame due by a clock started at the first frame and drops late ones, and the audio streams through an
/// <see cref="AudioStreamGenerator"/>. Not a media player: no seeking, no pause.
/// </summary>
public partial class ManagedCodecsVideo : TextureRect
{
    private MediaSource? _source;
    private ImageTexture? _texture;
    private Image? _image;
    private RgbaFrame? _next;
    private double _clock = -1;
    private AudioStreamGeneratorPlayback? _playback;
    private (int Rate, short[] Samples, int Used)? _pending;
    private int _shown;
    private int _dropped;
    private double _lastReport;

    /// <summary>The file, a res:// path (read through Godot's FileAccess, so it plays from an exported .pck too) or a file system path.</summary>
    [Export]
    public string FilePath { get; set; } = "res://video.mp4";

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
            _source = new MediaSource(new MemoryStream(bytes, writable: false));
        }
        catch (Exception e) when (e is NotSupportedException or InvalidDataException)
        {
            GD.PrintErr($"ManagedCodecsVideo: cannot play {FilePath}: {e.Message}");
            SetProcess(false);
            return;
        }

        StretchMode = StretchModeEnum.KeepAspectCentered;
        ExpandMode = ExpandModeEnum.IgnoreSize;
        _source.Start();
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        if (_source is null)
        {
            return;
        }

        PushAudio();

        // The newest frame due: frames come in display order, so take them while the next one's time has come.
        while (_next is null && _source.Frames.TryTake(out var frame))
        {
            _next = frame;
        }

        if (_next is null)
        {
            return;
        }

        _clock = _clock < 0 ? _next.Time : _clock + delta;
        RgbaFrame? due = null;
        while (_next is not null && _next.Time <= _clock)
        {
            if (due is not null)
            {
                _dropped++;
                _source.Return(due);
            }

            due = _next;
            _next = _source.Frames.TryTake(out var frame) ? frame : null;
        }

        if (due is not null)
        {
            Show(due);
            _source.Return(due);
            _shown++;
        }

        if (_clock - _lastReport >= 5)
        {
            _lastReport = _clock;
            Report();
        }
    }

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        if (_source is not null && _shown > 0)
        {
            Report();
        }

        _source?.Dispose();
        _source = null;
    }

    // Whether playback keeps up: frames dropped steadily mean decoding or the RGBA conversion (each timed per frame,
    // MediaSource) is slower than real time.
    private void Report()
    {
        var (_, decode) = _source!.DecodeStatistics;
        var (_, convert) = _source.ConvertStatistics;
        GD.Print($"ManagedCodecsVideo: {_source.VideoStream.Width}x{_source.VideoStream.Height}, {_shown} frames shown, {_dropped} dropped, ms a frame: decode {decode:F1}, RGBA conversion {convert:F1}");
    }

    // The frame's RGBA rows into one ImageTexture (Godot's images start at the top row, as the frames do). A sample
    // aspect ratio other than 1 is shown by the rectangle's size (keep aspect, centred), set from the shown width.
    private void Show(RgbaFrame frame)
    {
        if (_image is null || _image.GetWidth() != frame.Width || _image.GetHeight() != frame.Height)
        {
            _image = Image.CreateFromData(frame.Width, frame.Height, false, Image.Format.Rgba8, frame.Pixels);
            _texture = ImageTexture.CreateFromImage(_image);
            Texture = _texture;
            CustomMinimumSize = new Vector2((float)(frame.Width * frame.AspectRatio), frame.Height);
            return;
        }

        _image.SetData(frame.Width, frame.Height, false, Image.Format.Rgba8, frame.Pixels);
        _texture!.Update(_image);
    }

    // Decoded 16-bit stereo into the generator, as much as its buffer takes; the generator and its player are made once
    // the first audio gives the rate (HE-AAC's output is twice its core's).
    private void PushAudio()
    {
        if (_playback is null)
        {
            if (_pending is null && _source!.Audio.TryTake(out var first))
            {
                _pending = (first.SampleRate, first.Samples, 0);
            }

            if (_pending is not { } start)
            {
                return;
            }

            var player = new AudioStreamPlayer { Stream = new AudioStreamGenerator { MixRate = start.Rate, BufferLength = 0.5f } };
            AddChild(player);
            player.Play();
            _playback = (AudioStreamGeneratorPlayback)player.GetStreamPlayback();
        }

        int room = _playback.GetFramesAvailable();
        while (room > 0)
        {
            if (_pending is null)
            {
                if (!_source!.Audio.TryTake(out var chunk))
                {
                    return;
                }

                _pending = (chunk.SampleRate, chunk.Samples, 0);
            }

            var (rate, samples, used) = _pending.Value;
            int frames = Math.Min(room, (samples.Length - used) / 2);
            var buffer = new Vector2[frames];
            for (int i = 0; i < frames; i++)
            {
                buffer[i] = new Vector2(samples[used + (2 * i)] / 32768f, samples[used + (2 * i) + 1] / 32768f);
            }

            _playback.PushBuffer(buffer);
            room -= frames;
            used += 2 * frames;
            _pending = used < samples.Length ? (rate, samples, used) : null;
        }
    }
}
