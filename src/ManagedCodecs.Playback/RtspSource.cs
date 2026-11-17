using System.Net;

namespace ManagedCodecs.Playback;

/// <summary>
/// A camera serving H.264 or JPEG over RTSP (RTSP 1.0, RTP over the RTSP connection, or over UDP when the camera answers
/// 461 to that), or over RTSPS (<c>rtsps://</c>: RTSP over TLS, RTP over the encrypted connection only, the certificate
/// validated by the system or pinned by <see cref="CertificateHash"/>; docs/decisions/2026-11-15-live-rtsps.md): what a
/// <see cref="LivePlayer"/> connects to. Most IP cameras serve it, at a URL their manual gives (<c>rtsp://camera/stream1</c>, often with a
/// substream at a lower resolution: show a wall of cameras from those). The first H.264 or JPEG video track of the
/// camera's SDP plays. docs/decisions/2026-11-10-live-rtsp-h264.md, docs/decisions/2026-11-12-live-rtsp-jpeg.md,
/// docs/decisions/2026-11-14-live-rtsp-udp.md. With <see cref="Audio"/>, its sound too, or its sound alone when it has no
/// video track.
/// </summary>
public sealed class RtspSource
{
    /// <summary>A source whose credentials, if any, are in the URL (<c>rtsp://user:password@camera/stream</c>).</summary>
    /// <param name="uri">The stream's absolute rtsp or rtsps URL.</param>
    public RtspSource(Uri uri)
        : this(uri, null)
    {
    }

    /// <summary>A source with credentials of its own (used in place of any in the URL).</summary>
    /// <param name="uri">The stream's absolute rtsp or rtsps URL.</param>
    /// <param name="credential">The user name and password the camera asks for (Basic or Digest); null for none.</param>
    /// <exception cref="ArgumentException">Not an absolute rtsp or rtsps URL.</exception>
    public RtspSource(Uri uri, NetworkCredential? credential)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || !(uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase) || uri.Scheme.Equals("rtsps", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Not an absolute rtsp or rtsps URL.", nameof(uri));
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

    /// <summary>
    /// Whether to play the camera's sound too: the first AAC (RFC 3640) or G.711 audio track of its SDP, taken with
    /// <see cref="LivePlayer.TakeAudio"/>. False by default: the session is then for the video alone. A camera with no
    /// such track, or one that refuses to send it, plays its video without sound (<see cref="LivePlayer.Audio"/>,
    /// <see cref="LivePlayer.SkippedAudioCodec"/>). docs/decisions/2026-11-15-live-rtsp-audio.md. A stream with sound and
    /// no H.264 or JPEG video track plays only with this on: the sound alone, which then makes the player
    /// <see cref="LiveState.Playing"/> and is what <see cref="StallTimeout"/> watches, and
    /// <see cref="LivePlayer.TakeLatest"/> stays null; without it, or with sound that doesn't play, the player fails
    /// (docs/decisions/2026-11-16-live-audio-only.md).
    /// </summary>
    public bool Audio { get; init; }

    /// <summary>
    /// Whether to tunnel RTSP through HTTP, for a camera behind a firewall or proxy that lets only HTTP pass: two HTTP
    /// connections to the camera, a GET that carries what it sends and a POST that carries the requests (Apple's
    /// tunnelling, which many cameras, live555 and GStreamer's server offer). The URL stays an <c>rtsp://</c> one; its
    /// port is then the camera's HTTP port, 80 when it gives none (443 for <c>rtsps://</c>, the tunnel over HTTPS). RTP
    /// comes inside the GET's answer, as over the RTSP connection; a camera that refuses that (461) can't play this way.
    /// False by default. docs/decisions/2026-11-16-live-rtsp-http.md.
    /// </summary>
    public bool HttpTunnel { get; init; }

    /// <summary>
    /// For an <c>rtsps://</c> source, the SHA-256 of the certificate the camera presents, in hex (64 digits; colons and
    /// spaces between them, as browsers and <c>openssl x509 -fingerprint -sha256</c> show it, are ignored): that
    /// certificate is accepted whatever its chain, name or dates, and no other. Null (the default) leaves the check to the
    /// system: a trusted chain and the URL's host name. Most cameras' certificates are self-signed, or name the device
    /// rather than its address, so they need this. Not used for <c>rtsp://</c>.
    /// </summary>
    /// <exception cref="ArgumentException">Not 32 bytes of hex.</exception>
    public string? CertificateHash
    {
        get => _certificateHash;
        init
        {
            _pinned = value is null ? null : ParseHash(value);
            _certificateHash = value;
        }
    }

    private readonly string? _certificateHash;
    private readonly byte[]? _pinned;

    // Whether the source is rtsps: RTSP over TLS.
    internal bool Secure => Uri.Scheme.Equals("rtsps", StringComparison.OrdinalIgnoreCase);

    // The pinned certificate's SHA-256, or null for the system's validation.
    internal byte[]? PinnedCertificate => _pinned;

    // The URL as sent: without its user info, which goes into an Authorization header instead.
    internal string RequestUrl =>
        Uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped);

    // rtsp's port 554, rtsps' 322 (IANA), unless the URL gives one; through the HTTP tunnel HTTP's, 80 (443 for rtsps).
    internal int Port => Uri.Port > 0 ? Uri.Port : HttpTunnel ? (Secure ? 443 : 80) : Secure ? 322 : 554;

    // The path the HTTP tunnel's GET and POST name: the URL's, with its query.
    internal string? TunnelPath => HttpTunnel ? Uri.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped) : null;

    private static byte[] ParseHash(string hex)
    {
        var digits = new System.Text.StringBuilder(64);
        foreach (char c in hex)
        {
            if (c is ':' or ' ' or '-')
            {
                continue;
            }

            digits.Append(c);
        }

        if (digits.Length != 64)
        {
            throw new ArgumentException("A certificate's SHA-256 is 64 hex digits.", nameof(CertificateHash));
        }

        var bytes = new byte[32];
        for (int i = 0; i < 32; i++)
        {
            if (!byte.TryParse(digits.ToString(2 * i, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out bytes[i]))
            {
                throw new ArgumentException("A certificate's SHA-256 is 64 hex digits.", nameof(CertificateHash));
            }
        }

        return bytes;
    }

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
