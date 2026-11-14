using ManagedCodecs.Playback.Rtsp;

namespace ManagedCodecs.Tests.Playback;

/// <summary>
/// The reorder window in front of the depacketizers for RTP over UDP (docs/decisions/2026-11-14-live-rtsp-udp.md,
/// decision 2), on hand-made packets and times: the loopback interface never reorders.
/// </summary>
public class RtpReorderBufferTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(50);

    [Fact]
    public void PassesPacketsInOrderAtOnce()
    {
        var buffer = new RtpReorderBuffer(Window);
        Assert.Equal([10, 11, 12], Feed(buffer, (10, 0), (11, 1), (12, 2)));
        Assert.Null(buffer.Remaining(Ms(2)));
    }

    [Fact]
    public void PutsPacketsBackInOrderWithinTheWindow()
    {
        var buffer = new RtpReorderBuffer(Window);
        Assert.Equal([10, 11, 12, 13], Feed(buffer, (10, 0), (12, 1), (13, 2), (11, 30)));
    }

    [Fact]
    public void GivesUpAGapOnceTheWindowHasPassed()
    {
        var buffer = new RtpReorderBuffer(Window);
        Assert.Equal([10], Feed(buffer, (10, 0), (12, 10), (13, 20)));
        Assert.Equal(Ms(50), buffer.Remaining(Ms(10))); // the first held, 12, came at 10 ms

        var ready = new List<byte[]>();
        buffer.Poll(Ms(59), ready);
        Assert.Empty(ready);
        buffer.Poll(Ms(60), ready);
        Assert.Equal([12, 13], ready.Select(Sequence));

        // 11 comes after all: it was given up, so it is dropped (the depacketizer has counted it lost).
        Assert.Empty(Feed(buffer, (11, 70)));
        Assert.Equal([14], Feed(buffer, (14, 71)));
    }

    [Fact]
    public void DropsDuplicatesHeldOrPassedOn()
    {
        var buffer = new RtpReorderBuffer(Window);
        Assert.Equal([10, 11, 12, 13], Feed(buffer, (10, 0), (10, 1), (12, 2), (12, 3), (13, 4), (11, 5), (11, 6)));
    }

    [Fact]
    public void FollowsTheSequenceNumberRoundTheWrap()
    {
        var buffer = new RtpReorderBuffer(Window);
        Assert.Equal([65534, 65535, 0, 1], Feed(buffer, (65534, 0), (0, 1), (65535, 2), (1, 3)));
    }

    // Each (sequence number, arrival in ms) in turn; the sequence numbers of the packets that came out.
    private static List<int> Feed(RtpReorderBuffer buffer, params (int Sequence, int Ms)[] packets)
    {
        var ready = new List<byte[]>();
        foreach (var (sequence, ms) in packets)
        {
            buffer.Add(Packet(sequence), Ms(ms), ready);
        }

        return ready.Select(Sequence).ToList();
    }

    private static byte[] Packet(int sequence) => [0x80, 96, (byte)(sequence >> 8), (byte)sequence, 0, 0, 0, 0, 0, 0, 0, 1];

    private static int Sequence(byte[] packet) => (packet[2] << 8) | packet[3];

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);
}
