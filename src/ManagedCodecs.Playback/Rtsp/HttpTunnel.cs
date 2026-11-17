using System.Security.Cryptography;
using System.Text;

namespace ManagedCodecs.Playback.Rtsp;

/// <summary>
/// RTSP over HTTP, as Apple's QuickTime server first offered it and cameras, live555 and GStreamer's server still do: two
/// HTTP connections joined by a cookie (<c>x-sessioncookie</c>). The answer to a GET carries what the server sends (RTSP
/// responses and RTP interleaved on them, as on an RTSP connection), and the body of a POST what the client sends, in
/// base64 (Content-Type <c>application/x-rtsp-tunnelled</c>). Only HTTP's port has to pass a firewall or a proxy.
/// docs/decisions/2026-11-16-live-rtsp-http.md.
/// </summary>
internal sealed class HttpTunnel : Stream
{
    // The POST's body never ends: it says a length no client reaches (Apple's 32767), and the server reads on.
    private const string PostLength = "32767";
    private const int MaxHead = 16 * 1024;

    private readonly Stream _get;
    private readonly Stream _post;

    private HttpTunnel(Stream get, Stream post)
    {
        _get = get;
        _post = post;
    }

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => true;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Opens the tunnel on two connections to the server, already connected (and, for HTTPS, encrypted): the GET's,
    /// whose answer must be 200 and is read up to its end, and the POST's, whose head is sent. A cookie of 22 random
    /// characters joins them.
    /// </summary>
    /// <param name="get">The GET's connection: it then carries what the server sends.</param>
    /// <param name="post">The POST's connection: it then carries what the client sends.</param>
    /// <param name="host">The Host header's value.</param>
    /// <param name="path">The path and query of the stream's URL, which both requests name.</param>
    /// <exception cref="HttpTunnelException">The GET's answer was not 200 (its status), or no HTTP answer at all.</exception>
    public static async Task<HttpTunnel> OpenAsync(Stream get, Stream post, string host, string path, CancellationToken token)
    {
        string cookie = Cookie();
        string common = $"x-sessioncookie: {cookie}\r\nHost: {host}\r\nUser-Agent: ManagedCodecs\r\nPragma: no-cache\r\nCache-Control: no-cache\r\n";
        var request = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.0\r\n{common}Accept: application/x-rtsp-tunnelled\r\n\r\n");
        await get.WriteAsync(request, 0, request.Length, token).ConfigureAwait(false);
        int status = await ReadStatusAsync(get, token).ConfigureAwait(false);
        if (status != 200)
        {
            throw new HttpTunnelException(status, $"The camera answered {status} to the HTTP tunnel's GET.");
        }

        var head = Encoding.ASCII.GetBytes($"POST {path} HTTP/1.0\r\n{common}Content-Type: application/x-rtsp-tunnelled\r\nContent-Length: {PostLength}\r\nExpires: Sun, 9 Jan 1972 00:00:00 GMT\r\n\r\n");
        await post.WriteAsync(head, 0, head.Length, token).ConfigureAwait(false);
        return new HttpTunnel(get, post);
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => _get.Read(buffer, offset, count);

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _get.ReadAsync(buffer, offset, count, cancellationToken);

    // Each write in base64 of its own, padded: an RTSP request, or an interleaved RTCP report, whole. FFmpeg's and
    // live555's clients send theirs so, and servers decode them so.
    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        var encoded = Encoding.ASCII.GetBytes(Convert.ToBase64String(buffer, offset, count));
        _post.Write(encoded, 0, encoded.Length);
    }

    /// <inheritdoc/>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var encoded = Encoding.ASCII.GetBytes(Convert.ToBase64String(buffer, offset, count));
        return _post.WriteAsync(encoded, 0, encoded.Length, cancellationToken);
    }

    /// <inheritdoc/>
    public override void Flush() => _post.Flush();

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _get.Dispose();
            _post.Dispose();
        }

        base.Dispose(disposing);
    }

    // 22 characters, as QuickTime's: letters and digits from 16 random bytes.
    private static string Cookie()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var bytes = new byte[22];
        using (var random = RandomNumberGenerator.Create())
        {
            random.GetBytes(bytes);
        }

        var cookie = new StringBuilder(22);
        foreach (byte b in bytes)
        {
            cookie.Append(alphabet[b % alphabet.Length]);
        }

        return cookie.ToString();
    }

    // The answer's status code, its head read to its blank line a byte at a time, so that nothing after it is taken.
    private static async Task<int> ReadStatusAsync(Stream stream, CancellationToken token)
    {
        var head = new List<byte>();
        var one = new byte[1];
        while (head.Count < 4 || head[^1] != '\n' || head[^2] != '\r' || head[^3] != '\n' || head[^4] != '\r')
        {
            if (head.Count >= MaxHead || await stream.ReadAsync(one, 0, 1, token).ConfigureAwait(false) == 0)
            {
                throw new HttpTunnelException(0, "The camera sent no HTTP answer to the tunnel's GET.");
            }

            head.Add(one[0]);
        }

        var line = Encoding.ASCII.GetString(head.ToArray()).Split('\n')[0].Trim().Split(' ');
        return line.Length >= 2 && line[0].StartsWith("HTTP/", StringComparison.Ordinal)
            && int.TryParse(line[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int status)
            ? status
            : throw new HttpTunnelException(0, "The camera's answer to the tunnel's GET is not HTTP.");
    }
}

/// <summary>The HTTP tunnel's GET was refused (<see cref="Status"/>), or answered with something other than HTTP (0).</summary>
internal sealed class HttpTunnelException(int status, string message) : IOException(message)
{
    public int Status { get; } = status;
}
