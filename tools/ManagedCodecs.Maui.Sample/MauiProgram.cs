namespace ManagedCodecs.MauiSample;

/// <summary>The app's builder: MAUI's, with <see cref="LiveView"/> drawn by <see cref="LiveViewHandler"/>.</summary>
public static class MauiProgram
{
    /// <summary>Starting options, from the command line (Windows) or the intent's extras (Android), set before the app is built.</summary>
    internal static LiveOptions Options { get; set; } = LiveOptions.None;

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureMauiHandlers(handlers => handlers.AddHandler<LiveView, LiveViewHandler>());
        return builder.Build();
    }
}
