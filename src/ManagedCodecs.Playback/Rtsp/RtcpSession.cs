using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ManagedCodecs.Playback.Rtsp;

/// <summary>
/// What a receiver tells a sender about one RTP stream (RFC 3550): the packets of the stream counted as they arrive
/// (<see cref="Received"/>), the sender's reports read (<see cref="Report"/>), and a receiver report built from both
/// (<see cref="Build"/>): how many packets were lost, the interarrival jitter, and when the last sender report came.
/// Cameras and servers that time out a reader they hear nothing from hear this
/// (docs/decisions/2026-11-15-live-rtcp.md). Its members may be called on different threads.
/// </summary>
internal sealed class RtcpSession
{
    private const int SenderReport = 200;
    private const int ReceiverReport = 201;
    private const int SourceDescription = 202;

    private static readonly DateTime NtpEpoch = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly object _lock = new();
    private readonly int _clockRate;
    private readonly uint _ssrc;
    private readonly byte[] _name;
    private bool _any;
    private uint _source;
    private long _base;
    private long _highest;
    private long _received;
    private long _expectedPrior;
    private long _receivedPrior;
    private uint _transit;
    private uint _jitter;
    private uint _lastSenderReport;
    private uint _senderReportSource;
    private ulong _senderReportNtp;
    private uint _senderReportRtp;
    private TimeSpan? _senderReportArrival;

    /// <param name="clockRate">The RTP timestamps' rate: 90000 for video, the sample rate for audio.</param>
    public RtcpSession(int clockRate)
    {
        _clockRate = clockRate;

        // The receiver's SSRC and its CNAME, both random and of this session only (RFC 7022): nothing of the machine's.
        var random = new byte[12];
        using (var generator = RandomNumberGenerator.Create())
        {
            generator.GetBytes(random);
        }

        _ssrc = BinaryPrimitives.ReadUInt32BigEndian(random);
        var name = new StringBuilder();
        for (int i = 4; i < random.Length; i++)
        {
            name.Append(random[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        _name = Encoding.ASCII.GetBytes(name.ToString());
    }

    /// <summary>The SSRC the reports are sent as.</summary>
    public uint Ssrc => _ssrc;

    /// <summary>
    /// Counts an RTP packet of the stream that arrived at <paramref name="now"/>, in arrival order (before any
    /// reordering): a duplicate counts as received, as RFC 3550's A.3 has it. A packet of another SSRC than the last
    /// one's is a new stream (a camera that restarted its encoder), counted from there.
    /// </summary>
    public void Received(ReadOnlySpan<byte> packet, TimeSpan now)
    {
        if (packet.Length < 12)
        {
            return;
        }

        int sequence = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2));
        uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(4));
        uint source = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(8));

        // The arrival time in the timestamps' units, whole seconds apart from the rest: a product of all the ticks
        // would overflow after some months of running.
        long arrival = ((now.Ticks / TimeSpan.TicksPerSecond) * _clockRate) + ((now.Ticks % TimeSpan.TicksPerSecond) * _clockRate / TimeSpan.TicksPerSecond);
        uint transit = (uint)arrival - timestamp;
        lock (_lock)
        {
            if (!_any || source != _source)
            {
                _any = true;
                _source = source;
                _base = sequence;
                _highest = sequence;
                _received = 0;
                _expectedPrior = 0;
                _receivedPrior = 0;
                _jitter = 0;
                _transit = transit;
            }

            // The sequence number extended past 16 bits: within half the range of the highest seen, either side.
            long extended = _highest + (short)(ushort)(sequence - (int)(_highest & 0xFFFF));
            if (extended > _highest)
            {
                _highest = extended;
            }

            _received++;

            // The interarrival jitter (6.4.1, and A.8's arithmetic): the mean deviation of the difference in spacing
            // between arrival and RTP timestamp, kept times 16.
            int d = (int)(transit - _transit);
            _transit = transit;
            _jitter += (uint)Math.Abs((long)d) - ((_jitter + 8) >> 4);
        }
    }

    /// <summary>
    /// Reads a compound RTCP packet from the sender that arrived at <paramref name="now"/>: its sender report's NTP
    /// time is what the next receiver report echoes (LSR), while the stream's packets are from the SSRC that sent it.
    /// Anything else in it, and anything malformed, is ignored.
    /// </summary>
    public void Report(ReadOnlySpan<byte> packet, TimeSpan now)
    {
        while (packet.Length >= 4 && packet[0] >> 6 == 2)
        {
            int length = (BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2)) + 1) * 4;
            if (length > packet.Length)
            {
                return;
            }

            if (packet[1] == SenderReport && length >= 28)
            {
                // The middle 32 bits of the 64-bit NTP timestamp (6.4.1, LSR).
                uint middle = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(10));
                lock (_lock)
                {
                    _senderReportSource = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(4));
                    _senderReportNtp = BinaryPrimitives.ReadUInt64BigEndian(packet.Slice(8));
                    _senderReportRtp = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(16));
                    _lastSenderReport = middle;
                    _senderReportArrival = now;
                }
            }

            packet = packet.Slice(length);
        }
    }

    /// <summary>
    /// When the camera sampled what carries the RTP timestamp <paramref name="timestamp"/>, by the camera's own clock
    /// (UTC): the stream's last sender report pairs an NTP time with an RTP timestamp (6.4.1), and the timestamp is so
    /// many ticks of the stream's clock from that one, either side. Null before the stream's first sender report, and
    /// while the last one is another SSRC's than the stream's packets.
    /// </summary>
    public DateTime? CaptureTime(uint timestamp)
    {
        ulong ntp;
        uint rtp;
        lock (_lock)
        {
            if (_senderReportArrival is null || (_any && _senderReportSource != _source))
            {
                return null;
            }

            ntp = _senderReportNtp;
            rtp = _senderReportRtp;
        }

        // NTP's seconds count from 1900 and wrap in 2036: a count below half the range is of the era after (RFC 4330, 3).
        // An NTP time of zero is a camera with no clock at all (its timestamps then only relate its tracks, from 1900).
        long seconds = (long)(ntp >> 32);
        if (seconds < 0x80000000 && seconds != 0)
        {
            seconds += 1L << 32;
        }

        long fraction = ((long)(ntp & 0xFFFFFFFF) * TimeSpan.TicksPerSecond) >> 32;
        long offset = (long)(int)(timestamp - rtp) * TimeSpan.TicksPerSecond / _clockRate;
        return NtpEpoch.AddTicks((seconds * TimeSpan.TicksPerSecond) + fraction + offset);
    }

    /// <summary>
    /// The compound packet to send at <paramref name="now"/>: a receiver report (6.4.2) with one report block for the
    /// stream, or none before its first packet, and a source description with the CNAME every compound packet carries
    /// (6.1). The loss fraction is of the packets expected since the last call.
    /// </summary>
    public byte[] Build(TimeSpan now)
    {
        lock (_lock)
        {
            int blocks = _any ? 1 : 0;
            int reportLength = 8 + (24 * blocks);
            int descriptionLength = (4 + 4 + 2 + _name.Length + 1 + 3) & ~3; // header, SSRC, the item, its end, padded
            var packet = new byte[reportLength + descriptionLength];
            var report = packet.AsSpan(0, reportLength);
            report[0] = (byte)(0x80 | blocks);
            report[1] = ReceiverReport;
            BinaryPrimitives.WriteUInt16BigEndian(report.Slice(2), (ushort)((reportLength / 4) - 1));
            BinaryPrimitives.WriteUInt32BigEndian(report.Slice(4), _ssrc);
            if (_any)
            {
                // A.3: lost is what was expected less what came, negative when duplicates outnumber losses; the
                // fraction, of this interval's, is never negative.
                long expected = _highest - _base + 1;
                long lost = Math.Min(Math.Max(expected - _received, -0x800000), 0x7FFFFF);
                long expectedInterval = expected - _expectedPrior;
                long lostInterval = expectedInterval - (_received - _receivedPrior);
                _expectedPrior = expected;
                _receivedPrior = _received;
                int fraction = expectedInterval <= 0 || lostInterval <= 0 ? 0 : (int)Math.Min(255, (lostInterval << 8) / expectedInterval);

                // LSR and DLSR: the stream's last sender report (it may come before the stream's first packet) and the
                // time since it arrived, in 1/65536 s; both 0 before the first, or when the last is another stream's.
                bool echo = _senderReportArrival is not null && _senderReportSource == _source;
                uint delay = echo ? (uint)Math.Max(0, (now - _senderReportArrival!.Value).Ticks * 65536 / TimeSpan.TicksPerSecond) : 0;
                BinaryPrimitives.WriteUInt32BigEndian(report.Slice(8), _source);
                BinaryPrimitives.WriteUInt32BigEndian(report.Slice(12), ((uint)fraction << 24) | ((uint)lost & 0xFFFFFF));
                BinaryPrimitives.WriteUInt32BigEndian(report.Slice(16), (uint)_highest);
                BinaryPrimitives.WriteUInt32BigEndian(report.Slice(20), _jitter >> 4);
                BinaryPrimitives.WriteUInt32BigEndian(report.Slice(24), echo ? _lastSenderReport : 0);
                BinaryPrimitives.WriteUInt32BigEndian(report.Slice(28), delay);
            }

            var description = packet.AsSpan(reportLength);
            description[0] = 0x81;
            description[1] = SourceDescription;
            BinaryPrimitives.WriteUInt16BigEndian(description.Slice(2), (ushort)((descriptionLength / 4) - 1));
            BinaryPrimitives.WriteUInt32BigEndian(description.Slice(4), _ssrc);
            description[8] = 1; // CNAME
            description[9] = (byte)_name.Length;
            _name.CopyTo(description.Slice(10));
            return packet;
        }
    }
}
