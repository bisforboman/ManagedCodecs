using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ManagedCodecs.Playback.Rtsp;

/// <summary>
/// An RTSP 1.0 connection (RFC 2326): requests out, and in, their responses and the RTP and RTCP packets interleaved
/// on the same connection (10.12: <c>$</c>, a channel byte, a 16-bit length, then the packet). Answers a 401 once per
/// request with the camera's challenge (<see cref="RtspAuthenticator"/>), then keeps sending that authorization.
/// </summary>
internal sealed class RtspConnection : IDisposable
{
    // Longer than any RTSP response head a camera sends; a body longer than MaxBody is not an SDP.
    private const int MaxHead = 64 * 1024;
    private const int MaxBody = 1024 * 1024;

    private readonly TcpClient _tcp;
    private readonly Stream _stream;
    private readonly NetworkCredential? _credential;
    private readonly SemaphoreSlim _writing = new(1, 1);
    private RtspAuthenticator? _authenticator;
    private byte[] _buffer = new byte[64 * 1024];
    private int _start;
    private int _end;
    private int _sequence;

    private RtspConnection(TcpClient tcp, NetworkCredential? credential)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _credential = credential;
    }

    /// <summary>The session from SETUP's answer, sent with every later request.</summary>
    public string? Session { get; set; }

    public static async Task<RtspConnection> ConnectAsync(string host, int port, NetworkCredential? credential, CancellationToken token)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            // netstandard2.1 has no ConnectAsync with a token: closing the client ends the attempt.
            using (token.Register(tcp.Dispose))
            {
                await tcp.ConnectAsync(host, port).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            return new RtspConnection(tcp, credential);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Sends a request and reads until its response (packets interleaved before it are dropped). A 401 is answered once
    /// with the challenge's authorization, when there are credentials; its second 401 is returned.
    /// </summary>
    public async Task<RtspMessage> RequestAsync(string method, string url, IEnumerable<KeyValuePair<string, string>> headers, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            int sequence = await SendAsync(method, url, headers, token).ConfigureAwait(false);
            RtspMessage response;
            do
            {
                response = await ReadAsync(token).ConfigureAwait(false)
                    ?? throw new EndOfStreamException($"The server closed the connection before answering {method}.");
            }
            while (response.IsPacket || response.Sequence != sequence);

            if (response.Status == 401 && attempt == 0 && _credential is not null
                && RtspAuthenticator.From(response.HeaderValues("WWW-Authenticate"), _credential) is { } authenticator)
            {
                _authenticator = authenticator;
                continue;
            }

            return response;
        }
    }

    /// <summary>Sends a request without waiting for its response (a keep-alive, TEARDOWN); returns its CSeq.</summary>
    public async Task<int> SendAsync(string method, string url, IEnumerable<KeyValuePair<string, string>> headers, CancellationToken token)
    {
        await _writing.WaitAsync(token).ConfigureAwait(false);
        try
        {
            int sequence = ++_sequence;
            var request = new StringBuilder();
            request.Append(method).Append(' ').Append(url).Append(" RTSP/1.0\r\n");
            request.Append("CSeq: ").Append(sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("\r\n");
            request.Append("User-Agent: ManagedCodecs\r\n");
            if (_authenticator is not null)
            {
                request.Append("Authorization: ").Append(_authenticator.Header(method, url)).Append("\r\n");
            }

            if (Session is not null)
            {
                request.Append("Session: ").Append(Session).Append("\r\n");
            }

            foreach (var header in headers)
            {
                request.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }

            request.Append("\r\n");
            var bytes = Encoding.UTF8.GetBytes(request.ToString());
            await _stream.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
            return sequence;
        }
        finally
        {
            _writing.Release();
        }
    }

    /// <summary>The next message: an interleaved packet or a response; null at the end of the connection.</summary>
    /// <exception cref="InvalidDataException">Neither: a head or a body too long, or no status line.</exception>
    public async Task<RtspMessage?> ReadAsync(CancellationToken token)
    {
        // Line ends between messages, which some servers send, are skipped.
        while (true)
        {
            if (_start == _end && !await FillAsync(token).ConfigureAwait(false))
            {
                return null;
            }

            if (_buffer[_start] is (byte)'\r' or (byte)'\n')
            {
                _start++;
                continue;
            }

            break;
        }

        if (_buffer[_start] == '$')
        {
            if (!await EnsureAsync(4, token).ConfigureAwait(false))
            {
                return null;
            }

            int channel = _buffer[_start + 1];
            int length = (_buffer[_start + 2] << 8) | _buffer[_start + 3];
            if (!await EnsureAsync(4 + length, token).ConfigureAwait(false))
            {
                return null;
            }

            var packet = _buffer.AsSpan(_start + 4, length).ToArray();
            _start += 4 + length;
            return RtspMessage.Packet(channel, packet);
        }

        int headEnd;
        while ((headEnd = HeadEnd()) < 0)
        {
            if (_end - _start > MaxHead)
            {
                throw new InvalidDataException("An RTSP message head longer than any server sends.");
            }

            if (!await FillAsync(token).ConfigureAwait(false))
            {
                return null;
            }
        }

        var lines = Encoding.UTF8.GetString(_buffer, _start, headEnd - _start).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        int bodyStart = headEnd + (_buffer[headEnd] == '\r' ? 4 : 2);
        var status = lines[0].Split(new[] { ' ' }, 3);
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers.Add(new KeyValuePair<string, string>(line.Substring(0, colon).Trim(), line.Substring(colon + 1).Trim()));
            }
        }

        int bodyLength = 0;
        var contentLength = headers.FirstOrDefault(h => h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)).Value;
        if (contentLength is not null && (!int.TryParse(contentLength, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out bodyLength) || bodyLength > MaxBody))
        {
            throw new InvalidDataException($"An RTSP body of {contentLength} bytes.");
        }

        _start = bodyStart;
        if (!await EnsureAsync(bodyLength, token).ConfigureAwait(false))
        {
            return null;
        }

        var body = Encoding.UTF8.GetString(_buffer, _start, bodyLength);
        _start += bodyLength;

        // A request from the server (rare: ANNOUNCE, a server's OPTIONS) has no status: kept as status 0, ignored.
        int code = status.Length >= 2 && status[0].StartsWith("RTSP/", StringComparison.Ordinal)
            && int.TryParse(status[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 0;
        return RtspMessage.Response(code, status.Length >= 3 ? status[2] : "", headers, body);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _tcp.Dispose();
        _writing.Dispose();
    }

    // Where the head ends (its blank line's first byte), or -1 when it isn't all read yet.
    private int HeadEnd()
    {
        for (int i = _start; i + 1 < _end; i++)
        {
            if (_buffer[i] == '\n' && _buffer[i + 1] == '\n')
            {
                return i;
            }

            if (i + 3 < _end && _buffer[i] == '\r' && _buffer[i + 1] == '\n' && _buffer[i + 2] == '\r' && _buffer[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private async Task<bool> EnsureAsync(int count, CancellationToken token)
    {
        while (_end - _start < count)
        {
            if (!await FillAsync(token).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> FillAsync(CancellationToken token)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        if (_end == _buffer.Length)
        {
            Array.Resize(ref _buffer, _buffer.Length * 2);
        }

        int read = await _stream.ReadAsync(_buffer, _end, _buffer.Length - _end, token).ConfigureAwait(false);
        _end += read;
        return read > 0;
    }
}

/// <summary>An interleaved packet, or a response (status 0: a request from the server).</summary>
internal sealed class RtspMessage
{
    private RtspMessage(int channel, byte[] packet, int status, string reason, List<KeyValuePair<string, string>> headers, string body)
    {
        Channel = channel;
        Data = packet;
        Status = status;
        Reason = reason;
        Headers = headers;
        Body = body;
    }

    public bool IsPacket => Channel >= 0;

    public int Channel { get; }

    public byte[] Data { get; }

    public int Status { get; }

    public string Reason { get; }

    public List<KeyValuePair<string, string>> Headers { get; }

    public string Body { get; }

    /// <summary>The response's CSeq, or -1.</summary>
    public int Sequence => int.TryParse(Header("CSeq"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int sequence) ? sequence : -1;

    public static RtspMessage Packet(int channel, byte[] data) => new(channel, data, 0, "", [], "");

    public static RtspMessage Response(int status, string reason, List<KeyValuePair<string, string>> headers, string body) =>
        new(-1, [], status, reason, headers, body);

    /// <summary>The first header of the name (any case), or null.</summary>
    public string? Header(string name) => Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    public IEnumerable<string> HeaderValues(string name) =>
        Headers.Where(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value);
}
