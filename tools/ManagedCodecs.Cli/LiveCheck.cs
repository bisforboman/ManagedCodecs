#if LIVE
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using ManagedCodecs.Containers;
using ManagedCodecs.H264;
using ManagedCodecs.Mjpeg;
using ManagedCodecs.Playback;

namespace ManagedCodecs.Cli;

/// <summary>
/// <c>mcdec --live</c>: plays a live stream (RTSP, or MJPEG over HTTP) with <see cref="LivePlayer"/>, without a window,
/// and checks every frame it shows against a decode of the file the server is streaming: the frame's RGBA must be one
/// of the file's frames, converted as the player converts them. tools/rtsp-nightly.py runs it against mediamtx serving
/// FFmpeg's push of the file in a loop. Prints the statistics; fails on a frame not in the file, a lost packet, a
/// damaged frame, a reconnect, or too few frames before the time is up.
/// </summary>
[SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "MD5 to compare frames with the file's: a checksum, not security.")]
public static class LiveCheck
{
    /// <summary>Plays <paramref name="url"/> until <paramref name="frames"/> frames are shown; 0 when all is well.</summary>
    public static int Run(string url, string reference, int frames, TimeSpan timeout)
    {
        var known = ReferenceHashes(reference);
        Console.WriteLine($"{Path.GetFileName(reference)}: {known.Count} distinct frames.");

        var uri = new Uri(url);
        using var player = uri.Scheme == "rtsp" ? new LivePlayer(new RtspSource(uri)) : new LivePlayer(new MjpegHttpSource(uri));
        player.Start();
        var clock = Stopwatch.StartNew();
        int shown = 0;
        int unknown = 0;
        while (shown < frames && clock.Elapsed < timeout && player.State is not (LiveState.Failed or LiveState.Stopped))
        {
            if (player.TakeLatest() is not { } frame)
            {
                Thread.Sleep(2);
                continue;
            }

            shown++;
            if (!known.Contains(Hash(frame.Pixels)))
            {
                unknown++;
                Console.WriteLine($"Frame {shown} ({frame.Width}x{frame.Height}, at {frame.Time.TotalSeconds:0.000} s) is none of the file's.");
            }

            player.Return(frame);
        }

        var s = player.Statistics;
        Console.WriteLine($"{shown} frames shown in {clock.Elapsed.TotalSeconds:0.0} s, {unknown} not in the file; state {player.State}{(player.Error is { } e ? $" ({e.GetType().Name}: {e.Message})" : "")}.");
        Console.WriteLine($"received {s.ReceivedFrames}, decoded {s.DecodedFrames}, taken {s.TakenFrames}, skipped {s.SkippedFrames}, dropped {s.DroppedFrames}, "
            + $"damaged {s.DamagedFrames}, lost packets {s.LostPackets}, reconnects {s.Reconnects}; "
            + $"decode {s.DecodeMilliseconds:0.0} ms, convert {s.ConvertMilliseconds:0.0} ms a frame.");

        bool ok = shown == frames && unknown == 0 && s.LostPackets == 0 && s.DamagedFrames == 0 && s.Reconnects == 0;
        Console.WriteLine(ok ? "OK" : "FAILED");
        return ok ? 0 : 1;
    }

    // The MD5 of each of the file's frames, converted to RGBA as LivePlayer converts them (YuvToRgba.For).
    private static HashSet<string> ReferenceHashes(string path)
    {
        using var demuxer = Demuxer.Open(path);
        var video = demuxer.Video ?? throw new InvalidDataException($"{path} has no video.");
        IVideoDecoder decoder = video.Codec switch
        {
            CodecIds.H264 => new H264Decoder(video),
            CodecIds.Mjpeg => new MjpegDecoder(video),
            _ => throw new NotSupportedException($"No live decoding of {video.Codec}."),
        };

        var hashes = new HashSet<string>(StringComparer.Ordinal);
        void Add(IReadOnlyList<VideoFrame> decoded)
        {
            foreach (var frame in decoded)
            {
                var pixels = new byte[frame.Width * frame.Height * 4];
                YuvToRgba.For(frame).Convert(frame, pixels);
                hashes.Add(Hash(pixels));
                frame.Dispose();
            }
        }

        foreach (var packet in demuxer.ReadPackets())
        {
            if (packet.StreamIndex == video.StreamIndex)
            {
                Add(decoder.Decode(packet));
            }
        }

        Add(decoder.Flush());
        return hashes;
    }

    private static string Hash(byte[] pixels) => Convert.ToHexStringLower(MD5.HashData(pixels));
}
#endif
