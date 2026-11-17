using System.Runtime.InteropServices;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using ManagedCodecs.Playback;
using Microsoft.Maui.Handlers;

namespace ManagedCodecs.MauiSample;

/// <summary>
/// Android: an <see cref="ImageView"/> showing an <see cref="Bitmap"/> (<see cref="Bitmap.Config.Argb8888"/>, whose
/// bytes in memory are red, green, blue, alpha: the frame's own order), filled from a direct buffer on each frame
/// Android draws (<see cref="Choreographer"/>).
/// </summary>
public sealed partial class LiveViewHandler : ViewHandler<LiveView, ImageView>
{
    private Bitmap? _bitmap;
    private Java.Nio.ByteBuffer? _buffer;
    private FrameCallback? _callback;

    protected override ImageView CreatePlatformView() => new(Context);

    protected override void ConnectHandler(ImageView platformView)
    {
        base.ConnectHandler(platformView);
        _callback = new FrameCallback(this);
        Choreographer.Instance!.PostFrameCallback(_callback);
    }

    protected override void DisconnectHandler(ImageView platformView)
    {
        if (_callback is { } callback)
        {
            callback.Stopped = true;
            Choreographer.Instance!.RemoveFrameCallback(callback);
            callback.Dispose();
            _callback = null;
        }

        platformView.SetImageBitmap(null);
        _bitmap?.Recycle();
        _bitmap?.Dispose();
        _bitmap = null;
        _buffer?.Dispose();
        _buffer = null;
        base.DisconnectHandler(platformView);
    }

    private static void MapAspect(LiveViewHandler handler, LiveView view) =>
        handler.PlatformView.SetScaleType(view.Aspect switch
        {
            Aspect.AspectFill => ImageView.ScaleType.CenterCrop!,
            Aspect.Fill => ImageView.ScaleType.FitXy!,
            Aspect.Center => ImageView.ScaleType.Center!,
            _ => ImageView.ScaleType.FitCenter!,
        });

    // The frame into the bitmap, through a direct buffer of the same size (one copy, no Java array); true when a new
    // bitmap was made.
    private bool ShowOnPlatform(RgbaFrame frame)
    {
        bool made = false;
        if (_bitmap is null || _bitmap.Width != frame.Width || _bitmap.Height != frame.Height)
        {
            PlatformView.SetImageBitmap(null);
            _bitmap?.Recycle();
            _bitmap?.Dispose();
            _buffer?.Dispose();
            _bitmap = Bitmap.CreateBitmap(frame.Width, frame.Height, Bitmap.Config.Argb8888!)!;
            _buffer = Java.Nio.ByteBuffer.AllocateDirect(frame.Width * frame.Height * 4)!;
            made = true;
        }

        // TEMPORARY, to see the check fail: red and blue swapped.
        var swapped = (byte[])frame.Pixels.Clone();
        for (int i = 0; i < swapped.Length; i += 4)
        {
            (swapped[i], swapped[i + 2]) = (swapped[i + 2], swapped[i]);
        }

        Marshal.Copy(swapped, 0, _buffer!.GetDirectBufferAddress(), frame.Width * frame.Height * 4);
        _buffer.Rewind();
        _bitmap.CopyPixelsFromBuffer(_buffer);
        if (made)
        {
            PlatformView.SetImageBitmap(_bitmap);
        }
        else
        {
            PlatformView.Invalidate();
        }

        return made;
    }

    /// <summary>What Android draws of the view, at the view's size: the view drawn into a bitmap of its own.</summary>
    internal Task<DrawnPicture?> DrawnAsync()
    {
        int width = PlatformView.Width;
        int height = PlatformView.Height;
        if (width <= 0 || height <= 0)
        {
            return Task.FromResult<DrawnPicture?>(null);
        }

        using var target = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!)!;
        using (var canvas = new Canvas(target))
        {
            PlatformView.Draw(canvas);
        }

        var argb = new int[width * height];
        target.GetPixels(argb, 0, width, 0, 0, width, height);
        var rgba = new byte[argb.Length * 4];
        for (int i = 0; i < argb.Length; i++)
        {
            int c = argb[i];
            rgba[i * 4] = (byte)(c >> 16);
            rgba[(i * 4) + 1] = (byte)(c >> 8);
            rgba[(i * 4) + 2] = (byte)c;
            rgba[(i * 4) + 3] = (byte)(c >> 24);
        }

        target.Recycle();
        return Task.FromResult<DrawnPicture?>(new DrawnPicture(rgba, width, height));
    }

    // Choreographer calls back once per post: each frame posts the next until the handler disconnects.
    private sealed class FrameCallback(LiveViewHandler handler) : Java.Lang.Object, Choreographer.IFrameCallback
    {
        public bool Stopped { get; set; }

        public void DoFrame(long frameTimeNanos)
        {
            if (Stopped)
            {
                return;
            }

            handler.OnFrame();
            Choreographer.Instance!.PostFrameCallback(this);
        }
    }
}
