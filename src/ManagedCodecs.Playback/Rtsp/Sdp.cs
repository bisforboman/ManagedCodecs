namespace ManagedCodecs.Playback.Rtsp;

/// <summary>
/// What a live view needs from a camera's SDP (RFC 4566): the session's control URL and the first video track it can
/// play, in the SDP's order: H.264 (an <c>a=rtpmap</c> naming H264, with its <c>a=fmtp</c> parameters, RFC 6184, 8.1)
/// or JPEG (RFC 2435: the static payload type 26, or an <c>a=rtpmap</c> naming JPEG)
/// (docs/decisions/2026-11-12-live-rtsp-jpeg.md, decision 3), and the first audio track it can play: AAC (RFC 3640:
/// an <c>a=rtpmap</c> naming MPEG4-GENERIC, with its <c>a=fmtp</c>) or G.711 (RFC 3551: the static payload types 0 and
/// 8, or an <c>a=rtpmap</c> naming PCMU or PCMA) (docs/decisions/2026-11-15-live-rtsp-audio.md, decision 1).
/// </summary>
internal sealed class Sdp
{
    private Sdp(string? sessionControl, SdpVideoTrack? track, SdpAudioTrack? audio, string? skippedAudio)
    {
        SessionControl = sessionControl;
        Track = track;
        Audio = audio;
        SkippedAudio = skippedAudio;
    }

    /// <summary>The session's <c>a=control</c>, before any media line; null when it has none.</summary>
    public string? SessionControl { get; }

    /// <summary>The first H.264 or JPEG video track, or null when there is none.</summary>
    public SdpVideoTrack? Track { get; }

    /// <summary>The first AAC or G.711 audio track, or null when there is none.</summary>
    public SdpAudioTrack? Audio { get; }

    /// <summary>
    /// When there is audio but none that plays (<see cref="Audio"/> is null): the first audio format's encoding name
    /// (<c>MP4A-LATM</c>, <c>G726-32</c>), or its payload type when it has no <c>a=rtpmap</c>. Else null.
    /// </summary>
    public string? SkippedAudio { get; }

    public static Sdp Parse(string text)
    {
        string? sessionControl = null;
        SdpVideoTrack? found = null;
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
            else if (current is not null && line.StartsWith("a=x-dimensions:", StringComparison.Ordinal))
            {
                // GStreamer's, for JPEG larger than RFC 2435's header can say: "a=x-dimensions:2560,1440".
                current.Size = Size(line.Substring("a=x-dimensions:".Length), ',');
            }
            else if (current is not null && line.StartsWith("a=framesize:", StringComparison.Ordinal))
            {
                // RFC 6064: "a=framesize:26 2560-1440", for a payload type.
                var (type, value) = Attribute(line.Substring("a=framesize:".Length));
                if (type >= 0)
                {
                    current.FrameSize[type] = Size(value, '-');
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
                if (!int.TryParse(format, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int type))
                {
                    continue;
                }

                string? map = m.RtpMap.TryGetValue(type, out var mapped) ? mapped : null;
                if (map?.StartsWith("H264/", StringComparison.OrdinalIgnoreCase) == true)
                {
                    found = SdpVideoTrack.H264(type, m.Control, m.Fmtp.TryGetValue(type, out var fmtp) ? fmtp : "");
                    break;
                }

                // 26 is JPEG's static payload type (RFC 3551), which needs no a=rtpmap; mapped, it must say JPEG.
                if (map is null ? type == 26 : map.StartsWith("JPEG/", StringComparison.OrdinalIgnoreCase))
                {
                    var (width, height) = m.FrameSize.TryGetValue(type, out var size) && size.Width > 0 ? size : m.Size;
                    found = new SdpVideoTrack(SdpCodec.Jpeg, type, m.Control, 0, []) { Width = width, Height = height };
                    break;
                }
            }
        }

        SdpAudioTrack? audio = null;
        string? skipped = null;
        foreach (var m in media)
        {
            if (audio is not null || m.Fields.Length < 4 || m.Fields[0] != "audio")
            {
                continue;
            }

            foreach (var format in m.Fields.Skip(3))
            {
                if (!int.TryParse(format, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int type))
                {
                    continue;
                }

                string? map = m.RtpMap.TryGetValue(type, out var mapped) ? mapped : null;
                audio = SdpAudioTrack.From(type, m.Control, map, m.Fmtp.TryGetValue(type, out var fmtp) ? fmtp : "");
                if (audio is not null)
                {
                    break;
                }

                skipped ??= map is null ? "payload type " + format : map.Split('/')[0];
            }
        }

        return new Sdp(sessionControl, found, audio, audio is null ? skipped : null);
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

    // "2560,1440" or "2560-1440" as a size; (0, 0) when it isn't two positive numbers no JPEG is larger than.
    private static (int Width, int Height) Size(string text, char separator)
    {
        var parts = text.Split(separator);
        return parts.Length == 2
            && int.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int width)
            && int.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int height)
            && width is > 0 and <= 65535 && height is > 0 and <= 65535
            ? (width, height)
            : (0, 0);
    }

    private sealed class Media(string[] fields)
    {
        public string[] Fields { get; } = fields;

        public string? Control { get; set; }

        public Dictionary<int, string> RtpMap { get; } = [];

        public Dictionary<int, string> Fmtp { get; } = [];

        public (int Width, int Height) Size { get; set; }

        public Dictionary<int, (int Width, int Height)> FrameSize { get; } = [];
    }
}

/// <summary>What a video track of an SDP carries.</summary>
internal enum SdpCodec
{
    H264,
    Jpeg,
}

/// <summary>A video track of an SDP.</summary>
/// <param name="Codec">H.264 or JPEG.</param>
/// <param name="PayloadType">Its RTP payload type.</param>
/// <param name="Control">Its <c>a=control</c>, or null.</param>
/// <param name="PacketizationMode">H.264: the <c>a=fmtp</c>'s <c>packetization-mode</c> (0 when absent).</param>
/// <param name="ParameterSets">H.264: the <c>sprop-parameter-sets</c> NAL units (empty when absent or not base64).</param>
internal sealed record SdpVideoTrack(SdpCodec Codec, int PayloadType, string? Control, int PacketizationMode, byte[][] ParameterSets)
{
    /// <summary>
    /// JPEG: the frame's size when the SDP gives it (<c>a=x-dimensions</c>, GStreamer's, or RFC 6064's
    /// <c>a=framesize</c>), for frames over 2040 pixels, whose size RFC 2435's header can't say; else 0.
    /// </summary>
    public int Width { get; init; }

    /// <summary>JPEG: the frame's height when the SDP gives it (<see cref="Width"/>); else 0.</summary>
    public int Height { get; init; }

    public static SdpVideoTrack H264(int payloadType, string? control, string fmtp)
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

        return new SdpVideoTrack(SdpCodec.H264, payloadType, control, mode, parameterSets);
    }
}

/// <summary>An audio track of an SDP that plays.</summary>
/// <param name="Codec"><see cref="CodecIds.Aac"/>, <see cref="CodecIds.PcmMulaw"/> or <see cref="CodecIds.PcmAlaw"/>.</param>
/// <param name="PayloadType">Its RTP payload type.</param>
/// <param name="Control">Its <c>a=control</c>, or null.</param>
/// <param name="SampleRate">The <c>a=rtpmap</c>'s clock rate (8000 for the static G.711 types).</param>
/// <param name="Channels">The <c>a=rtpmap</c>'s channels (1 when it gives none).</param>
/// <param name="SizeLength">AAC: the bits of each AU header's size (<c>sizelength</c>: 13 for AAC-hbr, 6 for AAC-lbr).</param>
/// <param name="IndexLength">AAC: the bits of the first AU header's index (<c>indexlength</c>).</param>
/// <param name="IndexDeltaLength">AAC: the bits of the other AU headers' index delta (<c>indexdeltalength</c>).</param>
/// <param name="Config">AAC: the AudioSpecificConfig (<c>config</c>, hexadecimal); empty for G.711.</param>
internal sealed record SdpAudioTrack(string Codec, int PayloadType, string? Control, int SampleRate, int Channels, int SizeLength, int IndexLength, int IndexDeltaLength, byte[] Config)
{
    /// <summary>
    /// The track a format describes, or null when it isn't one that plays: an encoding other than MPEG4-GENERIC, PCMU
    /// and PCMA, or MPEG4-GENERIC in a mode other than AAC-hbr and AAC-lbr, or without a size field or a config.
    /// </summary>
    public static SdpAudioTrack? From(int payloadType, string? control, string? rtpMap, string fmtp)
    {
        if (rtpMap is null)
        {
            // G.711's static payload types (RFC 3551), which need no a=rtpmap.
            return payloadType switch
            {
                0 => new SdpAudioTrack(CodecIds.PcmMulaw, 0, control, 8000, 1, 0, 0, 0, []),
                8 => new SdpAudioTrack(CodecIds.PcmAlaw, 8, control, 8000, 1, 0, 0, 0, []),
                _ => null,
            };
        }

        // "MPEG4-GENERIC/48000/2": the encoding, the clock rate and, for audio, the channels (1 when left out).
        var parts = rtpMap.Split('/');
        if (parts.Length < 2 || !Number(parts[1], out int rate) || rate <= 0)
        {
            return null;
        }

        int channels = 1;
        if (parts.Length > 2 && (!Number(parts[2], out channels) || channels <= 0))
        {
            return null;
        }

        string encoding = parts[0].Trim();
        if (encoding.Equals("PCMU", StringComparison.OrdinalIgnoreCase) || encoding.Equals("PCMA", StringComparison.OrdinalIgnoreCase))
        {
            return new SdpAudioTrack(encoding.Equals("PCMU", StringComparison.OrdinalIgnoreCase) ? CodecIds.PcmMulaw : CodecIds.PcmAlaw, payloadType, control, rate, channels, 0, 0, 0, []);
        }

        if (!encoding.Equals("MPEG4-GENERIC", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string mode = "";
        int sizeLength = 0;
        int indexLength = 0;
        int indexDeltaLength = 0;
        byte[] config = [];
        foreach (var parameter in fmtp.Split(';'))
        {
            int equals = parameter.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            string name = parameter.Substring(0, equals).Trim();
            string value = parameter.Substring(equals + 1).Trim();
            if (name.Equals("mode", StringComparison.OrdinalIgnoreCase))
            {
                mode = value;
            }
            else if (name.Equals("sizelength", StringComparison.OrdinalIgnoreCase))
            {
                _ = Number(value, out sizeLength);
            }
            else if (name.Equals("indexlength", StringComparison.OrdinalIgnoreCase))
            {
                _ = Number(value, out indexLength);
            }
            else if (name.Equals("indexdeltalength", StringComparison.OrdinalIgnoreCase))
            {
                _ = Number(value, out indexDeltaLength);
            }
            else if (name.Equals("config", StringComparison.OrdinalIgnoreCase))
            {
                config = Hex(value);
            }
        }

        bool aac = mode.Equals("AAC-hbr", StringComparison.OrdinalIgnoreCase) || mode.Equals("AAC-lbr", StringComparison.OrdinalIgnoreCase);
        return aac && sizeLength is > 0 and <= 16 && indexLength <= 16 && indexDeltaLength <= 16 && config.Length > 0
            ? new SdpAudioTrack(CodecIds.Aac, payloadType, control, rate, channels, sizeLength, indexLength, indexDeltaLength, config)
            : null;
    }

    private static bool Number(string text, out int value) =>
        int.TryParse(text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value);

    // Hexadecimal digits as bytes; empty when they aren't (an odd count, another character).
    private static byte[] Hex(string text)
    {
        if (text.Length % 2 != 0)
        {
            return [];
        }

        var bytes = new byte[text.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(text.Substring(2 * i, 2), System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out bytes[i]))
            {
                return [];
            }
        }

        return bytes;
    }
}
