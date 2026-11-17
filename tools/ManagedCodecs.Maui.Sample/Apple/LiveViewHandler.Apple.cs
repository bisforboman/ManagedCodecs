using System.Runtime.InteropServices;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using ManagedCodecs.Playback;
using Microsoft.Maui.Handlers;
using UIKit;

namespace ManagedCodecs.MauiSample;

/// <summary>
/// iOS and Mac Catalyst: a <see cref="UIView"/> whose layer shows a <see cref="CGImage"/> of the frame, scaled by
/// Core Animation (<see cref="CALayer.ContentsGravity"/>). The frame is copied into a bitmap context of the frame's
/// size (red, green, blue and a skipped alpha: the frame's own order), and each shown frame is a new image of it, as a
/// layer's contents can't change in place; paced by <see cref="CADisplayLink"/>.
/// </summary>
public sealed partial class LiveViewHandler : ViewHandler<LiveView, UIView>
{
    private CGBitmapContext? _context;
    private CGImage? _image;
    private CADisplayLink? _displayLink;

    protected override UIView CreatePlatformView() => new() { ClipsToBounds = true };

    protected override void ConnectHandler(UIView platformView)
    {
        base.ConnectHandler(platformView);
        _displayLink = CADisplayLink.Create(OnFrame);
        _displayLink.AddToRunLoop(NSRunLoop.Main, NSRunLoopMode.Common);
    }

    protected override void DisconnectHandler(UIView platformView)
    {
        _displayLink?.Invalidate();
        _displayLink?.Dispose();
        _displayLink = null;
        platformView.Layer.Contents = null;
        _image?.Dispose();
        _image = null;
        _context?.Dispose();
        _context = null;
        base.DisconnectHandler(platformView);
    }

    // Core Animation's gravity names (kCAGravityResizeAspect and the others).
    private static void MapAspect(LiveViewHandler handler, LiveView view) =>
        handler.PlatformView.Layer.ContentsGravity = view.Aspect switch
        {
            Aspect.AspectFill => "resizeAspectFill",
            Aspect.Fill => "resize",
            Aspect.Center => "center",
            _ => "resizeAspect",
        };

    // The frame into the bitmap context (its rows may be padded) and a new image of it shown; true when a new context
    // was made.
    private bool ShowOnPlatform(RgbaFrame frame)
    {
        bool made = false;
        if (_context is null || (int)_context.Width != frame.Width || (int)_context.Height != frame.Height)
        {
            _context?.Dispose();
            using var space = CGColorSpace.CreateDeviceRGB();
            _context = new CGBitmapContext(IntPtr.Zero, frame.Width, frame.Height, 8, frame.Width * 4, space, CGImageAlphaInfo.NoneSkipLast);
            made = true;
        }

        int row = frame.Width * 4;
        int stride = (int)_context.BytesPerRow;
        for (int y = 0; y < frame.Height; y++)
        {
            Marshal.Copy(frame.Pixels, y * row, _context.Data + (y * stride), row);
        }

        if (_context.ToImage() is { } image)
        {
            PlatformView.Layer.Contents = image;
            _image?.Dispose();
            _image = image;
        }

        return made;
    }

    /// <summary>What Core Animation draws of the view, at its size in pixels: its layer rendered into a bitmap of its own.</summary>
    internal Task<DrawnPicture?> DrawnAsync()
    {
        var bounds = PlatformView.Bounds;
        var scale = PlatformView.ContentScaleFactor;
        int width = (int)(bounds.Width * scale);
        int height = (int)(bounds.Height * scale);
        if (width <= 0 || height <= 0)
        {
            return Task.FromResult<DrawnPicture?>(null);
        }

        var rgba = new byte[width * height * 4];
        var pinned = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try
        {
            using var space = CGColorSpace.CreateDeviceRGB();
            using var context = new CGBitmapContext(pinned.AddrOfPinnedObject(), width, height, 8, width * 4, space, CGImageAlphaInfo.PremultipliedLast);

            // Core Animation draws top down, Core Graphics bottom up: flipped, the bitmap's first row is the view's top.
            context.TranslateCTM(0, height);
            context.ScaleCTM(scale, -scale);
            PlatformView.Layer.RenderInContext(context);
        }
        finally
        {
            pinned.Free();
        }

        return Task.FromResult<DrawnPicture?>(new DrawnPicture(rgba, width, height));
    }
}
