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
/// damaged frame, a reconnect, or too few frames before the time is up. With <c>--audio</c> it plays the stream's sound
/// too (<see cref="RtspSource.Audio"/>) and checks each chunk taken against the file's decoded audio
/// (docs/decisions/2026-11-15-live-rtsp-audio.md). An <c>rtsps://</c> URL with <c>--certificate-hash</c> pins the server's
/// certificate (docs/decisions/2026-11-15-live-rtsps.md). With <c>--lossy</c> the network in between loses packets
/// for a while and then stops (tools/rtsp-lossy-relay.py): lost packets, damaged frames and frames not the file's are
/// expected, but no reconnect, and the last <see cref="LossyTail"/> frames and chunks must be the file's again (but for the
/// chunk that starts a loop of the file).
/// </summary>
[SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "MD5 to compare frames with the file's: a checksum, not security.")]
public static class LiveCheck
{
    /// <summary>
    /// With <c>--lossy</c>, the frames (and audio chunks) at the end that must be the file's: 3 s at 25 frames a
    /// second, longer than the 2 s between the nightly clips' IDR pictures, which end a loss's damage.
    /// </summary>
    public const int LossyTail = 75;

    /// <summary>Plays <paramref name="url"/> until <paramref name="frames"/> frames are shown; 0 when all is well.</summary>
    public static int Run(string url, string reference, int frames, TimeSpan timeout, bool audio = false, string? certificateHash = null, bool lossy = false)
    {
        var known = ReferenceHashes(reference);
        Console.WriteLine($"{Path.GetFileName(reference)}: {known.Count} distinct frames.");
        var (knownAudio, audioSeconds) = audio ? ReferenceAudio(reference) : ([], 0);

        var uri = new Uri(url);
        using var player = uri.Scheme is "rtsp" or "rtsps"
            ? new LivePlayer(new RtspSource(uri) { Audio = audio, CertificateHash = certificateHash })
            : new LivePlayer(new MjpegHttpSource(uri));
        player.Start();
        var clock = Stopwatch.StartNew();
        int shown = 0;
        int unknown = 0;
        int chunks = 0;
        int unknownChunks = 0;
        int lastUnknown = 0;
        var unknownChunkNumbers = new List<int>();
        while (shown < frames && clock.Elapsed < timeout && player.State is not (LiveState.Failed or LiveState.Stopped))
        {
            while (player.TakeAudio() is { } chunk)
            {
                chunks++;
                if (!knownAudio.Contains(Hash(System.Runtime.InteropServices.MemoryMarshal.AsBytes(chunk.Samples.AsSpan()))))
                {
                    unknownChunks++;
                    unknownChunkNumbers.Add(chunks);
                }
            }

            if (player.TakeLatest() is not { } frame)
            {
                Thread.Sleep(2);
                continue;
            }

            shown++;
            if (!known.Contains(Hash(frame.Pixels)))
            {
                unknown++;
                lastUnknown = shown;
                if (!lossy)
                {
                    Console.WriteLine($"Frame {shown} ({frame.Width}x{frame.Height}, at {frame.Time.TotalSeconds:0.000} s) is none of the file's.");
                }
            }

            player.Return(frame);
        }

        var s = player.Statistics;
        Console.WriteLine($"{shown} frames shown in {clock.Elapsed.TotalSeconds:0.0} s, {unknown} not in the file; state {player.State}{(player.Error is { } e ? $" ({e.GetType().Name}: {e.Message})" : "")}.");
        Console.WriteLine($"received {s.ReceivedFrames}, decoded {s.DecodedFrames}, taken {s.TakenFrames}, skipped {s.SkippedFrames}, dropped {s.DroppedFrames}, "
            + $"damaged {s.DamagedFrames}, lost packets {s.LostPackets}, reconnects {s.Reconnects}; "
            + $"decode {s.DecodeMilliseconds:0.0} ms, convert {s.ConvertMilliseconds:0.0} ms a frame.");

        bool ok = shown == frames && unknown == 0 && s.LostPackets == 0 && s.DamagedFrames == 0 && s.Reconnects == 0;
        if (lossy)
        {
            // The audio's last chunks may hold one that isn't the file's: the first of a loop of the file (below).
            int lateChunks = unknownChunkNumbers.Count(number => number > chunks - LossyTail);
            Console.WriteLine($"lossy: the last frame not in the file was number {lastUnknown}; the last {LossyTail} must be the file's."
                + (audio ? $" audio {player.Audio?.Codec ?? "none"}: {chunks} chunks taken, {unknownChunks} not in the file, {lateChunks} of them among the last {LossyTail} (1 allowed: a loop's first)." : ""));
            ok = shown == frames && s.Reconnects == 0 && player.State == LiveState.Playing && lastUnknown <= shown - LossyTail
                && (!audio || (player.Audio is not null && chunks > LossyTail && lateChunks <= 1 && s.DroppedAudioFrames == 0));
        }
        else if (audio)
        {
            // A frame's samples overlap the frame's before it (AAC LC without PNS depends on nothing earlier), so a
            // chunk is the file's unless the frame before it wasn't the file's previous one: the first one taken, and
            // the first of each loop of the file. The sound must keep up with the picture: as many seconds of it.
            int seams = 2 + (int)(clock.Elapsed.TotalSeconds / audioSeconds);
            Console.WriteLine($"audio {player.Audio?.Codec ?? player.SkippedAudioCodec ?? "none"}: {chunks} chunks taken, {unknownChunks} not in the file ({seams} allowed: the first, and each loop's); "
                + $"decoded {s.DecodedAudioFrames}, dropped {s.DroppedAudioFrames}, damaged {s.DamagedAudioFrames}.");
            ok &= player.Audio is not null && unknownChunks <= seams && chunks >= shown && s.DroppedAudioFrames == 0 && s.DamagedAudioFrames == 0;
        }

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

    // The MD5 of each of the file's audio frames as the player hands them over (float stereo), and the audio's length.
    private static (HashSet<string> Hashes, double Seconds) ReferenceAudio(string path)
    {
        using var demuxer = Demuxer.Open(path);
        var audio = demuxer.Audio ?? throw new InvalidDataException($"{path} has no audio.");
        var decoder = AudioPipeline.CreateDecoder(audio);
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        double seconds = 0;
        foreach (var packet in demuxer.ReadPackets())
        {
            if (packet.StreamIndex != audio.StreamIndex)
            {
                continue;
            }

            var frame = decoder.Decode(packet.Data.Span);
            if (frame.SampleCount > 0)
            {
                var chunk = AudioPipeline.ToStereo(frame);
                hashes.Add(Hash(System.Runtime.InteropServices.MemoryMarshal.AsBytes(chunk.Samples.AsSpan())));
                seconds += (double)frame.SampleCount / chunk.SampleRate;
            }
        }

        Console.WriteLine($"{Path.GetFileName(path)}: {hashes.Count} distinct audio chunks, {seconds:0.0} s.");
        return (hashes, seconds);
    }

    private static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(MD5.HashData(data));
}
#endif
