using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ManagedCodecs.Tests.Playback;

/// <summary>A request the test server received.</summary>
internal sealed record RtspRequest(int Connection, string Method, string Url, Dictionary<string, string> Headers);

/// <summary>
/// An RTSP 1.0 server on the loopback interface, for LivePlayer's RTSP tests: OPTIONS, DESCRIBE (<see cref="Sdp"/>),
/// SETUP (RTP over the connection, on the channels asked for: 0 and 1, and 2 and 3 for a second track), PLAY, keep-alives, with Basic or Digest authentication checked
/// by its own MD5 (not the client's code), and a status of a test's choosing for any method. After PLAY,
/// <see cref="OnPlay"/> sends the packets while the server goes on answering requests.
/// </summary>
internal sealed class RtspTestServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly UdpClient _rtp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly UdpClient _rtcp = new(new IPEndPoint(IPAddress.Loopback, 0));

    // A group's packets, from another address than the RTSP connection's, as a camera's (mediamtx's) come from
    // whichever of its addresses faces the network.
    private readonly UdpClient _groupRtp = new(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
    private readonly UdpClient _groupRtcp = new(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;
    private int _connections;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ByteReader>> _tunnels = new();

    public RtspTestServer(string sdp, Func<RtspPlay, Task> onPlay)
    {
        Sdp = sdp;
        OnPlay = onPlay;
        _listener.Start();
        _accepting = AcceptAsync();
        _ = RecordPunchesAsync(_rtp, "rtp");
        _ = RecordPunchesAsync(_rtcp, "rtcp");
    }

    /// <summary>
    /// RTP over UDP only: SETUP asking for RTP over the RTSP connection is answered 461, as mediamtx answers it with
    /// <c>rtspTransports: [udp]</c>. A SETUP asking for UDP is accepted either way.
    /// </summary>
    public bool UdpOnly { get; set; }

    /// <summary>
    /// Answer 461 to every SETUP but one for multicast, as mediamtx set to multicast only does, and send each track to
    /// a group of its own from this address on (the next track's is the next address), all on <see cref="MulticastPort"/>.
    /// </summary>
    public IPAddress? MulticastGroup { get; set; }

    /// <summary>The groups' RTP port; RTCP is the next.</summary>
    public int MulticastPort { get; set; }

    /// <summary>With <see cref="MulticastGroup"/>: leave the group and port out of SETUP's answer.</summary>
    public bool MulticastUnnamed { get; set; }

    /// <summary>
    /// RTSP over HTTP only (Apple's tunnelling): each connection starts with an HTTP GET or POST; a GET is answered 200 and
    /// carries what the server sends, and the POST with its cookie carries the requests, in base64.
    /// </summary>
    public bool HttpTunnel { get; set; }

    /// <summary>With <see cref="HttpTunnel"/>, the status to refuse the GET with, or null to take it.</summary>
    public int? TunnelStatus { get; set; }

    /// <summary>With <see cref="HttpTunnel"/>, each connection's HTTP request: its method, path and headers.</summary>
    public ConcurrentQueue<RtspRequest> TunnelRequests { get; } = new();

    /// <summary>With <see cref="HttpTunnel"/>, the POSTs' bodies as they came, base64 still.</summary>
    public ConcurrentQueue<string> TunnelBodies { get; } = new();

    /// <summary>The SSRC SETUP's answer gives for UDP, or null to give none.</summary>
    public uint? Ssrc { get; set; }

    /// <summary>The datagrams that reached the server's UDP ports ("rtp" or "rtcp"): the client's punch packets.</summary>
    public ConcurrentQueue<(string Port, byte[] Data)> Punches { get; } = new();

    public IPEndPoint RtpEndPoint => (IPEndPoint)_rtp.Client.LocalEndPoint!;

    public string Sdp { get; set; }

    public Func<RtspPlay, Task> OnPlay { get; }

    /// <summary>The credentials asked for, or null for none.</summary>
    public NetworkCredential? Credential { get; set; }

    /// <summary>Digest (with qop=auth when <see cref="Qop"/>) rather than Basic.</summary>
    public bool Digest { get; set; } = true;

    public bool Qop { get; set; } = true;

    public int SessionTimeout { get; set; } = 60;

    /// <summary>A status (and reason) to answer a method with in place of the usual answer, by method and connection.</summary>
    public Func<string, int, (int Status, string Reason)?>? Status { get; set; }

    /// <summary>A status (and reason) to answer a request with in place of the usual answer (the audio track's SETUP).</summary>
    public Func<RtspRequest, (int Status, string Reason)?>? StatusOf { get; set; }

    public ConcurrentQueue<RtspRequest> Requests { get; } = new();

    /// <summary>
    /// The packets the client sent interleaved on a connection, by channel: its RTCP receiver reports. (Over UDP they
    /// reach the server's RTCP port: <see cref="Punches"/>.)
    /// </summary>
    public ConcurrentQueue<(int Channel, byte[] Data)> Interleaved { get; } = new();

    public Uri Uri => new($"{(Certificate is null ? "rtsp://127.0.0.1" : "rtsps://localhost")}:{((IPEndPoint)_listener.LocalEndpoint).Port}/stream");

    /// <summary>Serve RTSPS: each connection over TLS with this certificate (and its private key).</summary>
    public System.Security.Cryptography.X509Certificates.X509Certificate2? Certificate { get; set; }

    public int Connections => Volatile.Read(ref _connections);

    /// <summary>A recording's SDP with a control line for its track (FFmpeg's has none).</summary>
    public static string SdpOf(string recording, string control = "trackID=1") =>
        Regex.Replace(File.ReadAllText(TestSamples.PathOf(recording + ".sdp")), @"(m=video[^\r\n]*\r?\n)", $"$1a=control:{control}\r\n");

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _rtp.Dispose();
        _rtcp.Dispose();
        _groupRtp.Dispose();
        _groupRtcp.Dispose();
        try
        {
            _accepting.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }
    }

    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "The test server checks RFC 2617 Digest answers, which are defined over MD5.")]
    private static string Md5(string text) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));

    private async Task RecordPunchesAsync(UdpClient socket, string port)
    {
        try
        {
            while (true)
            {
                var received = await socket.ReceiveAsync(_stop.Token);
                Punches.Enqueue((port, received.Buffer));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task AcceptAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            int index = Interlocked.Increment(ref _connections) - 1;
            _ = Task.Run(() => ServeAsync(client, index, token));
        }
    }

    private async Task ServeAsync(TcpClient client, int index, CancellationToken token)
    {
        using (client)
        {
            Stream stream = client.GetStream();
            if (Certificate is { } certificate)
            {
                var tls = new System.Net.Security.SslStream(stream);
                try
                {
                    await tls.AuthenticateAsServerAsync(certificate);
                }
                catch (Exception e) when (e is System.Security.Authentication.AuthenticationException or IOException)
                {
                    return; // the client refused the certificate
                }

                stream = tls;
            }

            var writing = new SemaphoreSlim(1, 1);
            var reader = new ByteReader(stream);
            if (HttpTunnel)
            {
                var tunnelled = await TunnelAsync(stream, reader, index, token);
                if (tunnelled is null)
                {
                    return; // refused, or the POST's connection, which tunnelled its requests to the GET's
                }

                reader = tunnelled;
            }

            Task? playing = null;
            var udpClients = new List<IPEndPoint>(); // a track's each, in SETUP's order
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(token);
            try
            {
                while (await ReadRequestAsync(reader, index, Interleaved, connection.Token) is { } request)
                {
                    Requests.Enqueue(request);
                    string cseq = request.Headers.GetValueOrDefault("CSeq", "0");
                    var (status, reason, headers, body) = Answer(request, index, udpClients.Count);
                    if (request.Method == "SETUP" && status == 200 && MulticastGroup is { } group)
                    {
                        // Sent on the loopback interface, where the client joins for a connection to this machine.
                        _groupRtp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.Loopback.GetAddressBytes());
                        _groupRtcp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.Loopback.GetAddressBytes());
                        udpClients.Add(new IPEndPoint(GroupOf(group, udpClients.Count), MulticastPort));
                    }
                    else if (request.Method == "SETUP" && status == 200 && ClientPort(request) is { } port)
                    {
                        udpClients.Add(new IPEndPoint(IPAddress.Loopback, port));
                    }

                    var head = new StringBuilder($"RTSP/1.0 {status} {reason}\r\nCSeq: {cseq}\r\n");
                    foreach (var (name, value) in headers)
                    {
                        head.Append(name).Append(": ").Append(value).Append("\r\n");
                    }

                    var bodyBytes = Encoding.UTF8.GetBytes(body);
                    if (bodyBytes.Length > 0)
                    {
                        head.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
                    }

                    head.Append("\r\n");
                    await writing.WaitAsync(connection.Token);
                    try
                    {
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(head.ToString()).Concat(bodyBytes).ToArray(), connection.Token);
                    }
                    finally
                    {
                        writing.Release();
                    }

                    if (request.Method == "PLAY" && status == 200 && playing is null)
                    {
                        var play = new RtspPlay(stream, writing, index, connection.Token, udpClients.Count == 0 ? null : MulticastGroup is null ? (_rtp, _rtcp, udpClients.ToArray()) : (_groupRtp, _groupRtcp, udpClients.ToArray()));
                        playing = Task.Run(async () =>
                        {
                            try
                            {
                                await OnPlay(play);
                            }
                            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
                            {
                            }

                            if (play.CloseAfter)
                            {
                                client.Close();
                            }
                        });
                    }
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The client closed the connection, or the test ended.
            }
            finally
            {
                connection.Cancel();
            }
        }
    }

    private static IPAddress GroupOf(IPAddress first, int track)
    {
        var bytes = first.GetAddressBytes();
        bytes[3] += (byte)track;
        return new IPAddress(bytes);
    }

    private (int Status, string Reason, List<(string, string)> Headers, string Body) Answer(RtspRequest request, int index, int tracks)
    {
        if (Status?.Invoke(request.Method, index) is { } forced)
        {
            return (forced.Status, forced.Reason, [], "");
        }

        if (StatusOf?.Invoke(request) is { } chosen)
        {
            return (chosen.Status, chosen.Reason, [], "");
        }

        if (Credential is not null && !Authorized(request))
        {
            string challenge = Digest
                ? $"Digest realm=\"camera\", nonce=\"n{index}x\"{(Qop ? ", qop=\"auth\"" : "")}, opaque=\"op\""
                : "Basic realm=\"camera\"";
            return (401, "Unauthorized", [("WWW-Authenticate", challenge)], "");
        }

        return request.Method switch
        {
            "OPTIONS" => (200, "OK", [("Public", "OPTIONS, DESCRIBE, SETUP, PLAY, GET_PARAMETER, TEARDOWN")], ""),
            "DESCRIBE" => (200, "OK", [("Content-Base", Uri + "/"), ("Content-Type", "application/sdp")], Sdp),
            "SETUP" when MulticastGroup is { } group => request.Headers.GetValueOrDefault("Transport", "").Contains("multicast", StringComparison.Ordinal)
                ? (200, "OK", [("Session", $"S{index}ession;timeout={SessionTimeout}"), ("Transport", MulticastUnnamed ? "RTP/AVP;multicast" : $"RTP/AVP;multicast;destination={GroupOf(group, tracks)};port={MulticastPort}-{MulticastPort + 1};ttl=1{(Ssrc is { } stream ? $";ssrc={stream:X8}" : "")}")], "")
                : (461, "Unsupported Transport", [], ""),
            "SETUP" when ClientPort(request) is { } port =>
                (200, "OK", [("Session", $"S{index}ession;timeout={SessionTimeout}"), ("Transport", $"RTP/AVP;unicast;client_port={port}-{port + 1};server_port={RtpEndPoint.Port}-{((IPEndPoint)_rtcp.Client.LocalEndPoint!).Port}{(Ssrc is { } ssrc ? $";ssrc={ssrc:X8}" : "")}")], ""),
            "SETUP" when UdpOnly => (461, "Unsupported Transport", [], ""),
            "SETUP" => (200, "OK", [("Session", $"S{index}ession;timeout={SessionTimeout}"), ("Transport", "RTP/AVP/TCP;unicast;interleaved=" + (Regex.Match(request.Headers.GetValueOrDefault("Transport", ""), @"interleaved=(\d+-\d+)") is { Success: true } asked ? asked.Groups[1].Value : "0-1"))], ""),
            _ => (200, "OK", [], ""),
        };
    }

    // The client's RTP port when SETUP asks for RTP over UDP (client_port=), else null.
    private static int? ClientPort(RtspRequest request) =>
        request.Method == "SETUP" && request.Headers.TryGetValue("Transport", out var transport)
            && Regex.Match(transport, @"client_port=(\d+)") is { Success: true } match
            ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : null;

    private bool Authorized(RtspRequest request)
    {
        if (!request.Headers.TryGetValue("Authorization", out var header))
        {
            return false;
        }

        if (!Digest)
        {
            return header == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Credential!.UserName}:{Credential.Password}"));
        }

        var fields = Regex.Matches(header, @"(\w+)=(""[^""]*""|[^,\s]+)").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim('"'));
        string ha1 = Md5($"{Credential!.UserName}:camera:{Credential.Password}");
        string ha2 = Md5($"{request.Method}:{fields.GetValueOrDefault("uri")}");
        string expected = Qop
            ? Md5($"{ha1}:{fields.GetValueOrDefault("nonce")}:{fields.GetValueOrDefault("nc")}:{fields.GetValueOrDefault("cnonce")}:auth:{ha2}")
            : Md5($"{ha1}:{fields.GetValueOrDefault("nonce")}:{ha2}");
        return fields.GetValueOrDefault("username") == Credential.UserName && fields.GetValueOrDefault("uri") == request.Url
            && fields.GetValueOrDefault("response") == expected && fields.GetValueOrDefault("opaque") == "op";
    }

    // The next request; the packets the client interleaves before it ('$', channel, length, data) go to interleaved.
    private static async Task<RtspRequest?> ReadRequestAsync(ByteReader reader, int index, ConcurrentQueue<(int Channel, byte[] Data)> interleaved, CancellationToken token)
    {
        string? line;
        while (true)
        {
            int first = await reader.PeekAsync(token);
            if (first < 0)
            {
                return null;
            }

            if (first == '$')
            {
                var head = await reader.ReadAsync(4, token);
                var data = head is null ? null : await reader.ReadAsync((head[2] << 8) | head[3], token);
                if (data is null)
                {
                    return null;
                }

                interleaved.Enqueue((head![1], data));
                continue;
            }

            line = await reader.ReadLineAsync(token);
            if (line is null)
            {
                return null;
            }

            if (line.Length > 0)
            {
                break;
            }
        }

        var parts = line.Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(token)))
        {
            int colon = line.IndexOf(':');
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        return new RtspRequest(index, parts[0], parts[1], headers);
    }

    // The HTTP request a tunnelled connection starts with. A GET is answered (200, or TunnelStatus), and once its POST
    // has come, the requests' reader is returned; a POST hands its decoded body to its GET's connection and waits.
    private async Task<ByteReader?> TunnelAsync(Stream stream, ByteReader reader, int index, CancellationToken token)
    {
        if (await ReadRequestAsync(reader, index, Interleaved, token) is not { } request)
        {
            return null;
        }

        TunnelRequests.Enqueue(request);
        var waiting = _tunnels.GetOrAdd(request.Headers.GetValueOrDefault("x-sessioncookie", ""), _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        if (request.Method == "POST")
        {
            waiting.TrySetResult(new ByteReader(new Base64Reader(reader, TunnelBodies)));
            await Task.Delay(Timeout.Infinite, token).ContinueWith(_ => { }, TaskScheduler.Default);
            return null;
        }

        string answer = TunnelStatus is { } refused
            ? $"HTTP/1.0 {refused} Refused\r\n\r\n"
            : "HTTP/1.0 200 OK\r\nContent-Type: application/x-rtsp-tunnelled\r\nCache-Control: no-cache\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(answer), token);
        return TunnelStatus is null ? await waiting.Task.WaitAsync(token) : null;
    }

    // A POST's body as the bytes it encodes: base64 in groups of four characters, each write padded on its own.
    private sealed class Base64Reader(ByteReader reader, ConcurrentQueue<string> bodies) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var quad = await reader.ReadAsync(4, cancellationToken);
            if (quad is null)
            {
                return 0;
            }

            string text = Encoding.ASCII.GetString(quad);
            bodies.Enqueue(text);
            var bytes = Convert.FromBase64String(text);
            bytes.CopyTo(buffer);
            return bytes.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Lines and counted bytes from one stream (a StreamReader would decode the interleaved packets as text).
    private sealed class ByteReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[4096];
        private int _start;
        private int _end;

        public async Task<int> PeekAsync(CancellationToken token)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await stream.ReadAsync(_buffer, token);
                if (_end == 0)
                {
                    return -1;
                }
            }

            return _buffer[_start];
        }

        public async Task<byte[]?> ReadAsync(int count, CancellationToken token)
        {
            var bytes = new byte[count];
            for (int i = 0; i < count; i++)
            {
                int next = await PeekAsync(token);
                if (next < 0)
                {
                    return null;
                }

                bytes[i] = (byte)next;
                _start++;
            }

            return bytes;
        }

        public async Task<string?> ReadLineAsync(CancellationToken token)
        {
            var line = new List<byte>();
            while (true)
            {
                int next = await PeekAsync(token);
                if (next < 0)
                {
                    return line.Count == 0 ? null : Encoding.UTF8.GetString([.. line]);
                }

                _start++;
                if (next == '\n')
                {
                    return Encoding.UTF8.GetString([.. line]).TrimEnd('\r');
                }

                line.Add((byte)next);
            }
        }
    }
}

/// <summary>What a test's <see cref="RtspTestServer.OnPlay"/> sends with: packets interleaved on the connection.</summary>
internal sealed class RtspPlay(Stream stream, SemaphoreSlim writing, int connection, CancellationToken token, (UdpClient Socket, UdpClient Rtcp, IPEndPoint[] Clients)? udp = null)
{
    /// <summary>Whether SETUP chose RTP over UDP: <see cref="SendAsync"/> then sends from the server's RTP port.</summary>
    public bool Udp => udp is not null;

    /// <summary>Where the client receives RTP over UDP.</summary>
    public IPEndPoint? UdpClient => udp?.Clients[0];

    public int Connection { get; } = connection;

    public CancellationToken Token { get; } = token;

    /// <summary>Close the connection once OnPlay returns (a camera that drops the stream).</summary>
    public bool CloseAfter { get; set; }

    // Over UDP, a channel's packets go to its track's port: channel 0 to the first SETUP's, channel 2 to the second's,
    // and an odd channel's (RTCP) from the server's RTCP port to the port after the track's.
    public async Task SendAsync(byte[] packet, int channel = 0)
    {
        if (udp is { } target)
        {
            var client = target.Clients[channel / 2];
            if (channel % 2 == 1)
            {
                await target.Rtcp.SendAsync(packet, new IPEndPoint(client.Address, client.Port + 1), Token);
            }
            else
            {
                await target.Socket.SendAsync(packet, client, Token);
            }

            return;
        }

        await writing.WaitAsync(Token);
        try
        {
            await stream.WriteAsync((byte[])[(byte)'$', (byte)channel, (byte)(packet.Length >> 8), (byte)packet.Length, .. packet], Token);
        }
        finally
        {
            writing.Release();
        }
    }
}
