namespace ManagedCodecs.Playback;

/// <summary>
/// Turns RTP packets carrying H.264 (RFC 6184, non-interleaved mode: single NAL units, STAP-A, FU-A) back into access
/// units, each an Annex B buffer (every NAL unit behind a 4-byte start code) for <c>H264Decoder</c>. An access unit
/// ends at the packet with the marker bit, or, if that packet was lost, at the first packet with another timestamp.
/// A gap in the sequence numbers marks the access units it may have cut <see cref="RtpAccessUnit.Damaged"/>, and a
/// NAL unit missing any of its fragments is left out, so that the decoder's concealment hides what is missing
/// (docs/decisions/2026-11-10-live-rtsp-h264.md).
/// </summary>
internal sealed class RtpH264Depacketizer
{
    // More than any access unit a camera sends; past it the unit is dropped as damaged rather than grown without bound.
    private const int MaxAccessUnit = 32 * 1024 * 1024;
    private static readonly byte[] StartCode = [0, 0, 0, 1];

    private readonly int _payloadType;
    private readonly MemoryStream _unit = new();
    private readonly MemoryStream _fragment = new();
    private byte[][] _parameterSets;
    private bool _inUnit;
    private uint _timestamp;
    private bool _damaged;
    private bool _keyframe;
    private bool _picture;
    private bool _inFragment;
    private int _expected = -1;
    private bool _gapBeforeNext;

    /// <param name="payloadType">The SDP's payload type for the track (96 to 127, from <c>a=rtpmap</c>); -1 for any.</param>
    /// <param name="parameterSets">
    /// The SDP's <c>sprop-parameter-sets</c> (SPS and PPS NAL units), put before the first access unit; empty for none.
    /// </param>
    public RtpH264Depacketizer(int payloadType = -1, byte[][]? parameterSets = null)
    {
        _payloadType = payloadType;
        _parameterSets = parameterSets ?? [];
    }

    /// <summary>Packets the sequence numbers say never arrived.</summary>
    public long LostPackets { get; private set; }

    /// <summary>The NAL units of a <c>sprop-parameter-sets</c> value: base64, comma separated (RFC 6184, 8.1).</summary>
    /// <exception cref="FormatException">Not base64.</exception>
    public static byte[][] ParseParameterSets(string value) =>
        value.Split(',')
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .Select(Convert.FromBase64String)
            .Where(nal => nal.Length > 0)
            .ToArray();

    /// <summary>
    /// Adds an RTP packet (header included), appending to <paramref name="completed"/> the access units it completes:
    /// the one before, if this packet starts another timestamp before that one's marker came, and its own, at the
    /// marker bit.
    /// </summary>
    public void Add(ReadOnlySpan<byte> packet, List<RtpAccessUnit> completed)
    {
        if (!TryParse(packet, out var header, out var payload))
        {
            // Not RTP version 2, or cut short: as good as lost.
            MarkGap();
            return;
        }

        if (_payloadType >= 0 && header.PayloadType != _payloadType)
        {
            return;
        }

        if (_expected >= 0)
        {
            int gap = (ushort)(header.Sequence - _expected);
            if (gap >= 0x8000)
            {
                return; // an old or repeated packet: over TCP never, and reordering over UDP isn't undone
            }

            if (gap > 0)
            {
                LostPackets += gap;
                MarkGap();
            }
        }

        _expected = (ushort)(header.Sequence + 1);
        if (_inUnit && header.Timestamp != _timestamp)
        {
            // The marker packet of the unit before was lost (or never sent): the new timestamp ends it.
            completed.Add(Complete(damaged: true));
        }

        if (!_inUnit)
        {
            _inUnit = true;
            _timestamp = header.Timestamp;
            _damaged = _gapBeforeNext;
            _gapBeforeNext = false;
        }

        AddPayload(payload);
        if (header.Marker)
        {
            completed.Add(Complete(damaged: false));
        }
    }

    /// <summary>The access unit in progress at the end of the stream, if any, as damaged (its marker never came).</summary>
    public RtpAccessUnit? Flush() => _inUnit ? Complete(damaged: true) : null;

    // Also RtpJpegDepacketizer's: the fixed header (RFC 3550, 5.1), its CSRCs, extension and padding skipped.
    internal static bool TryParse(ReadOnlySpan<byte> packet, out RtpHeader header, out ReadOnlySpan<byte> payload)
    {
        header = default;
        payload = default;
        if (packet.Length < 12 || packet[0] >> 6 != 2)
        {
            return false;
        }

        int offset = 12 + ((packet[0] & 0x0F) * 4);
        if ((packet[0] & 0x10) != 0)
        {
            // A header extension: 16 bits of profile, 16 of its length in 32-bit words.
            if (packet.Length < offset + 4)
            {
                return false;
            }

            offset += 4 + (((packet[offset + 2] << 8) | packet[offset + 3]) * 4);
        }

        int end = packet.Length;
        if ((packet[0] & 0x20) != 0)
        {
            end -= packet[^1]; // padding: its last byte counts it
        }

        if (offset > end)
        {
            return false;
        }

        header = new RtpHeader(
            (packet[1] & 0x80) != 0,
            packet[1] & 0x7F,
            (ushort)((packet[2] << 8) | packet[3]),
            (uint)((packet[4] << 24) | (packet[5] << 16) | (packet[6] << 8) | packet[7]));
        payload = packet[offset..end];
        return true;
    }

    private void AddPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            _damaged = true;
            return;
        }

        int type = payload[0] & 0x1F;
        switch (type)
        {
            case >= 1 and <= 23:
                AddNal(payload);
                break;
            case 24: // STAP-A: a byte of NAL header, then units each behind a 16-bit size
                for (int at = 1; at < payload.Length;)
                {
                    if (at + 2 > payload.Length)
                    {
                        _damaged = true;
                        break;
                    }

                    int size = (payload[at] << 8) | payload[at + 1];
                    at += 2;
                    if (size == 0 || at + size > payload.Length)
                    {
                        _damaged = true;
                        break;
                    }

                    AddNal(payload.Slice(at, size));
                    at += size;
                }

                break;
            case 28: // FU-A: the FU indicator (the NAL's F and NRI), the FU header (start, end, the NAL's type), a piece
                AddFragment(payload);
                break;
            default:
                // 25 to 27 and 29 (STAP-B, MTAP16, MTAP24, FU-B) are the interleaved mode's, which the SDP refused;
                // 0, 30 and 31 are reserved. Neither can be read: what they held is missing.
                _damaged = true;
                break;
        }
    }

    private void AddFragment(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 3)
        {
            _damaged = true;
            return;
        }

        bool start = (payload[1] & 0x80) != 0;
        bool end = (payload[1] & 0x40) != 0;
        if (start)
        {
            if (_inFragment)
            {
                _damaged = true; // the one before never ended
            }

            _fragment.SetLength(0);
            _fragment.WriteByte((byte)((payload[0] & 0xE0) | (payload[1] & 0x1F)));
            _inFragment = true;
        }
        else if (!_inFragment)
        {
            _damaged = true; // its start was lost: the rest of it is no use
            return;
        }

        _fragment.Write(payload[2..]);
        if (_fragment.Length > MaxAccessUnit)
        {
            _inFragment = false;
            _damaged = true;
            return;
        }

        if (end)
        {
            _inFragment = false;
            AddNal(_fragment.GetBuffer().AsSpan(0, (int)_fragment.Length));
        }
    }

    private void AddNal(ReadOnlySpan<byte> nal)
    {
        if (_unit.Length + nal.Length > MaxAccessUnit)
        {
            _damaged = true;
            return;
        }

        int type = nal[0] & 0x1F;
        _keyframe |= type == 5;
        _picture |= type is >= 1 and <= 5;

        _unit.Write(StartCode);
        _unit.Write(nal);
    }

    // A gap: the unit in progress (if any) may have lost packets, and so may the next one (the gap may hold its start).
    private void MarkGap()
    {
        if (_inUnit)
        {
            _damaged = true;
        }

        if (_inFragment)
        {
            _inFragment = false;
            _damaged = true;
        }

        _gapBeforeNext = true;
    }

    private RtpAccessUnit Complete(bool damaged)
    {
        if (_inFragment)
        {
            _inFragment = false;
            damaged = true;
        }

        var data = new MemoryStream((int)_unit.Length + _parameterSets.Sum(p => p.Length + 4));
        foreach (var parameterSet in _parameterSets)
        {
            data.Write(StartCode);
            data.Write(parameterSet);
        }

        _parameterSets = [];
        data.Write(_unit.GetBuffer().AsSpan(0, (int)_unit.Length));
        var unit = new RtpAccessUnit(data.ToArray(), _timestamp, _damaged || damaged, _keyframe, _picture);
        _unit.SetLength(0);
        _inUnit = false;
        _damaged = false;
        _keyframe = false;
        _picture = false;
        return unit;
    }

    internal readonly record struct RtpHeader(bool Marker, int PayloadType, ushort Sequence, uint Timestamp);
}

/// <summary>An access unit rebuilt from RTP: Annex B, its RTP timestamp (90 kHz), and what may be missing.</summary>
/// <param name="Data">Its NAL units, each behind a 4-byte start code (the SDP's parameter sets before the first unit).</param>
/// <param name="Timestamp">The RTP timestamp of its packets.</param>
/// <param name="Damaged">Packets may be missing from it (a sequence gap, a fragment without its start or end).</param>
/// <param name="HasIdr">It holds an IDR picture's slice (NAL type 5), where decoding can start again.</param>
/// <param name="HasPicture">
/// It holds a slice (NAL types 1 to 5). A damaged unit may hold none, when every slice of its picture lost a fragment:
/// <c>H264Decoder</c> refuses such a unit, so skip it, and the next picture's gap in <c>frame_num</c> stands in for
/// the lost one (<c>ConcealErrors</c>).
/// </param>
internal sealed record RtpAccessUnit(byte[] Data, uint Timestamp, bool Damaged, bool HasIdr, bool HasPicture);
