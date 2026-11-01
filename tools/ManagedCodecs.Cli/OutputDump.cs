using System.Security.Cryptography;
using ManagedCodecs.Aac;
using ManagedCodecs.Containers;
using ManagedCodecs.H264;
using ManagedCodecs.Mjpeg;

namespace ManagedCodecs.Cli;

/// <summary>
/// Decodes a file's video and audio and writes what FFmpeg's own decode is compared with (tools/realworld.py): the MD5 of
/// each video frame packed as FFmpeg's rawvideo packs it (<see cref="VideoFrame.WritePacked"/>, as `-f framemd5`
/// hashes it), one a line in output order, and the audio's samples as interleaved 32-bit floats (as `-f f32le`), each
/// packet's as the decoder gives them (edit lists applied: <see cref="Packet.SkipSamples"/>, <see cref="Packet.Discard"/>).
/// </summary>
public static class OutputDump
{
    /// <summary>Writes <paramref name="prefix"/>.video.md5 and <paramref name="prefix"/>.audio.f32 for the streams there are decoders for.</summary>
    public static void Write(string path, string prefix)
    {
        using var demuxer = Demuxer.Open(path);
        var video = demuxer.Video;
        var audio = demuxer.Audio;
        IVideoDecoder? videoDecoder = video?.Codec switch
        {
            CodecIds.Mjpeg => new MjpegDecoder(video),
            CodecIds.H264 => new H264Decoder(video),
            _ => null,
        };
        IAudioDecoder? audioDecoder = audio?.Codec == CodecIds.Aac ? new AacDecoder(audio) : null;
        using var hashes = videoDecoder is null ? null : new StreamWriter(prefix + ".video.md5");
        using var samples = audioDecoder is null ? null : new BinaryWriter(File.Create(prefix + ".audio.f32"));
        var packed = new MemoryStream();
        int videoFrames = 0;
        int refused = 0;
        long audioSamples = 0;

        void AddVideo(IReadOnlyList<VideoFrame> frames)
        {
            foreach (var frame in frames)
            {
                packed.SetLength(0);
                frame.WritePacked(packed);
                hashes!.WriteLine(Convert.ToHexStringLower(MD5.HashData(packed.GetBuffer().AsSpan(0, (int)packed.Length))));
                frame.Dispose();
                videoFrames++;
            }
        }

        foreach (var packet in demuxer.ReadPackets())
        {
            if (packet.StreamIndex == video?.StreamIndex && videoDecoder is not null)
            {
                if (videoDecoder is MjpegDecoder)
                {
                    // An MJPEG packet the decoder refuses (damaged beyond decoding) gives no frame, as FFmpeg skips it.
                    try
                    {
                        AddVideo(videoDecoder.Decode(packet));
                    }
                    catch (InvalidDataException)
                    {
                        refused++;
                    }
                }
                else
                {
                    AddVideo(videoDecoder.Decode(packet));
                }
            }
            else if (packet.StreamIndex == audio?.StreamIndex && audioDecoder is not null)
            {
                var decoded = audioDecoder.Decode(packet);
                for (int i = 0; i < decoded.SampleCount; i++)
                {
                    foreach (var channel in decoded.Samples)
                    {
                        samples!.Write(channel[i]);
                    }
                }

                audioSamples += decoded.SampleCount;
            }
        }

        if (videoDecoder is not null)
        {
            AddVideo(videoDecoder.Flush());
        }

        Console.WriteLine($"{Path.GetFileName(path)}: {videoFrames} video frames ({video?.Codec ?? "none"}{(video is not null && videoDecoder is null ? ", no decoder" : "")}{(refused > 0 ? $", {refused} packets refused" : "")}), "
            + $"{audioSamples} audio samples a channel ({audio?.Codec ?? "none"}{(audio is not null && audioDecoder is null ? ", no decoder" : "")})");
    }
}
