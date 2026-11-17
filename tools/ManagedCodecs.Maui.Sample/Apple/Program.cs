using Foundation;
using UIKit;

namespace ManagedCodecs.MauiSample;

/// <summary>
/// iOS and Mac Catalyst: the entry point. Its arguments are the options (<see cref="LiveOptions"/>), as
/// <c>xcrun simctl launch --console-pty booted io.github.managedcodecs.mclive URL --frames N</c> or the Mac Catalyst
/// app's binary run with them gives; arguments that aren't options (a launch from Finder adds its own) start the
/// page without a URL.
/// </summary>
public static class Program
{
    private static void Main(string[] args)
    {
        // On iOS, Main gets none of the process's arguments (simctl launch's among them): the process's own, its
        // executable's path left out.
        if (args.Length == 0)
        {
            args = NSProcessInfo.ProcessInfo.Arguments.Skip(1).ToArray();
        }

        // What arrived, for a run nobody watches (maui.yml reads lines that start "mclive-maui:", not this one).
        Console.WriteLine($"mclive-maui started with {args.Length} arguments: {string.Join(' ', args)}");
        MauiProgram.Options = LiveOptions.Parse(args) ?? LiveOptions.None;
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}

/// <summary>MAUI's application delegate.</summary>
[Register("AppDelegate")]
public sealed class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
