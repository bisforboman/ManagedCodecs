using ManagedCodecs.Playback.Rtsp;

namespace ManagedCodecs.Playback;

/// <summary>
/// Turns RTP packets carrying audio back into what a decoder takes: AAC's access units (RFC 3640, modes AAC-hbr and
/// AAC-lbr: an AU header section, then the access units, several to a packet or one fragmented over packets), or
/// AAC's as LATM (RFC 6416: audioMuxElements, each a length and an access unit, or several, whole in a packet or over
/// packets up to the marker bit), or G.711's samples and G.726's codes (RFC 3551: a packet's payload as it is). A gap in the sequence
/// numbers drops the fragmented access unit or audioMuxElement it cut; nothing stands in for what was lost
/// (docs/decisions/2026-11-15-live-rtsp-audio.md). RFC 3640's interleaved access units are put back in order: each is
/// placed in time by its AU-Index-deltas, held until those before it have come, and given up on once a later one has
/// come further than the SDP's maxDisplacement after it, at most a quarter of a second
/// (docs/decisions/2026-11-17-live-rtp-interleaving.md).
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

    // De-interleaving (RFC 3640, 3.2.3.2): on once the SDP says it may come (maxDisplacement) or a packet shows it (an
    // AU-Index-delta above 0). The access units held, by their RTP timestamps extended past 32 bits; the next one due;
    // the latest held; how long one may be waited for; and each one's duration.
    private readonly SortedDictionary<long, byte[]> _held = [];
    private readonly long _bound;
    private readonly int _duration;
    private bool _interleaved;
    private bool _started;
    private long _next;
    private long _newest;
    private long _reference;
    private bool _referenced;

    public RtpAudioDepacketizer(SdpAudioTrack track)
    {
        _payloadType = track.PayloadType;
        _aac = track.Codec == CodecIds.Aac;
        _sizeLength = track.SizeLength;
        _indexLength = track.IndexLength;
        _indexDeltaLength = track.IndexDeltaLength;
        _latmSubFrames = track.LatmSubFrames;

        // AAC's frame is 1024 samples (960 is left to constantDuration, which a sender that interleaves must give).
        _duration = track.ConstantDuration > 0 ? track.ConstantDuration : 1024;
        long cap = track.SampleRate / 4;
        _bound = track.MaxDisplacement > 0 ? Math.Min(track.MaxDisplacement, cap) : cap;
        _interleaved = _aac && track.MaxDisplacement > 0;
    }

    /// <summary>Packets the sequence numbers say never arrived.</summary>
    public long LostPackets { get; private set; }

    /// <summary>Access units dropped: a fragmented one that lost a packet, and packets whose AU headers don't fit their payload.</summary>
    public long Damaged { get; private set; }

    /// <summary>
    /// Adds an RTP packet (header included), appending to <paramref name="completed"/> the access units it completes
    /// (AAC), or its payload (G.711's samples, G.726's codes), in decoding order; and to <paramref name="times"/> where
    /// each is in time: an RTP timestamp, and the unit's place among those that follow it in one packet with that
    /// timestamp (their own duration apart). De-interleaved access units each have their own timestamp, and place 0.
    /// </summary>
    public void Add(ReadOnlySpan<byte> packet, List<byte[]> completed, List<(uint Timestamp, int Index)>? times = null)
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
                times?.Add((header.Timestamp, 0));
            }

            return;
        }

        int first = completed.Count;
        if (_latmSubFrames > 0)
        {
            AddLatm(header, payload, completed);
        }
        else
        {
            AddAac(header, payload, completed, times);
            return;
        }

        for (int i = first; i < completed.Count; i++)
        {
            times?.Add((header.Timestamp, i - first));
        }
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
    // its size in sizelength bits and an index (the first's) or index delta (the others'). The AAC modes code the
    // index 0 (3.3.5, 3.3.6), and a delta above 0 is the gap, less one, to the access unit before it in decoding
    // order: the access units are interleaved (3.2.1.1).
    private void AddAac(RtpH264Depacketizer.RtpHeader header, ReadOnlySpan<byte> payload, List<byte[]> completed, List<(uint Timestamp, int Index)>? times)
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
        Span<int> sizes = count <= 64 ? stackalloc int[count] : new int[count];
        Span<int> places = count <= 64 ? stackalloc int[count] : new int[count];
        int at = 16;
        int place = 0;
        for (int i = 0; i < count; i++)
        {
            sizes[i] = Bits(payload, ref at, _sizeLength);
            int index = Bits(payload, ref at, i == 0 ? _indexLength : _indexDeltaLength);
            if (i == 0 && index != 0)
            {
                Damage(); // an AU-Index other than 0: not an AAC mode's (variable durations aren't AAC's)
                return;
            }

            place += i == 0 ? 0 : index + 1;
            places[i] = place;
            _interleaved |= i > 0 && index > 0;
        }

        var units = new List<byte[]>(count);
        var unitPlaces = new List<int>(count);
        for (int i = 0; i < count; i++)
        {
            int size = sizes[i];
            int left = payload.Length - data;
            if (count == 1 && (size > left || _fragmentSize >= 0))
            {
                // A fragment: an access unit larger than a packet is alone in its packets, each with its whole size
                // in the header, and the last has the marker bit (RFC 3640, 3.2.3.1 and 3.1).
                AddFragment(header, size, payload.Slice(data), units);
                unitPlaces.Add(0);
                break;
            }

            if (size > left)
            {
                Damage(); // the headers promise more than the packet holds
                return;
            }

            if (size > 0)
            {
                units.Add(payload.Slice(data, size).ToArray());
                unitPlaces.Add(places[i]);
            }

            data += size;
        }

        for (int i = 0; i < units.Count; i++)
        {
            if (!_interleaved)
            {
                completed.Add(units[i]);
                times?.Add((header.Timestamp, i));
            }
            else
            {
                // Its sampling instant: the packet's timestamp, for the first, and so many durations after (3.2.3.2).
                Hold(Extend(header.Timestamp) + ((long)unitPlaces[i] * _duration), units[i], completed, times);
            }
        }
    }

    // De-interleaving: an access unit waits until those due before it have come, or until one has come more than the
    // bound after the earliest still missing (the maximum displacement, 3.2.3.3), when that one is given up on. At the
    // start nothing says which is due first, and one before the first to come may come up to the bound after it: the
    // first is the earliest held once the bound has passed. One that comes after it was given up on (a sender
    // interleaving further than the bound), or twice, is dropped.
    private void Hold(long time, byte[] unit, List<byte[]> completed, List<(uint Timestamp, int Index)>? times)
    {
        if ((_started && time < _next) || _held.ContainsKey(time))
        {
            if (_started && time < _next)
            {
                Damage(); // too late: given up on, or given already
            }

            return;
        }

        _held[time] = unit;
        _newest = _held.Count == 1 && !_started ? time : Math.Max(_newest, time);
        if (!_started)
        {
            if (_newest - _held.Keys.First() <= _bound)
            {
                return;
            }

            _started = true;
            _next = _held.Keys.First();
        }

        while (_held.Count > 0)
        {
            long earliest = _held.Keys.First();
            if (earliest != _next && _newest - _next <= _bound)
            {
                return; // the one due may still come
            }

            completed.Add(_held[earliest]);
            times?.Add(((uint)earliest, 0));
            _held.Remove(earliest);
            _next = earliest + _duration;
        }
    }

    // An RTP timestamp extended past 32 bits: within half its range of the one before, either side.
    private long Extend(uint timestamp)
    {
        if (!_referenced)
        {
            _referenced = true;
            _reference = timestamp;
        }

        _reference += (int)(timestamp - (uint)_reference);
        return _reference;
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
