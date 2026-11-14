using System.Net;
using System.Net.Sockets;

namespace ManagedCodecs.Playback.Rtsp;

/// <summary>
/// The two UDP ports an RTSP session over UDP receives on (RFC 3550, 11): RTP on an even port, RTCP on the next, both
/// on the local address of the RTSP connection. What comes on the RTCP port is never read (decision 3: no receiver
/// reports); the port is held so that the server's RTCP has somewhere to go. docs/decisions/2026-11-14-live-rtsp-udp.md.
/// </summary>
internal sealed class RtpUdpPorts : IDisposable
{
    private const int Attempts = 20;

    private readonly Socket _rtp;
    private readonly Socket _rtcp;
    private readonly byte[] _buffer = new byte[65536];

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

    /// <inheritdoc/>
    public void Dispose()
    {
        _rtp.Dispose();
        _rtcp.Dispose();
    }
}
