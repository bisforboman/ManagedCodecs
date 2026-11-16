using System.Buffers.Binary;
using ManagedCodecs.H264;
using ManagedCodecs.Playback;
using ManagedCodecs.Playback.Rtsp;

namespace ManagedCodecs.Tests.Playback;

/// <summary>
/// The receiving side of RTP over UDP under a network that loses, repeats, reorders and delays packets (issue #527):
/// recordings played three times over as one stream, through a simulated network of a seed's choosing, then the reorder
/// window, the depacketizer and the decoder, as LivePlayer chains them. The loopback interface does none of this, so the
/// test does it, on its own clock. What must hold whatever the seed:
/// <list type="bullet">
/// <item>The reorder window passes packets on in sequence order, each once.</item>
/// <item>The depacketizer counts as lost exactly the packets the window never passed on, between the first and last it
/// did.</item>
/// <item>A unit not marked damaged is the clean stream's, byte for byte; one whose packets all came, the packet before it
/// too, is not marked damaged.</item>
/// <item>H.264: the decoder throws nothing but InvalidDataException (after which LivePlayer waits for an IDR picture),
/// every frame it doesn't mark damaged is the clean stream's frame of that time, bit for bit, and an IDR picture that
/// came whole gives a clean frame whatever was lost before it.</item>
/// <item>AAC: the decoder throws nothing but InvalidDataException (a frame LivePlayer counts and skips).</item>
/// </list>
/// </summary>
public class LossyNetworkTests
{
    // The reorder window LivePlayer uses (docs/decisions/2026-11-14-live-rtsp-udp.md, decision 2).
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(50);

    public static TheoryData<string, int> VideoCases()
    {
        var data = new TheoryData<string, int>();
        foreach (string name in (string[])["h264-main-slices", "h264-bench-720p", "h264-b-motion"])
        {
            for (int seed = 1; seed <= 6; seed++)
            {
                data.Add(name, seed);
            }
        }

        return data;
    }

    public static TheoryData<string, int> AudioCases()
    {
        var data = new TheoryData<string, int>();
        foreach (string name in (string[])["aac-stereo-48k", "aac-stereo-48k-single", "gstreamer/aac-fragmented", "gstreamer/aac-single"])
        {
            for (int seed = 1; seed <= 6; seed++)
            {
                data.Add(name, seed);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(VideoCases))]
    public void H264ThroughALossyNetworkKeepsEveryUndamagedFrameExact(string name, int seed)
    {
        var clean = Looped(RtpH264DepacketizerTests.Recording(name), 3, 90000 / 25);
        var parameterSets = RtpH264DepacketizerTests.SdpParameterSets(name);
        var cleanUnits = Depacketize(clean, parameterSets, out _);
        var cleanFrames = Decode(cleanUnits, out _);
        Assert.Equal(cleanUnits.Count, cleanFrames.Count);
        Assert.DoesNotContain(cleanFrames.Values, f => f.Damaged);

        var (released, lostExpected) = ThroughNetwork(clean, seed, out var impairments);
        var units = Depacketize(released, parameterSets, out long lost);
        Assert.Equal(lostExpected, lost);

        // Units not marked damaged are the clean stream's (the first given, which the SDP's parameter sets are put
        // before, is left out); a unit whose packets all came, the one before it too, is not marked damaged.
        var byTimestamp = cleanUnits.ToDictionary(u => u.Timestamp, u => u.Data);
        var arrived = released.Select(Sequence).ToHashSet();
        var packetsOf = clean.GroupBy(Timestamp).ToDictionary(g => g.Key, g => g.Select(Sequence).ToList());
        foreach (var unit in units.Skip(1))
        {
            if (!unit.Damaged)
            {
                Assert.Equal(byTimestamp[unit.Timestamp], unit.Data);
            }

            var own = packetsOf[unit.Timestamp];
            if (own.All(arrived.Contains) && arrived.Contains((ushort)(own[0] - 1)))
            {
                Assert.False(unit.Damaged, $"Unit {unit.Timestamp} came whole but was marked damaged ({impairments}).");
            }
        }

        var frames = Decode(units, out int failed);
        foreach (var (pts, frame) in frames)
        {
            if (!frame.Damaged)
            {
                Assert.True(cleanFrames.TryGetValue(pts, out var expected), $"No clean frame at {pts} ({impairments}).");
                Assert.True(expected.Hash == frame.Hash, $"The undamaged frame at {pts} isn't the clean one ({impairments}).");
            }
        }

        // An IDR picture that came whole predicts from nothing: its frame is clean, whatever was lost before it.
        foreach (var idr in units.Where(u => u.HasIdr && !u.Damaged))
        {
            Assert.True(frames.TryGetValue(idr.Timestamp, out var frame) && !frame.Damaged, $"The whole IDR picture at {idr.Timestamp} gave no clean frame: {failed} units failed ({impairments}).");
        }
    }

    [Fact]
    public void RecoversAtTheNextIdrPictureAfterLoss()
    {
        // Every 20th packet of the first pass lost, the two passes after it whole: their frames all come back exact.
        // The recording's parameter sets are the SDP's alone (FFmpeg's packetizer), and the first IDR picture loses a
        // packet, so its unit, which they come before, is skipped: each later IDR picture must bring them again.
        const string Name = "h264-bench-720p";
        var clean = Looped(RtpH264DepacketizerTests.Recording(Name), 3, 90000 / 25);
        var parameterSets = RtpH264DepacketizerTests.SdpParameterSets(Name);
        var cleanFrames = Decode(Depacketize(clean, parameterSets, out _), out _);
        int pass = clean.Count / 3;
        var lossy = clean.Where((packet, i) => i >= pass || i % 20 != 19).ToList();
        var units = Depacketize(lossy, parameterSets, out long lost);
        var frames = Decode(units, out int failed);

        Assert.Equal((long)(pass / 20), lost);
        Assert.Equal(0, failed);
        Assert.True(frames.Count < cleanFrames.Count, "The first pass's IDR picture lost a packet: its pictures are skipped.");
        var later = units.Skip(units.Count / 3).Select(u => u.Timestamp).ToList();
        Assert.Equal(CleanUnitsOf(later), later.Select(t => frames[t]).ToList());

        List<(string Hash, bool Damaged)> CleanUnitsOf(List<uint> times) => times.Select(t => (cleanFrames[t].Hash, false)).ToList();
    }

    [Theory]
    [MemberData(nameof(AudioCases))]
    public void AacThroughALossyNetworkKeepsEveryAccessUnitExact(string name, int seed)
    {
        var track = RtpAudioDepacketizerTests.Track(name);
        var clean = Looped(RtpH264DepacketizerTests.Recording(name), 3, 1024);
        var cleanUnits = DepacketizeAudio(track, clean, out _, out _);

        var (released, lostExpected) = ThroughNetwork(clean, seed, out var impairments);
        var units = DepacketizeAudio(track, released, out long lost, out long damaged);
        Assert.Equal(lostExpected, lost);

        // Every access unit that comes out is one of the clean stream's, in order, each once.
        int at = 0;
        foreach (var unit in units)
        {
            while (at < cleanUnits.Count && !cleanUnits[at].AsSpan().SequenceEqual(unit))
            {
                at++;
            }

            Assert.True(at < cleanUnits.Count, $"An access unit that isn't the clean stream's, or out of order ({impairments}).");
            at++;
        }

        Assert.True(units.Count + damaged <= cleanUnits.Count, $"{units.Count} units and {damaged} damaged of {cleanUnits.Count}.");

        var decoder = AudioPipeline.CreateDecoder(new AudioStreamInfo(track.Codec, track.SampleRate, track.Channels, 0, new Rational(1, track.SampleRate), track.Config, 0));
        foreach (var unit in units)
        {
            try
            {
                decoder.Decode(unit);
            }
            catch (InvalidDataException)
            {
                // LivePlayer counts it damaged and goes on.
            }
        }
    }

    // A network of the seed's choosing between server and player: each packet sent a millisecond after the one
    // before; some lost, some sent twice, some delayed within the window (reordered), a few past it. Then the reorder
    // window as LivePlayer runs it, polled at each arrival and once all have come. Returns what it passed on, and how
    // many packets the depacketizer should count lost: those never passed on, between the first and last that were.
    private static (List<byte[]> Released, long Lost) ThroughNetwork(List<byte[]> packets, int seed, out string impairments)
    {
        var random = new Random(seed);
        double loss = 0.005 * seed;
        var arrivals = new List<(TimeSpan At, int Order, byte[] Packet)>();
        int dropped = 0;
        int repeated = 0;
        int reordered = 0;
        int late = 0;
        for (int i = 0; i < packets.Count; i++)
        {
            var sent = TimeSpan.FromMilliseconds(i);
            double roll = random.NextDouble();
            if (roll < loss)
            {
                dropped++;
                continue;
            }

            var delay = TimeSpan.Zero;
            if (roll < loss + 0.05)
            {
                delay = TimeSpan.FromMilliseconds(1 + (random.NextDouble() * 40));
                reordered++;
            }
            else if (roll < loss + 0.06)
            {
                delay = TimeSpan.FromMilliseconds(80 + (random.NextDouble() * 150));
                late++;
            }

            arrivals.Add((sent + delay, arrivals.Count, packets[i]));
            if (random.NextDouble() < 0.02)
            {
                arrivals.Add((sent + delay + TimeSpan.FromMilliseconds(random.NextDouble() * 20), arrivals.Count, packets[i]));
                repeated++;
            }
        }

        impairments = $"seed {seed}: {dropped} lost, {repeated} repeated, {reordered} reordered, {late} late of {packets.Count}";
        var buffer = new RtpReorderBuffer(Window);
        var released = new List<byte[]>();
        foreach (var (at, _, packet) in arrivals.OrderBy(a => a.At).ThenBy(a => a.Order))
        {
            buffer.Poll(at, released);
            buffer.Add(packet, at, released);
        }

        buffer.Poll(TimeSpan.FromHours(1), released);

        // In sequence order, each once, and only packets that were sent.
        var order = released.Select(p => Extended(Sequence(p), packets)).ToList();
        for (int i = 1; i < order.Count; i++)
        {
            Assert.True(order[i] > order[i - 1], $"Passed on out of order or twice at {i} ({impairments}).");
        }

        long lost = order.Count == 0 ? 0 : order[^1] - order[0] + 1 - order.Count;
        return (released, lost);
    }

    // A packet's place in the recording (sequence numbers there rise by one from the first).
    private static long Extended(ushort sequence, List<byte[]> packets) => (ushort)(sequence - Sequence(packets[0]));

    // The recording played times over as one stream: the sequence numbers go on, and each pass's timestamps follow the
    // last pass's by its length and one frame.
    private static List<byte[]> Looped(List<byte[]> packets, int times, uint frame)
    {
        uint first = Timestamp(packets[0]);
        uint length = packets.Select(p => Timestamp(p) - first).Max() + frame;
        var looped = new List<byte[]>();
        ushort sequence = Sequence(packets[0]);
        for (int pass = 0; pass < times; pass++)
        {
            foreach (var packet in packets)
            {
                var copy = (byte[])packet.Clone();
                BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(2), sequence++);
                BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(4), Timestamp(packet) + ((uint)pass * length));
                looped.Add(copy);
            }
        }

        return looped;
    }

    private static List<RtpAccessUnit> Depacketize(List<byte[]> packets, byte[][] parameterSets, out long lost)
    {
        var depacketizer = new RtpH264Depacketizer(96, parameterSets);
        var units = new List<RtpAccessUnit>();
        foreach (var packet in packets)
        {
            depacketizer.Add(packet, units);
        }

        if (depacketizer.Flush() is { } last)
        {
            units.Add(last);
        }

        lost = depacketizer.LostPackets;
        return units;
    }

    private static List<byte[]> DepacketizeAudio(SdpAudioTrack track, List<byte[]> packets, out long lost, out long damaged)
    {
        var depacketizer = new RtpAudioDepacketizer(track);
        var units = new List<byte[]>();
        foreach (var packet in packets)
        {
            depacketizer.Add(packet, units);
        }

        lost = depacketizer.LostPackets;
        damaged = depacketizer.Damaged;
        return units;
    }

    // Decodes as LivePlayer does: from an IDR picture on, skipping units with no picture left, and after a unit the
    // decoder refuses, waiting for the next IDR picture. Each frame by its time (the unit's RTP timestamp), with its MD5.
    private static Dictionary<uint, (string Hash, bool Damaged)> Decode(List<RtpAccessUnit> units, out int failed)
    {
        var decoder = new H264Decoder(ReadOnlySpan<byte>.Empty) { ConcealErrors = true, PacketTimes = true };
        var frames = new Dictionary<uint, (string Hash, bool Damaged)>();
        bool waitForIdr = true;
        long timestamp = 0;
        failed = 0;
        void Add(IReadOnlyList<VideoFrame> decoded)
        {
            foreach (var frame in decoded)
            {
                frames[(uint)frame.Pts] = (FrameMd5.Hash(frame), frame.IsDamaged);
                frame.Dispose();
            }
        }

        foreach (var unit in units)
        {
            timestamp += (int)(unit.Timestamp - (uint)timestamp);
            if (unit.HasIdr)
            {
                waitForIdr = false;
            }
            else if (waitForIdr || !unit.HasPicture)
            {
                continue;
            }

            try
            {
                Add(decoder.Decode(unit.Data, timestamp));
            }
            catch (InvalidDataException)
            {
                failed++;
                waitForIdr = true;
            }
        }

        Add(decoder.Flush());
        return frames;
    }

    private static ushort Sequence(byte[] packet) => BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2));

    private static uint Timestamp(byte[] packet) => BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4));
}
