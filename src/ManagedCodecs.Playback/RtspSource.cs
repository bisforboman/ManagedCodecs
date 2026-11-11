using System.Net;

namespace ManagedCodecs.Playback;

/// <summary>
/// A camera serving H.264 over RTSP (RTSP 1.0, RTP over the RTSP connection): what a <see cref="LivePlayer"/> connects
/// to. Most IP cameras serve it, at a URL their manual gives (<c>rtsp://camera/stream1</c>, often with a substream at
/// a lower resolution: show a wall of cameras from those). The first H.264 video track of the camera's SDP plays.
/// docs/decisions/2026-11-10-live-rtsp-h264.md.
/// </summary>
public sealed class RtspSource
{
    /// <summary>A source whose credentials, if any, are in the URL (<c>rtsp://user:password@camera/stream</c>).</summary>
    /// <param name="uri">The stream's absolute rtsp URL.</param>
    public RtspSource(Uri uri)
        : this(uri, null)
    {
    }

    /// <summary>A source with credentials of its own (used in place of any in the URL).</summary>
    /// <param name="uri">The stream's absolute rtsp URL.</param>
    /// <param name="credential">The user name and password the camera asks for (Basic or Digest); null for none.</param>
    /// <exception cref="ArgumentException">Not an absolute rtsp URL.</exception>
    public RtspSource(Uri uri, NetworkCredential? credential)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || !uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Not an absolute rtsp URL.", nameof(uri));
        }

        Uri = uri;
        Credential = credential ?? FromUserInfo(uri);
    }

    /// <summary>The stream's URL, as given (credentials in it included).</summary>
    public Uri Uri { get; }

    /// <summary>The credentials: the ones given, else the URL's, else null.</summary>
    public NetworkCredential? Credential { get; }

    /// <summary>
    /// How long a connection may go without an RTP packet (or, while connecting, without an answer) before the player
    /// drops it and connects again. 5 seconds by default.
    /// </summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How far decoding may fall behind the camera before the player skips ahead: past it, pictures are dropped up to
    /// the next IDR picture, where decoding goes on (H.264 can't skip a picture others are predicted from). 1 second by
    /// default.
    /// </summary>
    public TimeSpan MaxLag { get; init; } = TimeSpan.FromSeconds(1);

    // The URL as sent: without its user info, which goes into an Authorization header instead.
    internal string RequestUrl =>
        Uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped);

    internal int Port => Uri.Port > 0 ? Uri.Port : 554;

    private static NetworkCredential? FromUserInfo(Uri uri)
    {
        if (string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        int colon = uri.UserInfo.IndexOf(':');
        return colon < 0
            ? new NetworkCredential(Uri.UnescapeDataString(uri.UserInfo), "")
            : new NetworkCredential(Uri.UnescapeDataString(uri.UserInfo.Substring(0, colon)), Uri.UnescapeDataString(uri.UserInfo.Substring(colon + 1)));
    }
}
