using System.Windows;

namespace ManagedCodecs.WpfSample;

/// <summary>
/// mclive-wpf: shows a camera serving MJPEG over HTTP or H.264 over RTSP in a WPF window, decoded by ManagedCodecs (issue #462). A sample of
/// LivePlayer, not a viewer: one camera, its URL on the command line. With <c>--frames N</c> it is a check of itself
/// (.github/workflows/windows.yml): it exits after N frames, each compared with what WPF drew of it.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        int frames = 0;
        bool usage = args.Length is not (1 or 3)
            || !Uri.TryCreate(args[0], UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https" or "rtsp")
            || (args.Length == 3 && !(args[1] == "--frames" && int.TryParse(args[2], out frames) && frames > 0));
        if (usage)
        {
            MessageBox.Show("usage: mclive-wpf <http://camera/video.mjpg | rtsp://user:password@camera/stream> [--frames N]", "mclive-wpf");
            return 1;
        }

        var app = new Application();
        return app.Run(new LiveWindow(new Uri(args[0]), frames));
    }
}
