using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ManagedCodecs.Playback;

namespace ManagedCodecs.WpfSample;

/// <summary>
/// A window that shows the camera: on each frame WPF renders (<see cref="CompositionTarget.Rendering"/>), the newest
/// decoded frame, if there is one, is written into a <see cref="WriteableBitmap"/>, and the status line says what the
/// player is doing. WPF has no RGBA pixel format, so each frame's red and blue are swapped into BGRA first, in place:
/// the frame is the window's until it goes back to the player.
/// </summary>
internal sealed class LiveWindow : Window
{
    private readonly LivePlayer _player;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 4, 8, 4), FontFamily = new FontFamily("Consolas"), Foreground = Brushes.White };
    private WriteableBitmap? _bitmap;
    private int _shown;

    public LiveWindow(Uri uri)
    {
        Title = $"mclive-wpf: {uri}";
        Width = 960;
        Height = 600;
        var status = new Border { Background = Brushes.Black, Child = _status };
        DockPanel.SetDock(status, Dock.Bottom);
        var panel = new DockPanel { Background = Brushes.Black };
        panel.Children.Add(status);
        panel.Children.Add(_image);
        Content = panel;

        _player = new LivePlayer(new MjpegHttpSource(uri));
        _player.Start();
        CompositionTarget.Rendering += OnRendering;
    }

    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _player.Dispose();
        base.OnClosed(e);
    }

    private void OnRendering(object? sender, EventArgs e)
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
    }

    // Writes a frame into the bitmap (a new one when the size changes), as BGRA.
    private void Show(RgbaFrame frame)
    {
        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
        {
            _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
            _image.Source = _bitmap;
        }

        var pixels = frame.Pixels;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), pixels, frame.Width * 4, 0);
    }
}
