using ManagedCodecs.Playback;

namespace ManagedCodecs.MauiSample;

/// <summary>
/// Draws a <see cref="LiveView"/> on each platform (Platforms/Android, Platforms/Windows): a native image view showing a
/// bitmap, filled on each frame the platform draws (Android's <c>Choreographer</c>, WinUI's
/// <c>CompositionTarget.Rendering</c>) with the player's newest frame.
/// </summary>
public sealed partial class LiveViewHandler
{
    /// <summary>The view's properties mapped onto the native view.</summary>
    public static readonly IPropertyMapper<LiveView, LiveViewHandler> Mapper =
        new PropertyMapper<LiveView, LiveViewHandler>(ViewMapper)
        {
            [nameof(LiveView.Aspect)] = MapAspect,
        };

    public LiveViewHandler()
        : base(Mapper)
    {
    }

    // Each frame the platform draws: the player's newest frame, if one came since, into the bitmap.
    private void OnFrame()
    {
        if (VirtualView?.Player?.TakeLatest() is { } frame)
        {
            try
            {
                Show(frame);
            }
            finally
            {
                VirtualView.Player.Return(frame);
            }
        }
    }

    /// <summary>
    /// Copies a frame into the native bitmap (made anew when its size changes) and shows it. The page's check
    /// (<c>--frames</c>) calls it with frames it takes itself.
    /// </summary>
    internal void Show(RgbaFrame frame)
    {
        if (ShowOnPlatform(frame))
        {
            VirtualView.InvalidateMeasure();
        }

        VirtualView.ShownFrames++;
    }
}

/// <summary>A picture the platform drew, in red, green, blue, alpha order, <paramref name="Width"/> by <paramref name="Height"/>.</summary>
internal readonly record struct DrawnPicture(byte[] Rgba, int Width, int Height);
