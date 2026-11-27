using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ManagedCodecs.Tests.Playback;

/// <summary>How a simulated ONVIF device authenticates its media service's calls.</summary>
public enum OnvifAuth
{
    /// <summary>None asked for.</summary>
    None,

    /// <summary>HTTP Digest (RFC 2617, qop=auth), as ONVIF Core 5.9.1 makes the rule.</summary>
    Digest,

    /// <summary>The WS-Security UsernameToken only: 400 and a NotAuthorized fault without a valid one (legacy devices).</summary>
    UsernameToken,
}

/// <summary>
/// A simulated ONVIF camera, written from the specifications independently of the library's code: a WS-Discovery
/// responder (on a port of its own, or joined to the multicast group) and an HTTP server answering GetServices,
/// GetCapabilities, GetSystemDateAndTime, GetProfiles and GetStreamUri. Its XML uses other prefixes than the library's
/// (as devices' do), and its clock can be set off.
/// </summary>
public sealed class OnvifTestDevice : IDisposable
{
    public const string User = "admin";
    public const string Password = "Secret 42";

    private static readonly Regex DigestParameter = new(@"([a-z]+)=(""[^""]*""|[^,\s]*)", RegexOptions.CultureInvariant);

    private readonly Socket _udp;
    private readonly TcpListener _http;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _udpLoop;
    private readonly Task _httpLoop;
    private readonly string _nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <param name="multicast">Joins 239.255.255.250:3702 on loopback (shared, with address reuse) instead of a port of its own.</param>
    public OnvifTestDevice(bool multicast = false)
    {
        _http = new TcpListener(IPAddress.Loopback, 0);
        _http.Start();
        _udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (multicast)
        {
            _udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Bind(new IPEndPoint(IPAddress.Any, 3702));
            _udp.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(IPAddress.Parse("239.255.255.250"), IPAddress.Loopback));
        }
        else
        {
            _udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        }

        _udpLoop = Task.Run(UdpLoopAsync);
        _httpLoop = Task.Run(HttpLoopAsync);
    }

    /// <summary>The discovery responder's address (for a unicast probe).</summary>
    public IPEndPoint DiscoveryEndPoint => new(IPAddress.Loopback, ((IPEndPoint)_udp.LocalEndPoint!).Port);

    /// <summary>The device service's URL.</summary>
    public Uri DeviceService => new($"http://127.0.0.1:{((IPEndPoint)_http.LocalEndpoint).Port}/onvif/device_service");

    public string Endpoint { get; init; } = "urn:uuid:" + Guid.NewGuid();

    public string[] Types { get; init; } = ["dn:NetworkVideoTransmitter", "tds:Device"];

    public string[] Scopes { get; init; } =
    [
        "onvif://www.onvif.org/type/video_encoder",
        "onvif://www.onvif.org/Profile/Streaming",
        "onvif://www.onvif.org/name/Front%20door",
        "onvif://www.onvif.org/hardware/IPC-1234",
        "onvif://www.onvif.org/location/country/sweden",
    ];

    /// <summary>Extra addresses listed after the device service's own in XAddrs.</summary>
    public string[] ExtraAddresses { get; init; } = [];

    /// <summary>Answers every probe, whatever types it names (as some printers and PCs do, against WS-Discovery 5.1).</summary>
    public bool AnswersAnyProbe { get; init; }

    /// <summary>Lists the device service's own address after <see cref="ExtraAddresses"/> in XAddrs.</summary>
    public bool OwnAddressLast { get; init; }

    /// <summary>Answers a probe with this message ID in RelatesTo instead of the probe's.</summary>
    public string? WrongRelatesTo { get; init; }

    /// <summary>Also sends a datagram that isn't XML before each answer.</summary>
    public bool SendsGarbage { get; init; }

    public OnvifAuth Auth { get; init; } = OnvifAuth.Digest;

    /// <summary>The device's clock minus the real one.</summary>
    public TimeSpan ClockOffset { get; init; }

    /// <summary>GetServices answers with a fault (an older device): the media service is found by GetCapabilities.</summary>
    public bool NoGetServices { get; init; }

    /// <summary>The media service's path (on the device service's host and port).</summary>
    public string MediaPath { get; init; } = "/onvif/media_service";

    /// <summary>The probes received, by message ID.</summary>
    public List<string> ProbeIds { get; } = [];

    /// <summary>The SOAP actions received over HTTP, in order.</summary>
    public List<string> Calls { get; } = [];

    public void Dispose()
    {
        _stop.Cancel();
        _udp.Dispose();
        _http.Stop();
        try
        {
            Task.WaitAll([_udpLoop, _httpLoop], TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The loops end on the closed sockets.
        }

        _stop.Dispose();
    }

    private async Task UdpLoopAsync()
    {
        var buffer = new byte[65536];
        while (!_stop.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _udp.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _stop.Token);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                return;
            }

            XDocument probe;
            try
            {
                probe = XDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.ReceivedBytes));
            }
            catch (System.Xml.XmlException)
            {
                continue;
            }

            XNamespace a = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
            XNamespace d = "http://schemas.xmlsoap.org/ws/2005/04/discovery";
            string id = probe.Descendants(a + "MessageID").Single().Value;
            var types = probe.Descendants(d + "Types").SingleOrDefault();
            lock (ProbeIds)
            {
                ProbeIds.Add(id);
            }

            // WS-Discovery 5.1: a target matches only if it has every type the probe names.
            if (types is not null && !AnswersAnyProbe && !Matches(types))
            {
                continue;
            }

            if (SendsGarbage)
            {
                await _udp.SendToAsync(Encoding.UTF8.GetBytes("<not xml"), SocketFlags.None, result.RemoteEndPoint);
            }

            var answer = Encoding.UTF8.GetBytes(ProbeMatches(WrongRelatesTo ?? id));
            await _udp.SendToAsync(answer, SocketFlags.None, result.RemoteEndPoint);
        }
    }

    private bool Matches(XElement types)
    {
        foreach (var item in types.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = item.IndexOf(':', StringComparison.Ordinal);
            var ns = types.GetNamespaceOfPrefix(item[..colon])!.NamespaceName;
            string local = item[(colon + 1)..];
            bool has = (ns == "http://www.onvif.org/ver10/device/wsdl" && local == "Device" && Types.Contains("tds:Device"))
                || (ns == "http://www.onvif.org/ver10/network/wsdl" && local == "NetworkVideoTransmitter" && Types.Contains("dn:NetworkVideoTransmitter"))
                || (ns == "http://printer.example.org/2003/imaging" && Types.Contains("i:PrintBasic"));
            if (!has)
            {
                return false;
            }
        }

        return true;
    }

    private string ProbeMatches(string relatesTo)
    {
        string xaddrs = string.Join(' ', OwnAddressLast ? ExtraAddresses.Append(DeviceService.AbsoluteUri) : ExtraAddresses.Prepend(DeviceService.AbsoluteUri));
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <SOAP-ENV:Envelope xmlns:SOAP-ENV="http://www.w3.org/2003/05/soap-envelope" xmlns:wsa="http://schemas.xmlsoap.org/ws/2004/08/addressing" xmlns:wsdd="http://schemas.xmlsoap.org/ws/2005/04/discovery" xmlns:dn="http://www.onvif.org/ver10/network/wsdl" xmlns:tds="http://www.onvif.org/ver10/device/wsdl" xmlns:i="http://printer.example.org/2003/imaging">
              <SOAP-ENV:Header>
                <wsa:MessageID>urn:uuid:{Guid.NewGuid()}</wsa:MessageID>
                <wsa:RelatesTo>{relatesTo}</wsa:RelatesTo>
                <wsa:To SOAP-ENV:mustUnderstand="true">http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</wsa:To>
                <wsa:Action SOAP-ENV:mustUnderstand="true">http://schemas.xmlsoap.org/ws/2005/04/discovery/ProbeMatches</wsa:Action>
                <wsdd:AppSequence InstanceId="1" MessageNumber="1"/>
              </SOAP-ENV:Header>
              <SOAP-ENV:Body>
                <wsdd:ProbeMatches>
                  <wsdd:ProbeMatch>
                    <wsa:EndpointReference><wsa:Address>{Endpoint}</wsa:Address></wsa:EndpointReference>
                    <wsdd:Types>{string.Join(' ', Types)}</wsdd:Types>
                    <wsdd:Scopes>{string.Join(' ', Scopes)}</wsdd:Scopes>
                    <wsdd:XAddrs>{xaddrs}</wsdd:XAddrs>
                    <wsdd:MetadataVersion>1</wsdd:MetadataVersion>
                  </wsdd:ProbeMatch>
                </wsdd:ProbeMatches>
              </SOAP-ENV:Body>
            </SOAP-ENV:Envelope>
            """;
    }

    private async Task HttpLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _http.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            try
            {
                while (true)
                {
                    var (method, path, headers, body) = await ReadRequestAsync(stream);
                    if (method is null)
                    {
                        return;
                    }

                    var (status, extra, content) = Answer(method, path!, headers, body);
                    var bytes = Encoding.UTF8.GetBytes(content);
                    var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: application/soap+xml; charset=utf-8\r\nContent-Length: {bytes.Length}\r\n{extra}\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                    await stream.WriteAsync(bytes);
                }
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
                // The client closed the connection.
            }
        }
    }

    private static async Task<(string? Method, string? Path, Dictionary<string, string> Headers, string Body)> ReadRequestAsync(NetworkStream stream)
    {
        var head = new List<byte>();
        var one = new byte[1];
        while (!(head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
        {
            if (await stream.ReadAsync(one) == 0)
            {
                return (null, null, [], "");
            }

            head.Add(one[0]);
        }

        var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var parts = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        int length = headers.TryGetValue("Content-Length", out var l) ? int.Parse(l, CultureInfo.InvariantCulture) : 0;
        var body = new byte[length];
        await stream.ReadExactlyAsync(body);
        return (parts[0], parts[1], headers, Encoding.UTF8.GetString(body));
    }

    private (int Status, string Headers, string Body) Answer(string method, string path, Dictionary<string, string> headers, string body)
    {
        var request = XDocument.Parse(body);
        XNamespace s = "http://www.w3.org/2003/05/soap-envelope";
        var call = request.Root!.Element(s + "Body")!.Elements().Single();
        string action = call.Name.LocalName;
        lock (Calls)
        {
            Calls.Add(action);
        }

        // SOAP 1.2's action, a parameter of the media type (RFC 3902), must name the body's operation: devices route by either.
        var type = headers.GetValueOrDefault("Content-Type") ?? "";
        var named = Regex.Match(type, "action=\"([^\"]*)\"", RegexOptions.CultureInvariant).Groups[1].Value;
        if (named != call.Name.NamespaceName + "/" + action)
        {
            return (400, "", Fault("ter:ActionNotSupported", $"The action {named} is not the body's {action}"));
        }

        bool deviceService = path == DeviceService.AbsolutePath;
        bool preAuth = deviceService && action is "GetServices" or "GetCapabilities" or "GetSystemDateAndTime";
        if (!preAuth && Auth == OnvifAuth.Digest && !DigestValid(method, path, headers))
        {
            return (401, $"WWW-Authenticate: Digest realm=\"onvif\", qop=\"auth\", nonce=\"{_nonce}\", algorithm=MD5\r\n", "");
        }

        if (!preAuth && Auth == OnvifAuth.UsernameToken && !TokenValid(request))
        {
            return (400, "", Fault("ter:NotAuthorized", "Sender not authorized"));
        }

        return action switch
        {
            "GetServices" when deviceService && NoGetServices => (400, "", Fault("ter:ActionNotSupported", "Optional Action Not Implemented")),
            "GetServices" when deviceService => (200, "", Envelope($"""
                <tds:GetServicesResponse>
                  <tds:Service><tds:Namespace>http://www.onvif.org/ver10/device/wsdl</tds:Namespace><tds:XAddr>{DeviceService.AbsoluteUri}</tds:XAddr><tds:Version><tt:Major>2</tt:Major><tt:Minor>60</tt:Minor></tds:Version></tds:Service>
                  <tds:Service><tds:Namespace>http://www.onvif.org/ver10/media/wsdl</tds:Namespace><tds:XAddr>{Media}</tds:XAddr><tds:Version><tt:Major>2</tt:Major><tt:Minor>60</tt:Minor></tds:Version></tds:Service>
                </tds:GetServicesResponse>
                """)),
            "GetCapabilities" when deviceService => (200, "", Envelope($"""
                <tds:GetCapabilitiesResponse><tds:Capabilities><tt:Media><tt:XAddr>{Media}</tt:XAddr></tt:Media></tds:Capabilities></tds:GetCapabilitiesResponse>
                """)),
            "GetSystemDateAndTime" when deviceService => (200, "", Envelope(DateAndTime())),
            "GetProfiles" when path == MediaPath => (200, "", Envelope("""
                <trt:GetProfilesResponse>
                  <trt:Profiles token="Profile_1" fixed="true"><tt:Name>mainStream</tt:Name>
                    <tt:VideoEncoderConfiguration token="VE1"><tt:Name>VE1</tt:Name><tt:Encoding>H264</tt:Encoding><tt:Resolution><tt:Width>1920</tt:Width><tt:Height>1080</tt:Height></tt:Resolution></tt:VideoEncoderConfiguration>
                  </trt:Profiles>
                  <trt:Profiles token="Profile_2" fixed="true"><tt:Name>subStream</tt:Name>
                    <tt:VideoEncoderConfiguration token="VE2"><tt:Name>VE2</tt:Name><tt:Encoding>JPEG</tt:Encoding><tt:Resolution><tt:Width>640</tt:Width><tt:Height>360</tt:Height></tt:Resolution></tt:VideoEncoderConfiguration>
                  </trt:Profiles>
                  <trt:Profiles token="Profile_3" fixed="false"><tt:Name>metadata</tt:Name></trt:Profiles>
                </trt:GetProfilesResponse>
                """)),
            "GetStreamUri" when path == MediaPath => StreamUri(call),
            _ => (400, "", Fault("ter:ActionNotSupported", "Unknown action " + action)),
        };
    }

    private string Media => $"http://127.0.0.1:{((IPEndPoint)_http.LocalEndpoint).Port}{MediaPath}";

    private static (int, string, string) StreamUri(XElement call)
    {
        XNamespace trt = "http://www.onvif.org/ver10/media/wsdl";
        XNamespace tt = "http://www.onvif.org/ver10/schema";
        string token = call.Element(trt + "ProfileToken")!.Value;
        var setup = call.Element(trt + "StreamSetup")!;
        if (setup.Element(tt + "Stream")?.Value != "RTP-Unicast" || setup.Element(tt + "Transport")?.Element(tt + "Protocol")?.Value != "RTSP")
        {
            return (400, "", Fault("ter:InvalidStreamSetup", "Unsupported stream setup"));
        }

        string channel = token switch { "Profile_1" => "101", "Profile_2" => "102", _ => "103" };
        return (200, "", Envelope($"""
            <trt:GetStreamUriResponse><trt:MediaUri><tt:Uri>rtsp://127.0.0.1:554/Streaming/Channels/{channel}?transportmode=unicast&amp;profile={token}</tt:Uri><tt:InvalidAfterConnect>false</tt:InvalidAfterConnect><tt:InvalidAfterReboot>false</tt:InvalidAfterReboot><tt:Timeout>PT0S</tt:Timeout></trt:MediaUri></trt:GetStreamUriResponse>
            """));
    }

    private string DateAndTime()
    {
        var now = DateTime.UtcNow + ClockOffset;
        return $"""
            <tds:GetSystemDateAndTimeResponse><tds:SystemDateAndTime><tt:DateTimeType>Manual</tt:DateTimeType><tt:DaylightSavings>false</tt:DaylightSavings>
            <tt:UTCDateTime><tt:Time><tt:Hour>{now.Hour}</tt:Hour><tt:Minute>{now.Minute}</tt:Minute><tt:Second>{now.Second}</tt:Second></tt:Time><tt:Date><tt:Year>{now.Year}</tt:Year><tt:Month>{now.Month}</tt:Month><tt:Day>{now.Day}</tt:Day></tt:Date></tt:UTCDateTime>
            </tds:SystemDateAndTime></tds:GetSystemDateAndTimeResponse>
            """;
    }

    /// <summary>RFC 2617's check of the client's response, with qop=auth.</summary>
    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "The simulated device checks RFC 2617's Digest, which is defined over MD5.")]
    private bool DigestValid(string method, string path, Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Authorization", out var authorization) || !authorization.StartsWith("Digest ", StringComparison.Ordinal))
        {
            return false;
        }

        var p = DigestParameter.Matches(authorization[7..]).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim('"'));
        if (p.GetValueOrDefault("username") != User || p.GetValueOrDefault("nonce") != _nonce || p.GetValueOrDefault("uri") != path)
        {
            return false;
        }

        static string Md5(string text) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));
        string ha1 = Md5($"{User}:onvif:{Password}");
        string ha2 = Md5($"{method}:{path}");
        string expected = Md5($"{ha1}:{_nonce}:{p.GetValueOrDefault("nc")}:{p.GetValueOrDefault("cnonce")}:auth:{ha2}");
        return p.GetValueOrDefault("response") == expected;
    }

    /// <summary>
    /// The UsernameToken Profile's check: PasswordDigest = Base64(SHA-1(nonce + created + password)), and Created within
    /// five minutes of the device's own clock (which is what rejects a client that ignores the device's time).
    /// </summary>
    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "The simulated device checks the UsernameToken Profile's PasswordDigest, which is defined over SHA-1.")]
    private bool TokenValid(XDocument request)
    {
        XNamespace wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
        XNamespace wsu = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
        var token = request.Descendants(wsse + "UsernameToken").SingleOrDefault();
        if (token is null || token.Element(wsse + "Username")?.Value != User)
        {
            return false;
        }

        string created = token.Element(wsu + "Created")!.Value;
        byte[] nonce = Convert.FromBase64String(token.Element(wsse + "Nonce")!.Value);
        byte[] input = [.. nonce, .. Encoding.UTF8.GetBytes(created), .. Encoding.UTF8.GetBytes(Password)];
        string digest = Convert.ToBase64String(SHA1.HashData(input));
        var when = DateTime.Parse(created, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        return token.Element(wsse + "Password")?.Value == digest && Math.Abs((when - (DateTime.UtcNow + ClockOffset)).TotalMinutes) < 5;
    }

    private static string Envelope(string body) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <env:Envelope xmlns:env="http://www.w3.org/2003/05/soap-envelope" xmlns:tds="http://www.onvif.org/ver10/device/wsdl" xmlns:trt="http://www.onvif.org/ver10/media/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema"><env:Body>{body}</env:Body></env:Envelope>
        """;

    private static string Fault(string subcode, string reason) => Envelope($"""
        <env:Fault xmlns:ter="http://www.onvif.org/ver10/error"><env:Code><env:Value>env:Sender</env:Value><env:Subcode><env:Value>{subcode}</env:Value></env:Subcode></env:Code><env:Reason><env:Text xml:lang="en">{reason}</env:Text></env:Reason></env:Fault>
        """);
}
