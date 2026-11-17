using System.Globalization;
using System.Net;

namespace ManagedCodecs.Playback.Rtsp;

/// <summary>
/// What a SETUP answer's <c>Transport</c> header says (RFC 2326, 12.39): the interleaved channel for RTP over the RTSP
/// connection, or for RTP over UDP the server's ports, the stream's SSRC and the address it comes from, or for
/// multicast the group and its ports.
/// </summary>
/// <param name="Channel">The RTP channel on the RTSP connection (<c>interleaved=</c>); 0 when not said.</param>
/// <param name="ServerRtpPort">The server's RTP port (<c>server_port=</c>), or null.</param>
/// <param name="ServerRtcpPort">The server's RTCP port, or null.</param>
/// <param name="Ssrc">The stream's SSRC (<c>ssrc=</c>, hexadecimal), or null.</param>
/// <param name="Source">Where the packets come from (<c>source=</c>, an address), or null for the server's.</param>
internal sealed record RtspTransport(int Channel, int? ServerRtpPort, int? ServerRtcpPort, uint? Ssrc, IPAddress? Source)
{
    /// <summary>Multicast: the group the packets are sent to (<c>destination=</c>), or null.</summary>
    public IPAddress? Destination { get; init; }

    /// <summary>Multicast: the group's RTP port (<c>port=</c>), or null.</summary>
    public int? RtpPort { get; init; }

    /// <summary>Multicast: the group's RTCP port (<c>port=</c>'s second, else the one after), or null.</summary>
    public int? RtcpPort { get; init; }

    /// <summary>Multicast: how many routers the group's packets pass (<c>ttl=</c>); 1 when not said.</summary>
    public int TimeToLive { get; init; } = 1;

    public static RtspTransport Parse(string? header)
    {
        int channel = 0;
        int? rtp = null;
        int? rtcp = null;
        uint? ssrc = null;
        IPAddress? source = null;
        IPAddress? destination = null;
        int? groupRtp = null;
        int? groupRtcp = null;
        int ttl = 1;

        // A server may offer several transports, comma separated; the first is the one chosen.
        string first = header is null ? "" : header.Split(',')[0];
        foreach (var part in first.Split(';'))
        {
            int equals = part.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            string name = part.Substring(0, equals).Trim();
            string value = part.Substring(equals + 1).Trim();
            if (name.Equals("interleaved", StringComparison.OrdinalIgnoreCase))
            {
                channel = Ports(value).First ?? 0;
            }
            else if (name.Equals("server_port", StringComparison.OrdinalIgnoreCase))
            {
                (rtp, rtcp) = Ports(value);
            }
            else if (name.Equals("ssrc", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint parsed))
            {
                ssrc = parsed;
            }
            else if (name.Equals("source", StringComparison.OrdinalIgnoreCase) && IPAddress.TryParse(value, out var address))
            {
                source = address;
            }
            else if (name.Equals("destination", StringComparison.OrdinalIgnoreCase) && IPAddress.TryParse(value, out var group))
            {
                destination = group;
            }
            else if (name.Equals("port", StringComparison.OrdinalIgnoreCase))
            {
                (groupRtp, groupRtcp) = Ports(value);
            }
            else if (name.Equals("ttl", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int hops))
            {
                ttl = Math.Clamp(hops, 1, 255);
            }
        }

        return new RtspTransport(channel, rtp, rtcp, ssrc, source)
        {
            Destination = destination,
            RtpPort = groupRtp,
            RtcpPort = groupRtp is null ? null : groupRtcp ?? groupRtp + 1,
            TimeToLive = ttl,
        };
    }

    // "8000-8001" as (8000, 8001); "8000" as (8000, null).
    private static (int? First, int? Second) Ports(string value)
    {
        var parts = value.Split('-');
        int? First(int i) => i < parts.Length && int.TryParse(parts[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port) ? port : null;
        return (First(0), First(1));
    }
}
