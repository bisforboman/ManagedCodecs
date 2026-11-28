using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Xml.Linq;
using ManagedCodecs.Playback.Onvif;

namespace ManagedCodecs.Playback;

/// <summary>
/// Finds the ONVIF cameras on the machine's networks: WS-Discovery's probe (ONVIF Core 7), multicast to
/// 239.255.255.250:3702 from every IPv4 interface that is up, answered by each camera with its ONVIF device service's
/// addresses. docs/decisions/2026-11-27-live-onvif.md.
/// </summary>
public static class OnvifDiscovery
{
    /// <summary>WS-Discovery's IPv4 multicast group and port (WS-Discovery 3.1.1).</summary>
    internal static readonly IPEndPoint Group = new(IPAddress.Parse("239.255.255.250"), 3702);

    // SOAP over UDP's repetition (its Appendix I): two more copies after the first, the first wait random in 50 to 250 ms,
    // then doubled, at most 500 ms.
    private const int Repeats = 2;
    private const int MinDelayMs = 50;
    private const int MaxDelayMs = 250;
    private const int UpperDelayMs = 500;

    /// <summary>
    /// The cameras that answer within <paramref name="wait"/>, each once (a camera on two networks, or answering both
    /// of the probe's types, is merged by its endpoint). Cameras answer after a random delay of up to half a second; 2
    /// to 3 seconds leave room for a busy network.
    /// </summary>
    /// <param name="wait">How long to listen for answers.</param>
    /// <param name="cancellationToken">Ends the wait early, with what has answered by then.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="wait"/> is negative.</exception>
    /// <exception cref="PlatformNotSupportedException">The platform has no UDP sockets (a browser).</exception>
    public static Task<IReadOnlyList<OnvifDevice>> FindAsync(TimeSpan wait, CancellationToken cancellationToken = default)
    {
        if (wait < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(wait), "The wait is negative.");
        }

        return ProbeAsync(wait, Group, LocalAddresses(), cancellationToken);
    }

    /// <summary>The probe to <paramref name="target"/> (the group, or in tests a responder's own port) from each of <paramref name="locals"/>.</summary>
    internal static async Task<IReadOnlyList<OnvifDevice>> ProbeAsync(TimeSpan wait, IPEndPoint target, IReadOnlyList<IPAddress> locals, CancellationToken cancellationToken)
    {
        // One probe a type: a device matches a probe only if it lists every type the probe names (WS-Discovery 5.1), and
        // devices list tds:Device (ONVIF Core 7.3.2.1) or, older ones, only dn:NetworkVideoTransmitter.
        var probes = new[]
        {
            (Id: "urn:uuid:" + Guid.NewGuid().ToString(), Namespace: OnvifSoap.Device, Type: "Device"),
            (Id: "urn:uuid:" + Guid.NewGuid().ToString(), Namespace: OnvifSoap.NetworkWsdl, Type: "NetworkVideoTransmitter"),
        };
        var messages = probes.Select(p => OnvifSoap.Probe(p.Id, p.Namespace, p.Type)).ToArray();
        var ids = new HashSet<string>(probes.Select(p => p.Id), StringComparer.Ordinal);

        var sockets = new List<Socket>();
        foreach (var local in locals)
        {
            try
            {
                var socket = new Socket(local.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                try
                {
                    socket.Bind(new IPEndPoint(local, 0));
                    if (IsMulticast(target.Address))
                    {
                        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                    }

                    sockets.Add(socket);
                }
                catch (SocketException)
                {
                    socket.Dispose(); // an interface that can't send to the group: the others
                }
            }
            catch (SocketException)
            {
                // No socket for this address family.
            }
        }

        var found = new Matches();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(wait);
        try
        {
            var receivers = sockets.Select(s => ReceiveAsync(s, ids, found)).ToList();
            await SendAsync(sockets, messages, target, stop.Token).ConfigureAwait(false);
            try
            {
                await Task.Delay(Timeout.Infinite, stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The wait is over, or the caller ended it: what has answered.
            }

            foreach (var socket in sockets)
            {
                socket.Dispose(); // ends the receive loops
            }

            await Task.WhenAll(receivers).ConfigureAwait(false);
        }
        finally
        {
            foreach (var socket in sockets)
            {
                socket.Dispose();
            }
        }

        return found.Devices();
    }

    /// <summary>The IPv4 addresses of the interfaces that are up and take multicast, loopback included.</summary>
    internal static IReadOnlyList<IPAddress> LocalAddresses()
    {
        var addresses = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || (!nic.SupportsMulticast && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    addresses.Add(unicast.Address);
                }
            }
        }

        return addresses.Count > 0 ? addresses : [IPAddress.Any];
    }

    private static bool IsMulticast(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && (address.GetAddressBytes()[0] & 0xF0) == 0xE0;

    /// <summary>Each probe from each socket, then the two repetitions at growing random delays, the same message IDs.</summary>
    private static async Task SendAsync(List<Socket> sockets, byte[][] messages, IPEndPoint target, CancellationToken cancellationToken)
    {
        int delay = RandomNumberGenerator.GetInt32(MinDelayMs, MaxDelayMs + 1);
        for (int copy = 0; copy <= Repeats; copy++)
        {
            foreach (var socket in sockets)
            {
                foreach (var message in messages)
                {
                    try
                    {
                        await socket.SendToAsync(new ArraySegment<byte>(message), SocketFlags.None, target).ConfigureAwait(false);
                    }
                    catch (SocketException)
                    {
                        // An interface that went down or has no route to the group: the others still probe.
                    }
                    catch (ObjectDisposedException)
                    {
                        // Closed at the end of a short wait.
                    }
                }
            }

            if (copy == Repeats)
            {
                break;
            }

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = Math.Min(delay * 2, UpperDelayMs);
        }
    }

    /// <summary>Reads datagrams until the socket is closed, keeping the ProbeMatches that answer one of <paramref name="ids"/>.</summary>
    private static async Task ReceiveAsync(Socket socket, HashSet<string> ids, Matches found)
    {
        var buffer = new byte[65536];
        EndPoint any = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (true)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(new ArraySegment<byte>(buffer), SocketFlags.None, any).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                return; // closed at the end of the wait
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            var document = OnvifSoap.TryParse(buffer, result.ReceivedBytes);
            if (document is not null)
            {
                found.Add(document, ids, (IPEndPoint)result.RemoteEndPoint);
            }
        }
    }

    /// <summary>The devices that answered, by endpoint.</summary>
    private sealed class Matches
    {
        private readonly object _lock = new();
        private readonly List<(string Key, string? Endpoint, List<Uri> Addresses, List<string> Scopes, IPAddress From)> _devices = [];

        /// <summary>Takes a ProbeMatches answering one of <paramref name="ids"/>, and of a device type, from <paramref name="from"/>.</summary>
        public void Add(XDocument document, HashSet<string> ids, IPEndPoint from)
        {
            var header = OnvifSoap.Children(document.Root, "Header").FirstOrDefault();
            string? relatesTo = OnvifSoap.Children(header, "RelatesTo").FirstOrDefault()?.Value.Trim();
            var body = OnvifSoap.BodyContent(document);
            if (relatesTo is null || !ids.Contains(relatesTo) || body is null || body.Name != OnvifSoap.Discovery + "ProbeMatches")
            {
                return; // another client's probe's answer, or not an answer at all
            }

            foreach (var match in body.Elements(OnvifSoap.Discovery + "ProbeMatch"))
            {
                var types = match.Element(OnvifSoap.Discovery + "Types") is { } t ? OnvifSoap.QNames(t).ToList() : [];
                if (!types.Contains(OnvifSoap.Device + "Device") && !types.Contains(OnvifSoap.NetworkWsdl + "NetworkVideoTransmitter"))
                {
                    continue; // a printer or PC answering a probe it should have ignored
                }

                string? endpoint = OnvifSoap.Find(match.Element(OnvifSoap.Addressing + "EndpointReference"), "Address")?.Value.Trim();
                var addresses = OnvifSoap.List(match.Element(OnvifSoap.Discovery + "XAddrs"))
                    .Select(a => Uri.TryCreate(a, UriKind.Absolute, out var uri) && OnvifDevice.IsHttp(uri) ? uri : null)
                    .OfType<Uri>()
                    .ToList();
                if (addresses.Count == 0)
                {
                    continue; // nothing to ask for its streams (ONVIF Core 7.3.3 requires XAddrs)
                }

                var scopes = OnvifSoap.List(match.Element(OnvifSoap.Discovery + "Scopes"));
                string key = string.IsNullOrEmpty(endpoint) ? addresses[0].AbsoluteUri : endpoint!;
                lock (_lock)
                {
                    int index = _devices.FindIndex(d => d.Key == key);
                    if (index < 0)
                    {
                        _devices.Add((key, string.IsNullOrEmpty(endpoint) ? null : endpoint, addresses, scopes.ToList(), from.Address));
                        continue;
                    }

                    var device = _devices[index];
                    device.Addresses.AddRange(addresses.Where(a => !device.Addresses.Contains(a)));
                    device.Scopes.AddRange(scopes.Where(s => !device.Scopes.Contains(s)));
                }
            }
        }

        /// <summary>The devices, in the order they first answered, each one's addresses on the host it answered from first.</summary>
        public IReadOnlyList<OnvifDevice> Devices()
        {
            lock (_lock)
            {
                return _devices
                    .Select(d => new OnvifDevice(
                        d.Endpoint,
                        d.Addresses.OrderBy(a => SameHost(a, d.From) ? 0 : 1).ToList(),
                        d.Scopes.ToList()))
                    .ToList();
            }
        }

        private static bool SameHost(Uri uri, IPAddress address) =>
            IPAddress.TryParse(uri.Host.Trim('[', ']'), out var host) && host.Equals(address);
    }
}
