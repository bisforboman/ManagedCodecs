using ManagedCodecs.Playback.Rtsp;

namespace ManagedCodecs.Playback;

/// <summary>
/// Turns RTP packets carrying audio back into what a decoder takes: AAC's access units (RFC 3640, modes AAC-hbr and
/// AAC-lbr: an AU header section, then the access units, several to a packet or one fragmented over packets), or
/// AAC's as LATM (RFC 6416: audioMuxElements, each a length and an access unit, or several, whole in a packet or over
/// packets up to the marker bit), or G.711's samples and G.726's codes (RFC 3551: a packet's payload as it is). A gap in the sequence
/// numbers drops the fragmented access unit or audioMuxElement it cut; nothing stands in for what was lost
/// (docs/decisions/2026-11-15-live-rtsp-audio.md).
/// </summary>
internal sealed class RtpAudioDepacketizer
{
    // An AAC access unit is at most 6144 bits a channel (ISO/IEC 14496-3, 4.5.3.1): 48 channels' worth is past any.
    private const int MaxAccessUnit = 48 * 768;

    private readonly int _payloadType;
    private readonly bool _aac;
    private readonly int _sizeLength;
    private readonly int _indexLength;
    private readonly int _indexDeltaLength;
    private readonly int _latmSubFrames;
    private readonly MemoryStream _fragment = new();
    private int _fragmentSize = -1;
    private uint _fragmentTimestamp;
    private bool _dropped;
    private uint _droppedTimestamp;
    private int _expected = -1;
    private bool _afterGap;

    public RtpAudioDepacketizer(SdpAudioTrack track)
    {
        _payloadType = track.PayloadType;
        _aac = track.Codec == CodecIds.Aac;
        _sizeLength = track.SizeLength;
        _indexLength = track.IndexLength;
        _indexDeltaLength = track.IndexDeltaLength;
        _latmSubFrames = track.LatmSubFrames;
    }

    /// <summary>Packets the sequence numbers say never arrived.</summary>
    public long LostPackets { get; private set; }

    /// <summary>Access units dropped: a fragmented one that lost a packet, and packets whose AU headers don't fit their payload.</summary>
    public long Damaged { get; private set; }

    /// <summary>
    /// Adds an RTP packet (header included), appending to <paramref name="completed"/> the access units it completes
    /// (AAC), or its payload (G.711's samples, G.726's codes).
    /// </summary>
    /// <exception cref="NotSupportedException">AAC access units interleaved (a non-zero index, RFC 3640, 3.2.3.1).</exception>
    public void Add(ReadOnlySpan<byte> packet, List<byte[]> completed)
    {
        if (!RtpH264Depacketizer.TryParse(packet, out var header, out var payload))
        {
            // Not RTP version 2, or cut short: as good as lost.
            DropFragment();
            return;
        }

        if (header.PayloadType != _payloadType)
        {
            return;
        }

        if (_expected >= 0)
        {
            int gap = (ushort)(header.Sequence - _expected);
            if (gap >= 0x8000)
            {
                return; // an old or repeated packet
            }

            if (gap > 0)
            {
                LostPackets += gap;
                DropFragment();
                _afterGap = true;
            }
        }

        _expected = (ushort)(header.Sequence + 1);
        if (!_aac)
        {
            if (!payload.IsEmpty)
            {
                completed.Add(payload.ToArray());
            }

            return;
        }

        if (_latmSubFrames > 0)
        {
            AddLatm(header, payload, completed);
            return;
        }

        AddAac(header, payload, completed);
    }

    // LATM (RFC 6416, 6.1): an audioMuxElement's bytes over one packet or more, the last with the marker bit. Without
    // the config in the stream (cpresent=0) it is, for each access unit, PayloadLengthInfo (bytes that add up, the
    // last one below 255; ISO/IEC 14496-3, 1.7.3.2.5) and then that many bytes; other data may follow the last.
    // Nothing marks an element's first packet, so the one after a gap may have lost its start: it is given only if its
    // lengths add up to exactly its bytes.
    private void AddLatm(RtpH264Depacketizer.RtpHeader header, ReadOnlySpan<byte> payload, List<byte[]> completed)
    {
        if (_fragmentSize >= 0 && header.Timestamp != _fragmentTimestamp)
        {
            DropFragment(); // another element before the marker came
        }

        if (_dropped && header.Timestamp == _droppedTimestamp)
        {
            return; // the rest of an element already dropped (and counted)
        }

        if (_fragment.Length + payload.Length > _latmSubFrames * (MaxAccessUnit + 256))
        {
            // More than its access units can be: dropped, with what follows of it.
            _fragmentSize = 0;
            _fragmentTimestamp = header.Timestamp;
            DropFragment();
            return;
        }

        _fragmentSize = 0; // an element is open (its size isn't known ahead)
        _fragmentTimestamp = header.Timestamp;
        _fragment.Write(payload);
        if (!header.Marker)
        {
            return;
        }

        bool suspect = _afterGap;
        _afterGap = false;
        var element = _fragment.GetBuffer().AsSpan(0, (int)_fragment.Length);
        int at = 0;
        int first = completed.Count;
        bool whole = true;
        for (int i = 0; i < _latmSubFrames && whole; i++)
        {
            int size = 0;
            int part;
            do
            {
                whole = at < element.Length;
                part = whole ? element[at++] : 0;
                size += part;
            }
            while (whole && part == 255);

            whole = whole && size <= element.Length - at;
            if (whole && size > 0)
            {
                completed.Add(element.Slice(at, size).ToArray());
            }

            at += size;
        }

        if (!whole || (suspect && at != element.Length))
        {
            // The lengths promise more than came (or, after a gap, don't fit): none of its access units is given.
            completed.RemoveRange(first, completed.Count - first);
            Damage();
        }

        _fragment.SetLength(0);
        _fragmentSize = -1;
    }

    // The AU header section (RFC 3640, 3.2.1): 16 bits counting the headers' bits, then a header for each access unit,
    // its size in sizelength bits and an index (the first's) or index delta (the others'), 0 unless interleaved.
    private void AddAac(RtpH264Depacketizer.RtpHeader header, ReadOnlySpan<byte> payload, List<byte[]> completed)
    {
        int first = _sizeLength + _indexLength;
        int other = _sizeLength + _indexDeltaLength;
        if (payload.Length < 2)
        {
            Damage();
            return;
        }

        int bits = (payload[0] << 8) | payload[1];
        int data = 2 + ((bits + 7) / 8);
        if (bits < first || data > payload.Length || (bits - first) % other != 0)
        {
            Damage();
            return;
        }

        int count = 1 + ((bits - first) / other);
        int at = 16;
        for (int i = 0; i < count; i++)
        {
            int size = Bits(payload, ref at, _sizeLength);
            if (Bits(payload, ref at, i == 0 ? _indexLength : _indexDeltaLength) != 0)
            {
                throw new NotSupportedException("The camera interleaves its AAC access units (RFC 3640), which isn't supported.");
            }

            int left = payload.Length - data;
            if (count == 1 && (size > left || _fragmentSize >= 0))
            {
                // A fragment: an access unit larger than a packet is alone in its packets, each with its whole size
                // in the header, and the last has the marker bit (RFC 3640, 3.2.3.1 and 3.1).
                AddFragment(header, size, payload.Slice(data), completed);
                return;
            }

            if (size > left)
            {
                Damage(); // the headers promise more than the packet holds
                return;
            }

            if (size > 0)
            {
                completed.Add(payload.Slice(data, size).ToArray());
            }

            data += size;
        }
    }

    private void AddFragment(RtpH264Depacketizer.RtpHeader header, int size, ReadOnlySpan<byte> part, List<byte[]> completed)
    {
        if (_fragmentSize >= 0 && (header.Timestamp != _fragmentTimestamp || size != _fragmentSize))
        {
            DropFragment(); // another access unit before the last fragment came
        }

        if (_fragmentSize < 0)
        {
            if (_dropped && header.Timestamp == _droppedTimestamp)
            {
                return; // the rest of a unit already dropped (and counted)
            }

            if (size > MaxAccessUnit || part.Length >= size)
            {
                // Too large for AAC, or whole after all: the unit as it is, if it is one.
                if (part.Length == size && size > 0 && size <= MaxAccessUnit)
                {
                    completed.Add(part.ToArray());
                }
                else
                {
                    Damage();
                }

                return;
            }

            _fragmentSize = size;
            _fragmentTimestamp = header.Timestamp;
        }

        _fragment.Write(part);
        if (_fragment.Length == _fragmentSize)
        {
            completed.Add(_fragment.ToArray());
            _fragment.SetLength(0);
            _fragmentSize = -1;
        }
        else if (_fragment.Length > _fragmentSize || header.Marker)
        {
            DropFragment(); // more than the unit's size, or its end without all of it
        }
    }

    private void DropFragment()
    {
        if (_fragmentSize >= 0)
        {
            Damage();
            _dropped = true;
            _droppedTimestamp = _fragmentTimestamp;
        }

        _fragment.SetLength(0);
        _fragmentSize = -1;
    }

    private void Damage() => Damaged++;

    // The next count bits (at most 16), most significant first; the caller has checked they are there.
    private static int Bits(ReadOnlySpan<byte> data, ref int at, int count)
    {
        int value = 0;
        for (int i = 0; i < count; i++, at++)
        {
            value = (value << 1) | ((data[at >> 3] >> (7 - (at & 7))) & 1);
        }

        return value;
    }
}
