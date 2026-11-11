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
/// SETUP (RTP over the connection, channels 0 and 1), PLAY, keep-alives, with Basic or Digest authentication checked
/// by its own MD5 (not the client's code), and a status of a test's choosing for any method. After PLAY,
/// <see cref="OnPlay"/> sends the packets while the server goes on answering requests.
/// </summary>
internal sealed class RtspTestServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;
    private int _connections;

    public RtspTestServer(string sdp, Func<RtspPlay, Task> onPlay)
    {
        Sdp = sdp;
        OnPlay = onPlay;
        _listener.Start();
        _accepting = AcceptAsync();
    }

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

    public ConcurrentQueue<RtspRequest> Requests { get; } = new();

    public Uri Uri => new($"rtsp://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/stream");

    public int Connections => Volatile.Read(ref _connections);

    /// <summary>A recording's SDP with a control line for its track (FFmpeg's has none).</summary>
    public static string SdpOf(string recording, string control = "trackID=1") =>
        Regex.Replace(File.ReadAllText(TestSamples.PathOf(recording + ".sdp")), @"(m=video[^\r\n]*\r?\n)", $"$1a=control:{control}\r\n");

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
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
            var stream = client.GetStream();
            var writing = new SemaphoreSlim(1, 1);
            var reader = new StreamReader(stream, Encoding.UTF8);
            Task? playing = null;
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(token);
            try
            {
                while (await ReadRequestAsync(reader, index, connection.Token) is { } request)
                {
                    Requests.Enqueue(request);
                    string cseq = request.Headers.GetValueOrDefault("CSeq", "0");
                    var (status, reason, headers, body) = Answer(request, index);
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
                        var play = new RtspPlay(stream, writing, index, connection.Token);
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

    private (int Status, string Reason, List<(string, string)> Headers, string Body) Answer(RtspRequest request, int index)
    {
        if (Status?.Invoke(request.Method, index) is { } forced)
        {
            return (forced.Status, forced.Reason, [], "");
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
            "SETUP" => (200, "OK", [("Session", $"S{index}ession;timeout={SessionTimeout}"), ("Transport", "RTP/AVP/TCP;unicast;interleaved=0-1")], ""),
            _ => (200, "OK", [], ""),
        };
    }

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

    private static async Task<RtspRequest?> ReadRequestAsync(StreamReader reader, int index, CancellationToken token)
    {
        string? line;
        do
        {
            line = await reader.ReadLineAsync(token);
            if (line is null)
            {
                return null;
            }
        }
        while (line.Length == 0);

        var parts = line.Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(token)))
        {
            int colon = line.IndexOf(':');
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        return new RtspRequest(index, parts[0], parts[1], headers);
    }
}

/// <summary>What a test's <see cref="RtspTestServer.OnPlay"/> sends with: packets interleaved on the connection.</summary>
internal sealed class RtspPlay(Stream stream, SemaphoreSlim writing, int connection, CancellationToken token)
{
    public int Connection { get; } = connection;

    public CancellationToken Token { get; } = token;

    /// <summary>Close the connection once OnPlay returns (a camera that drops the stream).</summary>
    public bool CloseAfter { get; set; }

    public async Task SendAsync(byte[] packet, int channel = 0)
    {
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
