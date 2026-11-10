using System.Windows;

namespace ManagedCodecs.WpfSample;

/// <summary>
/// mclive-wpf: shows a camera serving MJPEG over HTTP in a WPF window, decoded by ManagedCodecs (issue #462). A sample of
/// LivePlayer, not a viewer: one camera, its URL on the command line.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 1 || !Uri.TryCreate(args[0], UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            MessageBox.Show("usage: mclive-wpf <http://camera/video.mjpg>", "mclive-wpf");
            return 1;
        }

        var app = new Application();
        return app.Run(new LiveWindow(uri));
    }
}
