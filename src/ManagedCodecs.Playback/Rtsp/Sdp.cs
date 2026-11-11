namespace ManagedCodecs.Playback.Rtsp;

/// <summary>
/// What a live view needs from a camera's SDP (RFC 4566): the session's control URL and the first video track whose
/// <c>a=rtpmap</c> names H264, with its payload type, control URL and <c>a=fmtp</c> parameters (RFC 6184, 8.1).
/// </summary>
internal sealed class Sdp
{
    private Sdp(string? sessionControl, SdpH264Track? track)
    {
        SessionControl = sessionControl;
        Track = track;
    }

    /// <summary>The session's <c>a=control</c>, before any media line; null when it has none.</summary>
    public string? SessionControl { get; }

    /// <summary>The first H.264 video track, or null when there is none.</summary>
    public SdpH264Track? Track { get; }

    public static Sdp Parse(string text)
    {
        string? sessionControl = null;
        SdpH264Track? found = null;
        var media = new List<Media>();
        Media? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                current = new Media(line.Substring(2).Split(' ', StringSplitOptions.RemoveEmptyEntries));
                media.Add(current);
            }
            else if (line.StartsWith("a=control:", StringComparison.Ordinal))
            {
                string control = line.Substring("a=control:".Length).Trim();
                if (current is null)
                {
                    sessionControl = control;
                }
                else
                {
                    current.Control = control;
                }
            }
            else if (current is not null && line.StartsWith("a=rtpmap:", StringComparison.Ordinal))
            {
                var (type, value) = Attribute(line.Substring("a=rtpmap:".Length));
                if (type >= 0)
                {
                    current.RtpMap[type] = value;
                }
            }
            else if (current is not null && line.StartsWith("a=fmtp:", StringComparison.Ordinal))
            {
                var (type, value) = Attribute(line.Substring("a=fmtp:".Length));
                if (type >= 0)
                {
                    current.Fmtp[type] = value;
                }
            }
        }

        foreach (var m in media)
        {
            if (found is not null || m.Fields.Length < 4 || m.Fields[0] != "video")
            {
                continue;
            }

            foreach (var format in m.Fields.Skip(3))
            {
                if (int.TryParse(format, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int type)
                    && m.RtpMap.TryGetValue(type, out var map)
                    && map.StartsWith("H264/", StringComparison.OrdinalIgnoreCase))
                {
                    found = SdpH264Track.From(type, m.Control, m.Fmtp.TryGetValue(type, out var fmtp) ? fmtp : "");
                    break;
                }
            }
        }

        return new Sdp(sessionControl, found);
    }

    /// <summary>
    /// A control URL made absolute: an absolute one as it is, <c>*</c> or none as the base, else the base and the
    /// control joined by one slash, as FFmpeg joins them (cameras send <c>trackID=1</c> against a base with no slash).
    /// </summary>
    public static string Resolve(string baseUrl, string? control)
    {
        if (string.IsNullOrEmpty(control) || control == "*")
        {
            return baseUrl;
        }

        if (control!.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) || control.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase))
        {
            return control;
        }

        return baseUrl.TrimEnd('/') + "/" + control.TrimStart('/');
    }

    // "96 H264/90000" as (96, "H264/90000"); -1 when it doesn't start with a payload type.
    private static (int Type, string Value) Attribute(string text)
    {
        int space = text.IndexOf(' ');
        string number = space < 0 ? text : text.Substring(0, space);
        return int.TryParse(number, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int type)
            ? (type, space < 0 ? "" : text.Substring(space + 1).Trim())
            : (-1, "");
    }

    private sealed class Media(string[] fields)
    {
        public string[] Fields { get; } = fields;

        public string? Control { get; set; }

        public Dictionary<int, string> RtpMap { get; } = [];

        public Dictionary<int, string> Fmtp { get; } = [];
    }
}

/// <summary>An H.264 track of an SDP.</summary>
/// <param name="PayloadType">Its RTP payload type.</param>
/// <param name="Control">Its <c>a=control</c>, or null.</param>
/// <param name="PacketizationMode">The <c>a=fmtp</c>'s <c>packetization-mode</c> (0 when absent).</param>
/// <param name="ParameterSets">The <c>sprop-parameter-sets</c> NAL units (empty when absent or not base64).</param>
internal sealed record SdpH264Track(int PayloadType, string? Control, int PacketizationMode, byte[][] ParameterSets)
{
    public static SdpH264Track From(int payloadType, string? control, string fmtp)
    {
        int mode = 0;
        byte[][] parameterSets = [];
        foreach (var parameter in fmtp.Split(';'))
        {
            int equals = parameter.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            string name = parameter.Substring(0, equals).Trim();
            string value = parameter.Substring(equals + 1).Trim();
            if (name.Equals("packetization-mode", StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out mode);
            }
            else if (name.Equals("sprop-parameter-sets", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    parameterSets = RtpH264Depacketizer.ParseParameterSets(value);
                }
                catch (FormatException)
                {
                    // Not base64: the parameter sets come in-band, as they do from most cameras anyway.
                }
            }
        }

        return new SdpH264Track(payloadType, control, mode, parameterSets);
    }
}
