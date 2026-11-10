namespace ManagedCodecs.Playback;

/// <summary>
/// A camera or device serving MJPEG over HTTP (<c>multipart/x-mixed-replace</c>, a JPEG a part): what a
/// <see cref="LivePlayer"/> connects to. ESP32-CAM, mjpg-streamer, OctoPrint and most IP cameras' <c>video.mjpg</c> or
/// <c>video.cgi</c> serve it. A snapshot URL (a single <c>image/jpeg</c>) is not this.
/// </summary>
public sealed class MjpegHttpSource
{
    /// <summary>A source read with an <see cref="HttpClient"/> of the player's own.</summary>
    /// <param name="uri">The stream's absolute http or https URL.</param>
    public MjpegHttpSource(Uri uri)
        : this(uri, null)
    {
    }

    /// <summary>A source read with the caller's <see cref="HttpClient"/>, which the player doesn't dispose.</summary>
    /// <param name="uri">The stream's absolute http or https URL.</param>
    /// <param name="client">
    /// The client to read it with, for credentials (a handler's <c>Credentials</c> answers Basic and Digest), a proxy or
    /// certificates; null for one of the player's own. Its <see cref="HttpClient.Timeout"/> bounds each connection's
    /// whole life, so give it <see cref="Timeout.InfiniteTimeSpan"/>: <see cref="StallTimeout"/> catches a stalled one.
    /// </param>
    /// <exception cref="ArgumentException">Not an absolute http or https URL.</exception>
    public MjpegHttpSource(Uri uri, HttpClient? client)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Not an absolute http or https URL.", nameof(uri));
        }

        Uri = uri;
        Client = client;
    }

    /// <summary>The stream's URL.</summary>
    public Uri Uri { get; }

    /// <summary>The caller's client, or null for one of the player's own.</summary>
    public HttpClient? Client { get; }

    /// <summary>
    /// How long a connection may go without a whole part (or, while connecting, without the response's headers) before
    /// the player drops it and connects again. 5 seconds by default; a camera sending a frame every few seconds needs more.
    /// </summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
