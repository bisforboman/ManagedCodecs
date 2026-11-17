using System.Net;
using System.Net.Sockets;

namespace ManagedCodecs.Playback.Rtsp;

/// <summary>
/// The two UDP ports an RTSP session over UDP receives on (RFC 3550, 11): RTP on an even port, RTCP on the next, both
/// on the local address of the RTSP connection. The RTCP port receives the server's sender reports and sends the receiver
/// reports. docs/decisions/2026-11-14-live-rtsp-udp.md, docs/decisions/2026-11-15-live-rtcp.md. Or, for multicast
/// (<see cref="Join"/>), the group's two ports, shared with every other receiver on the machine.
/// </summary>
internal sealed class RtpUdpPorts : IDisposable
{
    private const int Attempts = 20;

    private readonly Socket _rtp;
    private readonly Socket _rtcp;
    private readonly byte[] _buffer = new byte[65536];
    private readonly byte[] _rtcpBuffer = new byte[65536];

    private RtpUdpPorts(Socket rtp, Socket rtcp)
    {
        _rtp = rtp;
        _rtcp = rtcp;
    }

    public int RtpPort => ((IPEndPoint)_rtp.LocalEndPoint!).Port;

    public int RtcpPort => ((IPEndPoint)_rtcp.LocalEndPoint!).Port;

    /// <summary>An even port and the next one, free on <paramref name="local"/>.</summary>
    /// <exception cref="SocketException">No such pair in <see cref="Attempts"/> tries.</exception>
    public static RtpUdpPorts Bind(IPAddress local)
    {
        // A dual-mode TCP socket (TcpClient's, where the machine has IPv6) gives an IPv4 peer's address as ::ffff:a.b.c.d,
        // which a v6-only UDP socket can't bind (EINVAL): the IPv4 address it stands for.
        if (local.IsIPv4MappedToIPv6)
        {
            local = local.MapToIPv4();
        }

        for (int attempt = 0; ; attempt++)
        {
            var rtp = new Socket(local.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            Socket? rtcp = null;
            try
            {
                rtp.Bind(new IPEndPoint(local, 0));
                int port = ((IPEndPoint)rtp.LocalEndPoint!).Port;
                if (port % 2 == 0 && port < 65535)
                {
                    rtcp = new Socket(local.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                    rtcp.Bind(new IPEndPoint(local, port + 1));
                    rtp.ReceiveBufferSize = 4 * 1024 * 1024; // a keyframe's burst while the decoder is busy
                    return new RtpUdpPorts(rtp, rtcp);
                }
            }
            catch (SocketException)
            {
                // The next port was taken: try another pair, unless out of tries.
                if (attempt + 1 >= Attempts)
                {
                    rtp.Dispose();
                    rtcp?.Dispose();
                    throw;
                }
            }

            rtp.Dispose();
            rtcp?.Dispose();
            if (attempt + 1 >= Attempts)
            {
                throw new SocketException((int)SocketError.AddressAlreadyInUse);
            }
        }
    }

    /// <summary>
    /// The two ports of a multicast group (RFC 2326, 12.39: <c>destination</c> and <c>port</c> of SETUP's answer),
    /// joined on the interface the RTSP connection uses; a connection to this machine itself (loopback) also joins on
    /// the system's default interface, where a server here sends unless told otherwise.
    /// </summary>
    /// <exception cref="SocketException">The ports can't be bound or the group joined.</exception>
    public static RtpUdpPorts Join(IPAddress group, int rtpPort, int rtcpPort, IPAddress local, int timeToLive)
    {
        if (local.IsIPv4MappedToIPv6)
        {
            local = local.MapToIPv4();
        }

        Socket Open(int port)
        {
            var socket = new Socket(group.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                // Other receivers of the group on this machine bind the same port. Bound to the group itself, the
                // socket gets only its packets; Windows doesn't bind a group, and filters by what was joined.
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                bool windows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
                bool v6 = group.AddressFamily == AddressFamily.InterNetworkV6;
                socket.Bind(new IPEndPoint(windows ? (v6 ? IPAddress.IPv6Any : IPAddress.Any) : group, port));
                if (v6)
                {
                    socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership, new IPv6MulticastOption(group));
                    socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, timeToLive);
                }
                else
                {
                    bool here = local.AddressFamily == AddressFamily.InterNetwork;
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(group, here ? local : IPAddress.Any));
                    if (here && IPAddress.IsLoopback(local))
                    {
                        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(group, IPAddress.Any));
                    }

                    if (here)
                    {
                        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                    }

                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, timeToLive);
                }

                return socket;
            }
            catch (SocketException)
            {
                socket.Dispose();
                throw;
            }
        }

        var rtp = Open(rtpPort);
        try
        {
            rtp.ReceiveBufferSize = 4 * 1024 * 1024;
            return new RtpUdpPorts(rtp, Open(rtcpPort));
        }
        catch (SocketException)
        {
            rtp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// One small packet from each port to the server's, so that a NAT between lets the server's packets in: an RTP header
    /// with no payload, and an RTCP receiver report with no report blocks (RFC 3550, 6.4.2).
    /// </summary>
    public void Punch(IPEndPoint rtp, IPEndPoint rtcp)
    {
        byte[] header = [0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        byte[] report = [0x80, 201, 0, 1, 0, 0, 0, 0];
        _rtp.SendTo(header, rtp);
        _rtcp.SendTo(report, rtcp);
    }

    /// <summary>The next datagram on the RTP port and where it came from.</summary>
    public async Task<(byte[] Packet, IPEndPoint From)> ReceiveAsync()
    {
        EndPoint any = new IPEndPoint(_rtp.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        var result = await _rtp.ReceiveFromAsync(new ArraySegment<byte>(_buffer), SocketFlags.None, any).ConfigureAwait(false);
        return (_buffer.AsSpan(0, result.ReceivedBytes).ToArray(), (IPEndPoint)result.RemoteEndPoint);
    }

    /// <summary>The next datagram on the RTCP port and where it came from.</summary>
    public async Task<(byte[] Packet, IPEndPoint From)> ReceiveReportAsync()
    {
        EndPoint any = new IPEndPoint(_rtcp.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        var result = await _rtcp.ReceiveFromAsync(new ArraySegment<byte>(_rtcpBuffer), SocketFlags.None, any).ConfigureAwait(false);
        return (_rtcpBuffer.AsSpan(0, result.ReceivedBytes).ToArray(), (IPEndPoint)result.RemoteEndPoint);
    }

    /// <summary>Sends an RTCP packet from the RTCP port.</summary>
    public void SendReport(byte[] packet, IPEndPoint to) => _rtcp.SendTo(packet, to);

    /// <inheritdoc/>
    public void Dispose()
    {
        _rtp.Dispose();
        _rtcp.Dispose();
    }
}
