// mclive: shows a camera serving MJPEG over HTTP or H.264 over RTSP in an Avalonia window, decoded by ManagedCodecs (issue #462). A sample of
// LivePlayer, not a viewer: one camera, its URL on the command line.
using Avalonia;
using ManagedCodecs.AvaloniaSample;

if (!Options.TryParse(args, out var options))
{
    Console.Error.WriteLine("usage: mclive <http://camera/video.mjpg | rtsp://user:password@camera/stream> [--frames N]");
    Console.Error.WriteLine("  --frames N  print the statistics and exit after N frames have been shown (a smoke test)");
    return 1;
}

App.Options = options;
return AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
