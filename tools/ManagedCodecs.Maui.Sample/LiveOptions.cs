namespace ManagedCodecs.MauiSample;

/// <summary>
/// What to show, from the command line on Windows (<c>mclive-maui URL [--frames N] [--certificate-hash HEX]</c>) or the
/// intent's extras on Android (<c>adb shell am start -n ... -e url URL --ei frames N -e certificate-hash HEX</c>).
/// Without a URL the page asks for one.
/// </summary>
/// <param name="Uri">The camera's URL, or null to ask.</param>
/// <param name="Frames">Exit after this many frames shown, checking each (<see cref="FrameCheck"/>); 0 to run until closed.</param>
/// <param name="CertificateHash">RTSPS: the SHA-256 of the camera's certificate (64 hex digits), or null for the system's trust.</param>
internal sealed record LiveOptions(Uri? Uri, int Frames, string? CertificateHash)
{
    public static LiveOptions None { get; } = new(null, 0, null);

    /// <summary>The usage line, for an error.</summary>
    public const string Usage = "mclive-maui <http://camera/video.mjpg | rtsp[s]://user:password@camera/stream> [--frames N] [--certificate-hash HEX]";

    /// <summary>Options from a command line (without the program's name), or null when it doesn't parse.</summary>
    public static LiveOptions? Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return None;
        }

        if (!TryUri(args[0], out var uri))
        {
            return null;
        }

        int frames = 0;
        string? hash = null;
        for (int i = 1; i < args.Count; i += 2)
        {
            if (i + 1 >= args.Count)
            {
                return null;
            }

            switch (args[i])
            {
                case "--frames" when int.TryParse(args[i + 1], out frames) && frames > 0:
                    break;
                case "--certificate-hash":
                    hash = args[i + 1];
                    break;
                default:
                    return null;
            }
        }

        return new LiveOptions(uri, frames, hash);
    }

    /// <summary>Whether a text is a URL the player takes (http, https, rtsp, rtsps).</summary>
    public static bool TryUri(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? uri) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out uri) && uri.Scheme is "http" or "https" or "rtsp" or "rtsps";
}
