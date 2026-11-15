using System.Buffers.Binary;
using ManagedCodecs.H264;
using ManagedCodecs.Playback;

namespace ManagedCodecs.Tests.Playback;

/// <summary>
/// LivePlayer's RTCP receiver reports against the loopback RTSP server (docs/decisions/2026-11-15-live-rtcp.md): every
/// five seconds by the player's clock each track's report goes back the way its packets come, on the channel after
/// the track's on the RTSP connection or from the RTCP port to the server's, saying what arrived (the stream's SSRC,
/// its highest sequence number, what was lost) and echoing the camera's last sender report. And the capture times the
/// sender reports give: each frame and audio chunk placed on the camera's clock by its RTP timestamp, null before the
/// track's first report, a frame taking its own picture's time when B pictures are sent out of order.
/// </summary>
public class LiveRtcpTests
{
    private const string Slices = "h264-main-slices";
    private const string Alaw = "wav-alaw-codes";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Unstalled = TimeSpan.FromDays(10);
    private static readonly DateTime Noon = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ReportsEachTrackOnTheChannelAfterItsOwn()
    {
        var video = LiveRtspTests.AccessUnits(Slices)[0];
        var audio = RtpH264DepacketizerTests.Recording(Alaw)[0];
        using var server = new RtspTestServer(LiveRtspAudioTests.TwoTracks(Alaw), async play =>
        {
            // The camera's sender reports first: read before the packets after them on the connection.
            // The picture was captured a second after the video's report, the sound half a second after the audio's.
            await play.SendAsync(RtcpReport.SenderReport(Ssrc(video[0]), RtcpReport.NtpSeconds(Noon), 0x55667788, Timestamp(video[0]) - 90000), 1);
            await play.SendAsync(RtcpReport.SenderReport(Ssrc(audio), RtcpReport.NtpSeconds(Noon) + 7, 0xEEFF0011, Timestamp(audio) - 4000), 3);
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
        var frame = WaitFor(player, player.TakeLatest);
        var chunk = WaitFor(player, player.TakeAudio);

        // Both on the camera's clock, by each track's own report (their fractions: 0x55667788 and 0xEEFF0011 of 2^32).
        Assert.Equal(Noon.AddSeconds(1).AddTicks((0x55667788L * TimeSpan.TicksPerSecond) >> 32), frame.CaptureTime);
        Assert.Equal(Noon.AddSeconds(7.5).AddTicks((0xEEFF0011L * TimeSpan.TicksPerSecond) >> 32), chunk.CaptureTime);

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
            new RtcpReportBlock(Ssrc(video[0]), 0, 0, Sequence(video[^1]), 0, (RtcpReport.NtpSeconds(Noon) << 16) | 0x5566, 5 * 65536),
            block);
        block = Assert.Single(reports[3].Blocks);
        Assert.Equal(new RtcpReportBlock(Ssrc(audio), 0, 0, Sequence(audio), 0, ((RtcpReport.NtpSeconds(Noon) + 7) << 16) | 0xEEFF, 5 * 65536), block);
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

    [Fact]
    public void GivesNoCaptureTimeBeforeTheTracksFirstSenderReport()
    {
        // A picture and three packets of sound (one AAC frame each) before the camera's sender reports, and as much after.
        const string Single = "aac-stereo-48k-single";
        var track = RtpAudioDepacketizerTests.Track(Single);
        var audio = RtpH264DepacketizerTests.Recording(Single);
        var chunks = LiveRtspAudioTests.Expected(track, audio);
        var units = LiveRtspTests.AccessUnits(Slices);
        using var gate = new SemaphoreSlim(0);
        using var server = new RtspTestServer(LiveRtspAudioTests.TwoTracks(Single), async play =>
        {
            for (int i = 0; i < 2; i++)
            {
                foreach (var packet in units[i])
                {
                    await play.SendAsync(packet);
                }

                foreach (var packet in audio.Skip(3 * i).Take(3))
                {
                    await play.SendAsync(packet, 2);
                }

                await gate.WaitAsync(play.Token);
                await play.SendAsync(RtcpReport.SenderReport(Ssrc(units[0][0]), RtcpReport.NtpSeconds(Noon), 0, Timestamp(units[0][0])), 1);
                await play.SendAsync(RtcpReport.SenderReport(Ssrc(audio[0]), RtcpReport.NtpSeconds(Noon), 0, Timestamp(audio[0])), 3);
            }

            await Task.Delay(Timeout.Infinite, play.Token);
        });

        using var player = new LivePlayer(new RtspSource(server.Uri) { Audio = true, StallTimeout = Unstalled });
        player.Start();
        Assert.Null(WaitFor(player, player.TakeLatest).CaptureTime);
        int before = chunks.Take(3).Sum(c => c.Count);
        Assert.True(before > 0);
        for (int i = 0; i < before; i++)
        {
            Assert.Null(WaitFor(player, player.TakeAudio).CaptureTime);
        }

        gate.Release();

        // Then each by its own track's clock rate from its report's instant.
        var frame = WaitFor(player, player.TakeLatest);
        Assert.Equal(Noon.AddTicks((Timestamp(units[1][0]) - Timestamp(units[0][0])) * TimeSpan.TicksPerSecond / 90000), frame.CaptureTime);
        Assert.True(frame.CaptureTime > Noon);
        for (int i = 3; i < 6; i++)
        {
            Assert.Single(chunks[i]);
            var chunk = WaitFor(player, player.TakeAudio);
            Assert.Equal(Noon.AddTicks((Timestamp(audio[i]) - Timestamp(audio[0])) * TimeSpan.TicksPerSecond / track.SampleRate), chunk.CaptureTime);
        }
    }

    [Fact]
    public void GivesEachFrameItsOwnPicturesTimeWhenBPicturesComeOutOfOrder()
    {
        // B pictures are sent before pictures shown ahead of them, so the units' RTP timestamps don't rise; the frames
        // come out in the timestamps' order. For each unit, the test's own decoder says how many frames it gives.
        const string Name = "h264-b-motion";
        var units = LiveRtspTests.AccessUnits(Name);
        var stamps = units.Select(unit => Timestamp(unit[0])).ToList();
        Assert.Contains(Enumerable.Range(1, stamps.Count - 1), i => (int)(stamps[i] - stamps[i - 1]) < 0);
        var inOrder = stamps.OrderBy(stamp => (int)(stamp - stamps[0])).ToList();
        var counts = FrameCounts(Name, units);
        using var gate = new SemaphoreSlim(0);
        using var server = new RtspTestServer(RtspTestServer.SdpOf(Name), async play =>
        {
            await play.SendAsync(RtcpReport.SenderReport(Ssrc(units[0][0]), RtcpReport.NtpSeconds(Noon), 0, stamps[0]), 1);
            foreach (var unit in units)
            {
                foreach (var packet in unit)
                {
                    await play.SendAsync(packet);
                }

                await gate.WaitAsync(play.Token);
            }

            await Task.Delay(Timeout.Infinite, play.Token);
        });

        using var player = new LivePlayer(new RtspSource(server.Uri) { MaxLag = TimeSpan.FromHours(1), StallTimeout = Unstalled });
        player.Start();
        int shown = 0;
        int checkedFrames = 0;
        for (int i = 0; i < units.Count; i++)
        {
            shown += counts[i];
            if (counts[i] > 0)
            {
                var frame = WaitFor(player, player.TakeLatest);
                Assert.Equal(Noon.AddTicks((long)(int)(inOrder[shown - 1] - stamps[0]) * TimeSpan.TicksPerSecond / 90000), frame.CaptureTime);
                player.Return(frame);
                checkedFrames++;
            }

            gate.Release();
        }

        Assert.True(checkedFrames > 20, $"{checkedFrames} frames.");
        Assert.Equal(0L, player.Statistics.DamagedFrames);
    }

    [Fact]
    public void KeepsEachFramesOwnTimeAfterTheDecoderGaveAFrameMoreThanItWasGivenPictures()
    {
        // The recording twice over, the second time later by its length, with one B picture of the first sent twice:
        // the decoder then gives a frame more than there were pictures, and times dealt out in order to the frames as
        // they come would each be a neighbour's from there on. The second pass's frames have their own.
        const string Name = "h264-b-motion";
        var first = LiveRtspTests.AccessUnits(Name);
        var stamps = first.Select(unit => Timestamp(unit[0])).ToList();
        uint length = (uint)(stamps.Max(stamp => (int)(stamp - stamps[0])) + 3600);
        int repeated = Enumerable.Range(2, first.Count - 2).First(i => (int)(stamps[i] - stamps[i - 1]) < 0);
        var units = first.Take(repeated + 1).Append(first[repeated]).Concat(first.Skip(repeated + 1))
            .Concat(first.Select(unit => unit.Select(packet => WithTimestamp(packet, Timestamp(packet) + length)).ToList())).ToList();
        ushort sequence = 0;
        units = [.. units.Select(unit => unit.Select(packet => WithSequence(packet, sequence++)).ToList())];
        int secondIdr = first.Count + 1;
        var counts = FrameCounts(Name, units);
        int fromFirst = counts.Take(secondIdr + 1).Sum();
        Assert.NotEqual(first.Count, fromFirst);
        var inOrder = stamps.OrderBy(stamp => (int)(stamp - stamps[0])).ToList();
        using var gate = new SemaphoreSlim(0);
        using var server = new RtspTestServer(RtspTestServer.SdpOf(Name), async play =>
        {
            await play.SendAsync(RtcpReport.SenderReport(Ssrc(units[0][0]), RtcpReport.NtpSeconds(Noon), 0, stamps[0]), 1);
            foreach (var unit in units)
            {
                foreach (var packet in unit)
                {
                    await play.SendAsync(packet);
                }

                await gate.WaitAsync(play.Token);
            }

            await Task.Delay(Timeout.Infinite, play.Token);
        });

        using var player = new LivePlayer(new RtspSource(server.Uri) { MaxLag = TimeSpan.FromHours(1), StallTimeout = Unstalled });
        player.Start();
        int shown = 0;
        int checkedFrames = 0;
        for (int i = 0; i < units.Count; i++)
        {
            shown += counts[i];
            if (counts[i] > 0)
            {
                var frame = WaitFor(player, player.TakeLatest);
                if (i > secondIdr)
                {
                    long ticks = (long)(length + (int)(inOrder[shown - fromFirst - 1] - stamps[0])) * TimeSpan.TicksPerSecond / 90000;
                    Assert.Equal(Noon.AddTicks(ticks), frame.CaptureTime);
                    checkedFrames++;
                }

                player.Return(frame);
            }

            gate.Release();
        }

        Assert.True(checkedFrames > 20, $"{checkedFrames} frames.");
    }

    [Fact]
    public void PlacesAnAacPacketsLaterFramesByTheLengthOfThoseBeforeThem()
    {
        // FFmpeg's packetizer puts several access units in a packet, which has one RTP timestamp: the first's.
        const string Name = "aac-stereo-48k";
        var track = RtpAudioDepacketizerTests.Track(Name);
        var packets = RtpH264DepacketizerTests.Recording(Name);
        var expected = LiveRtspAudioTests.Expected(track, packets);
        Assert.Contains(expected, chunks => chunks.Count > 1);
        var video = LiveRtspTests.AccessUnits(Slices)[0];
        using var gate = new SemaphoreSlim(0);
        using var server = new RtspTestServer(LiveRtspAudioTests.TwoTracks(Name), async play =>
        {
            foreach (var packet in video)
            {
                await play.SendAsync(packet);
            }

            await play.SendAsync(RtcpReport.SenderReport(Ssrc(packets[0]), RtcpReport.NtpSeconds(Noon), 0, Timestamp(packets[0])), 3);
            foreach (var packet in packets)
            {
                await play.SendAsync(packet, 2);
                await gate.WaitAsync(play.Token);
            }

            await Task.Delay(Timeout.Infinite, play.Token);
        });

        using var player = new LivePlayer(new RtspSource(server.Uri) { Audio = true, StallTimeout = Unstalled });
        player.Start();
        Assert.NotNull(WaitFor(player, player.TakeLatest));
        Assert.True(packets.Count > 3, $"{packets.Count} packets.");
        for (int i = 0; i < packets.Count; i++)
        {
            long since = 0;
            foreach (var samples in expected[i])
            {
                var chunk = WaitFor(player, player.TakeAudio);
                var packetTime = Noon.AddTicks((Timestamp(packets[i]) - Timestamp(packets[0])) * TimeSpan.TicksPerSecond / track.SampleRate);
                Assert.Equal(packetTime.AddTicks(since), chunk.CaptureTime);
                since += samples.Length / 2 * TimeSpan.TicksPerSecond / chunk.SampleRate;
            }

            gate.Release();
        }
    }

    // How many frames the decoder gives for each unit, as LivePlayer decodes them.
    private static List<int> FrameCounts(string name, List<List<byte[]>> units)
    {
        var depacketizer = new RtpH264Depacketizer(96, RtpH264DepacketizerTests.SdpParameterSets(name));
        var decoder = new H264Decoder(ReadOnlySpan<byte>.Empty) { ConcealErrors = true };
        var counts = new List<int>();
        long pts = 0;
        foreach (var unit in units)
        {
            var done = new List<RtpAccessUnit>();
            foreach (var packet in unit)
            {
                depacketizer.Add(packet, done);
            }

            var frames = decoder.Decode(Assert.Single(done).Data, pts++);
            counts.Add(frames.Count);
            foreach (var frame in frames)
            {
                frame.Dispose();
            }
        }

        return counts;
    }

    private static uint Timestamp(byte[] packet) => BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4));

    // The receiver reports that reached the server's RTCP port (the punch packet, 8 bytes, is not one).
    private static List<RtcpReport> Reports(RtspTestServer server) =>
        [.. server.Punches.Where(p => p.Port == "rtcp" && p.Data.Length > 8).Select(p => RtcpReport.Parse(p.Data))];

    private static uint Ssrc(byte[] packet) => BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8));

    private static uint Sequence(byte[] packet) => BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2));

    private static byte[] WithTimestamp(byte[] packet, uint timestamp)
    {
        var copy = (byte[])packet.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(4), timestamp);
        return copy;
    }

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
