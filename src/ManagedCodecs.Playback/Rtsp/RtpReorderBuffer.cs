namespace ManagedCodecs.Playback.Rtsp;

/// <summary>
/// Puts RTP packets that came over UDP back in sequence-number order before a depacketizer sees them
/// (docs/decisions/2026-11-14-live-rtsp-udp.md, decision 2). A packet ahead of the next expected one is held; the held
/// packets go on in order once the missing one comes, or once the first of them has waited the window, by arrival
/// time. Then the missing one is skipped, and the depacketizer, seeing the gap, counts it lost. A duplicate, or a packet
/// that comes after it was skipped, is dropped.
/// </summary>
internal sealed class RtpReorderBuffer
{
    // More than a window's worth at any bitrate a camera sends; past it the oldest gap is given up at once.
    private const int MaxHeld = 2048;

    private readonly TimeSpan _window;
    private readonly SortedDictionary<long, (byte[] Packet, TimeSpan Arrival)> _held = [];
    private long _expected = -1;

    public RtpReorderBuffer(TimeSpan window)
    {
        _window = window;
    }

    /// <summary>
    /// Adds a packet that arrived at <paramref name="now"/>, appending to <paramref name="ready"/> the packets that can
    /// go on, in order.
    /// </summary>
    public void Add(byte[] packet, TimeSpan now, List<byte[]> ready)
    {
        if (packet.Length < 12)
        {
            ready.Add(packet); // not RTP: the depacketizer counts it as damage
            return;
        }

        int sequence = (packet[2] << 8) | packet[3];
        if (_expected < 0)
        {
            _expected = sequence;
        }

        // The sequence number extended past 16 bits: within half the range of the expected one, either side.
        long extended = _expected + (short)(ushort)(sequence - (int)(_expected & 0xFFFF));
        if (extended < _expected || _held.ContainsKey(extended))
        {
            return; // skipped already, or a duplicate
        }

        _held[extended] = (packet, now);
        Release(ready);
        Poll(now, ready);
    }

    /// <summary>Gives up the gaps whose first held packet has waited the window by <paramref name="now"/>.</summary>
    public void Poll(TimeSpan now, List<byte[]> ready)
    {
        while (_held.Count > 0 && (_held.Count > MaxHeld || OldestArrival() + _window <= now))
        {
            _expected = _held.Keys.First();
            Release(ready);
        }
    }

    /// <summary>How long from <paramref name="now"/> until <see cref="Poll"/> would give a gap up; null when none is held.</summary>
    public TimeSpan? Remaining(TimeSpan now)
    {
        if (_held.Count == 0)
        {
            return null;
        }

        var remaining = OldestArrival() + _window - now;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private TimeSpan OldestArrival()
    {
        var oldest = TimeSpan.MaxValue;
        foreach (var held in _held.Values)
        {
            if (held.Arrival < oldest)
            {
                oldest = held.Arrival;
            }
        }

        return oldest;
    }

    private void Release(List<byte[]> ready)
    {
        while (_held.TryGetValue(_expected, out var held))
        {
            ready.Add(held.Packet);
            _held.Remove(_expected);
            _expected++;
        }
    }
}
