using System.Buffers.Binary;
using ManagedCodecs.Playback;

namespace ManagedCodecs.Tests.Playback;

/// <summary>
/// LivePlayer's RTCP receiver reports against the loopback RTSP server (docs/decisions/2026-11-15-live-rtcp.md): every
/// five seconds by the player's clock each track's report goes back the way its packets come, on the channel after
/// the track's on the RTSP connection or from the RTCP port to the server's, saying what arrived (the stream's SSRC,
/// its highest sequence number, what was lost) and echoing the camera's last sender report.
/// </summary>
public class LiveRtcpTests
{
    private const string Slices = "h264-main-slices";
    private const string Alaw = "wav-alaw-codes";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Unstalled = TimeSpan.FromDays(10);

    [Fact]
    public void ReportsEachTrackOnTheChannelAfterItsOwn()
    {
        var video = LiveRtspTests.AccessUnits(Slices)[0];
        var audio = RtpH264DepacketizerTests.Recording(Alaw)[0];
        using var server = new RtspTestServer(LiveRtspAudioTests.TwoTracks(Alaw), async play =>
        {
            // The camera's sender reports first: read before the packets after them on the connection.
            await play.SendAsync(RtcpReport.SenderReport(Ssrc(video[0]), 0x11223344, 0x55667788, 0), 1);
            await play.SendAsync(RtcpReport.SenderReport(Ssrc(audio), 0xAABBCCDD, 0xEEFF0011, 0), 3);
            foreach (var packet in video)
            {
                await play.SendAsync(packet);
            }

            await play.SendAsync(audio, 2);
            await Task.Delay(Timeout.Infinite, play.Token);
        });

        var clock = new FakeLiveClock();
        using var player = new LivePlayer(new RtspSource(server.Uri) { Audio = true, StallTimeout = Unstalled }, clock);
        player.Start();
        Assert.NotNull(WaitFor(player, player.TakeLatest));
        Assert.NotNull(WaitFor(player, player.TakeAudio));

        // Nothing before the interval has passed, then one report a track.
        clock.Advance(TimeSpan.FromSeconds(4.9));
        Thread.Sleep(50);
        Assert.Empty(server.Interleaved);
        clock.Advance(TimeSpan.FromSeconds(0.1));
        WaitFor(player, () => server.Interleaved.Count >= 2 ? server : null);
        var reports = server.Interleaved.ToDictionary(p => p.Channel, p => RtcpReport.Parse(p.Data));
        Assert.Equal([1, 3], reports.Keys.Order());

        // Everything arrived at the clock's zero: no loss, no jitter, and five seconds since the sender reports.
        var block = Assert.Single(reports[1].Blocks);
        Assert.Equal(
            new RtcpReportBlock(Ssrc(video[0]), 0, 0, Sequence(video[^1]), 0, 0x33445566, 5 * 65536),
            block);
        block = Assert.Single(reports[3].Blocks);
        Assert.Equal(new RtcpReportBlock(Ssrc(audio), 0, 0, Sequence(audio), 0, 0xCCDDEEFF, 5 * 65536), block);
        Assert.NotEqual(reports[1].Ssrc, reports[3].Ssrc);

        // And again after the next interval.
        clock.Advance(TimeSpan.FromSeconds(5));
        WaitFor(player, () => server.Interleaved.Count >= 4 ? server : null);
        Assert.Equal(10u * 65536, Assert.Single(RtcpReport.Parse(server.Interleaved.Last(p => p.Channel == 1).Data).Blocks).Delay);
        Assert.Equal((LiveState.Playing, 1, 0L), (player.State, server.Connections, player.Statistics.Reconnects));
    }

    [Fact]
    public void ReportsOverUdpFromTheRtcpPortWithWhatWasLost()
    {
        // The second unit's packets numbered one further on: one packet between the units never comes.
        var units = LiveRtspTests.AccessUnits(Slices);
        var second = units[1].Select(packet => WithSequence(packet, Sequence(packet) + 1)).ToList();
        uint ssrc = Ssrc(units[0][0]);
        using var server = new RtspTestServer(RtspTestServer.SdpOf(Slices), async play =>
        {
            foreach (var packet in units[0].Concat(second))
            {
                await play.SendAsync(packet);
            }

            await play.SendAsync(RtcpReport.SenderReport(ssrc, 0x11223344, 0x55667788, 0), 1);
            await Task.Delay(Timeout.Infinite, play.Token);
        })
        {
            UdpOnly = true,
            Ssrc = ssrc,
        };

        var clock = new FakeLiveClock();
        using var player = new LivePlayer(new RtspSource(server.Uri) { StallTimeout = Unstalled }, clock);
        player.Start();
        Assert.NotNull(WaitFor(player, player.TakeLatest));

        // The datagrams arrive when they will: a report every five seconds of the clock, until one has seen them all.
        uint highest = Sequence(second[^1]);
        var block = WaitFor(player, () =>
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            return Reports(server).Select(r => r.Blocks.SingleOrDefault()).LastOrDefault(b => b is { LastSenderReport: not 0 } && b.Highest == highest);
        });
        Assert.Equal((ssrc, 1, 0x33445566u), (block.Source, block.Lost, block.LastSenderReport));
        Assert.Equal(0u, block.Delay % (5 * 65536));

        // The reports come from the client's RTCP port, the one after its RTP port, as one receiver.
        Assert.Single(Reports(server).Select(r => r.Ssrc).Distinct());
        Assert.Equal(LiveState.Playing, player.State);
    }

    // The receiver reports that reached the server's RTCP port (the punch packet, 8 bytes, is not one).
    private static List<RtcpReport> Reports(RtspTestServer server) =>
        [.. server.Punches.Where(p => p.Port == "rtcp" && p.Data.Length > 8).Select(p => RtcpReport.Parse(p.Data))];

    private static uint Ssrc(byte[] packet) => BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8));

    private static uint Sequence(byte[] packet) => BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2));

    private static byte[] WithSequence(byte[] packet, uint sequence)
    {
        var copy = (byte[])packet.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(2), (ushort)sequence);
        return copy;
    }

    private static T WaitFor<T>(LivePlayer player, Func<T?> poll)
        where T : class
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            if (poll() is { } value)
            {
                return value;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Timed out: {player.State}, {player.Error}, {player.Statistics}.");
            Thread.Sleep(2);
        }
    }
}
