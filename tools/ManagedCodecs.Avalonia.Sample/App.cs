using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace ManagedCodecs.AvaloniaSample;

/// <summary>The command line: the stream's URL, and how many frames to show before exiting (0: until closed).</summary>
internal sealed record Options(Uri Uri, int Frames)
{
    public static bool TryParse(string[] args, out Options options)
    {
        options = null!;
        Uri? uri = null;
        int frames = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--frames" && i + 1 < args.Length && int.TryParse(args[i + 1], out frames) && frames > 0)
            {
                i++;
            }
            else if (uri is null && Uri.TryCreate(args[i], UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" or "rtsp")
            {
                uri = parsed;
            }
            else
            {
                return false;
            }
        }

        if (uri is null)
        {
            return false;
        }

        options = new Options(uri, frames);
        return true;
    }
}

/// <summary>The application: Fluent's theme and one window.</summary>
internal sealed class App : Application
{
    public static Options Options { get; set; } = null!;

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new LiveWindow(Options);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
