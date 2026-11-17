using System.Runtime.InteropServices.WindowsRuntime;
using ManagedCodecs.Playback;
using Microsoft.Maui.Handlers;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinImage = Microsoft.UI.Xaml.Controls.Image;
using WinStretch = Microsoft.UI.Xaml.Media.Stretch;

namespace ManagedCodecs.MauiSample;

/// <summary>
/// Windows: WinUI's <see cref="WinImage"/> showing a <see cref="WriteableBitmap"/>, which is blue, green, red, alpha
/// (red and blue swapped from the frame, as in WPF), filled on each frame WinUI renders
/// (<see cref="CompositionTarget.Rendering"/>).
/// </summary>
public sealed partial class LiveViewHandler : ViewHandler<LiveView, WinImage>
{
    private WriteableBitmap? _bitmap;
    private byte[] _bgra = [];

    protected override WinImage CreatePlatformView() => new() { Stretch = WinStretch.Uniform };

    protected override void ConnectHandler(WinImage platformView)
    {
        base.ConnectHandler(platformView);
        CompositionTarget.Rendering += OnRendering;
    }

    protected override void DisconnectHandler(WinImage platformView)
    {
        CompositionTarget.Rendering -= OnRendering;
        platformView.Source = null;
        _bitmap = null;
        base.DisconnectHandler(platformView);
    }

    private static void MapAspect(LiveViewHandler handler, LiveView view) =>
        handler.PlatformView.Stretch = view.Aspect switch
        {
            Aspect.AspectFill => WinStretch.UniformToFill,
            Aspect.Fill => WinStretch.Fill,
            Aspect.Center => WinStretch.None,
            _ => WinStretch.Uniform,
        };

    private void OnRendering(object? sender, object e) => OnFrame();

    // The frame into the bitmap, red and blue swapped; true when a new bitmap was made.
    private bool ShowOnPlatform(RgbaFrame frame)
    {
        bool made = false;
        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
        {
            _bitmap = new WriteableBitmap(frame.Width, frame.Height);
            _bgra = new byte[frame.Width * frame.Height * 4];
            PlatformView.Source = _bitmap;
            made = true;
        }

        var rgba = frame.Pixels;
        for (int i = 0; i < _bgra.Length; i += 4)
        {
            _bgra[i] = rgba[i]; // TEMPORARY, to see the check fail: no swap of red and blue.
            _bgra[i + 1] = rgba[i + 1];
            _bgra[i + 2] = rgba[i + 2];
            _bgra[i + 3] = rgba[i + 3];
        }

        using (var stream = _bitmap.PixelBuffer.AsStream())
        {
            stream.Write(_bgra, 0, _bgra.Length);
        }

        _bitmap.Invalidate();
        return made;
    }

    /// <summary>What WinUI draws of the view, at its size in pixels (<see cref="RenderTargetBitmap"/>, blue, green, red, alpha).</summary>
    internal async Task<DrawnPicture?> DrawnAsync()
    {
        if (PlatformView.ActualWidth <= 0 || PlatformView.ActualHeight <= 0)
        {
            return null;
        }

        var target = new RenderTargetBitmap();
        await target.RenderAsync(PlatformView);
        var bgra = (await target.GetPixelsAsync()).ToArray();
        int width = target.PixelWidth;
        int height = target.PixelHeight;
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
        {
            return null;
        }

        var rgba = new byte[width * height * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = bgra[i + 2];
            rgba[i + 1] = bgra[i + 1];
            rgba[i + 2] = bgra[i];
            rgba[i + 3] = bgra[i + 3];
        }

        return new DrawnPicture(rgba, width, height);
    }
}
