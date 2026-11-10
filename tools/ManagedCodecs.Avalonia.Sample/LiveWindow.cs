using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ManagedCodecs.Playback;

namespace ManagedCodecs.AvaloniaSample;

/// <summary>
/// A window that shows the camera: on each frame Avalonia renders (<see cref="TopLevel.RequestAnimationFrame"/>), the
/// newest decoded frame, if there is one, is copied into a <see cref="WriteableBitmap"/>, and the status line says what
/// the player is doing.
/// </summary>
internal sealed class LiveWindow : Window
{
    private readonly LivePlayer _player;
    private readonly Options _options;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 4), FontFamily = FontFamily.Parse("monospace") };
    private WriteableBitmap? _bitmap;
    private int _shown;

    public LiveWindow(Options options)
    {
        _options = options;
        Title = $"mclive: {options.Uri}";
        Width = 960;
        Height = 600;
        var status = new Border { Background = Brushes.Black, Child = _status };
        DockPanel.SetDock(status, Dock.Bottom);
        _status.Foreground = Brushes.White;
        Content = new DockPanel { Background = Brushes.Black, Children = { status, _image } };

        _player = new LivePlayer(new MjpegHttpSource(options.Uri));
        _player.Start();
        RequestAnimationFrame(OnFrame);
    }

    protected override void OnClosed(EventArgs e)
    {
        _player.Dispose();
        _bitmap?.Dispose();
        base.OnClosed(e);
    }

    private void OnFrame(TimeSpan time)
    {
        if (_player.TakeLatest() is { } frame)
        {
            Show(frame);
            _player.Return(frame);
            _shown++;
        }

        var s = _player.Statistics;
        _status.Text = $"{_player.State}  received {s.ReceivedFrames}  shown {_shown}  skipped {s.SkippedFrames}  dropped {s.DroppedFrames}  "
            + $"damaged {s.DamagedFrames}  reconnects {s.Reconnects}  decode {s.DecodeMilliseconds:F1} ms  convert {s.ConvertMilliseconds:F1} ms"
            + (_player.Error is { } error ? $"  {error.GetType().Name}: {error.Message}" : "");

        // With --frames, a smoke test: exit after them, or at once if the player gave up. Without, the window stays to
        // show the error.
        if (_options.Frames > 0 && (_player.State == LiveState.Failed || _shown >= _options.Frames))
        {
            Console.WriteLine(_status.Text);
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown(_player.State == LiveState.Failed ? 1 : 0);
            }

            return;
        }

        RequestAnimationFrame(OnFrame);
    }

    // Copies a frame into the bitmap (a new one when the size changes), row by row: the bitmap's rows may be padded.
    private void Show(RgbaFrame frame)
    {
        if (_bitmap is null || _bitmap.PixelSize.Width != frame.Width || _bitmap.PixelSize.Height != frame.Height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormats.Rgba8888, AlphaFormat.Opaque);
            _image.Source = _bitmap;
        }

        using (var buffer = _bitmap.Lock())
        {
            int row = frame.Width * 4;
            for (int y = 0; y < frame.Height; y++)
            {
                Marshal.Copy(frame.Pixels, y * row, buffer.Address + (y * buffer.RowBytes), row);
            }
        }

        _image.InvalidateVisual();
    }
}
