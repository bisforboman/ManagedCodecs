using ManagedCodecs.Playback;

namespace ManagedCodecs.MauiSample;

/// <summary>
/// Shows a <see cref="LivePlayer"/>'s newest frame, scaled as <see cref="Image"/> scales its source
/// (<see cref="Aspect"/>). On each frame the platform draws while the view is on screen, its handler
/// (<see cref="LiveViewHandler"/>, one per platform) takes the newest decoded frame, if there is one, copies it into a
/// native bitmap and gives it back.
/// </summary>
/// <remarks>
/// The application makes the player, starts it and disposes it; the view only shows frames, as the Avalonia and WPF
/// <c>LiveView</c>s (docs/decisions/2026-11-15-live-controls.md). A sample to copy, not a package
/// (docs/decisions/2026-11-17-live-maui.md). For the UI thread only.
/// </remarks>
public sealed class LiveView : View
{
    /// <summary>The player whose frames are shown; null shows nothing.</summary>
    public static readonly BindableProperty PlayerProperty =
        BindableProperty.Create(nameof(Player), typeof(LivePlayer), typeof(LiveView));

    /// <summary>How the picture fills the view, as <see cref="Image.AspectProperty"/>: <see cref="Aspect.AspectFit"/> by default.</summary>
    public static readonly BindableProperty AspectProperty =
        BindableProperty.Create(nameof(Aspect), typeof(Aspect), typeof(LiveView), Aspect.AspectFit);

    /// <summary>The player whose frames are shown; null shows nothing. The application starts and disposes it.</summary>
    public LivePlayer? Player
    {
        get => (LivePlayer?)GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    /// <inheritdoc cref="AspectProperty"/>
    public Aspect Aspect
    {
        get => (Aspect)GetValue(AspectProperty);
        set => SetValue(AspectProperty, value);
    }

    /// <summary>The frames shown since the view was made.</summary>
    public long ShownFrames { get; internal set; }
}
